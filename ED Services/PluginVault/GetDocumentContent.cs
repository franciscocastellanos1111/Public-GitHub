
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
using System.Threading;
using System.Collections.Generic;
using System.Collections;
using static System.Net.WebRequestMethods;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using System.Net.NetworkInformation;

//using System.Security.Cryptography.X509Certificates;
//using PluginVault.DataAccessService;
//using System.Xml;

namespace EDServices
{
     
    public class GetDocumentContent : IPlugin
    {
        static Dictionary<string, string> EnvVariables;
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        public void Execute(IServiceProvider serviceProvider)
        {

            


            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));

            
            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Starting - EDServices.GetDocumentContent");       
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

            
            try
            {
                /**********GET Custom API Request Parameters*********/
                string documentId = (string)context.InputParameters["ts_documentid"];
                /****************************************************/

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "ts_documentid: " + documentId);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));



                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);

                EnvVariables = getEnvironmentVariables(service, tracingService);

                RetrieveCurrentOrganizationRequest request = new RetrieveCurrentOrganizationRequest();
                RetrieveCurrentOrganizationResponse response = (RetrieveCurrentOrganizationResponse)service.Execute(request);


                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "envURL: " + response.Detail.UrlName);
                //tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "envId: " + response.Detail.EnvironmentId);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                string docURL = $"https://{response.Detail.UrlName}.crm.dynamics.com/api/data/v9.2/msdyn_entityattachments({documentId})/msdyn_fileblob/$value";


                /*******************************************************************/

                string resource = "https://" + response.Detail.UrlName + ".crm.dynamics.com";
                string clientId = EnvVariables["ts_TSDynamicsClientId"];
                string clientSecret = EnvVariables["ts_TSDynamicsClientSecret"];

                IConfidentialClientApplication authBuilder = ConfidentialClientApplicationBuilder
                    .Create(clientId)
                    .WithClientSecret(clientSecret)
                    .Build();

                AuthenticationResult authResult = authBuilder
                    .AcquireTokenForClient(scopes: new[] { resource + "/.default" })
                    .WithTenantId("d8ba2331-6b05-4303-9a60-36c58c3e272d")
                    .ExecuteAsync().Result;


                HttpClient client = new HttpClient();

                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.AccessToken);
                headers.Add("OData-MaxVersion", "4.0");
                headers.Add("OData-Version", "4.0");
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));


                byte[] fileBytes = client.GetByteArrayAsync(docURL).Result;

                string fileString = Convert.ToBase64String(fileBytes);

                /**********ASSIGN Custom API Response Parameters*********/                
                context.OutputParameters["ts_documentid"] = documentId;
                context.OutputParameters["ts_documentcontent"] = fileString;
                /********************************************************/


            }

            catch (Exception e)
            {

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error during EDServices.GetDocumentContent: " + e.Message);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }

        }


        public static Dictionary<string, string> getEnvironmentVariables(IOrganizationService service, ITracingService tracingService)
        {

            Dictionary<string, string> envVariables = new Dictionary<string, string>();

            QueryExpression query = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("statecode", "defaultvalue", "valueschema",
                  "schemaname", "environmentvariabledefinitionid", "type"),
                LinkEntities =
                        {
                            new LinkEntity
                            {
                                JoinOperator = JoinOperator.LeftOuter,
                                LinkFromEntityName = "environmentvariabledefinition",
                                LinkFromAttributeName = "environmentvariabledefinitionid",
                                LinkToEntityName = "environmentvariablevalue",
                                LinkToAttributeName = "environmentvariabledefinitionid",
                                Columns = new ColumnSet("statecode", "value", "environmentvariablevalueid"),
                                EntityAlias = "v"
                            }
                        }
            };

            EntityCollection results = service.RetrieveMultiple(query);


            if (results?.Entities.Count > 0)
            {
                foreach (Entity entity in results.Entities)
                {
                    string schemaName = entity.GetAttributeValue<string>("schemaname");
                    string value = entity.GetAttributeValue<AliasedValue>("v.value")?.Value?.ToString();
                    string defaultValue = entity.GetAttributeValue<string>("defaultvalue");

                    if (schemaName != null && !envVariables.ContainsKey(schemaName))
                        envVariables.Add(schemaName, string.IsNullOrEmpty(value) ? defaultValue : value);
                }
            }

            return envVariables;


        }


    }
}