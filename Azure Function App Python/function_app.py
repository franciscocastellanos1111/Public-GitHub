import azure.functions as func
import asyncio
import logging
import json
import os
import requests
import re
import base64
import threading
from typing import Dict, List, Any, Union, Optional, Set
import techsoupservices_helper
from datetime import datetime, timezone, timedelta
from netsuite_token_generator import (
    NetSuiteTokenGenerator,
    SOAPTokenRequest,
    SOAPTokenResponse,
    RESTTokenRequest,
    RESTTokenResponse,
)
from ms_token_validator import MSTokenValidator


class DynamicsHelper:
    def __init__(self):
        self.dynamics_environment = os.getenv("DYNAMICS_ENVIRONMENT")
        self.client_id = os.getenv("CLIENT_ID")
        self.client_secret = os.getenv("CLIENT_SECRET")
        # Per-worker token cache. dynamics_helper is created once at module
        # import, so these fields behave like .NET static fields for the
        # lifetime of this Functions worker process.
        self.access_token: Optional[str] = None
        self.access_token_expiration: datetime = datetime.min.replace(
            tzinfo=timezone.utc
        )
        self._token_lock = threading.Lock()
        self.master_auth_key = os.getenv("master_auth_key")
        self.techsoupservices_api_resource = os.getenv("TECHSOUPSERVICES_API_RESOURCE")
        self._accepted_client_ids: Set[str] = self._load_accepted_client_ids()
        self._token_validator = MSTokenValidator(
            tenant_id="d8ba2331-6b05-4303-9a60-36c58c3e272d"
        )
        self.netsuite_order_process_url = os.getenv("NETSUITE_ORDER_PROCESS_URL")

    def _load_accepted_client_ids(self) -> Set[str]:

        accepted_ids_str = os.getenv("ACCEPTED_CLIENT_IDS", "")
        if not accepted_ids_str:
            return set()

        try:
            # Parse as JSON array
            accepted_clients = json.loads(accepted_ids_str)

            if not isinstance(accepted_clients, list):
                return set()

            # Extract clientId from each object, convert to lowercase
            return {
                client.get("clientId", "").strip().lower()
                for client in accepted_clients
                if isinstance(client, dict) and client.get("clientId", "").strip()
            }
        except json.JSONDecodeError:
            # If JSON parsing fails, return empty set
            return set()

    def get_accepted_client_ids(self) -> Set[str]:

        return self._accepted_client_ids.copy()

    def is_client_id_accepted(self, client_id: str) -> bool:

        if not client_id:
            return False
        return client_id.lower() in self._accepted_client_ids

    def validate_token_client_id(
        self, token: str, logger: logging.Logger = None
    ) -> Dict[str, Any]:

        result = {
            "is_valid": False,
            "client_id": None,
            "error": None,
            "token_info": None,
        }

        try:
            # Remove 'Bearer ' prefix if present
            if token and token.lower().startswith("bearer "):
                token = token[7:]

            if not token:
                result["error"] = "No token provided"
                if logger:
                    logger.warning("Token validation failed: No token provided")
                return result

            # Check if any client IDs are configured
            if not self._accepted_client_ids:
                result["error"] = "No accepted client IDs configured"
                if logger:
                    logger.error(
                        "Token validation failed: ACCEPTED_CLIENT_IDS not configured"
                    )
                return result

            # Extract client_id from token
            client_id = self._token_validator.get_client_id_from_token(token, logger)

            if not client_id:
                result["error"] = "Failed to extract client_id from token"
                if logger:
                    logger.warning(
                        "Token validation failed: Could not extract client_id"
                    )
                return result

            result["client_id"] = client_id

            # Get additional token info
            token_info = self._token_validator.get_token_info(token, logger)
            if token_info:
                result["token_info"] = {
                    "tenant_id": token_info.get("tenant_id"),
                    "audience": token_info.get("audience"),
                    "app_display_name": token_info.get("app_display_name"),
                    "is_expired": token_info.get("is_expired"),
                    "expiration": (
                        str(token_info.get("expiration"))
                        if token_info.get("expiration")
                        else None
                    ),
                }

                # Check if token is expired
                if token_info.get("is_expired"):
                    result["error"] = "Token is expired"
                    if logger:
                        logger.warning(
                            f"Token validation failed: Token expired at {token_info.get('expiration')}"
                        )
                    return result

            # Check if client_id is in accepted list
            if self.is_client_id_accepted(client_id):
                result["is_valid"] = True
                if logger:
                    logger.info(
                        f"Token validation successful for client_id: {client_id}"
                    )
            else:
                result["error"] = f"Client ID '{client_id}' is not in the accepted list"
                if logger:
                    logger.warning(
                        f"Token validation failed: Client ID '{client_id}' not accepted"
                    )

            return result

        except Exception as e:
            result["error"] = f"Token validation error: {str(e)}"
            if logger:
                logger.error(f"Token validation error: {str(e)}")
            return result

    def reload_accepted_client_ids(self) -> None:

        self._accepted_client_ids = self._load_accepted_client_ids()

    def validate_token_with_signature(
        self,
        token: str,
        logger: logging.Logger = None,
        alternative_audience: str = None,
    ) -> Dict[str, Any]:

        result = {
            "is_valid": False,
            "client_id": None,
            "signature_verified": False,
            "error": None,
            "token_info": None,
        }

        try:
            # Remove 'Bearer ' prefix if present
            if token and token.lower().startswith("bearer "):
                token = token[7:]

            if not token:
                result["error"] = "No token provided"
                if logger:
                    logger.warning("Token validation failed: No token provided")
                return result

            # Check if signature verification is available
            if not self._token_validator.is_signature_verification_available():
                result["error"] = (
                    "Signature verification not available. Install PyJWT: pip install PyJWT"
                )
                if logger:
                    logger.error(result["error"])
                return result

            # Verify signature against Microsoft's JWKS
            sig_result = self._token_validator.verify_token_signature(
                token,
                expected_audience=self.techsoupservices_api_resource,
                logger=logger,
            )

            if not sig_result["is_valid"] and alternative_audience:
                if logger:
                    logger.info(
                        f"Primary audience verification failed, retrying with alternative audience: {alternative_audience}"
                    )
                sig_result = self._token_validator.verify_token_signature(
                    token,
                    expected_audience=alternative_audience,
                    logger=logger,
                )

            if not sig_result["is_valid"]:
                result["error"] = (
                    f"Signature verification failed: {sig_result['error']}"
                )
                if logger:
                    logger.warning(
                        f"Token signature verification failed: {sig_result['error']}"
                    )
                return result

            result["signature_verified"] = True
            result["client_id"] = sig_result["client_id"]

            # Extract additional token info from verified payload
            payload = sig_result.get("payload", {})
            result["token_info"] = {
                "tenant_id": payload.get("tid"),
                "audience": payload.get("aud"),
                "issuer": payload.get("iss"),
                "app_display_name": payload.get("app_displayname"),
                "object_id": payload.get("oid"),
                "scopes": (
                    payload.get("scp", "").split()
                    if payload.get("scp")
                    else payload.get("roles", [])
                ),
            }

            # Check if client_id is in accepted list
            if not self._accepted_client_ids:
                result["error"] = "No accepted client IDs configured"
                if logger:
                    logger.error(
                        "Token validation failed: ACCEPTED_CLIENT_IDS not configured"
                    )
                return result

            if self.is_client_id_accepted(result["client_id"]):
                result["is_valid"] = True
                if logger:
                    logger.info(
                        f"Token fully validated (signature + client_id) for: {result['client_id']}"
                    )
            else:
                result["error"] = (
                    f"Client ID '{result['client_id']}' is not in the accepted list"
                )
                if logger:
                    logger.warning(
                        f"Token signature valid but client_id not accepted: {result['client_id']}"
                    )

            return result

        except Exception as e:
            result["error"] = f"Token validation error: {str(e)}"
            if logger:
                logger.error(f"Token validation error: {str(e)}")
            return result

    def get_ms_token_web_api(self, logger: logging.Logger) -> Optional[str]:
        try:
            # Fast path: cached token still valid (with the safety buffer
            # already baked into access_token_expiration).
            now = datetime.now(timezone.utc)
            if self.access_token and now < self.access_token_expiration:
                logger.info("Using cached Dataverse access token")
                return self.access_token

            # Slow path: serialize concurrent refreshes on this worker so
            # multiple in-flight requests don't all hit /oauth2/token.
            with self._token_lock:
                # Re-check inside the lock in case another thread refreshed
                # while we were waiting.
                now = datetime.now(timezone.utc)
                if self.access_token and now < self.access_token_expiration:
                    logger.info("Using cached Dataverse access token (post-lock)")
                    return self.access_token

                resource = self.dynamics_environment
                client_id = self.client_id
                client_secret = self.client_secret
                tenant_id = "d8ba2331-6b05-4303-9a60-36c58c3e272d"

                url = f"https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/token"

                parameters = {
                    "client_id": client_id,
                    "scope": f"{resource}/.default",
                    "client_secret": client_secret,
                    "grant_type": "client_credentials",
                }

                headers = {
                    "Accept": "application/json",
                    "Content-Type": "application/x-www-form-urlencoded",
                }

                logger.info(f"Requesting token from: {url}")
                logger.info(f"Client ID: {client_id}")
                logger.info(f"Resource: {resource}")

                response = requests.post(url, data=parameters, headers=headers)
                response_text = response.text

                logger.info(f"Token response status: {response.status_code}")

                if response.status_code == 200:
                    response_object = json.loads(response_text)
                    token = response_object.get("access_token")

                    if token:
                        # AAD returns expires_in in seconds (typically 3599).
                        # Refresh 200 s early to avoid races with downstream calls.
                        expires_in = int(response_object.get("expires_in", 3599))
                        self.access_token = token
                        self.access_token_expiration = datetime.now(
                            timezone.utc
                        ) + timedelta(seconds=max(expires_in - 200, 60))
                        logger.info(
                            f"Successfully obtained access token: {token[:30]}... "
                            f"(cached until {self.access_token_expiration.isoformat()})"
                        )
                        return token
                    else:
                        logger.error("No access_token in response")
                        logger.error(f"Response: {response_text}")
                        return None
                else:
                    logger.error(
                        f"Token request failed with status {response.status_code}"
                    )
                    logger.error(f"Response: {response_text}")
                    return None

        except Exception as e:
            logger.error(f"Error getting Dataverse access token via Web API: {str(e)}")
            return None


