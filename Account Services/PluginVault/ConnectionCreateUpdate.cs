
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
     
    public class ConnectionCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));            



            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Starting - AccountServices.ConnectionCreateUpdate");
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));



            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                Entity targetEntity = null;

                targetEntity = (Entity)context.InputParameters["Target"];

                if (targetEntity.LogicalName != "connection")
                    return;


                Entity connectionEntity = service.Retrieve("connection", targetEntity.Id, new ColumnSet(true));

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "connectionId: " + connectionEntity.Id.ToString());
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));


                updateOnyxcConnection(connectionEntity
                        , service, tracingService);


                //if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                //{ }
                //if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                //{ }
            }

            catch (Exception e)
            {

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error during AccountServices.ConnectionCreateUpdate: " + e.Message);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }

        }

        public static void updateOnyxcConnection(Entity connectionEntity
                                                                        , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                Guid modifiedBy = connectionEntity.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;


                int stateCode = connectionEntity.GetAttributeValue<OptionSetValue>("statecode").Value;
                int tiRecordStatus = stateCode == 0 ? 1 : 0;

                EntityReference connectionFromRef = connectionEntity.GetAttributeValue<EntityReference>("record1id");

                string iOwnerID = string.Empty;
                string contactFromEntity = string.Empty;
                string iCategoryId = string.Empty;
                if (connectionFromRef.LogicalName == "account")
                {
                    contactFromEntity = "account";
                    Entity account = service.Retrieve("account", connectionFromRef.Id, new ColumnSet("accountnumber", "ts_accountdirectives"));
                    iOwnerID = account.GetAttributeValue<string>("accountnumber");
                    iCategoryId = "2";

                    OptionSetValueCollection accountDirectivesCollection = account.GetAttributeValue<OptionSetValueCollection>("ts_accountdirectives");

                    bool excludeDataIntegration = accountDirectivesCollection != null && accountDirectivesCollection.Any(option => option.Value == 1); //1 - ExcludeDataIntegration

                    if (excludeDataIntegration)
                    {
                        AccountServicesHelper.writeToTrace("At updateOnyxcConnection - Account directives contain 'ExcludeDataIntegration'. Skipping Onyx integration"
                                                                                                                                                                  , tracingService);
                        return;
                    }

                }
                else if (connectionFromRef.LogicalName == "contact")
                {
                    contactFromEntity = "contact";
                    Entity contact = service.Retrieve("contact", connectionFromRef.Id, new ColumnSet("new_contactaccountnumber"));
                    iOwnerID = contact.GetAttributeValue<string>("new_contactaccountnumber");
                    iCategoryId = "1";
                }
                else
                {
                    return;
                }

                EntityReference connectionToRef = connectionEntity.GetAttributeValue<EntityReference>("record2id");
                string iContactID = string.Empty;
                string contactToEntity = string.Empty;
                //string iCategoryIdTo = string.Empty;
                if (connectionToRef.LogicalName == "account")
                {
                    contactToEntity = "account";
                    Entity account = service.Retrieve("account", connectionToRef.Id, new ColumnSet("accountnumber", "ts_accountdirectives"));
                    iContactID = account.GetAttributeValue<string>("accountnumber");
                    //iCategoryIdTo = "2";

                    OptionSetValueCollection accountDirectivesCollection = account.GetAttributeValue<OptionSetValueCollection>("ts_accountdirectives");

                    bool excludeDataIntegration = accountDirectivesCollection != null && accountDirectivesCollection.Any(option => option.Value == 1); //1 - ExcludeDataIntegration

                    if (excludeDataIntegration)
                    {
                        AccountServicesHelper.writeToTrace("At updateOnyxcConnection - Account directives contain 'ExcludeDataIntegration'. Skipping Onyx integration"
                                                                                                                                                                  , tracingService);
                        return;
                    }
                }
                else if (connectionToRef.LogicalName == "contact")
                {
                    contactToEntity = "contact";
                    Entity contact = service.Retrieve("contact", connectionToRef.Id, new ColumnSet("new_contactaccountnumber"));
                    iContactID = contact.GetAttributeValue<string>("new_contactaccountnumber");
                    //iCategoryIdTo = "1";
                }
                else
                {
                    return;
                }




                /*
                EntityReference connectionRoleFromRef = connectionEntity.GetAttributeValue<EntityReference>("record1roleid");

                if (connectionRoleFromRef == null && contactFromEntity == "account")
                {

                    QueryExpression queryConnectionFromRole = new QueryExpression("connectionrole");
                    queryConnectionFromRole.Criteria.AddCondition("name", ConditionOperator.Equal, "Employer");
                    EntityCollection connectionRoleFromCollection = service.RetrieveMultiple(queryConnectionFromRole);


                    if (connectionRoleFromCollection.Entities.Count > 0)
                    {
                        Guid employerConnectionRoleId = connectionRoleFromCollection.Entities.First().Id;
                        connectionEntity["record1roleid"] = new EntityReference("connectionrole", employerConnectionRoleId);

                        service.Update(connectionEntity);

                        connectionEntity = service.Retrieve(connectionEntity.LogicalName, connectionEntity.Id, new ColumnSet(true));
                    }
                }

                */





                Guid connectionRoleFromId = connectionEntity.GetAttributeValue<EntityReference>("record1roleid").Id;
                Entity connectionRoleFrom = service.Retrieve("connectionrole", connectionRoleFromId, new ColumnSet("name"));
                string contTypeFrom = connectionRoleFrom.GetAttributeValue<string>("name");

                Guid connectionRoleToId = connectionEntity.GetAttributeValue<EntityReference>("record2roleid").Id;
                Entity connectionRoleTo = service.Retrieve("connectionrole", connectionRoleToId, new ColumnSet("name"));
                string contTypeTo = connectionRoleTo.GetAttributeValue<string>("name");


                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "updateOnyxcConnection(...)"
                    + Environment.NewLine + "iOwnerID: " + iOwnerID + "; tiRecordStatus: " + tiRecordStatus.ToString() + "; iCategoryId: " + iCategoryId + "; iContactID: " + iContactID + "; contTypeTo: " + contTypeTo
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
                objRequest.SPName = "usp_dynamicsContactUpdate";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("iOwnerID");
                param1.InnerText = iOwnerID;
                elements.Add(param1);

                param1 = doc.CreateElement("tiRecordStatus");
                param1.InnerText = tiRecordStatus.ToString();
                elements.Add(param1);

                param1 = doc.CreateElement("iCategoryId");
                param1.InnerText = iCategoryId;
                elements.Add(param1);

                param1 = doc.CreateElement("iContactID");
                param1.InnerText = iContactID;
                elements.Add(param1);

                param1 = doc.CreateElement("contTypeTo");
                param1.InnerText = contTypeTo;
                elements.Add(param1);


                objRequest.@params.Any = elements.ToArray();
                

                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;


                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "updateOnyxcConnection(...) resultStatus: " + resultStatus);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                if (resultStatus.ToLower() != "success")
                {
                    string error = "Error in updateOnyxcConnection(...). resultStatus: " + resultStatus;
                    AccountServicesHelper.createDynamicsoOnyxIntegrationLog(connectionEntity.LogicalName, connectionEntity.Id.ToString(), error
                    , service, tracingService);
                }


            }
            catch (Exception e)
            {
                string error = "Error in updateOnyxcConnection(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "connectionId: " + connectionEntity.Id.ToString()
                    ;

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + error
                    );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                AccountServicesHelper.createDynamicsoOnyxIntegrationLog(connectionEntity.LogicalName, connectionEntity.Id.ToString()
                    , error, service, tracingService);
            }

        }

    }
}