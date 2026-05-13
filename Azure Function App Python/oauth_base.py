import hashlib
import hmac
import base64
import time
import random
import string
from urllib.parse import urlparse, parse_qs, quote
from typing import Tuple, List, Optional
from dataclasses import dataclass

@dataclass
class QueryParameter:
    """Represents a query parameter for OAuth"""
    name: str
    value: str

    def __lt__(self, other):
        """Comparison for sorting"""
        if self.name == other.name:
            return self.value < other.value
        return self.name < other.name


class SignatureTypes:
    """OAuth signature types enumeration"""
    HMACSHA1 = "HMAC-SHA1"
    HMACSHA256 = "HMAC-SHA256"
    PLAINTEXT = "PLAINTEXT"
    RSASHA1 = "RSA-SHA1"


class OAuthBase:
    """
    OAuth Base class for generating signatures and tokens
    Port of the C# OAuthBase class
    """
    
    # OAuth constants
    OAUTH_VERSION = "1.0"
    OAUTH_PARAMETER_PREFIX = "oauth_"
    OAUTH_CONSUMER_KEY = "oauth_consumer_key"
    OAUTH_CALLBACK = "oauth_callback"
    OAUTH_VERSION_KEY = "oauth_version"
    OAUTH_SIGNATURE_METHOD = "oauth_signature_method"
    OAUTH_SIGNATURE = "oauth_signature"
    OAUTH_TIMESTAMP = "oauth_timestamp"
    OAUTH_NONCE = "oauth_nonce"
    OAUTH_TOKEN = "oauth_token"
    OAUTH_TOKEN_SECRET = "oauth_token_secret"
    
    # Signature type constants
    HMACSHA1_SIGNATURE_TYPE = "HMAC-SHA1"
    HMACSHA256_SIGNATURE_TYPE = "HMAC-SHA256"
    PLAINTEXT_SIGNATURE_TYPE = "PLAINTEXT"
    RSASHA1_SIGNATURE_TYPE = "RSA-SHA1"
    
    # Unreserved characters for URL encoding
    UNRESERVED_CHARS = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~"
    
    def __init__(self):
        self.random = random.Random()
    
    def url_encode(self, value: str) -> str:
        """
        URL encode a string using OAuth specifications
        
        Args:
            value: String to encode
            
        Returns:
            URL encoded string
        """
        result = []
        for char in value:
            if char in self.UNRESERVED_CHARS:
                result.append(char)
            else:
                result.append('%' + format(ord(char), '02X'))
        return ''.join(result)
    
    def compute_hash(self, algorithm: str, data: str) -> str:
        """
        Compute hash using specified algorithm
        
        Args:
            algorithm: Hash algorithm to use
            data: Data to hash
            
        Returns:
            Base64 encoded hash
        """
        data_bytes = data.encode('ascii')
        
        if algorithm == 'sha1':
            hash_obj = hashlib.sha1(data_bytes)
        elif algorithm == 'sha256':
            hash_obj = hashlib.sha256(data_bytes)
        else:
            raise ValueError(f"Unsupported hash algorithm: {algorithm}")
        
        return base64.b64encode(hash_obj.digest()).decode('ascii')
    
    def get_query_parameters(self, query_string: str) -> List[QueryParameter]:
        """
        Parse query string into list of QueryParameter objects
        
        Args:
            query_string: Query string to parse
            
        Returns:
            List of QueryParameter objects
        """
        if query_string.startswith('?'):
            query_string = query_string[1:]
        
        result = []
        if query_string:
            pairs = query_string.split('&')
            for pair in pairs:
                if pair and not pair.startswith(self.OAUTH_PARAMETER_PREFIX):
                    if '=' in pair:
                        name, value = pair.split('=', 1)
                        result.append(QueryParameter(name, value))
                    else:
                        result.append(QueryParameter(pair, ''))
        
        return result
    
    def normalize_request_parameters(self, parameters: List[QueryParameter]) -> str:
        """
        Normalize request parameters for signature generation
        
        Args:
            parameters: List of QueryParameter objects
            
        Returns:
            Normalized parameter string
        """
        parts = []
        for param in parameters:
            parts.append(f"{param.name}={param.value}")
        return '&'.join(parts)
    
    def generate_signature_base(
        self,
        url: str,
        consumer_key: str,
        token: str,
        token_secret: str,
        http_method: str,
        timestamp: str,
        nonce: str,
        signature_type: str
    ) -> Tuple[str, str, str]:
        """
        Generate signature base string
        
        Args:
            url: Request URL
            consumer_key: OAuth consumer key
            token: OAuth token
            token_secret: OAuth token secret
            http_method: HTTP method (GET, POST, etc.)
            timestamp: OAuth timestamp
            nonce: OAuth nonce
            signature_type: Signature type
            
        Returns:
            Tuple of (signature_base, normalized_url, normalized_parameters)
        """
        token = token or ''
        token_secret = token_secret or ''
        
        if not consumer_key:
            raise ValueError("consumer_key is required")
        if not http_method:
            raise ValueError("http_method is required")
        if not signature_type:
            raise ValueError("signature_type is required")
        
        # Parse URL
        parsed_url = urlparse(url)
        
        # Get query parameters
        parameters = self.get_query_parameters(parsed_url.query)
        
        # Add OAuth parameters
        parameters.append(QueryParameter(self.OAUTH_VERSION_KEY, self.OAUTH_VERSION))
        parameters.append(QueryParameter(self.OAUTH_NONCE, nonce))
        parameters.append(QueryParameter(self.OAUTH_TIMESTAMP, timestamp))
        parameters.append(QueryParameter(self.OAUTH_SIGNATURE_METHOD, signature_type))
        parameters.append(QueryParameter(self.OAUTH_CONSUMER_KEY, consumer_key))
        
        if token:
            parameters.append(QueryParameter(self.OAUTH_TOKEN, token))
        
        # Sort parameters
        parameters.sort()
        
        # Build normalized URL
        normalized_url = f"{parsed_url.scheme}://{parsed_url.hostname}"
        
        # Add port if not default
        if not ((parsed_url.scheme == 'http' and parsed_url.port == 80) or 
                (parsed_url.scheme == 'https' and parsed_url.port == 443)):
            if parsed_url.port:
                normalized_url += f":{parsed_url.port}"
        
        normalized_url += parsed_url.path
        
        # Normalize parameters
        normalized_parameters = self.normalize_request_parameters(parameters)
        
        # Build signature base
        signature_base = (
            f"{http_method.upper()}&"
            f"{self.url_encode(normalized_url)}&"
            f"{self.url_encode(normalized_parameters)}"
        )
        
        return signature_base, normalized_url, normalized_parameters
    
    def generate_signature_using_hash(self, signature_base: str, key: bytes) -> str:
        """
        Generate signature using HMAC
        
        Args:
            signature_base: Signature base string
            key: HMAC key
            
        Returns:
            Base64 encoded signature
        """
        signature_base_bytes = signature_base.encode('ascii')
        hash_obj = hmac.new(key, signature_base_bytes, hashlib.sha256)
        return base64.b64encode(hash_obj.digest()).decode('ascii')
    
    def generate_signature(
        self,
        url: str,
        consumer_key: str,
        consumer_secret: str,
        token: str,
        token_secret: str,
        http_method: str,
        timestamp: str,
        nonce: str,
        signature_type: str = SignatureTypes.HMACSHA1
    ) -> Tuple[str, str, str]:
        """
        Generate OAuth signature
        
        Args:
            url: Request URL
            consumer_key: OAuth consumer key
            consumer_secret: OAuth consumer secret
            token: OAuth token
            token_secret: OAuth token secret
            http_method: HTTP method
            timestamp: OAuth timestamp
            nonce: OAuth nonce
            signature_type: Signature type (default: HMAC-SHA1)
            
        Returns:
            Tuple of (signature, normalized_url, normalized_parameters)
        """
        if signature_type == SignatureTypes.PLAINTEXT:
            raise NotImplementedError("PLAINTEXT signature type not implemented")
        elif signature_type == SignatureTypes.RSASHA1:
            raise NotImplementedError("RSA-SHA1 signature type not implemented")
        elif signature_type == SignatureTypes.HMACSHA1:
            signature_base, normalized_url, normalized_parameters = self.generate_signature_base(
                url, consumer_key, token, token_secret, http_method, timestamp, nonce,
                self.HMACSHA1_SIGNATURE_TYPE
            )
            
            key_string = f"{self.url_encode(consumer_secret)}&{self.url_encode(token_secret) if token_secret else ''}"
            key = key_string.encode('ascii')
            
            signature_base_bytes = signature_base.encode('ascii')
            hash_obj = hmac.new(key, signature_base_bytes, hashlib.sha1)
            signature = base64.b64encode(hash_obj.digest()).decode('ascii')
            
            return signature, normalized_url, normalized_parameters
        elif signature_type == SignatureTypes.HMACSHA256:
            signature_base, normalized_url, normalized_parameters = self.generate_signature_base(
                url, consumer_key, token, token_secret, http_method, timestamp, nonce,
                self.HMACSHA256_SIGNATURE_TYPE
            )
            
            key_string = f"{self.url_encode(consumer_secret)}&{self.url_encode(token_secret) if token_secret else ''}"
            key = key_string.encode('ascii')
            
            signature_base_bytes = signature_base.encode('ascii')
            hash_obj = hmac.new(key, signature_base_bytes, hashlib.sha256)
            signature = base64.b64encode(hash_obj.digest()).decode('ascii')
            
            return signature, normalized_url, normalized_parameters
        else:
            raise ValueError(f"Unknown signature type: {signature_type}")
    
    def generate_signature256(
        self,
        url: str,
        consumer_key: str,
        consumer_secret: str,
        token: str,
        token_secret: str,
        http_method: str,
        timestamp: str,
        nonce: str
    ) -> Tuple[str, str, str]:
        """
        Generate OAuth signature using HMAC-SHA256
        
        Args:
            url: Request URL
            consumer_key: OAuth consumer key
            consumer_secret: OAuth consumer secret
            token: OAuth token
            token_secret: OAuth token secret
            http_method: HTTP method
            timestamp: OAuth timestamp
            nonce: OAuth nonce
            
        Returns:
            Tuple of (signature, normalized_url, normalized_parameters)
        """
        return self.generate_signature(
            url, consumer_key, consumer_secret, token, token_secret,
            http_method, timestamp, nonce, SignatureTypes.HMACSHA256
        )
    
    def generate_timestamp(self) -> str:
        """
        Generate OAuth timestamp
        
        Returns:
            Unix timestamp as string
        """
        return str(int(time.time()))
    
    def generate_nonce(self) -> str:  
        # Generate a random string of 32 characters (fallback or when use_soap_service=False)
        chars = string.ascii_letters + string.digits
        return ''.join(self.random.choice(chars) for _ in range(32))
