"""
Azure Key Vault Helper
Python implementation of the GetVaultCertificate functionality from C# EDServicesHelper
"""

import os
import base64
import logging
from typing import Optional, Dict
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.backends import default_backend
from cryptography import x509
from azure.keyvault.certificates import CertificateClient
from azure.identity import ClientSecretCredential


class AzureKeyVaultHelper:
    """
    Helper class for retrieving certificates from Azure Key Vault
    Port of the C# GetVaultCertificate method
    """
    
    @staticmethod
    def get_vault_certificate(logger: Optional[logging.Logger] = None) -> Optional[bytes]:
        """
        Retrieve X.509 certificate from Azure Key Vault
        
        This method:
        1. Authenticates to Azure using client credentials (MSAL)
        2. Connects to Azure Key Vault
        3. Retrieves the certificate secret
        4. Returns the certificate as bytes (PKCS#12 format)
        
        Args:
            logger: Optional logger instance for logging
        
        Returns:
            Certificate bytes in PKCS#12 format, or None if error
        """
        try:
            import requests
            from azure.identity import ClientSecretCredential
            
            if not logger:
                logger = logging.getLogger(__name__)
            
            # Get configuration from environment variables
            tenant_id = os.environ.get('ts_AzureTenantId')
            client_id = os.environ.get('ts_DynamicsESBIntegrationClientId')
            client_secret = os.environ.get('ts_DynamicsESBIntegrationClientSecret')
            vault_secret_url = os.environ.get('ts_VaultESBSecretUrl')
            
            if not all([tenant_id, client_id, client_secret, vault_secret_url]):
                raise ValueError("Missing required Azure Key Vault configuration")
            
            # Create credential using client secret (equivalent to MSAL ConfidentialClientApplication)
            credential = ClientSecretCredential(
                tenant_id=tenant_id,
                client_id=client_id,
                client_secret=client_secret
            )
            
            # Get access token for Key Vault
            token = credential.get_token("https://vault.azure.net/.default")
            
            # Use the direct secret URL to get the certificate
            # This matches the C# implementation which uses the secret URL directly
            headers = {
                'Authorization': f'Bearer {token.token}',
                'Content-Type': 'application/json'
            }
            
            logger.info(
                f"Retrieving certificate from: {vault_secret_url}"
            )
            
            response = requests.get(vault_secret_url, headers=headers)
            
            if response.status_code != 200:
                raise ValueError(f"Failed to retrieve certificate: HTTP {response.status_code} - {response.text}")
            
            # Parse JSON response
            secret_data = response.json()
            cert_secret = secret_data.get('value')
            
            if not cert_secret:
                raise ValueError("No certificate value in response")
            
            # Decode base64 secret value to get certificate bytes
            certificate_bytes = base64.b64decode(cert_secret)
            
            logger.info(
                f"Successfully retrieved certificate from Key Vault ({len(certificate_bytes)} bytes)"
            )
            
            return certificate_bytes
            
        except Exception as e:
            error = f"Error in get_vault_certificate: {str(e)}"
            logger.error(error)
            return None
    
    @staticmethod
    def get_certificate_for_requests(certificate_bytes: bytes, password: Optional[str] = None, logger: Optional[logging.Logger] = None) -> Optional[tuple]:
        """
        Convert certificate bytes to format suitable for requests library
        
        Args:
            certificate_bytes: Certificate in PKCS#12 format
            password: Optional password for the certificate
            logger: Optional logger instance for logging
            
        Returns:
            Tuple of (cert_file_path, key_file_path) or None
        """
        try:
            from cryptography.hazmat.primitives.serialization import pkcs12
            import tempfile
            
            if not logger:
                logger = logging.getLogger(__name__)
            
            # Load PKCS#12 certificate
            password_bytes = password.encode() if password else None
            private_key, certificate, additional_certificates = pkcs12.load_key_and_certificates(
                certificate_bytes,
                password_bytes,
                backend=default_backend()
            )
            
            if not private_key or not certificate:
                raise ValueError("Failed to load private key or certificate from PKCS#12")
            
            # Create temporary files for cert and key
            cert_fd, cert_path = tempfile.mkstemp(suffix='.pem')
            key_fd, key_path = tempfile.mkstemp(suffix='.pem')
            
            try:
                # Write certificate to file
                with os.fdopen(cert_fd, 'wb') as cert_file:
                    cert_pem = certificate.public_bytes(serialization.Encoding.PEM)
                    cert_file.write(cert_pem)
                
                # Write private key to file
                with os.fdopen(key_fd, 'wb') as key_file:
                    key_pem = private_key.private_bytes(
                        encoding=serialization.Encoding.PEM,
                        format=serialization.PrivateFormat.TraditionalOpenSSL,
                        encryption_algorithm=serialization.NoEncryption()
                    )
                    key_file.write(key_pem)
                
                return (cert_path, key_path)
                
            except Exception as e:
                # Clean up files on error
                if os.path.exists(cert_path):
                    os.unlink(cert_path)
                if os.path.exists(key_path):
                    os.unlink(key_path)
                raise e
                
        except Exception as e:
            error = f"Error converting certificate for requests: {str(e)}"
            logger.error(error)
            return None
    
    @staticmethod
    def load_certificate_from_bytes(certificate_bytes: bytes, password: Optional[str] = None, logger: Optional[logging.Logger] = None):
        """
        Load a PKCS#12 certificate from bytes
        
        Args:
            certificate_bytes: Certificate in PKCS#12 format
            password: Optional password for the certificate
            logger: Optional logger instance for logging
            
        Returns:
            Tuple of (private_key, certificate, additional_certificates)
        """
        try:
            from cryptography.hazmat.primitives.serialization import pkcs12
            
            if not logger:
                logger = logging.getLogger(__name__)
            
            password_bytes = password.encode() if password else None
            private_key, certificate, additional_certificates = pkcs12.load_key_and_certificates(
                certificate_bytes,
                password_bytes,
                backend=default_backend()
            )
            
            return private_key, certificate, additional_certificates
            
        except Exception as e:
            error = f"Error loading certificate from bytes: {str(e)}"
            logger.error(error)
            return None, None, None
