
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
     
    public class AddressCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));            



            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Starting - AccountServices.AddressCreateUpdate");
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));



            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                Entity targetEntity = null;

                targetEntity = (Entity)context.InputParameters["Target"];

                if (targetEntity.LogicalName != "customeraddress")
                    return;

                Entity address = service.Retrieve("customeraddress", targetEntity.Id, new ColumnSet(true));


                updateOnyxLegalAddress(address
                    , service, tracingService);


                //if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                //{ }
                //if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                //{
                //}
            }

            catch (Exception e)
            {

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error during AccountServices.AddressCreateUpdate: " + e.Message);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }

        }

        public static void updateOnyxLegalAddress(Entity address
                                                                , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                OptionSetValue addressTypeOption = address.GetAttributeValue<OptionSetValue>("addresstypecode");

                int addressType = addressTypeOption == null ? 0 : addressTypeOption.Value;

                if (addressType != 5)
                    return;

                Guid modifiedBy = address.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;


                EntityReference parentRef = address.GetAttributeValue<EntityReference>("parentid");

                if (parentRef.LogicalName != "account")
                    return;


                Entity account = service.Retrieve("account", parentRef.Id, new ColumnSet("accountnumber", "ts_accountdirectives"));
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");


                OptionSetValueCollection accountDirectivesCollection = account.GetAttributeValue<OptionSetValueCollection>("ts_accountdirectives");

                bool excludeDataIntegration = accountDirectivesCollection != null && accountDirectivesCollection.Any(option => option.Value == 1); //1 - ExcludeDataIntegration

                if (excludeDataIntegration)
                {
                    AccountServicesHelper.writeToTrace("At updateOnyxLegalAddress - Account directives contain 'ExcludeDataIntegration'. Skipping Onyx integration"
                                                                                                                                                              , tracingService);
                    return;
                }


                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "updateOnyxLegalAddress(...). tsOrgId: " + tsOrgId
                    );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));


                string countryCode = address.GetAttributeValue<string>("country");
                string regionCode = address.GetAttributeValue<string>("stateorprovince");
                string address1 = address.GetAttributeValue<string>("line1");
                string address2 = address.GetAttributeValue<string>("line2");
                string address3 = address.GetAttributeValue<string>("line3");
                string city = address.GetAttributeValue<string>("city");
                string postCode = address.GetAttributeValue<string>("postalcode");


                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;


                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2kServer"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_dynamicsLegalAddressUpdate";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("TSOrgId");
                param1.InnerText = tsOrgId;
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

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "updateOnyxLegalAddress(...) resultStatus: " + resultStatus);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));


                if (resultStatus.ToLower() != "success")
                {
                    string error = "Error in updateOnyxLegalAddress(...). resultStatus: " + resultStatus;
                    AccountServicesHelper.createDynamicsoOnyxIntegrationLog(address.LogicalName, address.Id.ToString()
                    , error, service, tracingService);
                }


            }
            catch (Exception e)
            {
                string error = "Error in updateOnyxLegalAddress(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "addressId: " + address.Id.ToString()
                    ;
                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + error
                    );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
                AccountServicesHelper.createDynamicsoOnyxIntegrationLog(address.LogicalName, address.Id.ToString()
                    , error, service, tracingService);
            }

        }

    }
}