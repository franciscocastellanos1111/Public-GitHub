import azure.functions as func
import json
import os
import logging
from typing import Dict, Any, Optional
from urllib.parse import urlparse, parse_qs
from typing import Dict, List, Any, Union




def get_azure_function_url_info(req: func.HttpRequest) -> Dict[str, Any]:
    try:
        
        full_url = req.url
        
 
        parsed_url = urlparse(full_url)
        
    
        query_params = dict(req.params) 
        
        headers = dict(req.headers)
        host_header = headers.get('host', 'unknown')
        x_forwarded_host = headers.get('x-forwarded-host')
        x_forwarded_proto = headers.get('x-forwarded-proto')
        x_original_url = headers.get('x-original-url')
        
   
        is_proxied = bool(x_forwarded_host or x_forwarded_proto or x_original_url)
        

        url_info = {
            "full_url": full_url,
            "scheme": parsed_url.scheme,
            "hostname": parsed_url.hostname,
            "port": parsed_url.port,
            "path": parsed_url.path,
            "query_string": parsed_url.query,
            "query_params": query_params,  
            "fragment": parsed_url.fragment,
            "method": req.method,
            "host_header": host_header,
            "is_proxied": is_proxied,
            "proxy_headers": {
                "x_forwarded_host": x_forwarded_host,
                "x_forwarded_proto": x_forwarded_proto,
                "x_original_url": x_original_url
            },
            "subdomain": None,
            "azure_function_context": {
                "function_name": req.route_params.get('functionName', 'unknown'),
                "invocation_id": getattr(req, 'invocation_id', 'unknown'),
                "request_id": headers.get('x-ms-request-id')
            }
        }
        

        if parsed_url.hostname:
            hostname_parts = parsed_url.hostname.split('.')
            if len(hostname_parts) > 2:  # Has subdomain
                url_info["subdomain"] = hostname_parts[0]
        

        if 'azurewebsites.net' not in host_header:
            url_info["has_custom_domain"] = True
        else:
            url_info["has_custom_domain"] = False
            

        if x_original_url:
            original_parsed = urlparse(x_original_url)
            url_info["original_url"] = {
                "full_url": x_original_url,
                "path": original_parsed.path,
                "query_string": original_parsed.query,
                "hostname": original_parsed.hostname
            }
        
        return url_info
        
    except Exception as e:
        return {
            "error": f"Failed to parse URL info: {str(e)}",
            "full_url": getattr(req, 'url', 'unknown'),
            "method": getattr(req, 'method', 'unknown')
        }







def get_expando_object(json_element: Union[Dict[str, Any], List[Any], Any]) -> Dict[str, Any]:

    entity = {}
    
    entity["@odata.type"] = "#Microsoft.Dynamics.CRM.expando"
    
    if isinstance(json_element, dict):
        for key, value in json_element.items():
            if value is None or value == "":
                continue
                
            if isinstance(value, dict):
                nested_object = get_expando_object(value)
                entity[key] = nested_object
                
            elif isinstance(value, list):
                if not value:  # Empty array
                    continue
                    
                # Check if it's a list of strings
                is_string_list = all(isinstance(item, str) for item in value)
                
                if is_string_list:
                    # Transform list of strings into list of {"value": "string"} objects
                    array_name = key
                    entity[f"{array_name}@odata.type"] = "#Collection(Microsoft.Dynamics.CRM.expando)"
                    
                    converted_array = []
                    for string_item in value:
                        converted_array.append({"value": string_item})
                    
                    entity[array_name] = converted_array
                    
                else:
                    # Check if it's a uniform object array
                    is_array = _is_uniform_object_array(value)
                    
                    if is_array:
                        array_name = "legalIdentifiers" if key == "registeredIdentifiers" else key
                        entity[f"{array_name}@odata.type"] = "#Collection(Microsoft.Dynamics.CRM.expando)"
                        
                        converted_array = []
                        for array_item in value:
                            if isinstance(array_item, dict):
                                converted_item = get_expando_object(array_item)
                            else:
                           
                                converted_item = array_item
                            converted_array.append(converted_item)
                        
                        entity[array_name] = converted_array
                    else:
      
                        entity[f"{key}@odata.type"] = "#Collection(Microsoft.Dynamics.CRM.expando)"
                        entity[key] = value
                    
            else:
                entity[key] = value
    
    elif isinstance(json_element, list):
        entity["@odata.type"] = "#Collection(Microsoft.Dynamics.CRM.expando)"
        converted_array = []
        for item in json_element:
            if isinstance(item, dict):
                converted_array.append(get_expando_object(item))
            else:
                converted_array.append(item)
        return {"items": converted_array, "@odata.type": "#Collection(Microsoft.Dynamics.CRM.expando)"}
    
    else:
        return {"value": json_element, "@odata.type": "#Microsoft.Dynamics.CRM.expando"}
    
    return entity





