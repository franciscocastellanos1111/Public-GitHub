
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
     
    public class AccountRefCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));            



            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Starting - AccountServices.AccountRefCreateUpdate");
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));



            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                Entity targetEntity = null;

                targetEntity = (Entity)context.InputParameters["Target"];

                if (targetEntity.LogicalName != "ts_accountreference")
                    return;


                Entity accountRefEntity = service.Retrieve("ts_accountreference", targetEntity.Id, new ColumnSet(true));
                string referenceType = accountRefEntity.FormattedValues["ts_referencetype"];
                string referenceValue = accountRefEntity.GetAttributeValue<string>("ts_referencevalue");

                Guid accountId = accountRefEntity.GetAttributeValue<EntityReference>("ts_accountid").Id;
                Entity account = service.Retrieve("account", accountId, new ColumnSet("accountnumber"));
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "tsOrgId: " + tsOrgId + "; referenceType: " + referenceType + "; referenceValue: " + referenceValue
                    );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));


                updateOnyxcOrgCisco(accountRefEntity, tsOrgId, referenceType, referenceValue
                    , service, tracingService);


                //if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                //{ }
                //if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                //{
                //}
            }

            catch (Exception e)
            {

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error during AccountServices.AccountRefCreateUpdate: " + e.Message);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }

        }

        public static void updateOnyxcOrgCisco(Entity accountRefEntity, string tsOrgId, string referenceType, string referenceValue
           , IOrganizationService service, ITracingService tracingService)
        {
            try
            {

                Guid modifiedBy = accountRefEntity.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;



                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;


                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2kServer"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_dynamicsOrgCiscoUpdate";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("TSOrgId");
                param1.InnerText = tsOrgId;
                elements.Add(param1);

                param1 = doc.CreateElement("referenceType");
                param1.InnerText = referenceType;
                elements.Add(param1);

                param1 = doc.CreateElement("referenceValue");
                param1.InnerText = referenceValue;
                elements.Add(param1);
                

                objRequest.@params.Any = elements.ToArray();


                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "updateOnyxcOrgCisco - before call to esb"
                    );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "updateOnyxcOrgCisco(...) resultStatus: " + resultStatus);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));


                if (resultStatus.ToLower() != "success")
                {
                    string error = "Error in updateOnyxcOrgCisco(...). resultStatus: " + resultStatus;
                    AccountServicesHelper.createDynamicsoOnyxIntegrationLog(accountRefEntity.LogicalName, accountRefEntity.Id.ToString()
                    , error, service, tracingService);
                }


            }
            catch (Exception e)
            {
                string error = "Error in updateOnyxcOrgCisco(...). Exception message: "
                    + Environment.NewLine + e.Message
                    ;
                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + error
                    );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
                AccountServicesHelper.createDynamicsoOnyxIntegrationLog(accountRefEntity.LogicalName, accountRefEntity.Id.ToString()
                    , error, service, tracingService);
            }

        }

    }
}