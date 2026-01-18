
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
using System.Text;
using System.Security.Cryptography;
using System.Net.NetworkInformation;
using System.Collections;

namespace AccountServices
{
     
    public class RetrieveMultipleExternalData : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;
        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));

            try
            {
                if (!context.SharedVariables.Contains("MustHaveOpenOrder"))
                    return;



                AccountServicesHelper.writeToTrace("Starting - AccountServices.RetrieveMultipleExternalData"
                                                        , tracingService);


                

                bool mustHaveOpenOrder = false;
                if (context.SharedVariables.Contains("MustHaveOpenOrder"))
                {
                    mustHaveOpenOrder = (bool)context.SharedVariables["MustHaveOpenOrder"];

                    
                    AccountServicesHelper.writeToTrace("Contains musthaveorder. Value: " + mustHaveOpenOrder.ToString()
                                                            , tracingService);
                }


                if (!context.OutputParameters.Contains("BusinessEntityCollection"))
                    return;


                if (!(context.OutputParameters["BusinessEntityCollection"] is EntityCollection))
                    return;




                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);


                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                EntityCollection queryResult = (EntityCollection)context.OutputParameters["BusinessEntityCollection"];




                Entity firstRec = queryResult.Entities.First();

                if (
                        !(
                        context.SharedVariables.Contains("AccountNumberAlias") && firstRec.Contains((string)context.SharedVariables["AccountNumberAlias"])
                        )
                    )
                { return; }




                string tsOrgIdAlias = (string)context.SharedVariables["AccountNumberAlias"];







                List<string> tsOrgIdList = new List<string>();
                foreach (Entity resultItem in queryResult.Entities)
                {

                    AliasedValue tsOrgIdAliasValue = resultItem.GetAttributeValue<AliasedValue>(tsOrgIdAlias);

                    if (tsOrgIdAliasValue != null)
                    {
                        string tsOrgId = tsOrgIdAliasValue.Value.ToString();
                        tsOrgIdList.Add(tsOrgId);

                    }
                }


                string tsOrgIds = String.Join(",", tsOrgIdList.ToArray());

                AccountServicesHelper.writeToTrace("Result count: " + queryResult.TotalRecordCount.ToString()
                                                        + "\n" + "tsOrgIds: " + tsOrgIds
                                                        + "\n" + "queryResult.Entities.ToList().Count(): " + queryResult.Entities.ToList().Count()
                                                        , tracingService);



                Dictionary<string, bool> orgsWithOrders = getOrgsAndOrdersFlag(tsOrgIds
                                                                                , service, tracingService);



                if (orgsWithOrders == null)
                    return;



                AccountServicesHelper.writeToTrace("right before doing ueryResult.Entities.ToList().Where..."                                                        
                                                        , tracingService);



                var queryResultWithOrders = queryResult.Entities.ToList().Where(item => orgsWithOrders[item.GetAttributeValue<AliasedValue>(tsOrgIdAlias).Value.ToString()]);



                AccountServicesHelper.writeToTrace("queryResultWithOrders.Count(): " + queryResultWithOrders.Count().ToString()
                                                        , tracingService);
                



                EntityCollection orgsWithOrdersCollection = new EntityCollection(queryResultWithOrders.ToList());

                orgsWithOrdersCollection.EntityName = queryResult.EntityName;

                context.OutputParameters["BusinessEntityCollection"] = orgsWithOrdersCollection;

                //foreach (Entity resultItem in queryResult.Entities.ToList())
                //{
                //    AliasedValue tsOrgIdAliasValue = resultItem.GetAttributeValue<AliasedValue>(tsOrgIdAlias);

                //    if (tsOrgIdAliasValue != null)
                //    {
                //        string tsOrgId = tsOrgIdAliasValue.Value.ToString();

                //        if (!orgsWithOrders[tsOrgId])
                //            queryResult.Entities.Remove(resultItem);


                //        //tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "tsOrgId: " + tsOrgId
                //        //    + "\n" + "hasOrders: " + orgsWithOrders[tsOrgId]
                //        //    );
                //        //tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                //    }
                //}


            }

            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error during AccountServices.RetrieveMultipleExternalData: " + e.Message
                                                        , tracingService);
            }

        }







        public static Dictionary<string, bool> getOrgsAndOrdersFlag(string tsOrgIds
                                                                        , IOrganizationService service, ITracingService tracingService)
        {
            Dictionary<string, bool> orgsWithOrders = null;
            try
            {

                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;
                binding.MaxReceivedMessageSize = 52428800;
                binding.MaxBufferSize = 52428800;
                binding.MaxBufferPoolSize = 52428800;

                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2k14Server"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_orgsWithOrders";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("tsOrgIds");
                param1.InnerText = tsOrgIds;
                elements.Add(param1);


                objRequest.@params.Any = elements.ToArray();


                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                orgsWithOrders = new Dictionary<string, bool>();

                foreach (rowType row in returnXml)
                {
                    orgsWithOrders.Add(row.Any[0].InnerText, bool.Parse(row.Any[1].InnerText));

                }


            }
            catch (Exception e)
            {
                string error = "Error in getOrgsAndOrdersFlag(...). Exception message: "
                                    + Environment.NewLine + e.Message;
                
                AccountServicesHelper.writeToTrace(error , tracingService);
            }

            return orgsWithOrders;
        }

    }
}