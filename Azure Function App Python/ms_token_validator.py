"""
MS Token Validator
Validates Microsoft OAuth tokens and extracts client information.
Includes signature verification using Microsoft's JWKS endpoint.
"""

import json
import base64
import logging
import requests
from typing import Dict, Optional, Any
from datetime import datetime, timedelta

# Optional: PyJWT for full signature verification
try:
    import jwt
    from jwt import PyJWKClient
    JWT_AVAILABLE = True
except ImportError:
    JWT_AVAILABLE = False


class MSTokenValidator:
    """
    Validates Microsoft OAuth 2.0 tokens and extracts client information.
    Can decode tokens generated from https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/token
    Supports signature verification using Microsoft's JWKS endpoint.
    """

    # Microsoft's OpenID configuration endpoints
    OPENID_CONFIG_URL_TEMPLATE = "https://login.microsoftonline.com/{tenant_id}/v2.0/.well-known/openid-configuration"
    COMMON_OPENID_CONFIG_URL = "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"
    
    # JWKS URL template for signature verification
    JWKS_URL_TEMPLATE = "https://login.microsoftonline.com/{tenant_id}/discovery/v2.0/keys"
    
    def __init__(self, tenant_id: str = None):
    
        self.tenant_id = tenant_id
        self._jwks_client = None
        self._jwks_cache = None
        self._jwks_cache_time = None
        self._cache_duration_seconds = 3600  # Cache JWKS for 1 hour

    def _get_jwks_client(self, tenant_id: str = None) -> Optional[Any]:
      
        if not JWT_AVAILABLE:
            return None
        
        tid = tenant_id or self.tenant_id or 'common'
        jwks_url = self.JWKS_URL_TEMPLATE.format(tenant_id=tid)
        
        # Create new client if not cached or tenant changed
        if self._jwks_client is None:
            self._jwks_client = PyJWKClient(jwks_url, cache_keys=True, lifespan=3600)
        
        return self._jwks_client

    def verify_token_signature(self, token: str, expected_audience: str = None, 
                                logger: logging.Logger = None) -> Dict[str, Any]:
       
        result = {
            'is_valid': False,
            'client_id': None,
            'payload': None,
            'error': None
        }
        
        if not JWT_AVAILABLE:
            result['error'] = "PyJWT library not installed. Run: pip install PyJWT cryptography"
            if logger:
                logger.error(result['error'])
            return result
        
        try:
            # Remove 'Bearer ' prefix if present
            if token and token.lower().startswith('bearer '):
                token = token[7:]
            
            if not token:
                result['error'] = "No token provided"
                return result
            
            # Get the signing key from Microsoft's JWKS endpoint
            jwks_client = self._get_jwks_client()
            if not jwks_client:
                result['error'] = "Failed to initialize JWKS client"
                return result
            
            # Get the signing key that matches the token's key ID
            signing_key = jwks_client.get_signing_key_from_jwt(token)
            
            # Decode and verify the token
            # Microsoft uses RS256 algorithm for signing tokens
            decode_options = {
                'verify_signature': True,
                'verify_exp': True,
                'verify_nbf': True,
                'verify_iat': True,
                'require': ['exp', 'iat', 'nbf']
            }
            
            # Set audience validation
            if expected_audience:
                decode_options['verify_aud'] = True
                audience = expected_audience
            else:
                decode_options['verify_aud'] = False
                audience = None
            
            # Verify and decode the token
            payload = jwt.decode(
                token,
                signing_key.key,
                algorithms=["RS256"],
                audience=audience,
                options=decode_options
            )
            
            result['is_valid'] = True
            result['payload'] = payload
            result['client_id'] = payload.get('appid') or payload.get('azp')
            
            if logger:
                logger.info(f"Token signature verified successfully for client_id: {result['client_id']}")
            
            return result
            
        except jwt.ExpiredSignatureError:
            result['error'] = "Token has expired"
            if logger:
                logger.warning("Token signature verification failed: Token expired")
        except jwt.InvalidAudienceError:
            result['error'] = f"Invalid audience. Expected: {expected_audience}"
            if logger:
                logger.warning(f"Token signature verification failed: Invalid audience")
        except jwt.InvalidSignatureError:
            result['error'] = "Invalid token signature - token may be forged or tampered with"
            if logger:
                logger.error("Token signature verification failed: Invalid signature")
        except jwt.DecodeError as e:
            result['error'] = f"Failed to decode token: {str(e)}"
            if logger:
                logger.error(f"Token signature verification failed: {str(e)}")
        except Exception as e:
            result['error'] = f"Token verification error: {str(e)}"
            if logger:
                logger.error(f"Token signature verification error: {str(e)}")
        
        return result

    def is_signature_verification_available(self) -> bool:
       
        return JWT_AVAILABLE

    def get_client_id_from_token(self, token: str, logger: logging.Logger = None) -> Optional[str]:
       
        try:
            if logger:
                logger.info("Attempting to extract client_id from token")
            
            # Decode the token (without verification for extraction only)
            token_data = self._decode_token(token, logger)
            
            if not token_data:
                if logger:
                    logger.error("Failed to decode token")
                return None
            
            # Microsoft tokens use 'appid' for v1 tokens and 'azp' for v2 tokens
            # Also check 'aud' as fallback for some token types
            client_id = token_data.get('appid') or token_data.get('azp')
            
            if client_id:
                if logger:
                    logger.info(f"Successfully extracted client_id: {client_id}")
                return client_id
            
            if logger:
                logger.warning("Token does not contain appid or azp claim")
                logger.debug(f"Available claims: {list(token_data.keys())}")
            
            return None
            
        except Exception as e:
            if logger:
                logger.error(f"Error extracting client_id from token: {str(e)}")
            return None

    def get_token_info(self, token: str, logger: logging.Logger = None) -> Optional[Dict[str, Any]]:
      
        try:
            token_data = self._decode_token(token, logger)
            
            if not token_data:
                return None
            
            # Extract relevant information
            info = {
                'client_id': token_data.get('appid') or token_data.get('azp'),
                'tenant_id': token_data.get('tid'),
                'audience': token_data.get('aud'),
                'issuer': token_data.get('iss'),
                'subject': token_data.get('sub'),
                'object_id': token_data.get('oid'),
                'scopes': token_data.get('scp', '').split() if token_data.get('scp') else token_data.get('roles', []),
                'expiration': self._timestamp_to_datetime(token_data.get('exp')),
                'issued_at': self._timestamp_to_datetime(token_data.get('iat')),
                'not_before': self._timestamp_to_datetime(token_data.get('nbf')),
                'token_version': token_data.get('ver'),
                'app_display_name': token_data.get('app_displayname'),
                'idtyp': token_data.get('idtyp'),  # 'app' for application tokens
                'raw_claims': token_data
            }
            
            # Check if token is expired
            if info['expiration']:
                info['is_expired'] = datetime.utcnow() > info['expiration']
            else:
                info['is_expired'] = None
            
            if logger:
                logger.info(f"Token info extracted - client_id: {info['client_id']}, tenant: {info['tenant_id']}")
            
            return info
            
        except Exception as e:
            if logger:
                logger.error(f"Error getting token info: {str(e)}")
            return None

    def validate_token(self, token: str, expected_audience: str = None, 
                       expected_client_id: str = None, logger: logging.Logger = None) -> Dict[str, Any]:
       
        result = {
            'is_valid': True,
            'client_id': None,
            'errors': [],
            'token_info': None
        }
        
        try:
            token_info = self.get_token_info(token, logger)
            
            if not token_info:
                result['is_valid'] = False
                result['errors'].append("Failed to decode token")
                return result
            
            result['token_info'] = token_info
            result['client_id'] = token_info.get('client_id')
            
            # Check expiration
            if token_info.get('is_expired'):
                result['is_valid'] = False
                result['errors'].append("Token is expired")
            
            # Check audience if specified
            if expected_audience and token_info.get('audience') != expected_audience:
                result['is_valid'] = False
                result['errors'].append(f"Audience mismatch. Expected: {expected_audience}, Got: {token_info.get('audience')}")
            
            # Check client_id if specified
            if expected_client_id and token_info.get('client_id') != expected_client_id:
                result['is_valid'] = False
                result['errors'].append(f"Client ID mismatch. Expected: {expected_client_id}, Got: {token_info.get('client_id')}")
            
            if logger:
                if result['is_valid']:
                    logger.info("Token validation successful")
                else:
                    logger.warning(f"Token validation failed: {result['errors']}")
            
            return result
            
        except Exception as e:
            if logger:
                logger.error(f"Error validating token: {str(e)}")
            result['is_valid'] = False
            result['errors'].append(f"Validation error: {str(e)}")
            return result

    def _decode_token(self, token: str, logger: logging.Logger = None) -> Optional[Dict[str, Any]]:
 
        try:
            # JWT format: header.payload.signature
            parts = token.split('.')
            
            if len(parts) != 3:
                if logger:
                    logger.error(f"Invalid JWT format - expected 3 parts, got {len(parts)}")
                return None
            
            # Decode the payload (second part)
            payload = parts[1]
            
            # Add padding if necessary (JWT uses base64url encoding without padding)
            padding_needed = 4 - (len(payload) % 4)
            if padding_needed != 4:
                payload += '=' * padding_needed
            
            # Decode base64url
            decoded_bytes = base64.urlsafe_b64decode(payload)
            decoded_str = decoded_bytes.decode('utf-8')
            
            # Parse JSON
            token_data = json.loads(decoded_str)
            
            return token_data
            
        except Exception as e:
            if logger:
                logger.error(f"Error decoding token: {str(e)}")
            return None

    def _decode_token_header(self, token: str) -> Optional[Dict[str, Any]]:
        
        try:
            parts = token.split('.')
            if len(parts) != 3:
                return None
            
            header = parts[0]
            padding_needed = 4 - (len(header) % 4)
            if padding_needed != 4:
                header += '=' * padding_needed
            
            decoded_bytes = base64.urlsafe_b64decode(header)
            return json.loads(decoded_bytes.decode('utf-8'))
            
        except Exception:
            return None

    def _timestamp_to_datetime(self, timestamp: int) -> Optional[datetime]:
       
        if timestamp:
            try:
                return datetime.utcfromtimestamp(timestamp)
            except (ValueError, OSError):
                return None
        return None

    def get_openid_configuration(self, tenant_id: str = None) -> Optional[Dict[str, Any]]:
     
        try:
            tid = tenant_id or self.tenant_id or 'common'
            url = self.OPENID_CONFIG_URL_TEMPLATE.format(tenant_id=tid)
            
            response = requests.get(url, timeout=10)
            if response.status_code == 200:
                return response.json()
            return None
            
        except Exception:
            return None


# Singleton instance for convenience
_validator_instance = None

def get_validator(tenant_id: str = None) -> MSTokenValidator:
   
    global _validator_instance
    if _validator_instance is None or (tenant_id and _validator_instance.tenant_id != tenant_id):
        _validator_instance = MSTokenValidator(tenant_id)
    return _validator_instance


def get_client_id_from_token(token: str, logger: logging.Logger = None) -> Optional[str]:
 
    validator = get_validator()
    return validator.get_client_id_from_token(token, logger)