def _is_uniform_object_array(array: List[Any]) -> bool:


    if not array or len(array) <= 1:
        return len(array) == 1 and isinstance(array[0], dict)
    
    if not all(isinstance(item, dict) for item in array):
        return False
    
    first_item = array[0]
    if not isinstance(first_item, dict):
        return False
    
    first_keys = set(first_item.keys())
    
    for item in array[1:]:
        if not isinstance(item, dict):
            return False
        if set(item.keys()) != first_keys:
            return False
    
    return True










    







def convert_expando_to_json(expando_object: Union[Dict[str, Any], List[Any], Any]) -> Any:
    if isinstance(expando_object, dict):
        clean_object = {}
        
        for key, value in expando_object.items():
            if key.startswith("@odata") or key.endswith("@odata.type"):
                continue
            
            if isinstance(value, dict):
                # Recursively convert nested objects
                clean_object[key] = convert_expando_to_json(value)
                
            elif isinstance(value, list):
                clean_array = []
                for item in value:
                    clean_item = convert_expando_to_json(item)
                    clean_array.append(clean_item)
                clean_object[key] = clean_array
                    
            else:
                clean_object[key] = value
        
        return clean_object
        
    elif isinstance(expando_object, list):
        
        clean_array = []
        for item in expando_object:
            clean_item = convert_expando_to_json(item)
            clean_array.append(clean_item)
        return clean_array
        
    else:
        
        return expando_object






def process_dynamics_response_to_clean_json(dynamics_response: str) -> Dict[str, Any]:

    try:

        response_data = json.loads(dynamics_response)
        
    
        clean_response = convert_expando_to_json(response_data)
        
        return clean_response
        
    except json.JSONDecodeError as e:
        raise ValueError(f"Invalid JSON in Dynamics response: {str(e)}")
    except Exception as e:
        raise RuntimeError(f"Error processing Dynamics response: {str(e)}")


