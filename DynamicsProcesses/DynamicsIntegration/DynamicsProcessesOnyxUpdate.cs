using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


using DataverseClientLib = Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Crm.Sdk.Messages;
using System.IO;
using System.Configuration;
using Microsoft.Identity.Client;
using System.Security.Principal;
using System.Xml.Linq;
using DynamicsProcesses.DataAccessService;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.Xml;
using System.Runtime.Remoting.Services;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace DynamicsProcesses
{
    internal class DynamicsProcessesOnyxUpdate
    {

        private static string InitialTries = ConfigurationManager.AppSettings["InitialTries"];
        private static string TryIncrement = ConfigurationManager.AppSettings["TryIncrement"];

        static Dictionary<string, string> EnvVariables;

        public class LowerUpperBound
        {
            public int initialTries { get; set; }
            public int tier { get; set; }
            public int tryIncrement { get; set; }
            public int lowerBound
            {
                get
                { return initialTries + (tryIncrement * (tier - 1)); }

            }
            public int upperBound
            {
                get
                { return initialTries + (tryIncrement * tier); }
            }
        }
        public static void dynamicsToOnyxRetry()
        {
            try
            {
                //DynamicsInterface.writeToLog("Org Reload: Execution Started");

                QueryExpression queryDynOnyxLog = new QueryExpression("ts_dynamicsonyxintegrationlog");
                queryDynOnyxLog.ColumnSet = new ColumnSet(true);
                queryDynOnyxLog.Criteria.AddCondition("ts_name", ConditionOperator.In, new object[] { "ts_SendEmail", "dynamicstoonyx", "orgdesignationchange"}); 
                //queryDynOnyxLog.Criteria.AddCondition("modifiedon", ConditionOperator.LessEqual, endDateUTC);  
                queryDynOnyxLog.AddOrder("modifiedon", OrderType.Descending);
                EntityCollection dynOnyxIntLogCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryDynOnyxLog);

                foreach (Entity dynOnyxIntLog in dynOnyxIntLogCollection.Entities)
                {   

                    string entityName = dynOnyxIntLog.GetAttributeValue<string>("ts_entityname");
                    string entityId = dynOnyxIntLog.GetAttributeValue<string>("ts_entityid");                    
                    DateTime modifiedOnUtc = dynOnyxIntLog.GetAttributeValue<DateTime>("modifiedon");
                    //DateTime modifiedOn = TimeZoneInfo.ConvertTimeFromUtc(modifiedOnUtc, DynamicsInterface.pstZone);
                    string name = dynOnyxIntLog.GetAttributeValue<string>("ts_name");


                    TimeSpan timeSinceLastUpdate = DateTime.UtcNow - modifiedOnUtc;

                    LowerUpperBound lowerUpperBoud = new LowerUpperBound();

                    switch (name)
                    {
                        case "dynamicstoonyx":
                            lowerUpperBoud.initialTries = int.Parse(InitialTries);
                            break;
                        case "ts_SendEmail":
                            lowerUpperBoud.initialTries = int.Parse(InitialTries);
                            break;
                        default:
                            resolveAction(name, dynOnyxIntLog);
                            break;
                    }


                    if (name == "dynamicstoonyx" || name == "ts_SendEmail")
                    {
                        DynamicsInterface.errorStack = new List<string>();

                        int attemptCount = dynOnyxIntLog.GetAttributeValue<int>("ts_currentattemptcount");
                        attemptCount++;


                        lowerUpperBoud.tryIncrement = int.Parse(TryIncrement);

                        lowerUpperBoud.tier = 0;
                        if (attemptCount <= lowerUpperBoud.upperBound)
                            processRetry(dynOnyxIntLog, entityName, entityId);

                        lowerUpperBoud.tier = 1;
                        if (attemptCount > lowerUpperBoud.lowerBound && attemptCount <= lowerUpperBoud.upperBound && timeSinceLastUpdate.TotalMinutes > 5)
                            processRetry(dynOnyxIntLog, entityName, entityId);

                        lowerUpperBoud.tier = 2;
                        if (attemptCount > lowerUpperBoud.lowerBound && attemptCount <= lowerUpperBoud.upperBound && timeSinceLastUpdate.TotalMinutes > 30)
                            processRetry(dynOnyxIntLog, entityName, entityId);

                        lowerUpperBoud.tier = 3;
                        if (attemptCount > lowerUpperBoud.lowerBound && attemptCount <= lowerUpperBoud.upperBound && timeSinceLastUpdate.TotalMinutes > 60)
                            processRetry(dynOnyxIntLog, entityName, entityId);

                        lowerUpperBoud.tier = 4;
                        if (attemptCount > lowerUpperBoud.lowerBound && timeSinceLastUpdate.TotalHours >= 24)
                            processRetry(dynOnyxIntLog, entityName, entityId);

                        /*Checking for errorStack should only be if there is a processRetry; so moved it within processRetry() method
                         * 
                        if (DynamicsInterface.errorStack.Count > 0)
                        {
                            DynamicsProcessesHelper.updateDynamicsoOnyxIntegrationLog(dynOnyxIntLog);
                        }
                        else
                        {
                            DynamicsInterface.DataverseClient.Delete("ts_dynamicsonyxintegrationlog", dynOnyxIntLog.Id);
                        }
                         * 
                        */

                    }
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in dynamicsToOnyxRetry(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static void resolveAction(string name, Entity systemIntLog)
        {
            try
            {
                DynamicsInterface.errorStack = new List<string>();


                bool okToProcess = false;

                switch (name)
                {
                    case "orgdesignationchange":

                        okToProcess = evaluateConfigurationApprovalAction(systemIntLog, "RetryConfiguration");

                        if (okToProcess)
                            orgsWithMultipleQualOrgs();

                        break;
                }





                if (okToProcess)
                {
                    if (DynamicsInterface.errorStack.Count > 0)
                    {
                        DynamicsProcessesHelper.updateDynamicsoOnyxIntegrationLog(systemIntLog);
                    }
                    else
                    {
                        DynamicsInterface.DataverseClient.Delete("ts_dynamicsonyxintegrationlog", systemIntLog.Id);
                    }
                }


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in resolveAction(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static bool evaluateConfigurationApprovalAction(Entity systemIntLog, string configurationValue)
        {
            bool okToProcess = false;
            try
            {
                //"RetryConfiguration"
                QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryMapping.ColumnSet = new ColumnSet(true);
                queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "DynamicsSystemProcesses");
                queryMapping.Criteria.AddCondition("ts_value", ConditionOperator.Equal, configurationValue);
                EntityCollection mappingCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryMapping);

                if (mappingCollection.Entities.Count() == 0)
                {
                    DynamicsInterface.writeToLog("Error in postOrgDesignationChange(...). ts_fieldname = 'DynamicsSystemProcesses' was not found in ts_fieldhierarchyandmapping"

                    );
                    return false;
                }
                Entity fieldMap = mappingCollection.Entities.First();

                string segmentationDefinitionText = fieldMap.GetAttributeValue<string>("ts_valuedescription");


                okToProcess = getProcessingApproval(segmentationDefinitionText, systemIntLog);

                
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in postOrgDesignationChange(). Exception message: " + Environment.NewLine + e.Message);
            }

            return okToProcess;
        }

        public static void postOrgDesignationChange(Entity systemIntLog)
        {
            try
            {
                
                QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryMapping.ColumnSet = new ColumnSet(true);
                queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "DynamicsSystemProcesses");
                queryMapping.Criteria.AddCondition("ts_value", ConditionOperator.Equal, "RetryConfiguration");
                EntityCollection mappingCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryMapping);

                if (mappingCollection.Entities.Count() == 0)
                {
                    DynamicsInterface.writeToLog("Error in postOrgDesignationChange(...). ts_fieldname = 'DynamicsSystemProcesses' was not found in ts_fieldhierarchyandmapping"
                    );
                    return;
                }
                Entity fieldMap = mappingCollection.Entities.First();

                string segmentationDefinitionText = fieldMap.GetAttributeValue<string>("ts_valuedescription");


                bool okToProcess = getProcessingApproval(segmentationDefinitionText, systemIntLog);

                if (okToProcess)
                {
                    orgsWithMultipleQualOrgs();

                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in postOrgDesignationChange(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static bool getProcessingApproval(string segmentationDefinitionText, Entity systemIntLog)
        {
            bool okToProcess = false;

            try
            {
                int currentCount = systemIntLog.GetAttributeValue<int>("ts_currentattemptcount");

                dynamic segmentationDefinition = JsonConvert.DeserializeObject(segmentationDefinitionText);

                int topCount = segmentationDefinition.config.maximumCount;


                if (currentCount > topCount && topCount != -1)
                {
                    DynamicsInterface.DataverseClient.Delete(systemIntLog.LogicalName, systemIntLog.Id);
                    return false;
                }


                DateTime modifiedOnUtc = systemIntLog.GetAttributeValue<DateTime>("modifiedon");
                TimeSpan timeSinceLastUpdate = DateTime.UtcNow - modifiedOnUtc;
                double minutesSinceLastProcessing = timeSinceLastUpdate.TotalMinutes;




                var segmentationDefinitionEntity = JsonConvert.DeserializeObject<System.Dynamic.ExpandoObject>(segmentationDefinitionText) as IDictionary<string, Object>;

                var segments = segmentationDefinitionEntity.Where(item => item.Key.StartsWith("segment"));

                KeyValuePair<string, object> topLowerBoundSeg = new KeyValuePair<string, object>();

                foreach (KeyValuePair<string, object> segment in segments)
                {
                    dynamic segValue = segment.Value;
                    if (currentCount >= segValue.lowerBoundCount)
                    {
                        if (topLowerBoundSeg.Key == null)
                        {
                            topLowerBoundSeg = segment;
                        }
                        else
                        {
                            if (segValue.lowerBoundCount >= ((dynamic)topLowerBoundSeg.Value).lowerBoundCount)
                                topLowerBoundSeg = segment;
                        }
                    }
                }

                if (minutesSinceLastProcessing >= ((dynamic)topLowerBoundSeg.Value).minuteInterval)
                    okToProcess = true;


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getProcessingApproval(). Exception message: " + Environment.NewLine + e.Message

                    );
            }
            return okToProcess;
        }

        public static void orgsWithMultipleQualOrgs()
        {
            try
            {

                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification")
                {
                    ColumnSet = new ColumnSet(false),
                    LinkEntities =
                        {
                            new LinkEntity
                            {
                                JoinOperator = JoinOperator.Inner,
                                LinkFromEntityName = "ts_organizationqualification",
                                LinkFromAttributeName = "ts_qualificationcodeid",
                                LinkToEntityName = "new_qualificationcode",
                                LinkToAttributeName = "new_qualificationcodeid",
                                Columns = new ColumnSet(false),
                                EntityAlias = "qualcode"
                            }
                            ,new LinkEntity
                            {
                                JoinOperator = JoinOperator.Inner,
                                LinkFromEntityName = "ts_organizationqualification",
                                LinkFromAttributeName = "ts_accountid",
                                LinkToEntityName = "account",
                                LinkToAttributeName = "accountid",
                                Columns = new ColumnSet(false),
                                EntityAlias = "acc"
                            }
                        }
                };
                XrmAttributeExpression attributeExp = new XrmAttributeExpression(
                         attributeName: "ts_accountid",
                         alias: "count",
                         aggregateType: XrmAggregateType.Count
                         );

                queryOrgQualification.ColumnSet.AttributeExpressions.Add(attributeExp);


                attributeExp = new XrmAttributeExpression();

                attributeExp.AttributeName = "ts_accountid";
                attributeExp.Alias = "accountid";
                attributeExp.AggregateType = XrmAggregateType.None;
                attributeExp.HasGroupBy = true;

                queryOrgQualification.ColumnSet.AttributeExpressions.Add(attributeExp);

                queryOrgQualification.Orders.Add(new OrderExpression(
                attributeName: "ts_accountid",
                alias: "count",
                orderType: OrderType.Descending));


                //queryOrgQualification.Criteria.AddCondition("modifiedon", ConditionOperator.GreaterEqual, DateTime.UtcNow.AddDays(-3));

                queryOrgQualification.LinkEntities.First().LinkCriteria.AddCondition("new_qualcategory", ConditionOperator.Equal, "QualOrg");

                queryOrgQualification.LinkEntities[1].LinkCriteria.AddCondition("modifiedon", ConditionOperator.GreaterEqual, DateTime.UtcNow.AddDays(-3));

                queryOrgQualification.TopCount = 50;


                EntityCollection orgQualificationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryOrgQualification);

                foreach (Entity orgQual in orgQualificationCollection.Entities)
                {
                    AliasedValue countAlias = orgQual.GetAttributeValue<AliasedValue>("count");
                    int count = countAlias == null ? 0 : (int)countAlias.Value;

                    if (count <= 1)
                        break;

                    AliasedValue accountIdAlias = orgQual.GetAttributeValue<AliasedValue>("accountid");

                    EntityReference accountRef = accountIdAlias == null ? null : (EntityReference)accountIdAlias.Value;

                    Guid accountId = accountRef == null ? Guid.Empty : accountRef.Id;


                    if (accountId != Guid.Empty && count > 1)
                        cancelAccountNonDesignationOrgQuals(accountId);

                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in postOrgDesignationChange(). Exception message: " + Environment.NewLine + e.Message);
            }
        }





        public static void cancelAccountNonDesignationOrgQuals(Guid accountId)
        {
            try
            {

                Entity account = DynamicsInterface.DataverseClient.Retrieve("account", accountId, new ColumnSet(true));

                string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesig = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;


                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification")
                {
                    ColumnSet = new ColumnSet(true),
                    LinkEntities =
                        {
                            new LinkEntity
                            {
                                JoinOperator = JoinOperator.Inner,
                                LinkFromEntityName = "ts_organizationqualification",
                                LinkFromAttributeName = "ts_qualificationcodeid",
                                LinkToEntityName = "new_qualificationcode",
                                LinkToAttributeName = "new_qualificationcodeid",
                                Columns = new ColumnSet(true),
                                EntityAlias = "qualcode"
                            }
                        }
                };


                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.NotEqual, orgDesig);
                queryOrgQualification.LinkEntities.First().LinkCriteria.AddCondition("new_qualcategory", ConditionOperator.Equal, "QualOrg");


                EntityCollection orgQualificationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryOrgQualification);

                foreach (Entity orgQual in orgQualificationCollection.Entities)
                {

                    string orgQualStatus = orgQual.Contains("ts_qualificationstatus") ? orgQual.FormattedValues["ts_qualificationstatus"] : string.Empty;

                    if (orgQualStatus != "Canceled")
                    {
                        int qualStatusCode = 13;//13 - Canceled
                        orgQual["ts_qualificationstatus"] = new OptionSetValue(qualStatusCode);
                        orgQual["ts_qualificationstatusdate"] = DateTime.UtcNow;
                        DynamicsInterface.DataverseClient.Update(orgQual);

                        Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");
                        orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQual.Id);
                        orgQualHistory["ts_qualificationstatusaction"] = new OptionSetValue(qualStatusCode);
                        orgQualHistory["ts_qualificationactiondate"] = DateTime.UtcNow;

                        orgQualHistory["ts_name"] = orgQual.Id.ToString() + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                        DynamicsInterface.DataverseClient.Create(orgQualHistory);

                    }

                    EntityReference qualCodeRef = orgQual.GetAttributeValue<EntityReference>("ts_qualificationcodeid");

                    Guid qualCodeId = qualCodeRef == null ? Guid.Empty : qualCodeRef.Id;

                    //102074 - OQ - Cancelled
                    updateQualCaseStatus(account.Id, qualCodeId, 102074
                        );

                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in cancelAccountNonDesignationOrgQuals(). Exception message: " + Environment.NewLine + e.Message);
            }
        }


        public static void updateQualCaseStatus(Guid accountId, Guid qualCodeId, int caseStatus)
        {
            try
            {
                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet("ts_casestatus");

                queryQualCase.Criteria.AddCondition("casetypecode", ConditionOperator.Equal, 2);
                queryQualCase.Criteria.AddCondition("ts_type", ConditionOperator.Equal, 101996); //101996 - Organization Qualification
                queryQualCase.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                queryQualCase.Criteria.AddCondition("accountid", ConditionOperator.Equal, accountId);
                EntityCollection qualCaseCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQualCase);

                foreach (Entity caseEntity in qualCaseCollection.Entities)
                {
                    int currentCaseStatus = caseEntity.GetAttributeValue<OptionSetValue>("ts_casestatus").Value;

                    if (currentCaseStatus != caseStatus)
                    {
                        caseEntity["ts_casestatus"] = new OptionSetValue(caseStatus);

                        DynamicsInterface.DataverseClient.Update(caseEntity);
                    }
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateQualCaseStatus(). Exception message: " + Environment.NewLine + e.Message);
            }

        }

        

        public static void processRetry(Entity dynOnyxIntLog, string entityName, string entityId)
        {
            try
            {
                EnvVariables = DynamicsProcessesHelper.GetEnvironmentVariables();

                switch (entityName)
                {
                    case "account":
                        updateOnyxOrg(Guid.Parse(entityId));
                        break;

                    case "ts_organizationqualification":
                        updateOnyxOrgQualification(Guid.Parse(entityId));
                        break;

                    case "connection":
                        updateOnyxcConnection(Guid.Parse(entityId));
                        break;

                    case "contact":
                        updateOnyxcContact(Guid.Parse(entityId));
                        break;

                    case "ts_accountreference":
                        updateOnyxcOrgCisco(Guid.Parse(entityId));
                        break;

                    case "customeraddress":
                        updateOnyxLegalAddress(Guid.Parse(entityId));
                        break;
                }

                string name = dynOnyxIntLog.GetAttributeValue<string>("ts_name");
                switch (name)
                {
                    case "ts_SendEmail":
                        tsSendEmail(dynOnyxIntLog);
                        break;

                }


                if (DynamicsInterface.errorStack.Count > 0)
                {
                    DynamicsProcessesHelper.updateDynamicsoOnyxIntegrationLog(dynOnyxIntLog);
                }
                else
                {
                    DynamicsInterface.DataverseClient.Delete("ts_dynamicsonyxintegrationlog", dynOnyxIntLog.Id);
                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processRetry(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static void tsSendEmail(Entity dynOnyxIntLog)
        {
            try
            {

                string log = dynOnyxIntLog.GetAttributeValue<string>("ts_log");

                Regex regexAddFromTemplToken = new Regex(@"###tsSendEmailRequest###.+###tsSendEmailRequest###");
                Match match = regexAddFromTemplToken.Match(log, 0);

                if (!match.Success)
                {
                    return;
                }

                string sendEmailRequest = match.Value.Replace("###tsSendEmailRequest###", "");

                RetrieveCurrentOrganizationRequest currentOrgrequest = new RetrieveCurrentOrganizationRequest();
                RetrieveCurrentOrganizationResponse currentOrgResponse = (RetrieveCurrentOrganizationResponse)DynamicsInterface.DataverseClient.Execute(currentOrgrequest);

                string resource = "https://" + currentOrgResponse.Detail.UrlName + ".crm.dynamics.com";
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

                

                string json = sendEmailRequest;
                StringContent req = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = client.PostAsync(resource + "/api/data/v9.2/ts_SendEmail", req).Result;
                string responseTxt = response.Content.ReadAsStringAsync().Result;


                JObject jObject = (JObject)JsonConvert.DeserializeObject(responseTxt);
                string resultStatus = jObject.SelectToken("['ts_resultstatus']").ToString();

                if (resultStatus.ToLower() != "success")
                {
                    string error = string.Empty;

                    if (jObject.SelectToken("['error']") != null)
                        error = jObject.SelectToken("['error']").ToString();

                    DynamicsInterface.writeToLog("Error calling ts_SendEmail. Error message: "
                        + Environment.NewLine + error
                        );

                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in tsSendEmail(...). Exception message: "
                    + Environment.NewLine + e.Message
                    );

            }

        }
        public static void updateOnyxLegalAddress(Guid addressId)
        {
            try
            {
                Entity address = DynamicsInterface.DataverseClient.Retrieve("customeraddress", addressId, new ColumnSet(true));

                OptionSetValue addressTypeOption = address.GetAttributeValue<OptionSetValue>("addresstypecode");

                int addressType = addressTypeOption == null ? 0 : addressTypeOption.Value;

                if (addressType != 5)
                    return;

                Guid modifiedBy = address.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = DynamicsInterface.DataverseClient.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;


                EntityReference parentRef = address.GetAttributeValue<EntityReference>("parentid");

                if (parentRef.LogicalName != "account")
                    return;


                Entity account = DynamicsInterface.DataverseClient.Retrieve("account", parentRef.Id, new ColumnSet("accountnumber"));
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");




                string countryCode = address.GetAttributeValue<string>("country");
                string regionCode = address.GetAttributeValue<string>("stateorprovince");
                string address1 = address.GetAttributeValue<string>("line1");
                string address2 = address.GetAttributeValue<string>("line2");
                string city = address.GetAttributeValue<string>("city");
                string postCode = address.GetAttributeValue<string>("postalcode");


                DataAccessServiceClient client = new DataAccessServiceClient();

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = DynamicsInterface.Sql2kServer;
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

                param1 = doc.CreateElement("city");
                param1.InnerText = city;
                elements.Add(param1);

                param1 = doc.CreateElement("postCode");
                param1.InnerText = postCode;
                elements.Add(param1);

                objRequest.@params.Any = elements.ToArray();


                ExecuteStoredProcResponseType dataAccessresponse = client.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                if (resultStatus.ToLower() != "success")
                    DynamicsInterface.writeToLog("Error in updateOnyxLegalAddress(...). resultStatus: " + resultStatus);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateOnyxLegalAddress(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "addressId: " + addressId.ToString()
                    );                
            }

        }
        public static void updateOnyxcOrgCisco(Guid accountRefEntityId)
        {
            try
            {
                Entity accountRefEntity = DynamicsInterface.DataverseClient.Retrieve("ts_accountreference", accountRefEntityId, new ColumnSet(true));


                Guid modifiedBy = accountRefEntity.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = DynamicsInterface.DataverseClient.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;

                string referenceType = accountRefEntity.FormattedValues["ts_referencetype"];
                string referenceValue = accountRefEntity.GetAttributeValue<string>("ts_referencevalue");

                Guid accountId = accountRefEntity.GetAttributeValue<EntityReference>("ts_accountid").Id;
                Entity account = DynamicsInterface.DataverseClient.Retrieve("account", accountId, new ColumnSet("accountnumber"));
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                DataAccessServiceClient client = new DataAccessServiceClient();

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = DynamicsInterface.Sql2kServer;
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


                ExecuteStoredProcResponseType dataAccessresponse = client.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                if (resultStatus.ToLower() != "success")
                    DynamicsInterface.writeToLog("Error in updateOnyxcOrgCisco(...). resultStatus: " + resultStatus);


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateOnyxcOrgCisco(...). Exception message: "
                    + Environment.NewLine + e.Message
                    );
                
            }

        }

        public static void updateOnyxcContact(Guid contactId)
        {
            try
            {
                Entity contact = DynamicsInterface.DataverseClient.Retrieve("contact", contactId, new ColumnSet(true));

                Guid modifiedBy = contact.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = DynamicsInterface.DataverseClient.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;

                string TSContactId = contact.GetAttributeValue<string>("new_contactaccountnumber");
                string emailValidationStatus = contact.FormattedValues["ts_emailvalidationstatus"];

                if (emailValidationStatus != "Valid" && emailValidationStatus != "Invalid")
                    emailValidationStatus = string.Empty;

                string ctpVerificationCode = contact.GetAttributeValue<string>("new_ctpverificationcode");


                DataAccessServiceClient client = new DataAccessServiceClient();

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = DynamicsInterface.Sql2kServer;
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


                ExecuteStoredProcResponseType dataAccessresponse = client.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                if (resultStatus.ToLower() != "success")
                    DynamicsInterface.writeToLog("Error in updateOnyxcContact(...). resultStatus: " + resultStatus);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateOnyxcContact(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "contactId: " + contactId.ToString()
                    );                
            }

        }
        public static void updateOnyxcConnection(Guid connectionId)
        {
            try
            {
                Entity connectionEntity = DynamicsInterface.DataverseClient.Retrieve("connection", connectionId, new ColumnSet(true));

                Guid modifiedBy = connectionEntity.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = DynamicsInterface.DataverseClient.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
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
                    Entity account = DynamicsInterface.DataverseClient.Retrieve("account", connectionFromRef.Id, new ColumnSet("accountnumber"));
                    iOwnerID = account.GetAttributeValue<string>("accountnumber");
                    iCategoryId = "2";
                }
                else if (connectionFromRef.LogicalName == "contact")
                {
                    contactFromEntity = "contact";
                    Entity contact = DynamicsInterface.DataverseClient.Retrieve("contact", connectionFromRef.Id, new ColumnSet("new_contactaccountnumber"));
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
                    Entity account = DynamicsInterface.DataverseClient.Retrieve("account", connectionToRef.Id, new ColumnSet("accountnumber"));
                    iContactID = account.GetAttributeValue<string>("accountnumber");
                    //iCategoryIdTo = "2";
                }
                else if (connectionToRef.LogicalName == "contact")
                {
                    contactToEntity = "contact";
                    Entity contact = DynamicsInterface.DataverseClient.Retrieve("contact", connectionToRef.Id, new ColumnSet("new_contactaccountnumber"));
                    iContactID = contact.GetAttributeValue<string>("new_contactaccountnumber");
                    //iCategoryIdTo = "1";
                }
                else
                {
                    return;
                }

                Guid connectionRoleFromId = connectionEntity.GetAttributeValue<EntityReference>("record1roleid").Id;
                Entity connectionRoleFrom = DynamicsInterface.DataverseClient.Retrieve("connectionrole", connectionRoleFromId, new ColumnSet("name"));
                string contTypeFrom = connectionRoleFrom.GetAttributeValue<string>("name");

                Guid connectionRoleToId = connectionEntity.GetAttributeValue<EntityReference>("record2roleid").Id;
                Entity connectionRoleTo = DynamicsInterface.DataverseClient.Retrieve("connectionrole", connectionRoleToId, new ColumnSet("name"));
                string contTypeTo = connectionRoleTo.GetAttributeValue<string>("name");


                DataAccessServiceClient client = new DataAccessServiceClient();


                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = DynamicsInterface.Sql2kServer;
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


                ExecuteStoredProcResponseType dataAccessresponse = client.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;


                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                if (resultStatus.ToLower() != "success")
                    DynamicsInterface.writeToLog("Error in updateOnyxcConnection(...). resultStatus: " + resultStatus);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateOnyxcConnection(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "connectionId: " + connectionId.ToString()
                    );

            }

        }

        public static void updateOnyxOrgQualification(Guid orgQualificationId)
        {
            try
            {
                Entity orgQualification = DynamicsInterface.DataverseClient.Retrieve("ts_organizationqualification", orgQualificationId, new ColumnSet(true));

                Guid modifiedBy = orgQualification.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = DynamicsInterface.DataverseClient.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;


                Guid qualCodeId = orgQualification.GetAttributeValue<EntityReference>("ts_qualificationcodeid").Id;

                Entity qualCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", qualCodeId, new ColumnSet(true));
                string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                string qualCatefory = qualCodeEntity.GetAttributeValue<string>("new_qualcategory");


                string qualStatus = orgQualification.FormattedValues["ts_qualificationstatus"];
                DateTime qualStatusDateUTC = orgQualification.GetAttributeValue<DateTime>("ts_qualificationstatusdate");
                DateTime qualStatusDate = TimeZoneInfo.ConvertTimeFromUtc(qualStatusDateUTC, DynamicsInterface.pstZone);

                Guid accountId = orgQualification.GetAttributeValue<EntityReference>("ts_accountid").Id;
                Entity account = DynamicsInterface.DataverseClient.Retrieve("account", accountId, new ColumnSet("accountnumber"));
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");


                DataAccessServiceClient client = new DataAccessServiceClient();

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();

                objRequest.ServerName = DynamicsInterface.Sql2kServer;
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_dynamicsOrgQualUpdate";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("TSOrgId");
                param1.InnerText = tsOrgId;
                elements.Add(param1);

                param1 = doc.CreateElement("qualCode");
                param1.InnerText = qualCode;
                elements.Add(param1);

                param1 = doc.CreateElement("qualStatus");
                param1.InnerText = qualStatus;
                elements.Add(param1);

                param1 = doc.CreateElement("qualStatusDate");
                param1.InnerText = qualStatusDate.ToString("yyyy-MM-ddTHH:mm:ss");
                elements.Add(param1);

                objRequest.@params.Any = elements.ToArray();

                ExecuteStoredProcResponseType dataAccessresponse = client.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                if (resultStatus.ToLower() != "success")
                    DynamicsInterface.writeToLog("Error in updateOnyxOrgQualification(...). resultStatus: " + resultStatus);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateOnyxOrgQualification(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "orgQualificationId: " + orgQualificationId.ToString());
            }
        }
        public static void updateOnyxOrg(Guid accountId)
        {
            try
            {
                Entity account = DynamicsInterface.DataverseClient.Retrieve("account", accountId, new ColumnSet(true));
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                /*Todo: validate tsOrgId has a value. Get tsCustomerid if tsOrgId is empty
                 If getting tsCustomerId fails, log it again
                 */

                        Guid modifiedBy = account.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = DynamicsInterface.DataverseClient.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;

                EntityReference orgDesigEntRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                string orgDesignation = string.Empty;
                if (orgDesigEntRef != null)
                {
                    Entity qualCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", orgDesigEntRef.Id, new ColumnSet("new_qualcode"));
                    orgDesignation = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                }

                string vchCompanyName = account.GetAttributeValue<string>("name");

                string customerTypeCode = string.Empty;
                if (account.Contains("customertypecode"))
                    customerTypeCode = account.FormattedValues["customertypecode"];

                OptionSetValue orgSourceOption = account.GetAttributeValue<OptionSetValue>("new_source");
                int orgSource = orgSourceOption == null ? 0 : orgSourceOption.Value;

                string countryCode = account.GetAttributeValue<string>("address1_country");
                string regionCode = account.GetAttributeValue<string>("address1_stateorprovince");
                string address1 = account.GetAttributeValue<string>("address1_line1");
                string address2 = account.GetAttributeValue<string>("address1_line2");
                string address3 = account.GetAttributeValue<string>("address1_line3");
                string city = account.GetAttributeValue<string>("address1_city");
                string postCode = account.GetAttributeValue<string>("address1_postalcode");
                string vchAssignedId = account.GetAttributeValue<string>("new_platformid");
                string vchEmailAddress = account.GetAttributeValue<string>("emailaddress1");
                string vchPhoneNumber = account.GetAttributeValue<string>("telephone1");
                string vchTaxId = account.GetAttributeValue<string>("new_legalidentifier");
                string vchURL = account.GetAttributeValue<string>("websiteurl");
                string budget = account.GetAttributeValue<string>("new_budget");
                string isEmailValidCode = account.GetAttributeValue<bool>("new_isemailvalid") ? "0" : "1";
                string associationCode = account.GetAttributeValue<string>("new_associationcode");

                EntityReference activityCodeRef = account.GetAttributeValue<EntityReference>("new_activitycode");
                string activityCode = string.Empty;
                if (activityCodeRef != null)
                {
                    Entity activityCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_activitycodes", activityCodeRef.Id, new ColumnSet("new_activitycode"));
                    activityCode = activityCodeEntity.GetAttributeValue<string>("new_activitycode");
                }


                EntityReference duplicateOfRef = account.GetAttributeValue<EntityReference>("ts_duplicateofid");
                string duplicateOf = string.Empty;
                if (duplicateOfRef != null)
                {
                    Entity accountDupeOf = DynamicsInterface.DataverseClient.Retrieve("account", duplicateOfRef.Id, new ColumnSet("accountnumber"));
                    duplicateOf = accountDupeOf.GetAttributeValue<string>("accountnumber");
                }


                int numberOfEmployees = account.GetAttributeValue<int>("numberofemployees");

                string partnerCode = string.Empty;
                EntityReference pngoRef = account.GetAttributeValue<EntityReference>("ts_orgppid");
                if (pngoRef != null)
                {
                    Entity pngoAccount = DynamicsInterface.DataverseClient.Retrieve("account", pngoRef.Id, new ColumnSet("ts_tspngoid", "ts_tspngocode", "accountnumber"));
                    partnerCode = pngoAccount.GetAttributeValue<string>("ts_tspngocode");
                }

                string orgRefId = account.GetAttributeValue<string>("ts_pporgid");



                DataAccessServiceClient client = new DataAccessServiceClient();


                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = DynamicsInterface.Sql2kServer;
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_dynamicsOrgUpdate";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("TSOrgId");
                param1.InnerText = tsOrgId;
                elements.Add(param1);

                param1 = doc.CreateElement("orgDesignation");
                param1.InnerText = orgDesignation;
                elements.Add(param1);

                param1 = doc.CreateElement("vchCompanyName");
                param1.InnerText = vchCompanyName;
                elements.Add(param1);

                param1 = doc.CreateElement("customerTypeCode");
                param1.InnerText = customerTypeCode;
                elements.Add(param1);

                param1 = doc.CreateElement("orgSource");
                param1.InnerText = orgSource.ToString();
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

                param1 = doc.CreateElement("vchAssignedId");
                param1.InnerText = vchAssignedId;
                elements.Add(param1);

                param1 = doc.CreateElement("vchEmailAddress");
                param1.InnerText = vchEmailAddress;
                elements.Add(param1);

                param1 = doc.CreateElement("vchPhoneNumber");
                param1.InnerText = vchPhoneNumber;
                elements.Add(param1);

                param1 = doc.CreateElement("vchTaxId");
                param1.InnerText = vchTaxId;
                elements.Add(param1);

                param1 = doc.CreateElement("vchURL");
                param1.InnerText = vchURL;
                elements.Add(param1);

                param1 = doc.CreateElement("budget");
                param1.InnerText = budget;
                elements.Add(param1);

                param1 = doc.CreateElement("isEmailValidCode");
                param1.InnerText = isEmailValidCode;
                elements.Add(param1);

                param1 = doc.CreateElement("associationCode");
                param1.InnerText = associationCode;
                elements.Add(param1);


                param1 = doc.CreateElement("activityCode");
                param1.InnerText = activityCode;
                XmlAttribute dataType = doc.CreateAttribute("datatype");
                dataType.Value = "ntext";
                param1.Attributes.Append(dataType);
                elements.Add(param1);

                param1 = doc.CreateElement("duplicateOf");
                param1.InnerText = duplicateOf;
                elements.Add(param1);

                param1 = doc.CreateElement("numberOfEmployees");
                param1.InnerText = numberOfEmployees.ToString();
                elements.Add(param1);

                if (!string.IsNullOrEmpty(partnerCode) && !string.IsNullOrEmpty(orgRefId))
                {
                    param1 = doc.CreateElement("partnerCode");
                    param1.InnerText = partnerCode;
                    elements.Add(param1);

                    param1 = doc.CreateElement("orgRefId");
                    param1.InnerText = orgRefId;
                    elements.Add(param1);

                }

                objRequest.@params.Any = elements.ToArray();

                ExecuteStoredProcResponseType dataAccessresponse = client.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;

                if (resultStatus.ToLower() != "success")
                    DynamicsInterface.writeToLog("Error in updateOnyxOrg(...). resultStatus: " + resultStatus);


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateOnyxOrg(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "accountId: " + accountId.ToString());

            }
        }
    }
}
