"""
SOAP Data Access Service Client
Python implementation of the DataAccessServiceClient for executing stored procedures
"""

import os
import logging
from typing import Optional, List, Dict, Any
from dataclasses import dataclass
from zeep import Client, Settings
from zeep.transports import Transport
from requests import Session
from lxml import etree
from azure_keyvault_helper import AzureKeyVaultHelper


@dataclass
class ExecuteStoredProcRequest:
    """
    Request type for ExecuteStoredProc operation
    Equivalent to C# ExecuteStoredProcRequestType
    """
    server_name: str
    db_name: str
    sp_name: str
    parameters: Optional[Dict[str, Any]] = None


@dataclass
class ExecuteStoredProcResponse:
    """
    Response type for ExecuteStoredProc operation
    Equivalent to C# ExecuteStoredProcResponseType
    """
    return_xml: List[Dict[str, Any]]
    success: bool = True
    error: Optional[str] = None


class SOAPDataAccessClient:
    """
    SOAP client for Data Access Service
    Python implementation of the C# DataAccessServiceClient
    
    This client:
    1. Uses certificate authentication from Azure Key Vault
    2. Makes SOAP calls to execute stored procedures
    3. Parses XML responses
    """
    
    def __init__(self, wsdl_url: Optional[str] = None, endpoint_url: Optional[str] = None, logger: Optional[logging.Logger] = None):
        """
        Initialize SOAP client
        
        Args:
            wsdl_url: Optional WSDL URL (not used at runtime, kept for backward compatibility)
            endpoint_url: Optional endpoint URL (defaults to environment variable)
            logger: Optional logger instance for logging
        """
        self.logger = logger or logging.getLogger(__name__)
        self.endpoint_url = endpoint_url or self._get_endpoint_url()
        self.certificate_bytes = None
        self.cert_files = None
        self.client = None
        
    def _get_local_wsdl_path(self) -> str:
        """
        Get path to local WSDL file
        The WSDL is stored locally since the remote WSDL URL (esbdev.techsoup.org)
        is not accessible at runtime - only the service endpoint (esbdevca.techsoup.org) is available.
        
        Returns:
            Path to local WSDL file
        """
        import pathlib
        
        # Get the directory where this script is located
        script_dir = pathlib.Path(__file__).parent
        wsdl_path = script_dir / "DataAccessService.wsdl"
        
        if not wsdl_path.exists():
            raise FileNotFoundError(
                f"WSDL file not found at {wsdl_path}. "
                "Please ensure DataAccessService.wsdl is included in the deployment."
            )
        
        return str(wsdl_path)
    
    def _get_endpoint_url(self) -> str:
        """Get service endpoint URL from environment"""
        esb_url = os.environ.get('ts_ESBUrl')
        if not esb_url:
            raise ValueError("ts_ESBUrl environment variable is required")
        
        # Build full endpoint URL
        # C# code uses: EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"
        if not esb_url.endswith('/'):
            esb_url += '/'
        return esb_url + 'services/TSGDataAccessServiceEBS_V1'
    
    def _setup_certificate_auth(self) -> Optional[Session]:
        """
        Setup certificate authentication for SOAP client
        
        Returns:
            Configured requests Session with certificate authentication
        """
        try:
            # Get certificate from Azure Key Vault
            self.certificate_bytes = AzureKeyVaultHelper.get_vault_certificate(self.logger)
            
            if not self.certificate_bytes:
                raise ValueError("Failed to retrieve certificate from Key Vault")
            
            # Convert certificate to format suitable for requests
            self.cert_files = AzureKeyVaultHelper.get_certificate_for_requests(
                self.certificate_bytes,
                logger=self.logger
            )
            
            if not self.cert_files:
                raise ValueError("Failed to convert certificate for authentication")
            
            # Create session with certificate authentication
            session = Session()
            session.cert = self.cert_files  # (cert_file, key_file) tuple
            session.verify = True  # Verify server certificate
            
            self.logger.info("Certificate authentication configured successfully")
            
            return session
            
        except Exception as e:
            error = f"Error setting up certificate authentication: {str(e)}"
            self.logger.error(error)
            return None
    
    def initialize(self) -> bool:
        """
        Initialize SOAP client with certificate authentication
        Uses local WSDL file instead of fetching from remote URL since
        the WSDL endpoint (esbdev.techsoup.org) is not accessible at runtime.
        
        Returns:
            True if successful, False otherwise
        """
        try:
            # Setup certificate authentication
            session = self._setup_certificate_auth()
            
            if not session:
                return False
            
            # Create transport with certificate-authenticated session
            transport = Transport(session=session)
            
            # Get local WSDL file path
            local_wsdl_path = self._get_local_wsdl_path()
            self.logger.info(f"Loading WSDL from local file: {local_wsdl_path}")
            
            # Create SOAP client using local WSDL file
            settings = Settings(strict=False, xml_huge_tree=True)
            self.client = Client(
                wsdl=local_wsdl_path,
                transport=transport,
                settings=settings
            )
            
            # Set service endpoint to use the runtime endpoint (esbdevca.techsoup.org)
            # This overrides any endpoint URL that may be in the WSDL
            self.client.service._binding_options['address'] = self.endpoint_url
            
            self.logger.info(
                f"SOAP client initialized successfully. Endpoint: {self.endpoint_url}"
            )
            
            return True
            
        except Exception as e:
            error = f"Error initializing SOAP client: {str(e)}"
            self.logger.error(error)
            return False
    
    def execute_stored_proc(
        self,
        request: ExecuteStoredProcRequest
    ) -> Optional[ExecuteStoredProcResponse]:
        """
        Execute a stored procedure via SOAP
        
        Args:
            request: ExecuteStoredProcRequest with server, database, and SP info
            
        Returns:
            ExecuteStoredProcResponse with results or None on error
        """
        try:
            # Initialize client if not already done
            if not self.client:
                if not self.initialize():
                    return ExecuteStoredProcResponse(
                        return_xml=[],
                        success=False,
                        error="Failed to initialize SOAP client"
                    )
            
            # Build SOAP request
            # The WSDL defines ExecuteStoredProcRequest element with ServerName, DBName, SPName, params
            soap_request = {
                'ServerName': request.server_name,
                'DBName': request.db_name,
                'SPName': request.sp_name
            }
            
            # Add parameters if provided
            # C# code creates XmlElement[] for params.Any property
            if request.parameters:
                from lxml import etree
                
                # Create parameter elements
                param_elements = []
                for param_name, param_value in request.parameters.items():
                    # Create XML element for each parameter
                    elem = etree.Element(param_name)
                    elem.text = str(param_value)
                    param_elements.append(elem)
                
                # Add to SOAP request
                # The WSDL expects a 'params' element with inputParamsType
                # zeep uses _value_1 for xs:any elements (internal property name)
                if param_elements:
                    # Create inputParamsType object using zeep factory
                    input_params_type = self.client.get_type(
                        '{http://techsoupglobal.org/DataAccessService/}inputParamsType'
                    )
                    
                    # Set the _value_1 property (zeep's internal name for Any[])
                    params_obj = input_params_type(_value_1=param_elements)
                    soap_request['params'] = params_obj
                
                self.logger.info(
                    f"Added {len(param_elements)} parameter(s) to request"
                )
            
            self.logger.info(
                f"Executing stored procedure: {request.sp_name} on {request.server_name}.{request.db_name}"
            )
            
            # Call SOAP service
            response = self.client.service.ExecuteStoredProc(**soap_request)
            
            # Parse response
            # The C# code expects: ExecuteStoredProcResponseType with ReturnXml (rowType[])
            # Each row has an Any[] property containing XML elements
            return_xml = []
            
            self.logger.debug(f"SOAP Response type: {type(response)}")
            self.logger.debug(f"SOAP Response: {response}")
            
            if hasattr(response, 'ReturnXml') and response.ReturnXml:
                self.logger.debug(f"ReturnXml type: {type(response.ReturnXml)}")
                self.logger.debug(f"ReturnXml content: {response.ReturnXml}")
                
                # zeep returns ArrayOfRowType with 'row' attribute
                rows = None
                if hasattr(response.ReturnXml, 'row'):
                    rows = response.ReturnXml.row
                elif isinstance(response.ReturnXml, list):
                    rows = response.ReturnXml
                
                if rows:
                    for row in rows:
                        row_data = {}
                        
                        self.logger.debug(f"Row type: {type(row)}")
                        self.logger.debug(f"Row content: {row}")
                        
                        # zeep stores elements in _value_1
                        if hasattr(row, '_value_1') and row._value_1:
                            elements = row._value_1
                            self.logger.debug(f"Elements in _value_1: {elements}")
                            
                            for element in elements:
                                if hasattr(element, 'text'):
                                    # Get element tag name (without namespace)
                                    tag = element.tag.split('}')[-1] if '}' in element.tag else element.tag
                                    row_data[tag] = element.text
                                    self.logger.debug(f"Extracted {tag} = {element.text}")
                        
                        if row_data:
                            return_xml.append(row_data)
            
            self.logger.info(
                f"Stored procedure executed successfully. Rows returned: {len(return_xml)}"
            )
            
            return ExecuteStoredProcResponse(
                return_xml=return_xml,
                success=True
            )
            
        except Exception as e:
            error = f"Error executing stored procedure: {str(e)}"
            self.logger.error(error)
            
            return ExecuteStoredProcResponse(
                return_xml=[],
                success=False,
                error=error
            )
    
    def get_netsuite_nonce(self) -> Optional[str]:
        """
        Get NetSuite nonce from database via SOAP call
        
        This is the Python equivalent of EDServicesHelper.getNetSuiteNonce()
        
        Returns:
            Nonce string or None on error
        """
        try:
            # Get SQL server name from environment
            sql_server = os.environ.get('ts_Sql2k14Server')
            
            if not sql_server:
                raise ValueError("ts_Sql2k14Server environment variable is required")
            
            # Create request
            request = ExecuteStoredProcRequest(
                server_name=sql_server,
                db_name="ServiceAdmin",
                sp_name="usp_GetNetSuiteNonce"
            )
            
            # Execute stored procedure
            response = self.execute_stored_proc(request)
            
            if not response or not response.success:
                error = response.error if response else "Unknown error"
                raise ValueError(f"Failed to execute stored procedure: {error}")
            
            # Extract nonce from response
            # C# code: returnXml.First().Any[0].InnerText
            if response.return_xml and len(response.return_xml) > 0:
                first_row = response.return_xml[0]
                
                # Get first value from the row
                if first_row:
                    # Try different possible keys
                    nonce = (first_row.get('Nonce') or 
                            first_row.get('nonce') or 
                            first_row.get('value') or
                            next(iter(first_row.values()), None))
                    
                    if nonce:
                        self.logger.info(f"getNetSuiteNonce - nonce: {nonce}")
                        return nonce
            
            raise ValueError("No nonce returned from stored procedure")
            
        except Exception as e:
            error = f"Error in get_netsuite_nonce: {str(e)}"
            self.logger.error(error)
            return None
    
    def __del__(self):
        """Cleanup temporary certificate files"""
        if self.cert_files:
            import os
            try:
                cert_path, key_path = self.cert_files
                if os.path.exists(cert_path):
                    os.unlink(cert_path)
                if os.path.exists(key_path):
                    os.unlink(key_path)
            except:
                pass