def get_ts_pngo_id(auth_key: str, logger: logging.Logger = None) -> Optional[str]:
    """
    Get TechSoup PNGO ID from database via SOAP DataAccessService
    
    This is the Python equivalent of C# getTsPngoId method.
    It:
    1. Gets certificate from Azure Key Vault
    2. Creates SOAP client with certificate authentication
    3. Calls ExecuteStoredProc to run usp_getTsPngoId
    4. Returns the PNGO ID from the result
    
    Args:
        auth_key: Authentication key parameter for the stored procedure
        logger: Optional logger instance for logging
        
    Returns:
        TechSoup PNGO ID string or None on error
    """
    ts_pngo_id = ""
    
    try:
        from soap_data_access_client import SOAPDataAccessClient, ExecuteStoredProcRequest
        from lxml import etree
        
        if logger:
            logger.info(f"get_ts_pngo_id called with auth_key: {auth_key}")
        
        # Get SQL server name from environment
        sql_server = os.getenv('ts_Sql2k14Server')
        if not sql_server:
            error = "ts_Sql2k14Server environment variable is required"
            if logger:
                logger.error(error)
            return None
        
        # Create SOAP client with logger
        soap_client = SOAPDataAccessClient(logger=logger)
        
        # Initialize client with certificate authentication
        if not soap_client.initialize():
            error = "Failed to initialize SOAP Data Access client"
            if logger:
                logger.error(error)
            return None
        
        if logger:
            logger.info("SOAP client initialized successfully")
        
        # Create request with parameters
        # C# code: objRequest.params = new inputParamsType() with XmlElement for authKey
        request = ExecuteStoredProcRequest(
            server_name=sql_server,
            db_name="ServiceAdmin",
            sp_name="usp_getTsPngoId",
            parameters={'authKey': auth_key}
        )
        
        if logger:
            logger.info(f"Executing stored procedure: {request.sp_name}")
        
        # Execute stored procedure
        response = soap_client.execute_stored_proc(request)
        
        if not response or not response.success:
            error = f"Failed to execute stored procedure: {response.error if response else 'Unknown error'}"
            if logger:
                logger.error(error)
            return None
        
        # Extract PNGO ID from response
        # C# code: returnXml.First().Any[0].InnerText
        if response.return_xml and len(response.return_xml) > 0:
            first_row = response.return_xml[0]
            
            if logger:
                logger.info(f"Response row data: {first_row}")
            
            # Get first value from the row (PNGO ID)
            if first_row:
                # Try different possible keys
                ts_pngo_id = (
                    first_row.get('tsPngoId') or 
                    first_row.get('TsPngoId') or 
                    first_row.get('ts_pngo_id') or
                    first_row.get('pngoId') or
                    first_row.get('PngoId') or
                    first_row.get('value') or
                    next(iter(first_row.values()), None)
                )
                
                if ts_pngo_id:
                    if logger:
                        logger.info(f"get_ts_pngo_id - tsPngoId: {ts_pngo_id}")
                    return ts_pngo_id
        
        if logger:
            logger.warning("No PNGO ID returned from stored procedure")
        
    except Exception as e:
        error = f"Error in get_ts_pngo_id: {str(e)}"
        if logger:
            logger.error(error)
        else:
            print(error)
    
    return ts_pngo_id if ts_pngo_id else None

def get_netsuite_nonce(logger: logging.Logger = None) -> Optional[str]:  
    netsuite_nonce = ""
    
    try:
        from soap_data_access_client import SOAPDataAccessClient, ExecuteStoredProcRequest
        from lxml import etree
        
        if logger:
            logger.info(f"get_netsuite_nonce called")
        
        
        sql_server = os.getenv('ts_Sql2k14Server')
        if not sql_server:
            error = "ts_Sql2k14Server environment variable is required"
            if logger:
                logger.error(error)
            return None
        
       
        soap_client = SOAPDataAccessClient(logger=logger)
        
        
        if not soap_client.initialize():
            error = "Failed to initialize SOAP Data Access client"
            if logger:
                logger.error(error)
            return None
        
        if logger:
            logger.info("SOAP client initialized successfully")
        
        
        request = ExecuteStoredProcRequest(
            server_name=sql_server,
            db_name="ServiceAdmin",
            sp_name="usp_GetNetSuiteNonce"
        )
        
        if logger:
            logger.info(f"Executing stored procedure: {request.sp_name}")        
        
        response = soap_client.execute_stored_proc(request)
        
        if not response or not response.success:
            error = f"Failed to execute stored procedure: {response.error if response else 'Unknown error'}"
            if logger:
                logger.error(error)
            return None
        
        
        if response.return_xml and len(response.return_xml) > 0:
            first_row = response.return_xml[0]
            
            if logger:
                logger.info(f"Response row data: {first_row}")            
            
            if first_row:                
                netsuite_nonce = (
                    first_row.get('nonce') or
                    first_row.get('value') or
                    next(iter(first_row.values()), None)
                )
                
                if netsuite_nonce:
                    if logger:
                        logger.info(f"get_netsuite_nonce - nonce: {netsuite_nonce}")
                    return netsuite_nonce
        
        if logger:
            logger.warning("No nonce returned from stored procedure")
        
    except Exception as e:
        error = f"Error in get_netsuite_nonce: {str(e)}"
        if logger:
            logger.error(error)
        else:
            print(error)
    
    return netsuite_nonce if netsuite_nonce else None




