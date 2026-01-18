
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.ServiceModel;


using Microsoft.Identity.Client;
using System.Net.Http;
using System.Net.Http.Headers;


using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using System.Security.Cryptography.X509Certificates;
using AccountServices.DataAccessService;
using System.Xml;
using System.Collections.Generic;
using System.Security.Principal;
using AccountServices.orderService;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AccountServices
{
     
    public class ContactCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));            



            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Starting - AccountServices.ContactCreateUpdate");
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));



            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                Entity targetEntity = null;

                targetEntity = (Entity)context.InputParameters["Target"];

                if (targetEntity.LogicalName != "contact")
                    return;


                Entity contact = service.Retrieve("contact", targetEntity.Id, new ColumnSet(true));
                string tsContactId = contact.GetAttributeValue<string>("new_contactaccountnumber");

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "tsContactId: " + tsContactId
                    );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));



                int sourceId = contact.GetAttributeValue<OptionSetValue>("new_source")?.Value ?? 0;
                if (sourceId == 105000) //105000 - ValidationRequest
                {
                    updateOnyxValidationRequestAgent(contact
                                                                , service, tracingService);
                    return;
                }
                else
                {
                    if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                    {
                        updateOnyxGeneralContact(contact
                                                                , service, tracingService);
                    }
                }
               
                if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    updateOnyxContact(contact
                                            , service, tracingService);
                }
            }

            catch (Exception e)
            {

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error during AccountServices.ContactCreateUpdate: " + e.Message);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }

        }

        public static void updateOnyxContact(Entity contact
                                                    , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                Guid modifiedBy = contact.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;

                


                string TSContactId = contact.GetAttributeValue<string>("new_contactaccountnumber");

                string emailValidationStatus = string.Empty;
                if (contact.Contains("ts_emailvalidationstatus"))
                {
                    emailValidationStatus = contact.FormattedValues["ts_emailvalidationstatus"];
                    if (emailValidationStatus != "Valid" && emailValidationStatus != "Invalid")
                        emailValidationStatus = "Not Validated";
                }                

                string ctpVerificationCode =  contact.GetAttributeValue<string>("new_ctpverificationcode");

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "updateOnyxcContact(...)"
                     + Environment.NewLine + "TSContactId: " + TSContactId + "; ctpVerificationCode: " + ctpVerificationCode + "; emailValidationStatus: " + emailValidationStatus
                     );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;


                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2kServer"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_dynamicsIndividualUpdate";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("TSContactId");
                param1.InnerText = TSContactId;
                elements.Add(param1);

                param1 = doc.CreateElement("ctpVerificationCode");
                param1.InnerText = ctpVerificationCode;
                elements.Add(param1);

                param1 = doc.CreateElement("emailValidationStatus");
                param1.InnerText = emailValidationStatus;
                elements.Add(param1);
                

                objRequest.@params.Any = elements.ToArray();


                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "updateOnyxcContact(...) resultStatus: " + resultStatus);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));


                if (resultStatus.ToLower() != "success")
                {
                    string error = "Error in updateOnyxcContact(...). resultStatus: " + resultStatus;
                    AccountServicesHelper.createDynamicsoOnyxIntegrationLog(contact.LogicalName, contact.Id.ToString()
                    , error, service, tracingService);
                }

            }
            catch (Exception e)
            {
                string error = "Error in updateOnyxcContact(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "contactId: " + contact.Id.ToString()
                    ;
                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + error
                    );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                AccountServicesHelper.createDynamicsoOnyxIntegrationLog(contact.LogicalName, contact.Id.ToString()
                    , error, service, tracingService);
            }

        }
        public static void updateOnyxValidationRequestAgent(Entity contact
                                                                    , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                Guid modifiedBy = contact.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;


                int souceId = contact.GetAttributeValue<OptionSetValue>("new_source")?.Value ?? 0;

                string tsContactId = contact.GetAttributeValue<string>("new_contactaccountnumber");

                string emailValidationStatus = string.Empty;
                if (contact.Contains("ts_emailvalidationstatus"))
                {
                    emailValidationStatus = contact.FormattedValues["ts_emailvalidationstatus"];
                    if (emailValidationStatus != "Valid" && emailValidationStatus != "Invalid")
                        emailValidationStatus = "Not Validated";
                }

                

                string vchFirstName = contact.GetAttributeValue<string>("firstname");
                string vchLastName = contact.GetAttributeValue<string>("lastname");
                string vchEmailAddress = contact.GetAttributeValue<string>("emailaddress1");

                AccountServicesHelper.writeToTrace("updateOnyxValidationRequestAgent. "
                                                        + Environment.NewLine + "tsContactId: " + tsContactId.ToString()
                                                        + Environment.NewLine + "; vchFirstName: " + vchFirstName
                                                        + Environment.NewLine + "; vchLastName: " + vchLastName
                                                        + Environment.NewLine + "; vchEmailAddresse: " + vchEmailAddress
                                                        + Environment.NewLine + "; souceId: " + souceId.ToString()
                                                        + Environment.NewLine + "; emailValidationStatus: " + emailValidationStatus
                                                        , tracingService);
                                                        

                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;


                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2kServer"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_updateOnyxValidationRequestAgent";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("tsContactId");
                param1.InnerText = tsContactId;
                elements.Add(param1);

                param1 = doc.CreateElement("vchFirstName");
                param1.InnerText = vchFirstName;
                elements.Add(param1);

                param1 = doc.CreateElement("vchLastName");
                param1.InnerText = vchLastName;
                elements.Add(param1);

                param1 = doc.CreateElement("vchEmailAddress");
                param1.InnerText = vchEmailAddress;
                elements.Add(param1);

                param1 = doc.CreateElement("iSourceId");
                param1.InnerText = souceId.ToString();
                elements.Add(param1);

                param1 = doc.CreateElement("emailValidationStatus");
                param1.InnerText = emailValidationStatus;
                elements.Add(param1);


                objRequest.@params.Any = elements.ToArray();


                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                
                AccountServicesHelper.writeToTrace("updateOnyxValidationRequestAgent. resultStatus: " + resultStatus
                                                                                                            , tracingService);

                if (resultStatus.ToLower() != "success")
                {
                    string error = "Error in updateOnyxValidationRequestAgent(...). resultStatus: " + resultStatus;
                    AccountServicesHelper.createDynamicsoOnyxIntegrationLog(contact.LogicalName, contact.Id.ToString()
                    , error, service, tracingService);
                }

            }
            catch (Exception e)
            {
                string error = "Error in updateOnyxValidationRequestAgent(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "contactId: " + contact.Id.ToString()
                    ;
                AccountServicesHelper.writeToTrace(error
                                                        , tracingService);

                AccountServicesHelper.createDynamicsoOnyxIntegrationLog(contact.LogicalName, contact.Id.ToString()
                                                                                                                , error, service, tracingService);
            }

        }


        public static void updateOnyxGeneralContact(Entity contact
                                                                    , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                AccountServicesHelper.writeToTrace("Starting updateOnyxGeneralContact"
                                                                                    , tracingService);
                Guid modifiedBy = contact.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string modifiedByFullName = user.GetAttributeValue<string>("fullname");

                if (modifiedByFullName.Contains("TSDynamicsOnyx"))
                {
                    AccountServicesHelper.writeToTrace("updateOnyxGeneralContact. modifiedByFullName: " + modifiedByFullName + ". Bypassing method"
                                                                                                                                                , tracingService);
                    return;
                }

                int souceId = contact.GetAttributeValue<OptionSetValue>("new_source")?.Value ?? 0;

                string tsContactId = contact.GetAttributeValue<string>("new_contactaccountnumber");

                string emailValidationStatus = string.Empty;
                if (contact.Contains("ts_emailvalidationstatus"))
                {
                    emailValidationStatus = contact.FormattedValues["ts_emailvalidationstatus"];
                    if (emailValidationStatus != "Valid" && emailValidationStatus != "Invalid")
                        emailValidationStatus = "Not Validated";
                }



                string vchFirstName = contact.GetAttributeValue<string>("firstname");
                string vchLastName = contact.GetAttributeValue<string>("lastname");
                string vchEmailAddress = contact.GetAttributeValue<string>("emailaddress1");



                string userName = contact.GetAttributeValue<string>("adx_identity_username");


                string countryCode = contact.GetAttributeValue<string>("address1_country");
                string regionCode = contact.GetAttributeValue<string>("address1_stateorprovince");
                string address1 = contact.GetAttributeValue<string>("address1_line1");
                string address2 = contact.GetAttributeValue<string>("address1_line2");
                string address3 = contact.GetAttributeValue<string>("address1_line3");
                string city = contact.GetAttributeValue<string>("address1_city");
                string postCode = contact.GetAttributeValue<string>("address1_postalcode");


                AccountServicesHelper.writeToTrace("updateOnyxGeneralContact:"
                                                        + Environment.NewLine + "tsContactId: " + tsContactId.ToString()
                                                        + Environment.NewLine + "; vchFirstName: " + vchFirstName
                                                        + Environment.NewLine + "; vchLastName: " + vchLastName
                                                        + Environment.NewLine + "; vchEmailAddresse: " + vchEmailAddress
                                                        + Environment.NewLine + "; souceId: " + souceId.ToString()
                                                        + Environment.NewLine + "; emailValidationStatus: " + emailValidationStatus
                                                        + Environment.NewLine + "; userName: " + userName
                                                        + Environment.NewLine + "; countryCode: " + countryCode
                                                        + Environment.NewLine + "; regionCode: " + regionCode
                                                        + Environment.NewLine + "; address1: " + address1
                                                        + Environment.NewLine + "; address2: " + address2
                                                        + Environment.NewLine + "; address3: " + address3
                                                        + Environment.NewLine + "; city: " + city
                                                        + Environment.NewLine + "; postCode: " + postCode
                                                        , tracingService);


                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;


                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2kServer"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_updateOnyxGeneralContact";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("tsContactId");
                param1.InnerText = tsContactId;
                elements.Add(param1);

                param1 = doc.CreateElement("vchFirstName");
                param1.InnerText = vchFirstName;
                elements.Add(param1);

                param1 = doc.CreateElement("vchLastName");
                param1.InnerText = vchLastName;
                elements.Add(param1);

                param1 = doc.CreateElement("vchEmailAddress");
                param1.InnerText = vchEmailAddress;
                elements.Add(param1);

                param1 = doc.CreateElement("iSourceId");
                param1.InnerText = souceId.ToString();
                elements.Add(param1);

                param1 = doc.CreateElement("emailValidationStatus");
                param1.InnerText = emailValidationStatus;
                elements.Add(param1);

                param1 = doc.CreateElement("userName");
                param1.InnerText = userName;
                elements.Add(param1);

                param1 = doc.CreateElement("countryCode");
                param1.InnerText = countryCode;
                elements.Add(param1);

                param1 = doc.CreateElement("regionCode");
                param1.InnerText = regionCode;
                elements.Add(param1);

                param1 = doc.CreateElement("address1");
                param1.InnerText = address1;
                elements.Add(param1);

                param1 = doc.CreateElement("address2");
                param1.InnerText = address2;
                elements.Add(param1);

                param1 = doc.CreateElement("address3");
                param1.InnerText = address3;
                elements.Add(param1);

                param1 = doc.CreateElement("city");
                param1.InnerText = city;
                elements.Add(param1);

                param1 = doc.CreateElement("postCode");
                param1.InnerText = postCode;
                elements.Add(param1);

                objRequest.@params.Any = elements.ToArray();


                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;


                AccountServicesHelper.writeToTrace("updateOnyxGeneralContact. resultStatus: " + resultStatus
                                                                                                            , tracingService);

                if (resultStatus.ToLower() != "success")
                {
                    string error = "Error in updateOnyxGeneralContact(...). resultStatus: " + resultStatus;
                    AccountServicesHelper.createDynamicsoOnyxIntegrationLog(contact.LogicalName, contact.Id.ToString()
                    , error, service, tracingService);
                }

            }
            catch (Exception e)
            {
                string error = "Error in updateOnyxGeneralContact(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "contactId: " + contact.Id.ToString()
                    ;
                AccountServicesHelper.writeToTrace(error
                                                        , tracingService);

                AccountServicesHelper.createDynamicsoOnyxIntegrationLog(contact.LogicalName, contact.Id.ToString()
                                                                                                                , error, service, tracingService);
            }

        }

    }
}