import hashlib
import hmac
import base64
from dataclasses import asdict, dataclass
import logging
import os
from typing import Optional
from urllib.parse import quote
import json

import techsoupservices_helper
from oauth_base import OAuthBase, SignatureTypes


@dataclass
class SOAPTokenRequest:
    account_id: str
    consumer_key: Optional[str] = None
    token_id: Optional[str] = None


@dataclass
class SOAPTokenResponse:
    nonce: str
    timestamp: str
    signature: str
    error: Optional[str] = None


@dataclass
class RESTTokenRequest:
    url: str
    account_id: Optional[str] = None
    consumer_key: Optional[str] = None
    token_id: Optional[str] = None    
    http_method: Optional[str] = "GET"


@dataclass
class RESTTokenResponse:
    authorization: str
    error: Optional[str] = None
    
    def to_dict(self):
        return asdict(self)
    
    def to_json(self):
        return json.dumps(asdict(self))


class NetSuiteTokenGenerator:

    @staticmethod
    def get_soap_token(logger: logging.Logger = None) -> Optional[SOAPTokenResponse]:
        try:
            auth_base = OAuthBase()

            timestamp = auth_base.generate_timestamp()
            nonce = techsoupservices_helper.get_netsuite_nonce(logger)

            # Get credentials from environment
            account_id = os.getenv("NetSuiteAccountId")
            consumer_key = os.getenv("NetSuiteConsumerKey")
            token_id = os.getenv("NetSuiteTokenId")
            consumer_secret = os.getenv("NetSuiteConsumerSecret")
            token_secret = os.getenv("NetSuiteTokenSecret")

            # Build base string for signature
            base_string = (
                f"{account_id}&"
                f"{consumer_key}&"
                f"{token_id}&"
                f"{nonce}&"
                f"{timestamp}"
            )

            # Build HMAC key
            key = f"{consumer_secret}&{token_secret}"

            # Generate signature using HMAC-SHA256
            key_bytes = key.encode("ascii")
            message_bytes = base_string.encode("ascii")

            hmac_obj = hmac.new(key_bytes, message_bytes, hashlib.sha256)
            signature = base64.b64encode(hmac_obj.digest()).decode("ascii")

            # Create response
            response = SOAPTokenResponse(
                nonce=nonce, timestamp=timestamp, signature=signature
            )

            return response

        except Exception as e:
            error = f"Error in get_soap_token: {str(e)}"
            if logger:
                logger.error(error)
            else:
                print(error)
            return SOAPTokenResponse(nonce="", timestamp="", signature="", error=error)

    @staticmethod
    def get_rest_token(
        request: RESTTokenRequest, logger: logging.Logger = None
    ) -> Optional[RESTTokenResponse]:
        try:
            if logger:
                logger.info(f"get_rest_token called")

            auth_base = OAuthBase()

            timestamp = auth_base.generate_timestamp()
            nonce = techsoupservices_helper.get_netsuite_nonce(logger)

            if not nonce:
                return RESTTokenResponse(
                    authorization="",
                    error="Failed to generate nonce"
                    )
            
            account_id = os.getenv("NetSuiteAccountId")
            consumer_key = os.getenv("NetSuiteConsumerKey")
            consumer_secret = os.getenv("NetSuiteConsumerSecret")
            token_id = os.getenv("NetSuiteTokenId")            
            token_secret = os.getenv("NetSuiteTokenSecret")

            # Default HTTP method to GET if not specified
            http_method = request.http_method if request.http_method else "GET"

            # Generate signature using HMAC-SHA256
            signature, normalized_url, normalized_params = (
                auth_base.generate_signature256(
                    url=request.url,
                    consumer_key=consumer_key,
                    consumer_secret=consumer_secret,
                    token=token_id,
                    token_secret=token_secret,
                    http_method=http_method,
                    timestamp=timestamp,
                    nonce=nonce,
                )
            )

            # URL encode the signature if it contains '+'
            if "+" in signature:
                signature = signature.replace("+", "%2B")

            # Build authorization header
            header = "OAuth "
            header += f'oauth_signature="{signature}",'
            header += 'oauth_version="1.0",'
            header += f'oauth_nonce="{nonce}",'
            header += 'oauth_signature_method="HMAC-SHA256",'
            header += f'oauth_consumer_key="{consumer_key}",'
            header += f'oauth_token="{token_id}",'
            header += f'oauth_timestamp="{timestamp}",'
            header += f'realm="{account_id}"'

            response = RESTTokenResponse(authorization=header)

            return response

        except Exception as e:
            error = f"Error in get_rest_token: {str(e)}"
            if logger:
                logger.error(error)
            else:
                print(error)
            return RESTTokenResponse(
                authorization="",
                error=error
                )


def generate_soap_token() -> Optional[SOAPTokenResponse]:
    return NetSuiteTokenGenerator.get_soap_token()


def generate_rest_token(
    url: str, http_method: str = "GET"
) -> Optional[RESTTokenResponse]:

    request = RESTTokenRequest(url=url, http_method=http_method)
    return NetSuiteTokenGenerator.get_rest_token(request)
