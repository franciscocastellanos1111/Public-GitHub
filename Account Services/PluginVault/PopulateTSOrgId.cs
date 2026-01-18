
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

namespace AccountServices
{
     
    public class PopulateTSOrgId : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));


            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Starting - AccountServices.PopulateTSOrgId");
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                /****************************************************/

                //if (context.InputParameters.Contains("Target") &&
                //        context.InputParameters["Target"] is Entity)

                /****************************************************************/

                Entity accountEntity = null;


                accountEntity = (Entity)context.InputParameters["Target"];

                if (accountEntity.LogicalName != "account")
                    return;
                if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                {

                    Entity account = service.Retrieve("account", accountEntity.Id, new ColumnSet("accountnumber"));
                    string tsOrgId = account.GetAttributeValue<string>("accountnumber");


                    tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "tsOrgId PostOperation: " + tsOrgId);
                    tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                    if (string.IsNullOrEmpty(tsOrgId))
                    {
                        X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                        var binding = new BasicHttpsBinding();
                        binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                        binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;

                        DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                        dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                        ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();

                        objRequest.ServerName = EnvVariables["ts_Sql2kServer"];
                        objRequest.DBName = "ServiceAdmin";
                        objRequest.SPName = "usp_getNextOnyxId";



                        ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                        rowType[] returnXml = dataAccessresponse.ReturnXml;

                        string newAccountNumber = returnXml.First().Any[0].InnerText;

                        tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "New TSOrgId: " + newAccountNumber);
                        tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));


                        account["accountnumber"] = newAccountNumber;
                        service.Update(account);
                    }
                }



            }

            catch (Exception e)
            {

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error during AccountServices.PopulateTSOrgId: " + e.Message);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }

        }

    }
}