dynamics_helper = DynamicsHelper()
app = func.FunctionApp(http_auth_level=func.AuthLevel.ANONYMOUS)


def add_cors_headers(
    response: func.HttpResponse, origin: str = None
) -> func.HttpResponse:
    """Add CORS headers to the response"""
    # Allow the specific origin or use * for all
    if origin:
        response.headers["Access-Control-Allow-Origin"] = origin
    else:
        response.headers["Access-Control-Allow-Origin"] = "*"
    response.headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS"
    response.headers["Access-Control-Allow-Headers"] = (
        "Content-Type, Authorization, Accept"
    )
    response.headers["Access-Control-Max-Age"] = "86400"
    return response


@app.route(route="CRM/{custom_api}", methods=["POST", "OPTIONS"])
def RouteToDynamics(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("RouteToDynamics function starting processing a request")

    # Get the origin header from request
    origin = req.headers.get("Origin", "*")

    # Handle OPTIONS preflight request
    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin, origin)

    try:
        custom_api = req.route_params.get("custom_api")
        if not custom_api:
            response = func.HttpResponse(
                json.dumps({"error": "custom_api parameter is required"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        pattern = r"^\w+$"
        if not re.match(pattern, custom_api):
            response = func.HttpResponse(
                json.dumps(
                    {
                        "error": f"Invalid custom_api parameter '{custom_api}'. Must match pattern: {pattern}"
                    }
                ),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        req_body = req.get_json()
        if not req_body:
            response = func.HttpResponse(
                json.dumps({"error": "Request body must contain valid JSON"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        converted_data = techsoupservices_helper.get_expando_object(req_body)

        converted_data["requestBodyText"] = json.dumps(req_body)

        ts_request = {"ts_request": converted_data}

        auth_header = req.headers.get("Authorization")
        if not auth_header:
            response = func.HttpResponse(
                json.dumps({"error": "Authorization header is required"}),
                status_code=401,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        auth_validation_result = dynamics_helper.validate_token_with_signature(
            auth_header,
            logging,
            alternative_audience=dynamics_helper.dynamics_environment,
        )
        if not auth_validation_result["is_valid"]:
            response = func.HttpResponse(
                json.dumps({"error": "Invalid authorization token"}),
                status_code=401,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        access_token = dynamics_helper.get_ms_token_web_api(logging)
        if not access_token:
            response = func.HttpResponse(
                json.dumps({"error": "Failed to get access token for Dataverse"}),
                status_code=500,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        headers = {
            "Authorization": f"Bearer {access_token}",
            "Content-Type": "application/json",
            "Accept": "application/json",
            "OData-MaxVersion": "4.0",
            "OData-Version": "4.0",
            "If-None-Match": "null",
        }

        query_url = (
            f"{dynamics_helper.dynamics_environment}/api/data/v9.2/ts_{custom_api}"
        )

        logging.info(f"Making POST request to: {query_url}")

        response = requests.post(query_url, json=ts_request, headers=headers)

        logging.info(f"Dynamics API response status: {response.status_code}")

        if response.status_code in [200, 201, 204]:
            if response.content:
                response_data = response.json()
            else:
                response_data = {}

            clean_json = techsoupservices_helper.convert_expando_to_json(response_data)

            http_response = func.HttpResponse(
                json.dumps(clean_json, indent=2),
                status_code=response.status_code,
                mimetype="application/json",
            )
            return add_cors_headers(http_response, origin)
        else:
            error_message = (
                f"Dynamics API call failed with status {response.status_code}"
            )
            if response.content:
                try:
                    error_details = response.json()
                    error_message += f": {error_details}"
                except:
                    error_message += f": {response.text}"

            logging.error(error_message)
            http_response = func.HttpResponse(
                json.dumps({"error": error_message}),
                status_code=response.status_code,
                mimetype="application/json",
            )
            return add_cors_headers(http_response, origin)

    except json.JSONDecodeError:
        logging.error("Invalid JSON provided in request body")
        http_response = func.HttpResponse(
            json.dumps({"error": "Invalid JSON format in request body"}),
            status_code=400,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)
    except Exception as e:
        logging.error(f"Error in RouteToDynamics: {str(e)}")
        http_response = func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)


@app.route(route="CRM/{auth_key}/{custom_api}", methods=["POST", "OPTIONS"])
def RouteToDynamicsAuthKey(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("RouteToDynamicsAuthKey function starting processing a request")

    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin)

    try:
        auth_key = req.route_params.get("auth_key")
        if not auth_key:
            response = func.HttpResponse(
                json.dumps({"error": "auth_key parameter is required"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        if (
            dynamics_helper.master_auth_key
            and auth_key.lower() != dynamics_helper.master_auth_key.lower()
        ):
            logging.info(f"Validating auth_key: {auth_key}")
            ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)

            if not ts_pngo_id:
                logging.warning(f"Unauthorized: Invalid auth_key {auth_key}")
                response = func.HttpResponse(
                    json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                    status_code=401,
                    mimetype="application/json",
                )
                return add_cors_headers(response, origin)

            logging.info(f"auth_key validated successfully. PNGO ID: {ts_pngo_id}")

        custom_api = req.route_params.get("custom_api")
        if not custom_api:
            response = func.HttpResponse(
                json.dumps({"error": "custom_api parameter is required"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        pattern = r"^\w+$"
        if not re.match(pattern, custom_api):
            response = func.HttpResponse(
                json.dumps(
                    {
                        "error": f"Invalid custom_api parameter '{custom_api}'. Must match pattern: {pattern}"
                    }
                ),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        req_body = req.get_json()
        if not req_body:
            response = func.HttpResponse(
                json.dumps({"error": "Request body must contain valid JSON"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        converted_data = techsoupservices_helper.get_expando_object(req_body)

        converted_data["requestBodyText"] = json.dumps(req_body)

        ts_request = {"ts_request": converted_data}

        access_token = dynamics_helper.get_ms_token_web_api(logging)
        if not access_token:
            response = func.HttpResponse(
                json.dumps({"error": "Failed to get access token for Dataverse"}),
                status_code=500,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        headers = {
            "Authorization": f"Bearer {access_token}",
            "Content-Type": "application/json",
            "Accept": "application/json",
            "OData-MaxVersion": "4.0",
            "OData-Version": "4.0",
            "If-None-Match": "null",
        }

        query_url = (
            f"{dynamics_helper.dynamics_environment}/api/data/v9.2/ts_{custom_api}"
        )

        logging.info(f"Making POST request to: {query_url}")

        response = requests.post(query_url, json=ts_request, headers=headers)

        logging.info(f"Dynamics API response status: {response.status_code}")

        if response.status_code in [200, 201, 204]:
            if response.content:
                response_data = response.json()
            else:
                response_data = {}

            clean_json = techsoupservices_helper.convert_expando_to_json(response_data)

            http_response = func.HttpResponse(
                json.dumps(clean_json, indent=2),
                status_code=response.status_code,
                mimetype="application/json",
            )
            return add_cors_headers(http_response, origin)
        else:
            error_message = (
                f"Dynamics API call failed with status {response.status_code}"
            )
            if response.content:
                try:
                    error_details = response.json()
                    error_message += f": {error_details}"
                except:
                    error_message += f": {response.text}"

            logging.error(error_message)
            http_response = func.HttpResponse(
                json.dumps({"error": error_message}),
                status_code=response.status_code,
                mimetype="application/json",
            )
            return add_cors_headers(http_response, origin)

    except json.JSONDecodeError:
        logging.error("Invalid JSON provided in request body")
        http_response = func.HttpResponse(
            json.dumps({"error": "Invalid JSON format in request body"}),
            status_code=400,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)
    except Exception as e:
        logging.error(f"Error in RouteToDynamicsAuthKey: {str(e)}")
        http_response = func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)


@app.route(route="ERP/{auth_key}/{request_name}", methods=["POST", "OPTIONS"])
def RouteToERPAuthKey(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("RouteToERPAuthKey function starting processing a request")

    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin)

    try:
        auth_key = req.route_params.get("auth_key")
        if not auth_key:
            response = func.HttpResponse(
                json.dumps({"error": "auth_key parameter is required"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        # Validate auth_key by calling get_ts_pngo_id, unless it matches master_auth_key
        if (
            dynamics_helper.master_auth_key
            and auth_key.lower() != dynamics_helper.master_auth_key.lower()
        ):
            logging.info(f"Validating auth_key: {auth_key}")
            ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)

            if not ts_pngo_id:
                logging.warning(f"Unauthorized: Invalid auth_key {auth_key}")
                response = func.HttpResponse(
                    json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                    status_code=401,
                    mimetype="application/json",
                )
                return add_cors_headers(response, origin)

            logging.info(f"auth_key validated successfully. PNGO ID: {ts_pngo_id}")

        request_name = req.route_params.get("request_name")
        if not request_name:
            response = func.HttpResponse(
                json.dumps({"error": "request_name parameter is required"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        req_body = req.get_json()
        if not req_body:
            response = func.HttpResponse(
                json.dumps({"error": "Request body must contain valid JSON"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        netsuite_token_request = RESTTokenRequest(
            url="https://3424833-sb1.restlets.api.netsuite.com/app/site/hosting/restlet.nl?script=231&deploy=1",
            http_method="POST",
        )
        netsuite_token_response = NetSuiteTokenGenerator.get_rest_token(
            netsuite_token_request, logging
        )

        if not netsuite_token_response:
            logging.warning(f"NetSuite token generation failed")
            response = func.HttpResponse(
                json.dumps({"error": "Failed to generate NetSuite token"}),
                status_code=500,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        netsuite_authorization = netsuite_token_response.authorization

        headers = {
            "Authorization": netsuite_authorization,
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": "SuiteScript-Call",
        }

        query_url = f"https://3424833-sb1.restlets.api.netsuite.com/app/site/hosting/restlet.nl?script=231&deploy=1"

        logging.info(f"Making POST request to: {query_url}")

        response = requests.post(query_url, json=req_body, headers=headers)

        logging.info(f"NetSuite response status: {response.status_code}")

        if response.status_code in [200, 201, 204]:
            if response.content:
                response_data = response.json()
            else:
                response_data = {}

            http_response = func.HttpResponse(
                json.dumps(response_data, indent=2),
                status_code=response.status_code,
                mimetype="application/json",
            )
            return add_cors_headers(http_response, origin)
        else:
            error_message = f"ERP call failed with status {response.status_code}"
            if response.content:
                try:
                    error_details = response.json()
                    error_message += f": {error_details}"
                except:
                    error_message += f": {response.text}"

            logging.error(error_message)
            http_response = func.HttpResponse(
                json.dumps({"error": error_message}),
                status_code=response.status_code,
                mimetype="application/json",
            )
            return add_cors_headers(http_response, origin)

    except json.JSONDecodeError:
        logging.error("Invalid JSON provided in request body")
        http_response = func.HttpResponse(
            json.dumps({"error": "Invalid JSON format in request body"}),
            status_code=400,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)
    except Exception as e:
        logging.error(f"Error in RouteToERPAuthKey: {str(e)}")
        http_response = func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)


@app.route(route="ERP/{request_name}", methods=["POST", "OPTIONS"])
def RouteToERP(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("RouteToERP function starting processing a request")

    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin)

    try:
        auth_header = req.headers.get("Authorization")
        if not auth_header:
            response = func.HttpResponse(
                json.dumps({"error": "Authorization header is required"}),
                status_code=401,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        auth_validation_result = dynamics_helper.validate_token_with_signature(
            auth_header, logging
        )
        if not auth_validation_result["is_valid"]:
            response = func.HttpResponse(
                json.dumps({"error": "Invalid authorization token"}),
                status_code=401,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        request_name = req.route_params.get("request_name")
        if not request_name:
            response = func.HttpResponse(
                json.dumps({"error": "request_name parameter is required"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        query_url = None

        match request_name.lower():
            case "orderprocess":
                query_url = dynamics_helper.netsuite_order_process_url
            case _:
                response = func.HttpResponse(
                    json.dumps({"error": f"Unknown request_name: {request_name}"}),
                    status_code=400,
                    mimetype="application/json",
                )
                return add_cors_headers(response, origin)

        logging.info(f"query_url: {query_url}")

        req_body = req.get_json()
        if not req_body:
            response = func.HttpResponse(
                json.dumps({"error": "Request body must contain valid JSON"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        netsuite_token_request = RESTTokenRequest(url=query_url, http_method="POST")
        netsuite_token_response = NetSuiteTokenGenerator.get_rest_token(
            netsuite_token_request, logging
        )

        if not netsuite_token_response:
            logging.warning(f"NetSuite token generation failed")
            response = func.HttpResponse(
                json.dumps({"error": "Failed to generate NetSuite token"}),
                status_code=500,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        netsuite_authorization = netsuite_token_response.authorization

        headers = {
            "Authorization": netsuite_authorization,
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": "SuiteScript-Call",
        }

        logging.info(f"Making POST request to: {query_url}")

        response = requests.post(query_url, json=req_body, headers=headers)

        logging.info(f"NetSuite response status: {response.status_code}")

        if response.status_code in [200, 201, 204]:
            if response.content:
                response_data = response.json()
            else:
                response_data = {}

            http_response = func.HttpResponse(
                json.dumps(response_data, indent=2),
                status_code=response.status_code,
                mimetype="application/json",
            )
            return add_cors_headers(http_response, origin)
        else:
            error_message = f"ERP call failed with status {response.status_code}"
            if response.content:
                try:
                    error_details = response.json()
                    error_message += f": {error_details}"
                except:
                    error_message += f": {response.text}"

            logging.error(error_message)
            http_response = func.HttpResponse(
                json.dumps({"error": error_message}),
                status_code=response.status_code,
                mimetype="application/json",
            )
            return add_cors_headers(http_response, origin)

    except json.JSONDecodeError:
        logging.error("Invalid JSON provided in request body")
        http_response = func.HttpResponse(
            json.dumps({"error": "Invalid JSON format in request body"}),
            status_code=400,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)
    except Exception as e:
        logging.error(f"Error in RouteToERPAuthKey: {str(e)}")
        http_response = func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)


@app.route(route="Test", methods=["POST", "OPTIONS"])
def Test(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("Test function starting processing a request")

    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin)

    try:
        req_body = req.get_json()

        if not req_body:
            response = func.HttpResponse(
                json.dumps({"error": "Request body must contain valid JSON"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        # netsuite_nonce = techsoupservices_helper.get_netsuite_nonce(logging)

        # if not netsuite_nonce:
        #         logging.warning(f"NetSuite nonce retrieval failed")
        #         response = func.HttpResponse(
        #             json.dumps({"error": "Failed to get NetSuite nonce"}),
        #             status_code=500,
        #             mimetype="application/json",
        #         )
        #         return add_cors_headers(response, origin)

        netsuite_token_request = RESTTokenRequest(
            url="https://3424833-sb1.restlets.api.netsuite.com/app/site/hosting/restlet.nl?script=231&deploy=1",
            http_method="POST",
        )
        netsuite_token_response = NetSuiteTokenGenerator.get_rest_token(
            netsuite_token_request, logging
        )

        if not netsuite_token_response:
            logging.warning(f"NetSuite token generation failed")
            response = func.HttpResponse(
                json.dumps({"error": "Failed to generate NetSuite token"}),
                status_code=500,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        reponse_body = {"netsuite_token_response": netsuite_token_response.to_dict()}

        http_response = func.HttpResponse(
            json.dumps(reponse_body, indent=2),
            status_code=200,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)

    except json.JSONDecodeError:
        logging.error("Invalid JSON provided in request body")
        http_response = func.HttpResponse(
            json.dumps({"error": "Invalid JSON format in request body"}),
            status_code=400,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)
    except Exception as e:
        logging.error(f"Error in ConvertToDynamicsAPI: {str(e)}")
        http_response = func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)


@app.route(route="ValidateToken", methods=["GET", "POST", "OPTIONS"])
def ValidateToken(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("ValidateToken function starting processing a request")

    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin)

    try:
        auth_header = req.headers.get("Authorization")
        if not auth_header:
            response = func.HttpResponse(
                json.dumps({"error": "Authorization header is required"}),
                status_code=401,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        result = dynamics_helper.validate_token_with_signature(auth_header, logging)

        http_response = func.HttpResponse(
            json.dumps(result, indent=2),
            status_code=200 if result.get("is_valid") else 401,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)

    except Exception as e:
        logging.error(f"Error in ValidateToken: {str(e)}")
        http_response = func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)


@app.route(route="ConvertToDynamicsAPI", methods=["POST", "OPTIONS"])
def ConvertToDynamicsAPI(req: func.HttpRequest) -> func.HttpResponse:

    # Get the origin header from request
    origin = req.headers.get("Origin", "*")

    # Handle OPTIONS preflight request
    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin)

    try:
        req_body = req.get_json()

        if not req_body:
            response = func.HttpResponse(
                json.dumps({"error": "Request body must contain valid JSON"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        converted_data = techsoupservices_helper.get_expando_object(req_body)
        http_response = func.HttpResponse(
            json.dumps(converted_data, indent=2),
            status_code=200,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)

    except json.JSONDecodeError:
        logging.error("Invalid JSON provided in request body")
        http_response = func.HttpResponse(
            json.dumps({"error": "Invalid JSON format in request body"}),
            status_code=400,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)
    except Exception as e:
        logging.error(f"Error in ConvertToDynamicsAPI: {str(e)}")
        http_response = func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)


@app.route(route="ConvertFromDynamicsAPI", methods=["POST", "OPTIONS"])
def ConvertFromDynamicsAPI(req: func.HttpRequest) -> func.HttpResponse:
    logging.info("ConvertFromDynamicsAPI function processed a request.")

    # Get the origin header from request
    origin = req.headers.get("Origin", "*")

    # Handle OPTIONS preflight request
    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin)

    try:
        # Get the JSON body from the request
        req_body = req.get_json()

        if not req_body:
            response = func.HttpResponse(
                json.dumps({"error": "Request body must contain valid JSON"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        # Convert Dynamics API format back to clean JSON
        clean_json = techsoupservices_helper.convert_expando_to_json(req_body)

        # Return the converted data
        http_response = func.HttpResponse(
            json.dumps(clean_json, indent=2),
            status_code=200,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)

    except json.JSONDecodeError:
        logging.error("Invalid JSON provided in request body")
        http_response = func.HttpResponse(
            json.dumps({"error": "Invalid JSON format in request body"}),
            status_code=400,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)
    except Exception as e:
        logging.error(f"Error in ConvertFromDynamicsAPI: {str(e)}")
        http_response = func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)


@app.function_name(name="SubDomainRouting")
@app.route(route="", methods=["GET", "POST", "PUT", "DELETE", "OPTIONS"])
def subdomain_routing(req: func.HttpRequest) -> func.HttpResponse:

    # Get the origin header from request
    origin = req.headers.get("Origin", "*")

    # Handle OPTIONS preflight request
    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin)

    try:
        incoming_url_info = techsoupservices_helper.get_azure_function_url_info(req)

        auth_header = req.headers.get("authorization")
        if not auth_header:
            response = func.HttpResponse(
                json.dumps(
                    {
                        "error": "Authorization required for proxy requests",
                        "request_url_info": incoming_url_info,
                    }
                ),
                status_code=401,
                headers={"Content-Type": "application/json"},
            )
            return add_cors_headers(response, origin)

        if incoming_url_info.get("subdomain"):
            subdomain = incoming_url_info["subdomain"]

            if subdomain.lower() == "ngosource":
                ngosource()
            else:
                techsoupservices()

    except Exception as e:
        print(f"Error in subdomain_routing: {str(e)}")


def ngosource():
    pass


def techsoupservices():
    pass


@app.route(
    route="case/artifacts/{auth_key}/{transaction_id}", methods=["GET", "OPTIONS"]
)
def GetCaseArtifacts(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("GetCaseArtifacts function starting processing a request")

    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        response = func.HttpResponse(status_code=200)
        return add_cors_headers(response, origin)

    try:
        auth_key = req.route_params.get("auth_key")
        if not auth_key:
            response = func.HttpResponse(
                json.dumps({"error": "auth_key parameter is required"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        if (
            dynamics_helper.master_auth_key
            and auth_key.lower() != dynamics_helper.master_auth_key.lower()
        ):
            logging.info(f"Validating auth_key: {auth_key}")
            ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)

            if not ts_pngo_id:
                logging.warning(f"Unauthorized: Invalid auth_key {auth_key}")
                response = func.HttpResponse(
                    json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                    status_code=401,
                    mimetype="application/json",
                )
                return add_cors_headers(response, origin)

            logging.info(f"auth_key validated successfully. PNGO ID: {ts_pngo_id}")

        transaction_id = req.route_params.get("transaction_id")
        if not transaction_id:
            response = func.HttpResponse(
                json.dumps({"error": "transaction_id parameter is required"}),
                status_code=400,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        access_token = dynamics_helper.get_ms_token_web_api(logging)
        if not access_token:
            response = func.HttpResponse(
                json.dumps({"error": "Failed to get access token for Dataverse"}),
                status_code=500,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        headers = {
            "Authorization": f"Bearer {access_token}",
            "Accept": "application/json",
            "OData-MaxVersion": "4.0",
            "OData-Version": "4.0",
        }

        # region Find case by transaction ID
        case_select = ",".join(
            [
                "incidentid",
                "title",
                "ts_validationrequesttransactionid",
                "ts_validationrequestlegalname",
                "ts_validationrequestlegalidentifier",
                "ts_validationrequestorgtype",
                "ts_validationrequestaddressline1",
                "ts_validationrequestaddresscity",
                "ts_validationrequestaddressstateregion",
                "ts_validationrequestaddresspostalcode",
                "ts_validationrequestaddresscountryid",
                "ts_validationrequestemail",
                "ts_validationrequestphone",
                "ts_validationrequestwebsite",
                "ts_validationrequestagentfirstname",
                "ts_validationrequestagentlastname",
                "ts_validationrequestagentemail",
                "_customerid_value",
            ]
        )

        case_filter = f"ts_validationrequesttransactionid eq '{transaction_id}'"
        case_url = (
            f"{dynamics_helper.dynamics_environment}/api/data/v9.2/incidents"
            f"?$select={case_select}"
            f"&$expand=customerid_account($select=name,accountid)"
            f"&$filter={case_filter}"
        )

        logging.info(f"Searching for case with transaction ID: {transaction_id}")
        case_response = requests.get(case_url, headers=headers)

        if case_response.status_code != 200:
            logging.error(f"Failed to query incidents: {case_response.status_code}")
            response = func.HttpResponse(
                json.dumps(
                    {"error": f"Failed to query incidents: {case_response.text}"}
                ),
                status_code=case_response.status_code,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        case_records = case_response.json().get("value", [])
        if not case_records:
            logging.warning(f"No case found for transaction ID: {transaction_id}")
            response = func.HttpResponse(
                json.dumps(
                    {"error": f"No case found for transaction_id: {transaction_id}"}
                ),
                status_code=404,
                mimetype="application/json",
            )
            return add_cors_headers(response, origin)

        case_record = case_records[0]
        incident_id = case_record.get("incidentid")
        logging.info(f"Found case: {incident_id}")
        # endregion

        # region Get entity attachments for the case
        attachments_filter = f"_msdyn_relatedentity_value eq {incident_id}"
        attachments_url = (
            f"{dynamics_helper.dynamics_environment}/api/data/v9.2/msdyn_entityattachments"
            f"?$select=msdyn_entityattachmentid,msdyn_name,ts_referencevalue"
            f"&$filter={attachments_filter}"
        )

        logging.info(f"Querying entity attachments for case: {incident_id}")
        attachments_response = requests.get(attachments_url, headers=headers)

        attachment_records = []
        if attachments_response.status_code == 200:
            attachment_records = attachments_response.json().get("value", [])
            logging.info(f"Found {len(attachment_records)} entity attachment(s)")
        else:
            logging.warning(
                f"Failed to query attachments: {attachments_response.status_code}"
            )
        # endregion

        # region Download file blobs and build artifact list
        artifacts = []
        for record in attachment_records:
            attachment_id = record.get("msdyn_entityattachmentid")
            file_name = record.get("msdyn_name", f"{attachment_id}.bin")
            regulatory_body = record.get("ts_referencevalue")

            artifact_entry = {
                "attachmentId": attachment_id,
                "fileName": file_name,
                "regulatoryBody": regulatory_body,
                "fileContent": None,
                "contentType": None,
                "fileSizeInBytes": None,
            }

            file_info_url = (
                f"{dynamics_helper.dynamics_environment}/api/data/v9.2/msdyn_entityattachments({attachment_id})"
                f"?$select=msdyn_entityattachmentid"
                f"&$expand=msdyn_entityattachment_FileAttachments("
                f"$select=createdon,mimetype,filesizeinbytes,filename,regardingfieldname,fileattachmentid)"
            )

            try:
                file_info_response = requests.get(file_info_url, headers=headers)
                if file_info_response.status_code == 200:
                    file_attachments = file_info_response.json().get(
                        "msdyn_entityattachment_FileAttachments", []
                    )
                    if file_attachments:
                        file_info = file_attachments[0]
                        artifact_entry["contentType"] = file_info.get("mimetype")
                        artifact_entry["fileSizeInBytes"] = file_info.get(
                            "filesizeinbytes"
                        )
                        artifact_entry["createdOn"] = file_info.get("createdon")
            except Exception as e:
                logging.warning(f"Failed to get file info for {attachment_id}: {e}")

            download_url = (
                f"{dynamics_helper.dynamics_environment}/api/data/v9.2"
                f"/msdyn_entityattachments({attachment_id})/msdyn_fileblob/$value"
            )

            try:
                download_response = requests.get(download_url, headers=headers)
                if download_response.status_code == 200:
                    file_bytes = download_response.content
                    artifact_entry["fileContent"] = base64.b64encode(file_bytes).decode(
                        "utf-8"
                    )
                    artifact_entry["fileSizeInBytes"] = artifact_entry[
                        "fileSizeInBytes"
                    ] or len(file_bytes)
                    logging.info(
                        f"Downloaded file: {file_name} ({len(file_bytes)} bytes)"
                    )
                else:
                    logging.warning(
                        f"Failed to download {file_name}: HTTP {download_response.status_code}"
                    )
            except Exception as e:
                logging.warning(f"Error downloading {file_name}: {e}")

            artifacts.append(artifact_entry)
        # endregion

        # region Get annotations (notes) attached to the case
        annotations_filter = f"_objectid_value eq {incident_id}"
        annotations_url = (
            f"{dynamics_helper.dynamics_environment}/api/data/v9.2/annotations"
            f"?$select=annotationid,subject,notetext,filename,mimetype,createdon,documentbody"
            f"&$filter={annotations_filter}"
            f"&$orderby=createdon desc"
        )

        annotation_records = []
        try:
            annotations_response = requests.get(annotations_url, headers=headers)
            if annotations_response.status_code == 200:
                annotation_records = annotations_response.json().get("value", [])
                logging.info(f"Found {len(annotation_records)} annotation(s)")
            else:
                logging.warning(
                    f"Failed to query annotations: {annotations_response.status_code}"
                )
        except Exception as e:
            logging.warning(f"Error querying annotations: {e}")

        notes = []
        for ann in annotation_records:
            note_entry = {
                "annotationId": ann.get("annotationid"),
                "subject": ann.get("subject"),
                "noteText": ann.get("notetext"),
                "fileName": ann.get("filename"),
                "contentType": ann.get("mimetype"),
                "createdOn": ann.get("createdon"),
            }
            if ann.get("documentbody"):
                note_entry["fileContent"] = ann.get("documentbody")
            notes.append(note_entry)
        # endregion

        # region Build response
        account_info = case_record.get("customerid_account")
        result_payload = {
            "transactionId": transaction_id,
            "caseId": incident_id,
            "caseTitle": case_record.get("title"),
            "organization": {
                "legalName": case_record.get("ts_validationrequestlegalname"),
                "legalIdentifier": case_record.get(
                    "ts_validationrequestlegalidentifier"
                ),
                "orgType": case_record.get("ts_validationrequestorgtype"),
                "address": {
                    "line1": case_record.get("ts_validationrequestaddressline1"),
                    "city": case_record.get("ts_validationrequestaddresscity"),
                    "stateRegion": case_record.get(
                        "ts_validationrequestaddressstateregion"
                    ),
                    "postalCode": case_record.get(
                        "ts_validationrequestaddresspostalcode"
                    ),
                    "countryId": case_record.get(
                        "ts_validationrequestaddresscountryid"
                    ),
                },
                "email": case_record.get("ts_validationrequestemail"),
                "phone": case_record.get("ts_validationrequestphone"),
                "website": case_record.get("ts_validationrequestwebsite"),
                "accountName": account_info.get("name") if account_info else None,
                "accountId": account_info.get("accountid") if account_info else None,
            },
            "agent": {
                "firstName": case_record.get("ts_validationrequestagentfirstname"),
                "lastName": case_record.get("ts_validationrequestagentlastname"),
                "email": case_record.get("ts_validationrequestagentemail"),
            },
            "artifacts": artifacts,
            "notes": notes,
            "summary": {
                "totalArtifacts": len(artifacts),
                "totalNotes": len(notes),
                "artifactsWithContent": sum(
                    1 for a in artifacts if a.get("fileContent")
                ),
                "notesWithAttachments": sum(1 for n in notes if n.get("fileContent")),
            },
        }
        # endregion

        http_response = func.HttpResponse(
            json.dumps(result_payload, indent=2),
            status_code=200,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)

    except Exception as e:
        logging.error(f"Error in GetCaseArtifacts: {str(e)}")
        http_response = func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json",
        )
        return add_cors_headers(http_response, origin)


# ---------------------------------------------------------------------------
# Nonprofit Verification Agent endpoints (Claude Opus 4.7 on Microsoft Foundry)
# ---------------------------------------------------------------------------
def _ensure_nonprofit_agent_on_path():
    try:
        import sys, os as _os

        here = _os.path.dirname(_os.path.abspath(__file__))
        # Sibling project: ../Nonprofit Verification Agent
        npv_root = _os.path.normpath(
            _os.path.join(here, "..", "Nonprofit Verification Agent")
        )
        # Sibling project: ../Foundry Opus 4.7 Agentic Library
        foundry_root = _os.path.normpath(
            _os.path.join(here, "..", "Foundry Opus 4.7 Agentic Library")
        )
        # Parent Python/ folder hosts validation_request_processing.py
        python_root = _os.path.normpath(_os.path.join(here, ".."))
        for p in (foundry_root, npv_root, python_root):
            if _os.path.isdir(p) and p not in sys.path:
                sys.path.insert(0, p)
    except Exception as e:
        logging.warning(f"Failed to extend sys.path for Nonprofit agent: {e}")


@app.route(route="agent/nonprofit/health", methods=["GET", "OPTIONS"])
def NonprofitAgentHealth(req: func.HttpRequest) -> func.HttpResponse:
    origin = req.headers.get("Origin", "*")
    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)
    try:
        _ensure_nonprofit_agent_on_path()
        from foundry_opus import FoundryClient

        ok = FoundryClient().health_check()
        body = {
            "status": "ok" if ok else "degraded",
            "model": os.getenv("FOUNDRY_DEPLOYMENT", "claude-opus-4-7-2"),
        }
        return add_cors_headers(
            func.HttpResponse(
                json.dumps(body),
                status_code=200 if ok else 503,
                mimetype="application/json",
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"NonprofitAgentHealth error: {e}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"status": "error", "error": str(e)}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


def _run_nonprofit_verification(req_body: dict, logger=logging) -> dict:
    try:
        _ensure_nonprofit_agent_on_path()
        from nonprofit_verifier import (
            verify_nonprofit_case,
            VerificationRequest,
            CaseContext,
            EmailMessage,
            Attachment,
        )

        if not isinstance(req_body, dict):
            raise ValueError("Request body must be a JSON object.")

        case_data = req_body.get("case") or {}
        email_data = req_body.get("email") or {}
        if not email_data.get("sender_email"):
            raise ValueError("email.sender_email is required.")

        attachments = []
        for a in email_data.get("attachments") or []:
            attachments.append(
                Attachment(
                    **{
                        k: a.get(k)
                        for k in (
                            "filename",
                            "content_type",
                            "url",
                            "content_base64",
                            "extracted_text",
                            "size_bytes",
                        )
                        if k in a
                    }
                )
            )

        request = VerificationRequest(
            case=CaseContext(**{k: v for k, v in case_data.items() if v is not None}),
            email=EmailMessage(
                sender_name=email_data.get("sender_name"),
                sender_email=email_data["sender_email"],
                sender_title=email_data.get("sender_title"),
                subject=email_data.get("subject"),
                body=email_data.get("body") or "",
                attachments=attachments,
            ),
        )

        result = verify_nonprofit_case(request)
        return json.loads(result.model_dump_json())
    except Exception as e:
        logger.error(f"_run_nonprofit_verification error: {e}")
        return {
            "status": "Requires Further Review",
            "confidence": "Low",
            "recommendation": "Escalate For Manual Review",
            "requires_human_review": True,
            "reasoning": f"Server-side error during verification: {e}",
            "case_note": f"Automated verification failed: {e}",
        }


@app.route(route="agent/nonprofit/verify", methods=["POST", "OPTIONS"])
def NonprofitVerify(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("NonprofitVerify function starting processing a request")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        try:
            req_body = req.get_json()
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        result = _run_nonprofit_verification(req_body, logging)
        return add_cors_headers(
            func.HttpResponse(
                json.dumps(result, indent=2, default=str),
                status_code=200,
                mimetype="application/json",
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in NonprofitVerify: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


@app.route(route="agent/nonprofit/verify/{auth_key}", methods=["POST", "OPTIONS"])
def NonprofitVerifyAuthKey(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("NonprofitVerifyAuthKey function starting processing a request")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        auth_key = req.route_params.get("auth_key")
        if not auth_key:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "auth_key parameter is required"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        # Same auth gate as RouteToDynamicsAuthKey: master key bypass or PNGO lookup.
        if (
            dynamics_helper.master_auth_key
            and auth_key.lower() != dynamics_helper.master_auth_key.lower()
        ):
            ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)
            if not ts_pngo_id:
                logging.warning(f"Unauthorized: Invalid auth_key {auth_key}")
                return add_cors_headers(
                    func.HttpResponse(
                        json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                        status_code=401,
                        mimetype="application/json",
                    ),
                    origin,
                )

        try:
            req_body = req.get_json()
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        result = _run_nonprofit_verification(req_body, logging)

        # Optionally write the case note back to Dynamics directly via the consolidated helper.
        case_id = (req_body.get("case") or {}).get("case_id")
        write_back = req.params.get("writeCaseNote", "true").lower() != "false"
        if case_id and write_back and result.get("case_note"):
            try:
                _ensure_nonprofit_agent_on_path()
                from nonprofit_verifier.dynamics_tools import create_case_note

                annotation_resp = create_case_note(
                    case_id=case_id,
                    subject=f"Nonprofit Verification — {result.get('status', 'Result')}",
                    notetext=result["case_note"],
                )
                ok = annotation_resp.get("success") is not False and bool(
                    annotation_resp.get("annotationid")
                    or annotation_resp.get("entityId")
                )
                result["_dynamics_writeback"] = {"ok": ok, "response": annotation_resp}
            except Exception as e:
                logging.warning(f"Case note writeback failed: {e}")
                result["_dynamics_writeback"] = {"ok": False, "error": str(e)}

        return add_cors_headers(
            func.HttpResponse(
                json.dumps(result, indent=2, default=str),
                status_code=200,
                mimetype="application/json",
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in NonprofitVerifyAuthKey: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


@app.route(route="agent/nonprofit/analyze-document", methods=["POST", "OPTIONS"])
def NonprofitAnalyzeDocument(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("NonprofitAnalyzeDocument function starting processing a request")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        _ensure_nonprofit_agent_on_path()
        from nonprofit_verifier.tools import (
            fetch_document_text,
            decode_attachment_text,
            classify_registration_number,
            scan_authenticity_indicators,
        )

        try:
            req_body = req.get_json() or {}
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        url = req_body.get("url")
        content_base64 = req_body.get("content_base64")
        filename = req_body.get("filename") or "document"
        content_type = req_body.get("content_type") or ""
        registration_number = req_body.get("registration_number")

        text = ""
        source = None
        if url:
            text = fetch_document_text.invoke({"url": url})
            source = {"type": "url", "value": url}
        elif content_base64:
            text = decode_attachment_text.invoke(
                {
                    "filename": filename,
                    "content_base64": content_base64,
                    "content_type": content_type,
                }
            )
            source = {"type": "base64", "filename": filename}
        elif req_body.get("text"):
            text = req_body["text"]
            source = {"type": "text"}
        else:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps(
                        {"error": "Provide one of: url, content_base64, or text."}
                    ),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        indicators = scan_authenticity_indicators.invoke({"text": text})
        rn_classification = (
            classify_registration_number.invoke(
                {"registration_number": registration_number}
            )
            if registration_number
            else None
        )

        body = {
            "source": source,
            "extracted_text_preview": text[:3000],
            "extracted_text_length": len(text),
            "authenticity_indicators": indicators,
            "registration_number_classification": rn_classification,
        }
        return add_cors_headers(
            func.HttpResponse(
                json.dumps(body, indent=2), status_code=200, mimetype="application/json"
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in NonprofitAnalyzeDocument: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


def _run_nonprofit_verification_by_case_id(
    case_id: str, max_iterations=None, logger=logging
) -> dict:
    try:
        _ensure_nonprofit_agent_on_path()
        from nonprofit_verifier import verify_nonprofit_case_by_id

        result = verify_nonprofit_case_by_id(case_id, max_iterations=max_iterations)
        result_dict = json.loads(result.model_dump_json())
        try:
            proposals = result_dict.get("memory_proposals") or []
            if proposals:
                import npv_memory_service

                summary = npv_memory_service.apply_proposals(
                    proposals=proposals,
                    case_id=case_id,
                    logger=logger,
                )
                result_dict["memory_write_summary"] = summary
                logger.info(
                    f"_run_nonprofit_verification_by_case_id - memory_proposals applied for case_id={case_id}: {summary}"
                )
        except Exception as mem_ex:
            logger.error(
                f"_run_nonprofit_verification_by_case_id - memory write failed: {mem_ex}"
            )
            result_dict["memory_write_summary"] = {
                "recorded": 0,
                "feedback": 0,
                "rejected": 0,
                "errors": [str(mem_ex)],
            }
        return result_dict
    except Exception as e:
        logger.error(f"_run_nonprofit_verification_by_case_id error: {e}")
        return {
            "case_id": case_id,
            "status": "Requires Further Review",
            "confidence": "Low",
            "recommendation": "Escalate For Manual Review",
            "requires_human_review": True,
            "reasoning": f"Server-side error during autonomous case verification: {e}",
            "case_note": f"Automated case verification failed: {e}",
        }


@app.route(route="agent/nonprofit/verify-case/{auth_key}", methods=["POST", "OPTIONS"])
def NonprofitVerifyCase(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("NonprofitVerifyCase function starting processing a request")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        auth_key = req.route_params.get("auth_key")
        if not auth_key:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "auth_key parameter is required"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        if (
            dynamics_helper.master_auth_key
            and auth_key.lower() != dynamics_helper.master_auth_key.lower()
        ):
            ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)
            if not ts_pngo_id:
                logging.warning(f"Unauthorized: Invalid auth_key {auth_key}")
                return add_cors_headers(
                    func.HttpResponse(
                        json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                        status_code=401,
                        mimetype="application/json",
                    ),
                    origin,
                )

        try:
            req_body = req.get_json() or {}
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        case_id = (
            req_body.get("caseId")
            or req_body.get("case_id")
            or req_body.get("incidentId")
            or req_body.get("incidentid")
            or req.params.get("caseId")
        )
        if not case_id:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps(
                        {"error": "caseId is required (Dynamics 365 incidentid)."}
                    ),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        max_iter_raw = req_body.get("maxIterations") or req.params.get("maxIterations")
        try:
            max_iterations = int(max_iter_raw) if max_iter_raw else None
        except Exception:
            max_iterations = None

        result = _run_nonprofit_verification_by_case_id(
            case_id, max_iterations=max_iterations, logger=logging
        )

        write_back = req.params.get("writeCaseNote", "true").lower() != "false"
        if write_back and result.get("case_note"):
            try:
                _ensure_nonprofit_agent_on_path()
                from nonprofit_verifier.dynamics_tools import create_case_note

                annotation_resp = create_case_note(
                    case_id=case_id,
                    subject=f"Nonprofit Verification — {result.get('status', 'Result')}",
                    notetext=result["case_note"],
                )
                ok = annotation_resp.get("success") is not False and bool(
                    annotation_resp.get("annotationid")
                    or annotation_resp.get("entityId")
                )
                result["_dynamics_writeback"] = {"ok": ok, "response": annotation_resp}
            except Exception as e:
                logging.warning(f"Case note writeback failed: {e}")
                result["_dynamics_writeback"] = {"ok": False, "error": str(e)}

        return add_cors_headers(
            func.HttpResponse(
                json.dumps(result, indent=2, default=str),
                status_code=200,
                mimetype="application/json",
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in NonprofitVerifyCase: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


# ---------------------------------------------------------------------------
# Async (queue-based) Nonprofit Verification — mirrors RevalidationQueueCore
# pattern in ValidationServices/ValidationFunction/ValidationRequest.cs.
# Endpoint enqueues a message and returns 202 immediately; a queue-trigger
# worker performs the long-running verification and writes the case note
# back to Dynamics. This avoids the Azure App Service ~230s gateway timeout.
# ---------------------------------------------------------------------------
NPV_QUEUE_NAME = os.getenv("NPV_QUEUE_NAME", "nonprofit-verification-queue")


def _enqueue_npv_message(message_dict: dict, logger=logging) -> None:
    try:
        from azure.storage.queue import (
            QueueClient,
            TextBase64EncodePolicy,
            TextBase64DecodePolicy,
        )

        connection_string = os.getenv("AzureWebJobsStorage")
        if not connection_string:
            raise RuntimeError("AzureWebJobsStorage is not configured.")

        queue_client = QueueClient.from_connection_string(
            conn_str=connection_string,
            queue_name=NPV_QUEUE_NAME,
            message_encode_policy=TextBase64EncodePolicy(),
            message_decode_policy=TextBase64DecodePolicy(),
        )

        try:
            queue_client.create_queue()
        except Exception:
            pass

        message_json = json.dumps(message_dict, default=str)
        queue_client.send_message(message_json)
        logger.info(
            f"Enqueued NPV message to '{NPV_QUEUE_NAME}'. request_id={message_dict.get('request_id')}, "
            f"case_id={message_dict.get('case_id')}, bytes={len(message_json)}"
        )
    except Exception as e:
        logger.error(f"_enqueue_npv_message error: {e}")
        raise


@app.route(
    route="agent/nonprofit/verify-case-async/{auth_key}", methods=["POST", "OPTIONS"]
)
def NonprofitVerifyCaseAsync(req: func.HttpRequest) -> func.HttpResponse:
    import uuid

    logging.info("NonprofitVerifyCaseAsync function starting processing a request")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        auth_key = req.route_params.get("auth_key")
        if not auth_key:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "auth_key parameter is required"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        if (
            dynamics_helper.master_auth_key
            and auth_key.lower() != dynamics_helper.master_auth_key.lower()
        ):
            ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)
            if not ts_pngo_id:
                logging.warning(f"Unauthorized: Invalid auth_key {auth_key}")
                return add_cors_headers(
                    func.HttpResponse(
                        json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                        status_code=401,
                        mimetype="application/json",
                    ),
                    origin,
                )

        try:
            req_body = req.get_json() or {}
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        case_id = (
            req_body.get("caseId")
            or req_body.get("case_id")
            or req_body.get("incidentId")
            or req_body.get("incidentid")
            or req.params.get("caseId")
        )
        if not case_id:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps(
                        {"error": "caseId is required (Dynamics 365 incidentid)."}
                    ),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        max_iter_raw = req_body.get("maxIterations") or req.params.get("maxIterations")
        try:
            max_iterations = int(max_iter_raw) if max_iter_raw else None
        except Exception:
            max_iterations = None

        write_back_param = req.params.get("writeCaseNote", "true").lower() != "false"

        request_id = str(uuid.uuid4())
        enqueued_at = datetime.utcnow().isoformat() + "Z"

        message = {
            "request_id": request_id,
            "auth_key": auth_key,
            "case_id": case_id,
            "max_iterations": max_iterations,
            "write_case_note": write_back_param,
            "enqueued_at": enqueued_at,
        }

        try:
            _enqueue_npv_message(message, logging)
        except Exception as queue_ex:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps(
                        {"error": f"Failed to enqueue verification request: {queue_ex}"}
                    ),
                    status_code=500,
                    mimetype="application/json",
                ),
                origin,
            )

        body = {
            "status": "Accepted",
            "request_id": request_id,
            "case_id": case_id,
            "queue": NPV_QUEUE_NAME,
            "enqueued_at": enqueued_at,
            "message": (
                "Verification has been queued. The result will be written as a "
                "Dynamics 365 case note (annotation) on completion."
            ),
        }
        return add_cors_headers(
            func.HttpResponse(
                json.dumps(body, indent=2), status_code=202, mimetype="application/json"
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in NonprofitVerifyCaseAsync: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


@app.queue_trigger(
    arg_name="msg",
    queue_name=os.getenv("NPV_QUEUE_NAME", "nonprofit-verification-queue"),
    connection="AzureWebJobsStorage",
)
async def NonprofitVerifyQueueWorker(msg: func.QueueMessage) -> None:

    import npv_archive_service

    request_id = None
    case_id = None
    archive = None
    queue_name = os.getenv("NPV_QUEUE_NAME", "nonprofit-verification-queue")
    body_text = ""
    try:
        body_text = msg.get_body().decode("utf-8")

        archive = npv_archive_service.archive_message(
            message_json=body_text,
            queue_name=queue_name,
            function_name="NonprofitVerifyQueueWorker",
            logger=logging,
        )

        try:
            payload = json.loads(body_text)
        except Exception as parse_ex:
            logging.error(
                f"NonprofitVerifyQueueWorker - Invalid JSON in queue message: {body_text[:500]}"
            )
            npv_archive_service.mark_failed(
                archive,
                error_message=f"Invalid JSON in queue message: {parse_ex}",
                exception=parse_ex,
                logger=logging,
            )
            return

        request_id = payload.get("request_id")
        case_id = payload.get("case_id")
        max_iterations = payload.get("max_iterations")
        write_case_note = bool(payload.get("write_case_note", True))
        auth_key = payload.get("auth_key")

        npv_archive_service.update_with_parsed_details(
            archive,
            request_id=request_id,
            case_id=case_id,
            auth_key=auth_key,
            logger=logging,
        )

        logging.info(
            f"NonprofitVerifyQueueWorker - Start. request_id={request_id}, case_id={case_id}, "
            f"max_iterations={max_iterations}, write_case_note={write_case_note}"
        )

        if not case_id:
            logging.error(
                f"NonprofitVerifyQueueWorker - Missing case_id. request_id={request_id}"
            )
            npv_archive_service.mark_failed(
                archive,
                error_message="Missing case_id in queue message",
                logger=logging,
            )
            return

        result = await asyncio.to_thread(
            _run_nonprofit_verification_by_case_id,
            case_id,
            max_iterations,
            logging,
        )

        # annotation_id = None
        # write_back_ok = None
        # write_back_error = None
        # if write_case_note and result.get("case_note"):
        #     try:
        #         _ensure_nonprofit_agent_on_path()
        #         from nonprofit_verifier.dynamics_tools import create_case_note

        #         annotation_resp = await asyncio.to_thread(
        #             create_case_note,
        #             case_id,
        #             f"Nonprofit Verification — {result.get('status', 'Result')}",
        #             result["case_note"],
        #         )
        #         if not isinstance(annotation_resp, dict):
        #             annotation_resp = {
        #                 "success": False,
        #                 "error": f"unexpected response type: {type(annotation_resp).__name__}",
        #                 "raw": str(annotation_resp)[:500],
        #             }
        #         annotation_id = annotation_resp.get(
        #             "annotationid"
        #         ) or annotation_resp.get("entityId")
        #         write_back_ok = annotation_resp.get("success") is not False and bool(
        #             annotation_id
        #         )
        #         logging.info(
        #             f"NonprofitVerifyQueueWorker - Writeback ok={write_back_ok}. request_id={request_id}, "
        #             f"annotationid={annotation_id}"
        #         )
        #     except Exception as wb_ex:
        #         write_back_ok = False
        #         write_back_error = str(wb_ex)
        #         logging.error(
        #             f"NonprofitVerifyQueueWorker - Writeback failed. request_id={request_id}, error={wb_ex}"
        #         )

        # logging.info(
        #     f"NonprofitVerifyQueueWorker - Done. request_id={request_id}, case_id={case_id}, "
        #     f"status={result.get('status')}, confidence={result.get('confidence')}"
        # )

        # if write_case_note and write_back_ok is False:
        #     npv_archive_service.mark_partially_completed(
        #         archive,
        #         notes=f"Verification completed but case-note writeback failed: {write_back_error}",
        #         result=result,
        #         logger=logging,
        #     )
        # else:
        #     npv_archive_service.mark_completed(
        #         archive,
        #         result=result,
        #         notes="Processing completed successfully",
        #         annotation_id=annotation_id,
        #         write_back_ok=write_back_ok,
        #         logger=logging,
        #     )

        npv_archive_service.mark_completed(
                archive,
                result=result,
                notes="Processing completed successfully",
                annotation_id="waived_for_now",
                write_back_ok=True,
                logger=logging,
            )
    except Exception as e:
        logging.error(
            f"NonprofitVerifyQueueWorker - Unhandled error. request_id={request_id}, "
            f"case_id={case_id}, error={e}"
        )
        try:
            npv_archive_service.mark_failed(
                archive,
                error_message=str(e),
                exception=e,
                logger=logging,
            )
        except Exception:
            pass
        raise


@app.route(
    route="agent/nonprofit/archive-query/{auth_key}", methods=["POST", "OPTIONS"]
)
def NonprofitVerifyArchiveQuery(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("NonprofitVerifyArchiveQuery - Start")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        import npv_archive_service

        auth_key = req.route_params.get("auth_key")
        if not auth_key:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "auth_key parameter is required"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        if (
            dynamics_helper.master_auth_key
            and auth_key.lower() != dynamics_helper.master_auth_key.lower()
        ):
            ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)
            if not ts_pngo_id:
                logging.warning(
                    f"NonprofitVerifyArchiveQuery - Unauthorized auth_key {auth_key}"
                )
                return add_cors_headers(
                    func.HttpResponse(
                        json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                        status_code=401,
                        mimetype="application/json",
                    ),
                    origin,
                )

        try:
            query_fields = req.get_json() or {}
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        slot_name = npv_archive_service.get_slot_name()
        logging.info(f"NonprofitVerifyArchiveQuery - SlotName: {slot_name}")

        results = npv_archive_service.build_and_execute_adhoc_query(
            query_fields, slot_name, logging
        )

        logging.info(f"NonprofitVerifyArchiveQuery - Returned {len(results)} record(s)")

        body = {
            "slot": slot_name,
            "query": query_fields,
            "resultCount": len(results),
            "requests": results,
        }
        return add_cors_headers(
            func.HttpResponse(
                json.dumps(body, indent=2, default=str),
                status_code=200,
                mimetype="application/json",
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in NonprofitVerifyArchiveQuery: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


def _npv_memory_authorize(req: func.HttpRequest, origin: str):
    auth_key = req.route_params.get("auth_key")
    if not auth_key:
        return None, add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": "auth_key parameter is required"}),
                status_code=400,
                mimetype="application/json",
            ),
            origin,
        )
    if (
        dynamics_helper.master_auth_key
        and auth_key.lower() != dynamics_helper.master_auth_key.lower()
    ):
        ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)
        if not ts_pngo_id:
            logging.warning(f"NPV memory endpoint - Unauthorized auth_key {auth_key}")
            return None, add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                    status_code=401,
                    mimetype="application/json",
                ),
                origin,
            )
    return auth_key, None


@app.route(route="agent/nonprofit/memory-query/{auth_key}", methods=["POST", "OPTIONS"])
def NonprofitVerifyMemoryQuery(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("NonprofitVerifyMemoryQuery - Start")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        import npv_memory_service

        _auth, err = _npv_memory_authorize(req, origin)
        if err is not None:
            return err

        try:
            query_fields = req.get_json() or {}
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        slot_name = query_fields.get("slot") or npv_memory_service.get_slot_name()
        logging.info(f"NonprofitVerifyMemoryQuery - SlotName: {slot_name}")

        results = npv_memory_service.build_and_execute_adhoc_query(
            query_fields, slot_name, logging
        )

        logging.info(f"NonprofitVerifyMemoryQuery - Returned {len(results)} record(s)")

        body = {
            "slot": slot_name,
            "query": query_fields,
            "resultCount": len(results),
            "entries": results,
        }
        return add_cors_headers(
            func.HttpResponse(
                json.dumps(body, indent=2, default=str),
                status_code=200,
                mimetype="application/json",
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in NonprofitVerifyMemoryQuery: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


@app.route(route="agent/nonprofit/memory-admin/{auth_key}", methods=["POST", "OPTIONS"])
def NonprofitVerifyMemoryAdmin(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("NonprofitVerifyMemoryAdmin - Start")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        import npv_memory_service

        _auth, err = _npv_memory_authorize(req, origin)
        if err is not None:
            return err

        try:
            body_in = req.get_json() or {}
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        action = (body_in.get("action") or "").lower()
        slot_name = body_in.get("slot") or npv_memory_service.get_slot_name()
        logging.info(f"NonprofitVerifyMemoryAdmin - action={action} slot={slot_name}")

        result_obj = None
        status_code = 200

        if action == "pin":
            result_obj = npv_memory_service.pin_manual(
                category=body_in.get("category"),
                scope_key=body_in.get("scope_key"),
                subject_key=body_in.get("subject_key"),
                subject=body_in.get("subject") or "",
                content=body_in.get("content") or {},
                tags=body_in.get("tags"),
                notes=body_in.get("notes"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "deprecate":
            result_obj = npv_memory_service.set_status(
                ref=body_in.get("ref"),
                new_status="deprecated",
                notes=body_in.get("notes"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "reactivate":
            result_obj = npv_memory_service.set_status(
                ref=body_in.get("ref"),
                new_status="active",
                notes=body_in.get("notes"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "needs_review":
            result_obj = npv_memory_service.set_status(
                ref=body_in.get("ref"),
                new_status="needsReview",
                notes=body_in.get("notes"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "feedback":
            result_obj = npv_memory_service.record_feedback(
                ref=body_in.get("ref"),
                outcome=(body_in.get("outcome") or "").lower(),
                notes=body_in.get("notes"),
                case_id=body_in.get("case_id"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "lookup":
            results = npv_memory_service.lookup_entries(
                category=body_in.get("category"),
                scope_key=body_in.get("scope_key"),
                subject_contains=body_in.get("subject_contains"),
                min_confidence=body_in.get("min_confidence") or "Low",
                include_statuses=body_in.get("include_statuses"),
                max_results=int(body_in.get("max_results") or 10),
                slot_name=slot_name,
                logger=logging,
            )
            result_obj = {"count": len(results), "entries": results}
        elif action == "bootstrap":
            try:
                import bootstrap_memory

                seeded = 0
                rejected = 0
                for ent in bootstrap_memory._seed_entries():
                    r = npv_memory_service.pin_manual(
                        category=ent["category"],
                        scope_key=ent["scope_key"],
                        subject_key=ent["subject_key"],
                        subject=ent["subject"],
                        content=ent["content"],
                        tags=ent.get("tags"),
                        notes="seeded via memory-admin bootstrap",
                        slot_name=slot_name,
                        logger=logging,
                    )
                    if r is not None:
                        seeded += 1
                    else:
                        rejected += 1
                result_obj = {"seeded": seeded, "rejected": rejected, "slot": slot_name}
            except Exception as ex:
                result_obj = {"error": f"bootstrap failed: {ex}"}
                status_code = 500
        else:
            result_obj = {
                "error": f"unknown action '{action}'. Valid: pin, deprecate, reactivate, needs_review, feedback, lookup, bootstrap"
            }
            status_code = 400

        return add_cors_headers(
            func.HttpResponse(
                json.dumps(
                    {"slot": slot_name, "action": action, "result": result_obj},
                    indent=2,
                    default=str,
                ),
                status_code=status_code,
                mimetype="application/json",
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in NonprofitVerifyMemoryAdmin: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


# ===========================================================================
# Global Support Case Agent — HTTP enqueue + queue worker
# ===========================================================================
GSC_QUEUE_NAME = os.getenv("GSC_QUEUE_NAME", "global-support-case-queue")


def _enqueue_gsc_message(message_dict: dict, logger=logging) -> None:
    try:
        from azure.storage.queue import (
            QueueClient,
            TextBase64EncodePolicy,
            TextBase64DecodePolicy,
        )

        connection_string = os.getenv("AzureWebJobsStorage")
        if not connection_string:
            raise RuntimeError("AzureWebJobsStorage is not configured.")

        queue_client = QueueClient.from_connection_string(
            conn_str=connection_string,
            queue_name=GSC_QUEUE_NAME,
            message_encode_policy=TextBase64EncodePolicy(),
            message_decode_policy=TextBase64DecodePolicy(),
        )

        try:
            queue_client.create_queue()
        except Exception:
            pass

        message_json = json.dumps(message_dict, default=str)
        queue_client.send_message(message_json)
        logger.info(
            f"Enqueued GSC message to '{GSC_QUEUE_NAME}'. request_id={message_dict.get('request_id')}, "
            f"case_id={message_dict.get('case_id')}, bytes={len(message_json)}"
        )
    except Exception as e:
        logger.error(f"_enqueue_gsc_message error: {e}")
        raise


@app.route(
    route="agent/gsc/handle-case-async/{auth_key}", methods=["POST", "OPTIONS"]
)
def GlobalSupportCaseHandleAsync(req: func.HttpRequest) -> func.HttpResponse:
    import uuid

    logging.info("GlobalSupportCaseHandleAsync function starting processing a request")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        auth_key = req.route_params.get("auth_key")
        if not auth_key:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "auth_key parameter is required"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        if (
            dynamics_helper.master_auth_key
            and auth_key.lower() != dynamics_helper.master_auth_key.lower()
        ):
            ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)
            if not ts_pngo_id:
                logging.warning(f"GSC Unauthorized: Invalid auth_key {auth_key}")
                return add_cors_headers(
                    func.HttpResponse(
                        json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                        status_code=401,
                        mimetype="application/json",
                    ),
                    origin,
                )

        try:
            req_body = req.get_json() or {}
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        case_id = (
            req_body.get("caseId")
            or req_body.get("case_id")
            or req_body.get("incidentId")
            or req_body.get("incidentid")
            or req.params.get("caseId")
        )
        email_id = req_body.get("emailId") or req_body.get("email_id")
        trigger = req_body.get("trigger")
        correlation_id = req_body.get("correlationId") or req_body.get("correlation_id")

        if not case_id:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "caseId is required (Dynamics 365 incidentid)."}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        max_iter_raw = req_body.get("maxIterations") or req.params.get("maxIterations")
        try:
            max_iterations = int(max_iter_raw) if max_iter_raw else None
        except Exception:
            max_iterations = None

        agent_mode_raw = (req_body.get("agentMode") or req_body.get("agent_mode") or "active_agent")
        agent_mode = str(agent_mode_raw).strip().lower()
        if agent_mode not in ("active_agent", "simulate"):
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "agentMode must be 'active_agent' or 'simulate'."}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        request_id = str(uuid.uuid4())
        enqueued_at = datetime.utcnow().isoformat() + "Z"

        message = {
            "request_id": request_id,
            "auth_key": auth_key,
            "case_id": case_id,
            "email_id": email_id,
            "trigger": trigger,
            "correlation_id": correlation_id,
            "max_iterations": max_iterations,
            "agent_mode": agent_mode,
            "enqueued_at": enqueued_at,
        }

        try:
            _enqueue_gsc_message(message, logging)
        except Exception as queue_ex:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": f"Failed to enqueue GSC request: {queue_ex}"}),
                    status_code=500,
                    mimetype="application/json",
                ),
                origin,
            )

        body = {
            "status": "Accepted",
            "request_id": request_id,
            "case_id": case_id,
            "agent_mode": agent_mode,
            "queue": GSC_QUEUE_NAME,
            "enqueued_at": enqueued_at,
            "message": (
                "Case queued for the Global Support Case Agent. In 'active_agent' "
                "mode the agent will either reply directly from the receiving queue "
                "or route the case to the Global Support queue with notes. In "
                "'simulate' mode no direct writes will be made to the case, but "
                "memory proposals will still be applied."
            ),
        }
        return add_cors_headers(
            func.HttpResponse(
                json.dumps(body, indent=2), status_code=202, mimetype="application/json"
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in GlobalSupportCaseHandleAsync: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


def _run_gsc_handle_by_case_id(case_id: str, max_iterations, email_id, correlation_id, request_id, logger, agent_mode: str = "active_agent"):
    try:
        from global_support_case_agent import handle_case_by_id, CaseRequest, AgentMode

        try:
            mode_enum = AgentMode(agent_mode)
        except Exception:
            mode_enum = AgentMode.ACTIVE_AGENT

        gsc_req = CaseRequest(
            case_id=case_id,
            email_id=email_id,
            correlation_id=correlation_id,
            agent_mode=mode_enum,
        )
        result = handle_case_by_id(
            case_id=case_id,
            max_iterations=max_iterations,
            operation_id=request_id,
            request=gsc_req,
        )
        return result.model_dump(mode="json")
    except Exception as e:
        logger.error(f"_run_gsc_handle_by_case_id failed for case {case_id}: {e}")
        return {
            "case_id": case_id,
            "intent": "Other",
            "confidence": "Low",
            "recommendation": "EscalateToGlobalSupport",
            "summary": "",
            "reasoning": f"Agent execution failed: {e}",
            "requires_human_review": True,
            "case_note": f"GSC agent execution failed: {e}",
            "action_taken": "Failed",
        }


@app.queue_trigger(
    arg_name="msg",
    queue_name=os.getenv("GSC_QUEUE_NAME", "global-support-case-queue"),
    connection="AzureWebJobsStorage",
)
async def GlobalSupportCaseQueueWorker(msg: func.QueueMessage) -> None:

    request_id = None
    case_id = None
    archive = None
    queue_name = os.getenv("GSC_QUEUE_NAME", "global-support-case-queue")
    body_text = ""
    try:
        body_text = msg.get_body().decode("utf-8")

        try:
            import npv_archive_service
            archive = npv_archive_service.archive_message(
                message_json=body_text,
                queue_name=queue_name,
                function_name="GlobalSupportCaseQueueWorker",
                logger=logging,
            )
        except Exception as arch_ex:
            logging.warning(f"GSC archive_message failed (continuing): {arch_ex}")
            archive = None

        try:
            payload = json.loads(body_text)
        except Exception as parse_ex:
            logging.error(
                f"GlobalSupportCaseQueueWorker - Invalid JSON in queue message: {body_text[:500]}"
            )
            try:
                if archive is not None:
                    import npv_archive_service
                    npv_archive_service.mark_failed(
                        archive,
                        error_message=f"Invalid JSON in queue message: {parse_ex}",
                        exception=parse_ex,
                        logger=logging,
                    )
            except Exception:
                pass
            return

        request_id = payload.get("request_id")
        case_id = payload.get("case_id")
        email_id = payload.get("email_id")
        correlation_id = payload.get("correlation_id")
        max_iterations = payload.get("max_iterations")
        auth_key = payload.get("auth_key")
        agent_mode = (payload.get("agent_mode") or "active_agent")

        try:
            if archive is not None:
                import npv_archive_service
                npv_archive_service.update_with_parsed_details(
                    archive,
                    request_id=request_id,
                    case_id=case_id,
                    auth_key=auth_key,
                    logger=logging,
                )
        except Exception:
            pass

        logging.info(
            f"GlobalSupportCaseQueueWorker - Start. request_id={request_id}, case_id={case_id}, "
            f"max_iterations={max_iterations}"
        )

        if not case_id:
            logging.error(f"GlobalSupportCaseQueueWorker - Missing case_id. request_id={request_id}")
            try:
                if archive is not None:
                    import npv_archive_service
                    npv_archive_service.mark_failed(
                        archive,
                        error_message="Missing case_id in queue message",
                        logger=logging,
                    )
            except Exception:
                pass
            return

        result = await asyncio.to_thread(
            _run_gsc_handle_by_case_id,
            case_id,
            max_iterations,
            email_id,
            correlation_id,
            request_id,
            logging,
            agent_mode,
        )

        logging.info(
            f"GlobalSupportCaseQueueWorker - Done. request_id={request_id}, case_id={case_id}, "
            f"intent={result.get('intent')}, confidence={result.get('confidence')}, "
            f"recommendation={result.get('recommendation')}, action_taken={result.get('action_taken')}, "
            f"agent_mode={agent_mode}"
        )

        try:
            if archive is not None:
                import npv_archive_service
                npv_archive_service.mark_completed(
                    archive,
                    result=result,
                    notes="Processing completed successfully",
                    annotation_id=None,
                    write_back_ok=True,
                    logger=logging,
                )
        except Exception:
            pass
    except Exception as e:
        logging.error(
            f"GlobalSupportCaseQueueWorker - Unhandled error. request_id={request_id}, "
            f"case_id={case_id}, error={e}"
        )
        try:
            if archive is not None:
                import npv_archive_service
                npv_archive_service.mark_failed(
                    archive,
                    error_message=str(e),
                    exception=e,
                    logger=logging,
                )
        except Exception:
            pass
        raise


def _gsc_memory_authorize(req: func.HttpRequest, origin: str):
    auth_key = req.route_params.get("auth_key")
    if not auth_key:
        return None, add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": "auth_key parameter is required"}),
                status_code=400,
                mimetype="application/json",
            ),
            origin,
        )
    if (
        dynamics_helper.master_auth_key
        and auth_key.lower() != dynamics_helper.master_auth_key.lower()
    ):
        ts_pngo_id = techsoupservices_helper.get_ts_pngo_id(auth_key, logging)
        if not ts_pngo_id:
            logging.warning(f"GSC memory endpoint - Unauthorized auth_key {auth_key}")
            return None, add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Unauthorized: Invalid auth_key"}),
                    status_code=401,
                    mimetype="application/json",
                ),
                origin,
            )
    return auth_key, None


@app.route(route="agent/gsc/memory-query/{auth_key}", methods=["POST", "OPTIONS"])
def GlobalSupportCaseMemoryQuery(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("GlobalSupportCaseMemoryQuery - Start")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        import gsc_memory_service

        _auth, err = _gsc_memory_authorize(req, origin)
        if err is not None:
            return err

        try:
            query_fields = req.get_json() or {}
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        slot_name = query_fields.get("slot") or gsc_memory_service.get_slot_name()
        logging.info(f"GlobalSupportCaseMemoryQuery - SlotName: {slot_name}")

        results = gsc_memory_service.build_and_execute_adhoc_query(
            query_fields, slot_name, logging
        )

        logging.info(f"GlobalSupportCaseMemoryQuery - Returned {len(results)} record(s)")

        body = {
            "slot": slot_name,
            "query": query_fields,
            "resultCount": len(results),
            "entries": results,
        }
        return add_cors_headers(
            func.HttpResponse(
                json.dumps(body, indent=2, default=str),
                status_code=200,
                mimetype="application/json",
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in GlobalSupportCaseMemoryQuery: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )


@app.route(route="agent/gsc/memory-admin/{auth_key}", methods=["POST", "OPTIONS"])
def GlobalSupportCaseMemoryAdmin(req: func.HttpRequest) -> func.HttpResponse:

    logging.info("GlobalSupportCaseMemoryAdmin - Start")
    origin = req.headers.get("Origin", "*")

    if req.method == "OPTIONS":
        return add_cors_headers(func.HttpResponse(status_code=200), origin)

    try:
        import gsc_memory_service

        _auth, err = _gsc_memory_authorize(req, origin)
        if err is not None:
            return err

        try:
            body_in = req.get_json() or {}
        except ValueError:
            return add_cors_headers(
                func.HttpResponse(
                    json.dumps({"error": "Request body must be valid JSON"}),
                    status_code=400,
                    mimetype="application/json",
                ),
                origin,
            )

        action = (body_in.get("action") or "").lower()
        slot_name = body_in.get("slot") or gsc_memory_service.get_slot_name()
        logging.info(f"GlobalSupportCaseMemoryAdmin - action={action} slot={slot_name}")

        result_obj = None
        status_code = 200

        if action == "pin":
            result_obj = gsc_memory_service.pin_manual(
                category=body_in.get("category"),
                scope_key=body_in.get("scope_key"),
                subject_key=body_in.get("subject_key"),
                subject=body_in.get("subject") or "",
                content=body_in.get("content") or {},
                tags=body_in.get("tags"),
                notes=body_in.get("notes"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "deprecate":
            result_obj = gsc_memory_service.set_status(
                ref=body_in.get("ref"),
                new_status="deprecated",
                notes=body_in.get("notes"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "reactivate":
            result_obj = gsc_memory_service.set_status(
                ref=body_in.get("ref"),
                new_status="active",
                notes=body_in.get("notes"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "needs_review":
            result_obj = gsc_memory_service.set_status(
                ref=body_in.get("ref"),
                new_status="needsReview",
                notes=body_in.get("notes"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "feedback":
            result_obj = gsc_memory_service.record_feedback(
                ref=body_in.get("ref"),
                outcome=(body_in.get("outcome") or "").lower(),
                notes=body_in.get("notes"),
                case_id=body_in.get("case_id"),
                slot_name=slot_name,
                logger=logging,
            )
        elif action == "lookup":
            results = gsc_memory_service.lookup_entries(
                category=body_in.get("category"),
                scope_key=body_in.get("scope_key"),
                subject_contains=body_in.get("subject_contains"),
                min_confidence=body_in.get("min_confidence") or "Low",
                include_statuses=body_in.get("include_statuses"),
                max_results=int(body_in.get("max_results") or 10),
                slot_name=slot_name,
                logger=logging,
            )
            result_obj = {"count": len(results), "entries": results}
        elif action == "bootstrap":
            try:
                from scripts import seed_gsc_memory as _seeder

                sources = body_in.get("sources") or ["kb", "web", "latam"]
                sources = [s.lower() for s in sources]
                totals = {}
                if "kb" in sources:
                    totals["kb"] = _seeder._seed_kb_hits(gsc_memory_service, slot_name, dry_run=False)
                if "web" in sources:
                    totals["web"] = _seeder._seed_web_sources(gsc_memory_service, slot_name, dry_run=False)
                if "latam" in sources:
                    totals["latam"] = _seeder._seed_latam_intents(gsc_memory_service, slot_name, dry_run=False)
                result_obj = {
                    "slot": slot_name,
                    "seeded": totals,
                    "total": sum(totals.values()),
                }
            except Exception as ex:
                result_obj = {"error": f"bootstrap failed: {ex}"}
                status_code = 500
        else:
            result_obj = {
                "error": f"unknown action '{action}'. Valid: pin, deprecate, reactivate, needs_review, feedback, lookup, bootstrap"
            }
            status_code = 400

        return add_cors_headers(
            func.HttpResponse(
                json.dumps(
                    {"slot": slot_name, "action": action, "result": result_obj},
                    indent=2,
                    default=str,
                ),
                status_code=status_code,
                mimetype="application/json",
            ),
            origin,
        )
    except Exception as e:
        logging.error(f"Error in GlobalSupportCaseMemoryAdmin: {str(e)}")
        return add_cors_headers(
            func.HttpResponse(
                json.dumps({"error": f"Internal server error: {str(e)}"}),
                status_code=500,
                mimetype="application/json",
            ),
            origin,
        )
