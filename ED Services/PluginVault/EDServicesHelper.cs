using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.UI;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Net.Http;

using EDServices.DataAccessService;
using System.ServiceModel;
using System.Xml;
using System.Net;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Dynamic;
using System.Security.Principal;
using Microsoft.Crm.Sdk.Messages;

namespace EDServices
{
    internal class EDServicesHelper
    {
        public static Dictionary<string, string> EnvVariables;
        public static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        //public static ITracingService TracingService = null;
        //public static IPluginExecutionContext Context = null;
        //public static IOrganizationService DynamicsService = null;


        public static Dictionary<string, string> DynamicsEnvironments = new Dictionary<string, string>()
        {
           { "dev" , "https://org90a61c80.crm.dynamics.com" },
            { "qa", "https://tsdynamicsqa.crm.dynamics.com"},
            { "stage" , "https://tsdynamicsstage.crm.dynamics.com" },
            { "prod" , "https://techsoup.crm.dynamics.com" },
            { "https://org90a61c80.crm.dynamics.com" , "dev" },
            { "https://tsdynamicsqa.crm.dynamics.com", "qa"},
            {  "https://tsdynamicsstage.crm.dynamics.com" , "stage"},
            { "https://techsoup.crm.dynamics.com" , "prod" }

        };

        public const string BoxClientId = "REDACTED";
        public const string BoxClientSecret = "REDACTED";

        public static Dictionary<string, string> GetEnvironmentVariables(IOrganizationService service)
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

        public static string getErrorsFromStack(List<string> errorStack)
        {
            string errorStackText = string.Empty;

            int i = 0;
            foreach (string errorEntry in errorStack)
            {
                i++;
                if (i > 1)
                    errorStackText += ". ";

                errorStackText += errorEntry;
            }

            return errorStackText;
        }

        public static X509Certificate2 GetVaultCertificate(Dictionary<string, string> envVariables
                                                                                                   , ITracingService tracingService)
        {
            X509Certificate2 cer = null;
            try
            {
                string resource = "https://dynamicsesbintegration.vault.azure.net";
                string clientId = envVariables["ts_DynamicsESBIntegrationClientId"];
                string clientSecret = envVariables["ts_DynamicsESBIntegrationClientSecret"];

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
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));



                HttpResponseMessage response = client.GetAsync(envVariables["ts_VaultESBSecretUrl"]).Result;

                string responseTxt = response.Content.ReadAsStringAsync().Result;


                JObject jObject = (JObject)JsonConvert.DeserializeObject(responseTxt);
                JToken keyVaultSecret = jObject.SelectToken("['value']");
                string cerSecret = keyVaultSecret.ToString();

                var privateKeyBytes = Convert.FromBase64String(cerSecret);
                cer = new X509Certificate2(privateKeyBytes);
            }
            catch (Exception e)
            {
                EDServicesHelper.writeToTrace("Error in GetVaultCertificate: " + e.Message
                                                                                        , tracingService);  
            }

            return cer;
        }


        public static void writeToTrace(string message, ITracingService tracingService)
        {
            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": "
                + "\n" + message
                    );
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
        }



        public static string getNetSuiteNonce(
                                                ITracingService tracingService
                                                , List<string> errorStack)
        {
            string nonce = string.Empty;
            try
            {
                X509Certificate2 cer = EDServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                BasicHttpsBinding binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;


                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2k14Server"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_GetNetSuiteNonce";
              

                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;
               
                if (returnXml.Length > 0)
                    nonce = returnXml.First().Any[0].InnerText;


                writeToTrace("getNetSuiteNonce - nonce: " + nonce, tracingService);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
            }

            return nonce;
        }

        public static string getOrgQualStatus(Entity account
                                                            , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            string orgQualStatus = string.Empty;
            try
            {

                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;

                if (orgDesigId == Guid.Empty)
                    return "";

                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, orgDesigId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, account.Id);
                EntityCollection orgQualificationCollection = service.RetrieveMultiple(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                {
                    Entity orgQualification = orgQualificationCollection.Entities.First();
                    orgQualStatus = orgQualification.FormattedValues["ts_qualificationstatus"];
                }
            }
            catch (Exception e)
            {
                string error = "Error in getOrgQualStatus(Entity account). Exception message: " + Environment.NewLine + e.Message
                                    + Environment.NewLine + "accountId: " + account.Id.ToString()
                                    ;

                writeToTrace(error, tracingService);
                errorStack.Add(error);
            }

            return orgQualStatus;
        }

        public static string getOrgQualStatus(string tsOrgId
                                                            , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            string orgQualStatus = string.Empty;
            try
            {
                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet("new_orgdesignation");
                queryAccount.Criteria.AddCondition("accountnumber", ConditionOperator.Equal, tsOrgId);
                EntityCollection accountCollection = service.RetrieveMultiple(queryAccount);

                if (accountCollection.Entities.Count == 0)
                {
                    string error = "TSOrgId not found";
                    writeToTrace("At getOrgQualStatus(string tsOrgId). " + error, tracingService);
                    errorStack.Add(error);
                    return null;
                }

                Entity account = accountCollection.Entities.First();

                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;

                if (orgDesigId == Guid.Empty)
                    return "";

                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, orgDesigId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, account.Id);
                EntityCollection orgQualificationCollection = service.RetrieveMultiple(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                    orgQualStatus = orgQualificationCollection.Entities.First().FormattedValues["ts_qualificationstatus"];

            }
            catch (Exception e)
            {
                string error = "Error in getOrgQualStatus(string tsOrgId). Exception message: " + Environment.NewLine + e.Message
                                    + Environment.NewLine + "tsOrgId: " + tsOrgId;

                writeToTrace(error, tracingService);
                errorStack.Add(error);
            }

            return orgQualStatus;
        }


       
        public static string getOrgQualStatus(Guid accountId, string qualCode
                                                                   , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            string orgQualStatus = string.Empty;
            try
            {
                QueryExpression queryQualCode = new QueryExpression("new_qualificationcode");
                queryQualCode.ColumnSet = new ColumnSet(true);
                queryQualCode.Criteria.AddCondition("new_qualcode", ConditionOperator.Equal, qualCode);
                EntityCollection qualCodeCollection = service.RetrieveMultiple(queryQualCode);

                if (qualCodeCollection.Entities.Count == 0)
                {
                    errorStack.Add("Qualification Code not found: " + qualCode);
                    return orgQualStatus;
                }

                Guid qualCodeId = qualCodeCollection.Entities.First().Id;


                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                EntityCollection orgQualificationCollection = service.RetrieveMultiple(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                {
                    Entity orgQualification = orgQualificationCollection.Entities.First();
                    orgQualStatus = orgQualification.FormattedValues["ts_qualificationstatus"];
                }
            }
            catch (Exception e)
            {
                string error = "Error in getOrgQualStatus(accountId, qualCode). Exception message: " + Environment.NewLine + e.Message
                               + Environment.NewLine + "accountId: " + accountId + "; qualCode: " + qualCode
                                ;

                writeToTrace(error, tracingService);
                errorStack.Add(error);
            }

            return orgQualStatus;
        }

        public static Entity getQualCodeEntity(string qualCode
                                                        , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            string orgQualStatus = string.Empty;
            try
            {
                QueryExpression queryQualCode = new QueryExpression("new_qualificationcode");
                queryQualCode.ColumnSet = new ColumnSet(true);
                queryQualCode.Criteria.AddCondition("new_qualcode", ConditionOperator.Equal, qualCode);
                EntityCollection qualCodeCollection = service.RetrieveMultiple(queryQualCode);

                if (qualCodeCollection.Entities.Count == 0)
                {
                    errorStack.Add("Qualification Code not found: " + qualCode);
                    return null;
                }

                return qualCodeCollection.Entities.First();

            }
            catch (Exception e)
            {
                string error = "Error in getQualCodeId(qualCode). Exception message: " + Environment.NewLine + e.Message
                               + Environment.NewLine + "qualCode: " + qualCode
                                ;

                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }

        }


        public static Entity getEdCase(Entity tsRequest, Entity accountNgo
                                                                   , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            writeToTrace("At getEdCase(...) for NGO: " + accountNgo.GetAttributeValue<string>("accountnumber"), tracingService);
            try
            {
                Entity edCase = null;
                QueryExpression queryCase = new QueryExpression("incident");
                queryCase.ColumnSet = new ColumnSet(true);
                queryCase.Criteria.AddCondition("casetypecode", ConditionOperator.Equal, 3);
                queryCase.Criteria.AddCondition("customerid", ConditionOperator.Equal, accountNgo.Id);
                EntityCollection caseCollection = service.RetrieveMultiple(queryCase);


                if (caseCollection.Entities.Count == 0)
                {

                    edCase = EDServicesHelper.createEdCase(tsRequest, accountNgo
                                                                        , service, tracingService, errorStack);
                    edCase["ts_originalcaseid"] = edCase.ToEntityReference();
                    service.Update(edCase);
                    return edCase;
                }

                List<Entity> sortedEds = caseCollection.Entities.OrderByDescending(ed =>
                                                                regexMatch(@"^\d+$", ed.GetAttributeValue<string>("ts_tsincidentid"))
                                                                                                        ? Int64.Parse(ed.GetAttributeValue<string>("ts_tsincidentid"))
                                                                                                           : 0
                                                               ).ToList();

                Entity latestEDCase = sortedEds.First();
            
                string edDetermination = getOrgQualStatus(accountNgo.Id, "NGOR-EDApp"
                                                             , service, tracingService, errorStack);


                if (edDetermination == "ED - In Progress")
                {
                    edCase = latestEDCase;
                    return edCase;
                }

                if (edDetermination == "ED - Denied")
                {
                    string error = "Cannot proceed - ED was denied to this NGO";
                    writeToTrace(error, tracingService);
                    errorStack.Add(error);
                    return null;
                }
                
                DateTime expirationDate = latestEDCase.GetAttributeValue<DateTime>("ts_expirationdate");
                expirationDate = expirationDate == DateTime.MinValue ? DateTime.MaxValue.Date : expirationDate.Date;
                DateTime currentDate = DateTime.UtcNow.Date;
                TimeSpan daysBeforeExpiration = expirationDate - currentDate;

                if (edDetermination == "ED - Expired" || (edDetermination == "ED - Approved" && daysBeforeExpiration.TotalDays < 180))
                {
                    Dictionary<string, object> extraCaseFields = new Dictionary<string, object>();
                    extraCaseFields.Add("ts_originalcaseid", latestEDCase.GetAttributeValue<EntityReference>("ts_originalcaseid"));
                    extraCaseFields.Add("ts_previouscaseid", latestEDCase.ToEntityReference());

                    edCase = EDServicesHelper.createEdCase(tsRequest, accountNgo
                                                                        , service, tracingService, errorStack
                                                                        , extraCaseFields);
                    
                    if (edCase == null)
                        return null;

                    latestEDCase["ts_nextcaseid"] = edCase.ToEntityReference();
                    service.Update(latestEDCase);

                    return edCase;
                }             

                if (edDetermination == "ED - Approved")
                {
                    edCase = latestEDCase;
                    return edCase;
                }


                return edCase;
            }
            catch (Exception e)
            {
                string error = "Error in getEdCase(...). Exception message: " + Environment.NewLine + e.Message
                               //+ Environment.NewLine + "accountId: " + accountId + "; qualCode: " + qualCode
                                ;

                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }

            
        }

        public static EntityCollection findEntityeGenericFilterInAndOut(string entityLogicalName, Dictionary<string, object> filterFieldsIn, Dictionary<string, object> filterFieldsOut
            , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            Entity caseEntity = null;
            try
            {
                QueryExpression queryEntity = new QueryExpression(entityLogicalName);
                queryEntity.ColumnSet = new ColumnSet(true);


                if (filterFieldsIn != null)
                {
                    foreach (KeyValuePair<string, object> criteriaField in filterFieldsIn)
                    {
                        assignValueToQueryExpressionCondition(queryEntity, criteriaField, service, tracingService, errorStack, "equal");
                    }
                }


                if (filterFieldsOut != null)
                {
                    foreach (KeyValuePair<string, object> criteriaField in filterFieldsOut)
                    {
                        assignValueToQueryExpressionCondition(queryEntity, criteriaField, service, tracingService, errorStack, "notequal");
                    }
                }

                if (errorStack.Count > 0)
                    return null;

                EntityCollection entityCollection = service.RetrieveMultiple(queryEntity);

                return entityCollection;

            }
            catch (Exception e)
            {
                writeToTrace("Error in findCaseGenericFilterInAndOut(...). Exception message: " + Environment.NewLine + e.Message, tracingService);
                return null;
            }


        }

        public static void assignValueToQueryExpressionCondition(QueryExpression queryQualCase, KeyValuePair<string, object> criteriaField , IOrganizationService service, ITracingService tracingService, List<string> errorStack, string conditionOperator = "equal")

        {
            try
            {

                string fieldName = criteriaField.Key;
                object fieldValue = criteriaField.Value;



                switch (fieldValue)
                {
                    case EntityReference entityRef:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, entityRef);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, entityRef);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, entityRef);
                                break;
                        }
                        break;

                    case OptionSetValue optionSet:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, optionSet);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, optionSet);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, optionSet);
                                break;
                        }
                        break;

                    case Money money:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, money);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, money);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, money);
                                break;
                        }
                        break;

                    case DateTime dateTime:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, dateTime);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, dateTime);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, dateTime);
                                break;
                        }
                        break;

                    case int intValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, intValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, intValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, intValue);
                                break;
                        }
                        break;

                    case decimal decimalValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, decimalValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, decimalValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, decimalValue);
                                break;
                        }
                        break;

                    case double doubleValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, doubleValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, doubleValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, doubleValue);
                                break;
                        }
                        break;

                    case bool boolValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, boolValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, boolValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, boolValue);
                                break;
                        }
                        break;

                    case Guid guidValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, guidValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, guidValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, guidValue);
                                break;
                        }
                        break;

                    case string stringValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, stringValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, stringValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, stringValue);
                                break;
                        }
                        break;

                    default:
                        //caseEntity[fieldName] = TryConvertValue(fieldName, fieldValue);
                        break;
                }

            }
            catch (Exception e)
            {
                string error = "Error in assignValueToQueryExpressionCondition(QueryExpression queryQualCase, KeyValuePair<string, object> criteriaField, string conditionOperator = \"equal\"). Exception message: "
                                + Environment.NewLine + e.Message
                                + Environment.NewLine + "fieldName: " + criteriaField.Key
                                + Environment.NewLine + "fieldValue: " + criteriaField.Value.ToString()
                                ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);

            }

        }
        public static Entity createEdCase(Entity tsRequest, Entity accountNgo
                                                                   , IOrganizationService service, ITracingService tracingService, List<string> errorStack
                                                                    , Dictionary<string, object> additionalFields = null)
        {

            try
            {

                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet(true);
                queryQualCase.Criteria.AddCondition("casetypecode", ConditionOperator.Equal, 3);
                queryQualCase.Criteria.AddCondition("customerid", ConditionOperator.Equal, accountNgo.Id);
                //queryQualCase.AddOrder("createdon", OrderType.Descending);
                EntityCollection qualCaseCollection = service.RetrieveMultiple(queryQualCase);


                if (qualCaseCollection.Entities.Count == 0)
                {
                    //edCase["ts_originalcaseid"] = edCaseId;
                    //service.Update(edCase);

                }

                Entity qualCodeEntity = EDServicesHelper.getQualCodeEntity("NGOR-EDApp"
                                                                                    , service, tracingService, errorStack);

                string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");


                Entity pngoEdMap = EDServicesHelper.determinePngoEdMap(countryCode: accountNgo.GetAttributeValue<string>("address1_country")
                                                                    , tsRequest.GetAttributeValue<string>("preferredLanguage")
                                                                     , service, tracingService, errorStack);


                EntityReference pngoAccountRef = pngoEdMap.GetAttributeValue<EntityReference>("ts_pngoaccountid");




                string tsIncidentId = EDServicesHelper.getNextTsIncidentId(service, tracingService, errorStack);

                if (string.IsNullOrEmpty(tsIncidentId))
                    return null;


                bool expediteEd = tsRequest.Contains("expedite") ? tsRequest.GetAttributeValue<bool>("expedite") : false;
                string preferredLanguage = tsRequest.GetAttributeValue<string>("preferredLanguage");

                string ngoTsOrgId = accountNgo.GetAttributeValue<string>("accountnumber");

                /*
                    16	"ED - In Progress"
                    18	"ED - Expired"
                    19	"ED - Approved"
                    20	"ED - Denied"
                */

                Dictionary<string, object> extraCaseFields = new Dictionary<string, object>();

                if (additionalFields != null)
                {
                    foreach (KeyValuePair<string, object> additionalField in additionalFields)
                    {
                        extraCaseFields.Add(additionalField.Key, additionalField.Value);
                    }
                }
                extraCaseFields.Add("ts_tsincidentid", tsIncidentId);
                extraCaseFields.Add("ts_edstatus", new OptionSetValue(102244)); // ED Requested
                extraCaseFields.Add("ts_pngoaccountid", pngoAccountRef);
                extraCaseFields.Add("ts_caseexpedited", expediteEd);
                extraCaseFields.Add("ts_preferredlanguage", preferredLanguage);

                Guid edCaseId = EDServicesHelper.createCaseGeneric(title: $"ED (NGOId: {ngoTsOrgId}) (IncidentId: {tsIncidentId})"
                                                                        , caseTypeCode: 3 //ED Case
                                                                        , type: null
                                                                        , customerRef: accountNgo.ToEntityReference()
                                                                        , caseStatus: null
                                                                        , qualCodeId: qualCodeEntity.Id
                                                                        , extraCaseFields: extraCaseFields
                                                                        , service, tracingService, errorStack);

                if (edCaseId == Guid.Empty)
                    return null;


                Entity edCase = service.Retrieve("incident", edCaseId, new ColumnSet(true));


                return edCase;
            }
            catch (Exception e)
            {
                string error = "Error in createEdCase(...). Exception message: " + Environment.NewLine + e.Message
                                //+ Environment.NewLine + "accountId: " + accountId + "; qualCode: " + qualCode
                                ;

                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }


        }


        public static Guid createCaseGeneric(
                                        string title
                                        , int caseTypeCode
                                        , int? type
                                        , EntityReference customerRef
                                        , int? caseStatus
                                        , Guid? qualCodeId
                                        , Dictionary<string, object> extraCaseFields
                                        , IOrganizationService service, ITracingService tracingService
                                        , List<string> errorStack
                                     )
        {
            Guid caseId = Guid.Empty;

            try
            {
                Entity caseEntity = new Entity("incident");


                caseEntity["title"] = title;
                caseEntity["casetypecode"] = new OptionSetValue(caseTypeCode);

                if (type != null)
                    caseEntity["ts_type"] = new OptionSetValue(type.Value);

                caseEntity["customerid"] = customerRef;

                if (caseStatus != null)
                    caseEntity["ts_casestatus"] = new OptionSetValue(caseStatus.Value);

                if (qualCodeId != null)
                    caseEntity["ts_qualificationcodeid"] = new EntityReference("new_qualificationcode", qualCodeId.Value);


                if (extraCaseFields != null)
                {
                    foreach (KeyValuePair<string, object> caseField in extraCaseFields)
                    {
                        assignValueToAttribute(caseEntity, caseField, service, tracingService, errorStack);
                    }
                }

                caseId = service.Create(caseEntity);

            }
            catch (Exception e)
            {

                string error = "Error in createCaseGeneric(...). Exception message: " + Environment.NewLine + e.Message
                                    + "accountId: " + customerRef.Id.ToString()
                                ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);

            }

            return caseId;
        }


        public static void assignValueToAttribute(Entity caseEntity, KeyValuePair<string, object> caseField
                                                                                                    , IOrganizationService service, ITracingService tracingService
                                                                                                    , List<string> errorStack)
        {
            try
            {

                string fieldName = caseField.Key;
                object fieldValue = caseField.Value;



                switch (fieldValue)
                {
                    case EntityReference entityRef:
                        caseEntity[fieldName] = entityRef;
                        break;

                    case OptionSetValue optionSet:
                        caseEntity[fieldName] = optionSet;
                        break;

                    case Money money:
                        caseEntity[fieldName] = money;
                        break;

                    case DateTime dateTime:
                        caseEntity[fieldName] = dateTime;
                        break;

                    case int intValue:
                        caseEntity[fieldName] = intValue;
                        break;

                    case decimal decimalValue:
                        caseEntity[fieldName] = decimalValue;
                        break;

                    case double doubleValue:
                        caseEntity[fieldName] = doubleValue;
                        break;

                    case bool boolValue:
                        caseEntity[fieldName] = boolValue;
                        break;

                    case Guid guidValue:
                        caseEntity[fieldName] = guidValue;
                        break;

                    case string stringValue:
                        caseEntity[fieldName] = stringValue;
                        break;

                    default:
                        //caseEntity[fieldName] = TryConvertValue(fieldName, fieldValue);
                        break;
                }




            }
            catch (Exception e)
            {
                string error = "Error in assignValueToAttribute(...). Exception message: " + Environment.NewLine + e.Message
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);

            }

        }

        public static string getNextTsIncidentId(
                                                    IOrganizationService service, ITracingService tracingService
                                                    , List<string> errorStack)
        {
            string tsIncidentId = string.Empty;
            try
            {
                X509Certificate2 cer = GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;

                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();

                objRequest.ServerName = EnvVariables["ts_Sql2kServer"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_getNextIncidentId";



                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                if (returnXml.Length == 0)
                {
                    errorStack.Add("At getNextTsIncidentId() - could not generate new tsIncidentId");
                    return "";
                }

                tsIncidentId = returnXml.First().Any[0].InnerText;
                return tsIncidentId;

            }
            catch (Exception e)
            {
                string error = "Error in getNextTsIncidentId(). Exception message: " + Environment.NewLine + e.Message;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return "";
            }
        }


        public static Dictionary<string, string> processBoxEDSubfolders(string edFolderBoxId
                                                                                        , IOrganizationService service, ITracingService tracingService
                                                                                        , List<string> errorStack)
        {
            try
            {
                Dictionary<string, string> boxEDSubfolders = new Dictionary<string, string>();


                boxEDSubfolders["edSystemAddedFolderBoxId"] = createBoxFolderInternal(edFolderBoxId, "SYSTEM ADDED", service, tracingService, errorStack);
                boxEDSubfolders["edNgoAddedOriginalsFolderBoxId"] = createBoxFolderInternal(edFolderBoxId, "NGO ADDED ORIGINALS", service, tracingService, errorStack);
                boxEDSubfolders["edNgoAddedScannedFolderBoxId"] = createBoxFolderInternal(edFolderBoxId, "NGO ADDED SCANNED", service, tracingService, errorStack);
                boxEDSubfolders["edForFileFolderBoxId"] = createBoxFolderInternal(edFolderBoxId, "FOR FILE", service, tracingService, errorStack);
                boxEDSubfolders["edForRedactionFolderBoxId"] = createBoxFolderInternal(edFolderBoxId, "FOR REDACTION", service, tracingService, errorStack);
                

                boxEDSubfolders["edForGrantmakerFolderBoxId"] = createBoxFolderInternal(edFolderBoxId, "FOR GRANTMAKER", service, tracingService, errorStack);             


                boxEDSubfolders["edForGrantmakerSupportingDocumentsFolderBoxId"] = createBoxFolderInternal(boxEDSubfolders["edForGrantmakerFolderBoxId"], "SUPPORTING DOCUMENTS", service, tracingService, errorStack);
                boxEDSubfolders["edForGrantmakerAnalysisFolderBoxId"] = createBoxFolderInternal(boxEDSubfolders["edForGrantmakerFolderBoxId"], "ANALYSIS", service, tracingService, errorStack);
                boxEDSubfolders["edForGrantmakerSanctionChecksFolderBoxId"] = createBoxFolderInternal(boxEDSubfolders["edForGrantmakerFolderBoxId"], "SANCTION CHECKS", service, tracingService, errorStack);
               

                return boxEDSubfolders;
            }
            catch (Exception e)
            {
                string error = "Error in processBoxEDSubfolders(...). Exception message: " + Environment.NewLine + e.Message;
                writeToTrace(error, tracingService);
                errorStack.Add(error);

                return null;
            }
        }

        public static IDictionary<string, Object> processBoxFolderED(Entity edCase, Entity accountNgo, string ngoFolderBoxId
                                                                                                , IOrganizationService service, ITracingService tracingService
                                                                                                , List<string> errorStack)
        {
            IDictionary<string, Object> response = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;

            try
            {

                QueryExpression externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery.ColumnSet = new ColumnSet(true);
                externalSystemRefQuery.Criteria.AddCondition("ts_objectid", ConditionOperator.Equal, edCase.Id);
                externalSystemRefQuery.Criteria.AddCondition("ts_externalsystemname", ConditionOperator.Equal, "Box");
                externalSystemRefQuery.Criteria.AddCondition("ts_referencetype", ConditionOperator.Equal, 2); //referenceType: 2 - ED Folder Id
                EntityCollection externalSystemRefCollection = service.RetrieveMultiple(externalSystemRefQuery);

                string edFolderBoxId = "";
                if (externalSystemRefCollection.Entities.Count == 0)
                {
                    DateTime currentDate = DateTime.UtcNow;
                    string currentYear = currentDate.Year.ToString();

                    string incidentId = edCase.GetAttributeValue<string>("ts_tsincidentid");

                    string ngoName = accountNgo.GetAttributeValue<string>("name");

                    string edFolderName = $"{currentYear}_{incidentId}_{ngoName}";


                    edFolderBoxId = createBoxFolderInternal(ngoFolderBoxId, edFolderName, service, tracingService, errorStack);

                    if (errorStack.Count > 0)
                        return null;

                    if (!string.IsNullOrEmpty(edFolderBoxId))
                    {
                        createExternalSystemReference(entity: edCase, externalSystem: "Box"
                                                        , referenceType: 2, referenceValue: edFolderBoxId  //referenceType: 2 - ED Folder Id
                                                        , name: incidentId + " - " + edFolderBoxId
                                                        , service, tracingService, errorStack);

                    }

                    response["createdNew"] = true;
                }
                else
                {
                    Entity externalReferenceEdBoxFolder = externalSystemRefCollection.Entities.First();

                    edFolderBoxId = externalReferenceEdBoxFolder.GetAttributeValue<string>("ts_referencevalue");

                    response["createdNew"] = false;
                }

                response["edFolderBoxId"] = edFolderBoxId;

                return response;
            }
            catch (Exception e)
            {

                string error = "Error in processBoxFolderED(...). Exception message: " + Environment.NewLine + e.Message;
                writeToTrace(error, tracingService);
                errorStack.Add(error);

                return null;
            }

        }


        public static string processBoxFolderNGO(Entity accountNgo
                                                            , IOrganizationService service, ITracingService tracingService
                                                            , List<string> errorStack)
        {
            string ngoFolderBoxId = "";

            try
            {
                QueryExpression externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery.ColumnSet = new ColumnSet(true);
                externalSystemRefQuery.Criteria.AddCondition("ts_objectid", ConditionOperator.Equal, accountNgo.Id);
                externalSystemRefQuery.Criteria.AddCondition("ts_externalsystemname", ConditionOperator.Equal, "Box");
                externalSystemRefQuery.Criteria.AddCondition("ts_referencetype", ConditionOperator.Equal, 1); //1 - NGO Folder Id
                EntityCollection externalSystemRefCollection = service.RetrieveMultiple(externalSystemRefQuery);

                if (externalSystemRefCollection.Entities.Count == 0)
                {
                    string ngoTsOrgId = accountNgo.GetAttributeValue<string>("accountnumber");
                    string ngoName = accountNgo.GetAttributeValue<string>("name");
                    string boxRootId = EnvVariables["ts_BoxNGOSourceCertificationsFolderId"];

                    string ngoFolderName = $"{ngoTsOrgId}_{ngoName}";


                    ngoFolderBoxId = createBoxFolderInternal(boxRootId, ngoFolderName, service, tracingService, errorStack);

                    if (errorStack.Count > 0)
                        return null;

                    if (!string.IsNullOrEmpty(ngoFolderBoxId))
                    {
                        createExternalSystemReference(entity: accountNgo, externalSystem: "Box"
                                                       , referenceType: 1, referenceValue: ngoFolderBoxId  //referenceType: 1 - NGO Folder Id
                                                       , name: ngoTsOrgId + " - " + ngoFolderBoxId
                                                       , service, tracingService, errorStack);


                    }
                }
                else
                {
                    Entity externalReferenceNgoBoxFolder = externalSystemRefCollection.Entities.First();

                    ngoFolderBoxId = externalReferenceNgoBoxFolder.GetAttributeValue<string>("ts_referencevalue");

                    writeToTrace("There's already an external reference for NGO Box Folder. ngoFolderBoxId: " + ngoFolderBoxId, tracingService);
                }

            }
            catch (Exception e)
            {

                string error = "Error in processBoxFolderNGO(...). Exception message: " + Environment.NewLine + e.Message;
                writeToTrace(error, tracingService);
                errorStack.Add(error);

            }

            return ngoFolderBoxId;
        }

        public static void createExternalSystemReference(Entity entity, string externalSystem, int referenceType, string referenceValue, string name
                                                                                                                                        , IOrganizationService service, ITracingService tracingService
                                                                                                                                        , List<string> errorStack)
        {
            try
            {
                Entity externalSystemReference = new Entity("ts_externalsystemreference");
                externalSystemReference["ts_objectid"] = new EntityReference(entity.LogicalName, entity.Id);
                externalSystemReference["ts_externalsystemname"] = externalSystem;
                externalSystemReference["ts_referencetype"] = new OptionSetValue(referenceType);
                externalSystemReference["ts_referencevalue"] = referenceValue;
                externalSystemReference["ts_name"] = name;
                service.Create(externalSystemReference);
            }
            catch (Exception e)
            {
                string error = "Error in createExternalSystemReference(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "externalSystem: " + externalSystem + "; referenceType: " + referenceType.ToString() + "; name: " + referenceValue
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
            }
        }


        

        public static string createBoxFolderInternal(string locationFolderId, string folderName
                                                                                        , IOrganizationService service, ITracingService tracingService
                                                                                        , List<string> errorStack)
        {
            try
            {
                IDictionary<string, Object> boxInterfaceRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                //boxInterfaceRequest["@odata.type"] = "#Microsoft.Dynamics.CRM.expando";
                boxInterfaceRequest["requestCategory"] = "Item";
                boxInterfaceRequest["requestType"] = "Create";
                boxInterfaceRequest["itemType"] = "Folder";
                boxInterfaceRequest["folderId"] = locationFolderId;
                boxInterfaceRequest["folderName"] = folderName;

                

                string requestJson = JsonConvert.SerializeObject(boxInterfaceRequest, Newtonsoft.Json.Formatting.None);

                writeToTrace("At createBoxFolderInternal(...) - requestJson: " + requestJson, tracingService);

                Entity tsResponse = CallBoxApiServiceDirect(requestJson, tracingService);

                string tsResponseText = JsonConvert.SerializeObject(tsResponse);
                //EDServicesHelper.writeToTrace("createBoxFolderInternal(...) - tsResponse: " + tsResponseText, tracingService);

                if (tsResponse.Contains("success") && !tsResponse.GetAttributeValue<bool>("success"))
                {
                    string message = tsResponse.Contains("message") ? tsResponse.GetAttributeValue<string>("message") : "Unknown error from Box API";
                    string error = $"At createBoxFolderInternal(...). Error creating Box folder. Box API message: {Environment.NewLine}{message}"
                        + $"{Environment.NewLine}locationFolderId: {locationFolderId}; folderName: {folderName}";
                    writeToTrace(error, tracingService);
                    errorStack.Add(error);
                    return null;
                }


                string newFolderBoxId = tsResponse.Contains("itemId") ? tsResponse.GetAttributeValue<string>("itemId") : null;


                return newFolderBoxId;
            }
            catch (Exception e)
            {
                string error = "Error in createBoxFolderInternal(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "locationFolderId: " + locationFolderId + "; folderName: " + folderName
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
        }

        public static Entity findBoxFolderByName(string folderName
                                                                      , IOrganizationService service, ITracingService tracingService
                                                                      , List<string> errorStack, string searchUnderFolderId = null)
        {
            try
            {
                IDictionary<string, Object> boxInterfaceRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxInterfaceRequest["itemType"] = "Folder";
                boxInterfaceRequest["itemName"] = folderName;
                boxInterfaceRequest["searchUnderFolderId"] = searchUnderFolderId;


                string requestJson = JsonConvert.SerializeObject(boxInterfaceRequest, Newtonsoft.Json.Formatting.None);

                writeToTrace("At findBoxFolderByName(...) - requestJson: " + requestJson, tracingService);

                Entity tsResponse = CallBoxApiServiceDirect(requestJson, tracingService);


                try
                {
                    string tsResponseText = JsonConvert.SerializeObject(tsResponse);
                    EDServicesHelper.writeToTrace("findBoxFolderByName(...) - tsResponse: " + tsResponseText, tracingService);
                }
                catch (Exception ex)
                {
                    EDServicesHelper.writeToTrace("findBoxFolderByName(...). Error serializing tsResponse for trace: " + ex.Message, tracingService);
                }



                return tsResponse;
            }
            catch (Exception e)
            {
                string error = "Error in findBoxFolderByName(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "searchUnderFolderId: " + searchUnderFolderId + "; folderName: " + folderName
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
        }


        public static Entity findBoxFolderGoingDownTheTree(string folderName, string searchUnderFolderId
                                                                                                        , IOrganizationService service, ITracingService tracingService
                                                                                                        , List<string> errorStack)
        {
            try
            {
                string folderId = "";
                IDictionary<string, Object> boxParameters = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxParameters["itemType"] = "Folder";
                boxParameters["itemId"] = searchUnderFolderId;

                Entity boxResponseEntity = EDServicesHelper.makeBoxItemBasedRequest(boxParameters, service, tracingService, errorStack);


                EntityCollection edFolders = boxResponseEntity.GetAttributeValue<EntityCollection>("folderContents");

                Entity folderEntity = edFolders.Entities.Where(item => item.GetAttributeValue<string>("name").ToLower() == folderName.ToLower())?.FirstOrDefault();

                if (folderEntity == null)
                {
                    Entity grantMakerFolderEntity = edFolders.Entities.Where(item => item.GetAttributeValue<string>("name").ToLower().Contains("for grantmaker"))?.FirstOrDefault();

                    if (grantMakerFolderEntity == null)
                    {
                        errorStack.Add("Could not find 'FOR GRANTMAKER' folder");
                        return null;
                    }

                    string grantMakerFolderId = grantMakerFolderEntity.GetAttributeValue<string>("id");

                    boxParameters = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                    boxParameters["itemType"] = "Folder";
                    boxParameters["itemId"] = grantMakerFolderId;

                    boxResponseEntity = EDServicesHelper.makeBoxItemBasedRequest(boxParameters, service, tracingService, errorStack);

                    edFolders = boxResponseEntity.GetAttributeValue<EntityCollection>("folderContents");

                    folderEntity = edFolders.Entities.Where(item => item.GetAttributeValue<string>("name").ToLower() == folderName.ToLower())?.FirstOrDefault();

                    if (folderEntity == null)
                    {
                        errorStack.Add("Could not find '" + folderName + "' folder");
                        return null;
                    }

                    folderId = folderEntity.GetAttributeValue<string>("id");
                }
                else
                {
                    folderId = folderEntity.GetAttributeValue<string>("id");
                }

                boxParameters = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxParameters["itemType"] = "Folder";
                boxParameters["itemId"] = folderId;

                Entity tsResponse = EDServicesHelper.makeBoxItemBasedRequest(boxParameters, service, tracingService, errorStack);

                return tsResponse;


            }
            catch (Exception e)
            {
                string error = "Error in findBoxFolderGoingDownTheTree(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "searchUnderFolderId: " + searchUnderFolderId + "; folderName: " + folderName
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
        }

        public static Entity makeBoxItemBasedRequest(IDictionary<string, Object> parameters
                                                                                            , IOrganizationService service, ITracingService tracingService
                                                                                            , List<string> errorStack)
        {
            try
            {
                string requestJson = JsonConvert.SerializeObject(parameters, Newtonsoft.Json.Formatting.None);

                writeToTrace("At makeBoxItemBasedRequest(...) - requestJson: " + requestJson, tracingService);

                Entity tsResponse = CallBoxApiServiceDirect(requestJson, tracingService);


                string tsResponseText = JsonConvert.SerializeObject(tsResponse);
                EDServicesHelper.writeToTrace("makeBoxItemBasedRequest(...) - tsResponse: " + tsResponseText, tracingService);



                return tsResponse;
            }
            catch (Exception e)
            {
                string error = "Error in makeBoxItemBasedRequest(...). Exception message: " + Environment.NewLine + e.Message
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
        }
        public static void getAllFilesInSubTree(string folderId, EntityCollection edFileList
                                                                            , IOrganizationService service, ITracingService tracingService
                                                                            , List<string> errorStack, string tsIncidentId = null)
        {
            try
            {
                IDictionary<string, Object> boxInterfaceRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxInterfaceRequest["requestCategory"] = "List";
                boxInterfaceRequest["requestType"] = "allFilesInSubTree";
                boxInterfaceRequest["folderId"] = folderId;


                string requestJson = JsonConvert.SerializeObject(boxInterfaceRequest, Newtonsoft.Json.Formatting.None);

                writeToTrace("At getAllFilesInSubTree(string folderId, EntityCollection edFileList, string tsIncidentId = null) - requestJson: " + requestJson, tracingService);

                Entity tsResponse = CallBoxApiServiceDirect(requestJson, tracingService);


                try
                {
                    string tsResponseText = JsonConvert.SerializeObject(tsResponse);
                    //EDServicesHelper.writeToTrace("getAllFilesInSubTree(...) - tsResponse: " + tsResponseText);
                }
                catch (Exception ex)
                {
                    EDServicesHelper.writeToTrace("getAllFilesInSubTree(...). Error serializing tsResponse for trace: " + ex.Message, tracingService);
                }

                EntityCollection fileListEntityCollection = tsResponse.GetAttributeValue<EntityCollection>("folderContents");

                if (fileListEntityCollection.Entities.Count == 0)
                    return;

                List<Entity> sortedFileList = fileListEntityCollection.Entities.OrderBy(item => item.GetAttributeValue<string>("folderName")).ToList();

                foreach (Entity item in sortedFileList)
                {
                    string fileName = item.GetAttributeValue<string>("name");
                    bool match = EDServicesHelper.regexMatch(@"_\d{14}\.\w{3,10}$", fileName);

                    if (match)
                        continue;

                    Entity fileEntity = new Entity();
                    
                    fileEntity["fileId"] = item.GetAttributeValue<string>("id");
                    fileEntity["fileName"] = item.GetAttributeValue<string>("name");
                    fileEntity["fileSize"] = item.GetAttributeValue<int>("size");
                    fileEntity["createdOn"] = item.GetAttributeValue<string>("createdOn");
                    fileEntity["modifiedOn"] = item.GetAttributeValue<string>("modifiedOn");
                    fileEntity["contentType"] = item.GetAttributeValue<string>("contentType");
                    fileEntity["category"] = item.GetAttributeValue<string>("folderName");
                    fileEntity["folderPath"] = item.GetAttributeValue<string>("subFolderTree");
                    if (tsIncidentId != null)
                        fileEntity["incidentId"] = tsIncidentId;

                    EntityCollection fileTags = item.GetAttributeValue<EntityCollection>("tags");
                    string descriptionTag = fileTags.Entities.Where(tag => tag.GetAttributeValue<string>("tag").ToLower().StartsWith("descriptiontag:"))?.FirstOrDefault()?.GetAttributeValue<string>("tag");
                    string description = descriptionTag == null ? "" : descriptionTag.Replace("descriptionTag:", "").Trim();

                    fileEntity["description"] = description;

                    edFileList.Entities.Add(fileEntity);
                }

                return;
            }
            catch (Exception e)
            {
                string error = "Error in getAllFilesInSubTree(string folderId, EntityCollection edFileList, string tsIncidentId = null). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "folderId: " + folderId + "; tsIncidentId: " + tsIncidentId
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return;
            }
        }
        
        public static EntityCollection getAllFilesInSubTree(string folderId
                                                                            , IOrganizationService service, ITracingService tracingService
                                                                            , List<string> errorStack)
        {
            EntityCollection edFileList = new EntityCollection();
            try
            {
                IDictionary<string, Object> boxInterfaceRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxInterfaceRequest["requestCategory"] = "List";
                boxInterfaceRequest["requestType"] = "allFilesInSubTree";
                boxInterfaceRequest["folderId"] = folderId;


                string requestJson = JsonConvert.SerializeObject(boxInterfaceRequest, Newtonsoft.Json.Formatting.None);

                writeToTrace("At getAllFilesInSubTree(...) - requestJson: " + requestJson, tracingService);

                Entity tsResponse = CallBoxApiServiceDirect(requestJson, tracingService);

                
                try
                {
                    string tsResponseText = JsonConvert.SerializeObject(tsResponse);
                    //EDServicesHelper.writeToTrace("getAllFilesInSubTree(...) - tsResponse: " + tsResponseText);
                }
                catch (Exception ex)
                {
                    EDServicesHelper.writeToTrace("getAllFilesInSubTree(...). Error serializing tsResponse for trace: " + ex.Message, tracingService);
                }

                EntityCollection fileListEntityCollection = tsResponse.GetAttributeValue<EntityCollection>("folderContents");

                if (fileListEntityCollection.Entities.Count == 0)
                    return edFileList;

                List<Entity> sortedFileList = fileListEntityCollection.Entities.OrderBy(item => item.GetAttributeValue<string>("folderName")).ToList();

                foreach (Entity item in sortedFileList)
                {
                    string fileName = item.GetAttributeValue<string>("name");

                    bool match = EDServicesHelper.regexMatch(@"_\d{14}\.\w{3,10}$", fileName);

                    if (match)
                        continue;

                    Entity fileEntity = new Entity();
                    fileEntity["fileId"] = item.GetAttributeValue<string>("id");                    
                    fileEntity["fileName"] = item.GetAttributeValue<string>("name");
                    fileEntity["fileSize"] = item.GetAttributeValue<int>("size");
                    fileEntity["createdOn"] = item.GetAttributeValue<string>("createdOn");
                    fileEntity["modifiedOn"] = item.GetAttributeValue<string>("modifiedOn");
                    fileEntity["contentType"] = item.GetAttributeValue<string>("contentType");
                    fileEntity["category"] = item.GetAttributeValue<string>("folderName");
                    fileEntity["folderPath"] = item.GetAttributeValue<string>("subFolderTree");

                    EntityCollection fileTags = item.GetAttributeValue<EntityCollection>("tags");
                    string descriptionTag = fileTags.Entities.Where(tag => tag.GetAttributeValue<string>("tag").ToLower().StartsWith("descriptiontag:"))?.FirstOrDefault()?.GetAttributeValue<string>("tag");
                    string description = descriptionTag == null ? "" : descriptionTag.Replace("descriptionTag:", "").Trim();

                    fileEntity["description"] = description;

                    edFileList.Entities.Add(fileEntity);
                }

                return edFileList;
            }
            catch (Exception e)
            {
                string error = "Error in getAllFilesInSubTree(...). Exception message: " + Environment.NewLine + e.Message
                   
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
        }

        public static EntityCollection getAllFolderFilesInSubTree(string folderId
                                                                                , IOrganizationService service, ITracingService tracingService
                                                                                , List<string> errorStack)
        {
            EntityCollection edFileList = new EntityCollection();
            try
            {
                IDictionary<string, Object> boxInterfaceRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxInterfaceRequest["requestCategory"] = "List";
                boxInterfaceRequest["requestType"] = "allFolderFilesInSubTree";
                boxInterfaceRequest["folderId"] = folderId;


                string requestJson = JsonConvert.SerializeObject(boxInterfaceRequest, Newtonsoft.Json.Formatting.None);

                writeToTrace("At getAllFolderFilesInSubTree(...) - requestJson: " + requestJson, tracingService);

                Entity tsResponse = CallBoxApiServiceDirect(requestJson, tracingService);


                //EDServicesHelper.writeToTrace("getAllFolderFilesInSubTree(...). Error serializing tsResponse for trace: " + ex.Message);
                
                EntityCollection fileListEntityCollection = tsResponse.GetAttributeValue<EntityCollection>("foldersInSubTree");

                if (fileListEntityCollection.Entities.Count == 0)
                    return edFileList;

                List<Entity> sortedCategoryList = fileListEntityCollection.Entities.OrderBy(item => item.GetAttributeValue<string>("folderName")).ToList();

                foreach (Entity categoryItem in sortedCategoryList)
                {
                    string categoryId = categoryItem.GetAttributeValue<string>("folderId");

                    if (categoryId == folderId)
                        continue;

                    Entity category = new Entity();

                    category["category"] = categoryItem.GetAttributeValue<string>("folderName");

                    EntityCollection files = new EntityCollection();

                    EntityCollection categoryFileList = categoryItem.GetAttributeValue<EntityCollection>("folderContents");

                    foreach (Entity categoryFile in categoryFileList.Entities)
                    {
                        Entity fileEntity = new Entity();
                        fileEntity["fileId"] = categoryFile.GetAttributeValue<string>("id");
                        fileEntity["fileName"] = categoryFile.GetAttributeValue<string>("name");
                        fileEntity["fileSize"] = categoryFile.GetAttributeValue<int>("size");
                        fileEntity["createdOn"] = categoryFile.GetAttributeValue<string>("createdOn");
                        fileEntity["modifiedOn"] = categoryFile.GetAttributeValue<string>("modifiedOn");
                        fileEntity["contentType"] = categoryFile.GetAttributeValue<string>("contentType");
                        fileEntity["folderPath"] = categoryFile.GetAttributeValue<string>("subFolderTree");

                        EntityCollection fileTags = categoryFile.GetAttributeValue<EntityCollection>("tags");
                        string descriptionTag = fileTags.Entities.Where(tag => tag.GetAttributeValue<string>("tag").ToLower().StartsWith("descriptiontag:"))?.FirstOrDefault()?.GetAttributeValue<string>("tag");
                        string description = descriptionTag == null ? "" : descriptionTag.Replace("descriptionTag:", "").Trim();

                        fileEntity["description"] = description;

                        files.Entities.Add(fileEntity);
                    }

                    category["categoryFiles"] = files;

                    edFileList.Entities.Add(category);
                }

                return edFileList;
            }
            catch (Exception e)
            {
                string error = "Error in getAllFolderFilesInSubTree(...). Exception message: " + Environment.NewLine + e.Message

                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
        }

        public static bool deleteBoxItem(string itemType, string itemId
                                                                        , IOrganizationService service, ITracingService tracingService
                                                                        , List<string> errorStack)
        {
            try
            {
                bool success = false;
                IDictionary<string, Object> boxInterfaceRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxInterfaceRequest["itemType"] = itemType;
                boxInterfaceRequest["itemId"] = itemId;
                boxInterfaceRequest["itemOperation"] = "delete";


                string requestJson = JsonConvert.SerializeObject(boxInterfaceRequest, Newtonsoft.Json.Formatting.None);

                writeToTrace("At deleteBoxItem(...) - requestJson: " + requestJson, tracingService);

                Entity tsResponse = CallBoxApiServiceDirect(requestJson, tracingService);

                if (tsResponse.Contains("success"))
                    success = tsResponse.GetAttributeValue<bool>("success");
                

                return success;
            }
            catch (Exception e)
            {
                string error = "Error in deleteBoxItem(...). Exception message: " + Environment.NewLine + e.Message
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return false;
            }
        }

        public static bool renameBoxItem(string itemType, string itemId, string newItemName
                                                                                        , IOrganizationService service, ITracingService tracingService
                                                                                        , List<string> errorStack)
        {
            try
            {
                bool success = false;
                IDictionary<string, Object> boxInterfaceRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxInterfaceRequest["itemType"] = itemType;
                boxInterfaceRequest["itemId"] = itemId;
                boxInterfaceRequest["itemOperation"] = "rename";
                boxInterfaceRequest["newItemName"] = newItemName;

                string requestJson = JsonConvert.SerializeObject(boxInterfaceRequest, Newtonsoft.Json.Formatting.None);

                writeToTrace("At renameBoxItem(...) - requestJson: " + requestJson, tracingService);

                Entity tsResponse = CallBoxApiServiceDirect(requestJson, tracingService);

                if (tsResponse.Contains("success"))
                    success = tsResponse.GetAttributeValue<bool>("success");


                return success;
            }
            catch (Exception e)
            {
                string error = "Error in renameBoxItem(...). Exception message: " + Environment.NewLine + e.Message
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return false;
            }
        }

        public static string fileUpload(string folderId, string fileName
                                                                        , int fileSize, string fileContent, string contentType
                                                                        , string uploaderEmail, string descriptionTag
                                                                        , IOrganizationService service, ITracingService tracingService
                                                                        , List<string> errorStack)

        {

            string fileId = "";
            try
            {
                bool success = false;
                IDictionary<string, Object> boxInterfaceRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxInterfaceRequest["itemType"] = "File";
                boxInterfaceRequest["itemOperation"] = "upload";
                boxInterfaceRequest["versionUploadOnExistingFile"] = true;

                boxInterfaceRequest["folderId"] = folderId;
                boxInterfaceRequest["fileName"] = fileName;
                boxInterfaceRequest["fileSize"] = fileSize;
                boxInterfaceRequest["fileContent"] = fileContent;
                boxInterfaceRequest["contentType"] = contentType;

                if (!string.IsNullOrEmpty(uploaderEmail))
                    boxInterfaceRequest["uploaderEmail"] = uploaderEmail;

                if (!string.IsNullOrEmpty(descriptionTag))
                    boxInterfaceRequest["descriptionTag"] = descriptionTag;

                string requestJson = JsonConvert.SerializeObject(boxInterfaceRequest, Newtonsoft.Json.Formatting.None);

                writeToTrace("At fileUpload(...) - requestJson: " + requestJson, tracingService);

                Entity tsResponse = CallBoxApiServiceDirect(requestJson, tracingService);

                if (tsResponse.Contains("success"))
                    success = tsResponse.GetAttributeValue<bool>("success");

                if (success && tsResponse.Contains("itemId"))
                    fileId = tsResponse.GetAttributeValue<string>("itemId");


                return fileId;
            }
            catch (Exception e)
            {
                string error = "Error in fileUpload(...). Exception message: " + Environment.NewLine + e.Message
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return "";
            }
        }


        public static dynamic createBoxFolder(string locationFolderId, string folderName
                                                                                    , IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                IDictionary<string, Object> boxInterfaceRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxInterfaceRequest["@odata.type"] = "#Microsoft.Dynamics.CRM.expando";
                boxInterfaceRequest["requestCategory"] = "Item";
                boxInterfaceRequest["requestType"] = "Create";
                boxInterfaceRequest["itemType"] = "Folder";
                boxInterfaceRequest["folderId"] = locationFolderId;
                boxInterfaceRequest["folderName"] = folderName;

                IDictionary<string, Object> tsRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;

                tsRequest.Add("ts_request", boxInterfaceRequest);

                string requestJson = JsonConvert.SerializeObject(tsRequest, Newtonsoft.Json.Formatting.None);

                string dynamicsEnvironmentCurrent = DynamicsEnvironments["DynamicsEnvironmentCurrent"];
                string dynamicsEnvironmentUrl = DynamicsEnvironments[dynamicsEnvironmentCurrent];

                writeToTrace("At createBoxFolder(...) - dynamicsEnvironmentUrl: " + dynamicsEnvironmentUrl, tracingService);

                dynamic response = makeMsAuthPostCall(requestJson
                                                        , dynamicsEnvironmentUrl, "/api/data/v9.2/ts_BoxInterface"
                                                        , null, null
                                                        , null, null
                                                        , tracingService, errorStack);


                dynamic boxFolderCreationResult = new System.Dynamic.ExpandoObject();

                boxFolderCreationResult.response = response;
                boxFolderCreationResult.responseText = JsonConvert.SerializeObject(response, Newtonsoft.Json.Formatting.Indented);
                boxFolderCreationResult.newFolderBoxId = response.itemId;

                writeToTrace("At createBoxFolder(...) - responseText: " + boxFolderCreationResult.responseText, tracingService);

                return boxFolderCreationResult;
            }
            catch (Exception e)
            {
                string error = "Error in createBoxFolder(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "locationFolderId: " + locationFolderId + "; folderName: " + folderName
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
        }


        public static Entity determinePngoEdMap(string countryCode, string preferredLanguage
                                                                                                , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                preferredLanguage = regexReplace(@"-\w+$", preferredLanguage, "");


                QueryExpression queryPngoEdMap = new QueryExpression("ts_pngoedmap");
                queryPngoEdMap.ColumnSet = new ColumnSet(true);
                queryPngoEdMap.Criteria.AddCondition("ts_countrycode", ConditionOperator.Equal, countryCode);
                queryPngoEdMap.Criteria.AddCondition("ts_preferredlanguage", ConditionOperator.Equal, preferredLanguage);
                EntityCollection pngoEdMapCollection = service.RetrieveMultiple(queryPngoEdMap);

                if (pngoEdMapCollection.Entities.Count > 0)
                    return pngoEdMapCollection.Entities.First();


                queryPngoEdMap = new QueryExpression("ts_pngoedmap");
                queryPngoEdMap.ColumnSet = new ColumnSet(true);
                queryPngoEdMap.Criteria.AddCondition("ts_countrycode", ConditionOperator.Equal, countryCode);
                queryPngoEdMap.Criteria.AddCondition("ts_preferredlanguage", ConditionOperator.Null);
                pngoEdMapCollection = service.RetrieveMultiple(queryPngoEdMap);

                if (pngoEdMapCollection.Entities.Count > 0)
                    return pngoEdMapCollection.Entities.First();

                queryPngoEdMap = new QueryExpression("ts_pngoedmap");
                queryPngoEdMap.ColumnSet = new ColumnSet(true);
                queryPngoEdMap.Criteria.AddCondition("ts_countrycode", ConditionOperator.Null);
                queryPngoEdMap.Criteria.AddCondition("ts_preferredlanguage", ConditionOperator.Equal, preferredLanguage);
                pngoEdMapCollection = service.RetrieveMultiple(queryPngoEdMap);

                if (pngoEdMapCollection.Entities.Count > 0)
                    return pngoEdMapCollection.Entities.First();

                queryPngoEdMap = new QueryExpression("ts_pngoedmap");
                queryPngoEdMap.ColumnSet = new ColumnSet(true);
                queryPngoEdMap.Criteria.AddCondition("ts_countrycode", ConditionOperator.Null);
                queryPngoEdMap.Criteria.AddCondition("ts_preferredlanguage", ConditionOperator.Null);
                pngoEdMapCollection = service.RetrieveMultiple(queryPngoEdMap);

                if (pngoEdMapCollection.Entities.Count > 0)
                    return pngoEdMapCollection.Entities.First();
            }
            catch (Exception e)
            {
                string error = "Error in determinePngoEdMap(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "countryCode: " + countryCode + "; preferredLanguage: " + preferredLanguage
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }

            return null;
        }


        public static IDictionary<string, System.Object> findOrganizationAccountMatches(Entity organization
                                                                                                              , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            IDictionary<string, System.Object> response = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;
            try
            {
              
                writeToTrace("Starting findOrganizationAccountMatches(...)"
                                                                            , tracingService);
                #region Call Account Match Service


                MatchConfiguration config = new MatchConfiguration()
                {
                    NameWeight = 0.5,
                    AddressWeight = 0.3,
                    PostalCodeWeight = 0.1,
                    StateProvinceWeight = 0.1,
                    CountryWeight = 0,
                    WebsiteWeight = 0,
                    LegalIdWeight = 0,
                    PhoneWeight = 0
                };


                string name = organization.GetAttributeValue<string>("orgName");
                string phone = organization.GetAttributeValue<string>("phone") ?? "";

                Entity address = organization.GetAttributeValue<Entity>("address");
                string address1 = address.GetAttributeValue<string>("address1");
                string stateProvince = address.GetAttributeValue<string>("regionCode") ?? "";
                string postalCode = address.GetAttributeValue<string>("postalCode") ?? "";
                string countryCode = address.GetAttributeValue<string>("countryCode");



                IDictionary<string, System.Object> matchRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;
                //matchRequest["legalId"] = legalId;
                matchRequest["name"] = name;
                matchRequest["address1"] = address1;
                matchRequest["stateProvince"] = stateProvince;
                matchRequest["postalCode"] = postalCode;
                matchRequest["countryCode"] = countryCode;
                //matchRequest["website"] = website;
                matchRequest["phone"] = phone;

                string matchRequestText = JsonConvert.SerializeObject(matchRequest);
                writeToTrace("Account Match Request: " + Environment.NewLine + matchRequestText
                                                , tracingService);
                AccountMatchService accountMatchService = new AccountMatchService(service
                                                                                    , EDServicesHelper.DynamicsEnvironments
                                                                                    , EDServicesHelper.EnvVariables
                                                                                    , config);

                AccountMatchResponse accountMatchResponse = accountMatchService.FindMatches(matchRequestText);

                List<AccountMatch> accountMatches = accountMatchResponse.Matches.Where(Match => Match.OverallScore >= 0.80)?.ToList();

               writeToTrace("Account Match Response. Number of matches found: " + accountMatches.Count.ToString() + " of total " + accountMatchResponse.Matches.Count.ToString()
                                                                                                                                                                            , tracingService);
                if (accountMatches == null || accountMatches.Count == 0)
                    return response;
                #endregion

                #region Process Matches
                response["existsAccount"] = true;
                if (accountMatches.Count > 1)
                {
                    AccountMatch accountMatch = accountMatches.First();
                    //Entity matchingAccount = service.Retrieve("account", accountMatch.AccountId, new ColumnSet(true));
                    string tsOrgId = accountMatch.TSOrgId;
                    response["tsOrgId"] = tsOrgId;
                    /*Todo: Deal with duplicates later
                    */
                    //string[] matchingTsOrgIds = accountMatches.Select(match => match.TSOrgId).ToArray();

                    //string matchingTsOrgIdsCsv = string.Join(", ", matchingTsOrgIds);


                    //string dupesNoteDesc = "TSOrgIds of matching orgs: " + Environment.NewLine + Environment.NewLine;
                    //dupesNoteDesc += matchingTsOrgIdsCsv + Environment.NewLine + Environment.NewLine;
                    //dupesNoteDesc += "Routing case to Duplicate Review queue";
                    //processSystemNote("Initial Duplicate Check - Org Matches Found", dupesNoteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id)
                    //                                                                                                                                                                        , service, tracingService);


                    //validationRequestCase["ts_casestatus"] = new OptionSetValue(104696); //OQ - AutoValidation - Duplicate Review
                    //service.Update(validationRequestCase);

                    //if (!string.IsNullOrEmpty(duplicateReviewQueue))
                    //    QualificationHelper.addCaseToQueue(validationRequestCase.Id, duplicateReviewQueue
                    //                                                                        , service, tracingService);

                    //response["validationProcessAction"] = "terminate";
                }
                else if (accountMatches.Count == 1)
                {
                    AccountMatch accountMatch = accountMatches.First();
                    //Entity matchingAccount = service.Retrieve("account", accountMatch.AccountId, new ColumnSet(true));
                    string tsOrgId = accountMatch.TSOrgId;

                    writeToTrace("Account Match Request. One match tsOrgId: " + tsOrgId
                                               , tracingService);
                    response["tsOrgId"] = tsOrgId;
                }
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                writeToTrace("Error in findOrganizationAccountMatches(...). Exception message: " + Environment.NewLine + e.Message
                                                //+ Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                , tracingService);


            }
            #endregion
            return response;
        }


        public static List<AccountMatch> getOrganizationMatchList(Entity organization
                                                                                    , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            
            try
            {

                writeToTrace("Starting getOrganizationMatchList(...)"
                                                                            , tracingService);



                MatchConfiguration config = new MatchConfiguration()
                {
                    NameWeight = 0.5,
                    AddressWeight = 0.3,
                    PostalCodeWeight = 0.1,
                    StateProvinceWeight = 0.1,
                    CountryWeight = 0,
                    WebsiteWeight = 0,
                    LegalIdWeight = 0,
                    PhoneWeight = 0
                };


                string name = organization.GetAttributeValue<string>("orgName");
                string phone = organization.GetAttributeValue<string>("phone") ?? "";

                Entity address = organization.GetAttributeValue<Entity>("address");
                string address1 = address.GetAttributeValue<string>("address1");
                string stateProvince = address.GetAttributeValue<string>("regionCode") ?? "";
                string postalCode = address.GetAttributeValue<string>("postalCode") ?? "";
                string countryCode = address.GetAttributeValue<string>("countryCode");



                IDictionary<string, System.Object> matchRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;
                //matchRequest["legalId"] = legalId;
                matchRequest["name"] = name;
                matchRequest["address1"] = address1;
                matchRequest["stateProvince"] = stateProvince;
                matchRequest["postalCode"] = postalCode;
                matchRequest["countryCode"] = countryCode;
                //matchRequest["website"] = website;
                matchRequest["phone"] = phone;

                string matchRequestText = JsonConvert.SerializeObject(matchRequest);

                writeToTrace("Account Match Request: " + Environment.NewLine + matchRequestText
                                                                                                , tracingService);

                AccountMatchService accountMatchService = new AccountMatchService(service
                                                                                    , EDServicesHelper.DynamicsEnvironments
                                                                                    , EDServicesHelper.EnvVariables
                                                                                    , config);

                AccountMatchResponse accountMatchResponse = accountMatchService.FindMatches(matchRequestText);

                List<AccountMatch> accountMatches = accountMatchResponse.Matches.Where(Match => Match.OverallScore >= 0.80)?.ToList();

                writeToTrace("Account Match Response. Number of matches found: " + accountMatches.Count.ToString() + " of total " + accountMatchResponse.Matches.Count.ToString()
                                                                                                                                                                        , tracingService);


                return accountMatches;


            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getOrganizationMatchList(...). " + error, tracingService);
                errorStack.Add(error);
                return null;
            }
            #endregion
           
        }
        public static void addOptionToFieldCollection(Entity entity, string fieldName, int optionValue
                                                                                            , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                OptionSetValueCollection optionCollection = entity.GetAttributeValue<OptionSetValueCollection>(fieldName);

                if (optionCollection == null)
                    optionCollection = new OptionSetValueCollection()
                        {
                            new OptionSetValue(optionValue)
                        };
                else if (!optionCollection.ToList().Exists(option => option.Value == optionValue))
                    optionCollection.Add(new OptionSetValue(optionValue));


                entity[fieldName] = optionCollection;

                service.Update(entity);
            }
            catch (Exception e)
            {
                string error = "Error in addOptionToFieldCollection(...). Exception message: " + Environment.NewLine + e.Message
                   + Environment.NewLine + "entity: " + entity.LogicalName + "; fieldName: " + fieldName + "; optionValue: " + optionValue.ToString()
                   ;
                writeToTrace(error, tracingService);
                //errorStack.Add(error);
            }
        }
        public static Entity createNgo(Entity organization
                                                          , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            writeToTrace("Starting createNgo"
                                             , tracingService);


            #region Parameters
            Guid accountId = Guid.Empty;
            string tsOrgId = string.Empty;
            Entity orgAccount = null;
            #endregion

            try
            {
                #region New Account Entity
                orgAccount = new Entity("account");

                orgAccount["customertypecode"] = new OptionSetValue(14); //NGO
                orgAccount["new_source"] = new OptionSetValue(101892); //TSS Web Site 101892

                orgAccount["ts_accounttype"] = new OptionSetValueCollection()
                                                    {
                                                        new OptionSetValue(14)
                                                    };
                #endregion

               
                orgAccount["name"] = organization.GetAttributeValue<string>("orgName");
                orgAccount["telephone1"] = organization.GetAttributeValue<string>("phone");
                orgAccount["emailaddress1"] = organization.GetAttributeValue<string>("email");



                #region Address
                Entity address = organization.GetAttributeValue<Entity>("address");
                orgAccount["address1_country"] = address.GetAttributeValue<string>("countryCode");
                orgAccount["address1_stateorprovince"] = address.GetAttributeValue<string>("regionCode");
                orgAccount["address1_line1"] = address.GetAttributeValue<string>("address1");
                orgAccount["address1_line2"] = address.Contains("address2") ? address.GetAttributeValue<string>("address2") : null;
                orgAccount["address1_city"] = address.GetAttributeValue<string>("city");
                orgAccount["address1_postalcode"] = address.GetAttributeValue<string>("postalCode");


                #region Country And State Hierarchy Mapping
                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, address.GetAttributeValue<string>("countryCode"));
                EntityCollection fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    orgAccount["ts_countrydesc"] = new OptionSetValue(countryOptionValue);
                }


                queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, address.GetAttributeValue<string>("countryCode"));
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, address.GetAttributeValue<string>("regionCode"));
                fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int stateRegionOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    orgAccount["ts_stateprovdesc"] = new OptionSetValue(stateRegionOptionValue);
                }
                #endregion
                #endregion

                accountId = service.Create(orgAccount);

              writeToTrace("Created NGO Account with AccountId: " + accountId.ToString()
                                             , tracingService);

                orgAccount = service.Retrieve("account", accountId, new ColumnSet(true));

                return orgAccount;

            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error in createNgo(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "organization: " + Environment.NewLine + JsonConvert.SerializeObject(organization)
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;

              


            }
            #endregion

            
        }
        public static string getNextTsCustomerId(Dictionary<string, string> envVariables, ITracingService tracingService)
        {
            string tsCustomerId = string.Empty;
            try
            {
                X509Certificate2 cer = GetVaultCertificate(envVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;

                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(envVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();

                objRequest.ServerName = envVariables["ts_Sql2kServer"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_getNextOnyxId";



                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                if (returnXml.Length > 0)
                    tsCustomerId = returnXml.First().Any[0].InnerText;

            }
            catch (Exception e)
            {
                writeToTrace("Error in getNextTsCustomerId(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                        , tracingService);
            }

            return tsCustomerId;
        }


        public static Entity connectContactToAccount(string tsOrgId, Guid accountId, Guid contactId
                                                                                    , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                string connectionRoleToName = "Staff";
                string connectionRoleFromName = "Contact";

                Entity connectionEntity = null;

                #region Check If Connection Already Exists
                QueryExpression queryConnection = new QueryExpression("connection");
                queryConnection.ColumnSet = new ColumnSet(true);
                queryConnection.Criteria.AddCondition("record1id", ConditionOperator.Equal, accountId);
                queryConnection.Criteria.AddCondition("record2id", ConditionOperator.Equal, contactId);
                EntityCollection connectionCollection = service.RetrieveMultiple(queryConnection);

                if (connectionCollection.Entities.Count > 0)
                {
                    connectionEntity = connectionCollection.Entities.First();
                    int stateCode = connectionEntity.GetAttributeValue<OptionSetValue>("statecode").Value;
                    if ( stateCode == 1) //stateCode = 1: inactive
                    {
                        connectionEntity["statecode"] = new OptionSetValue(0);
                        connectionEntity["statuscode"] = new OptionSetValue(1);
                        service.Update(connectionEntity);
                    }

                    
                    return connectionEntity;
                }
                #endregion


                connectionEntity = new Entity("connection");


                #region Connection Roles
                QueryExpression queryConnectionRole = new QueryExpression("connectionrole");
                queryConnectionRole.Criteria.AddCondition("name", ConditionOperator.Equal, connectionRoleToName);
                EntityCollection connectionRoleCollection = service.RetrieveMultiple(queryConnectionRole);

                if (connectionRoleCollection.Entities.Count == 0)
                    return null;

                Guid connectionRoleToId = connectionRoleCollection.Entities.First().Id;


                QueryExpression queryConnectionFromRole = new QueryExpression("connectionrole");
                queryConnectionFromRole.Criteria.AddCondition("name", ConditionOperator.Equal, connectionRoleFromName);
                EntityCollection connectionRoleFromCollection = service.RetrieveMultiple(queryConnectionFromRole);

                if (connectionRoleFromCollection.Entities.Count == 0)
                    return null;

                Guid connectionRoleFromId = connectionRoleFromCollection.Entities.First().Id;
                #endregion

                #region Connection Attributes & Create
                connectionEntity["record1id"] = new EntityReference("account", accountId);
                connectionEntity["record1objecttypecode"] = new OptionSetValue(1);

                connectionEntity["record2id"] = new EntityReference("contact", contactId);
                connectionEntity["record2objecttypecode"] = new OptionSetValue(2);

                connectionEntity["record1roleid"] = new EntityReference("connectionrole", connectionRoleFromId);
                connectionEntity["record2roleid"] = new EntityReference("connectionrole", connectionRoleToId);



                Guid connectionId = service.Create(connectionEntity);

                connectionEntity = service.Retrieve("connection", connectionId, new ColumnSet(true));
                return connectionEntity;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error in connectContactToAccount(...). Exception message: " + Environment.NewLine + e.Message
                  + Environment.NewLine + "tsOrgId: " + tsOrgId
                  ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
            #endregion
        }


        public static Entity connectAccountToAccount(string accountFromTsOrgId, Guid accountFromId, Guid accountToId
                                                                                    , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                string connectionRoleToName = "ED Applicant";
                string connectionRoleFromName = "ED Requestor";

                Entity connectionEntity = null;

                #region Check If Connection Already Exists
                QueryExpression queryConnection = new QueryExpression("connection");
                queryConnection.ColumnSet = new ColumnSet(true);
                queryConnection.Criteria.AddCondition("record1id", ConditionOperator.Equal, accountFromId);
                queryConnection.Criteria.AddCondition("record2id", ConditionOperator.Equal, accountToId);
                EntityCollection connectionCollection = service.RetrieveMultiple(queryConnection);

                if (connectionCollection.Entities.Count > 0)
                {
                    connectionEntity = connectionCollection.Entities.First();
                    int stateCode = connectionEntity.GetAttributeValue<OptionSetValue>("statecode").Value;
                    if (stateCode == 1) //stateCode = 1: inactive
                    {
                        connectionEntity["statecode"] = new OptionSetValue(0);
                        connectionEntity["statuscode"] = new OptionSetValue(1);
                        service.Update(connectionEntity);
                    }


                    return connectionEntity;
                }
                #endregion


                connectionEntity = new Entity("connection");


                #region Connection Roles
                QueryExpression queryConnectionRole = new QueryExpression("connectionrole");
                queryConnectionRole.Criteria.AddCondition("name", ConditionOperator.Equal, connectionRoleToName);
                EntityCollection connectionRoleCollection = service.RetrieveMultiple(queryConnectionRole);

                if (connectionRoleCollection.Entities.Count == 0)
                    return null;

                Guid connectionRoleToId = connectionRoleCollection.Entities.First().Id;


                QueryExpression queryConnectionFromRole = new QueryExpression("connectionrole");
                queryConnectionFromRole.Criteria.AddCondition("name", ConditionOperator.Equal, connectionRoleFromName);
                EntityCollection connectionRoleFromCollection = service.RetrieveMultiple(queryConnectionFromRole);

                if (connectionRoleFromCollection.Entities.Count == 0)
                    return null;

                Guid connectionRoleFromId = connectionRoleFromCollection.Entities.First().Id;
                #endregion

                #region Connection Attributes & Create
                connectionEntity["record1id"] = new EntityReference("account", accountFromId);
                connectionEntity["record1objecttypecode"] = new OptionSetValue(1);

                connectionEntity["record2id"] = new EntityReference("account", accountToId);
                connectionEntity["record2objecttypecode"] = new OptionSetValue(1);

                connectionEntity["record1roleid"] = new EntityReference("connectionrole", connectionRoleFromId);
                connectionEntity["record2roleid"] = new EntityReference("connectionrole", connectionRoleToId);



                Guid connectionId = service.Create(connectionEntity);

                connectionEntity = service.Retrieve("connection", connectionId, new ColumnSet(true));
                return connectionEntity;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error in connectAccountToAccount(...). Exception message: " + Environment.NewLine + e.Message
                  + Environment.NewLine + "accountFromTsOrgId: " + accountFromTsOrgId
                  ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
            #endregion
        }

        public static Entity addContact(Entity edContact
                                                          , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                
                #region FindIfExists
                QueryExpression queryContact = new QueryExpression("contact");
                queryContact.ColumnSet = new ColumnSet(true);
                queryContact.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, edContact.GetAttributeValue<string>("email"));
                EntityCollection contactCollection = service.RetrieveMultiple(queryContact);

                if (contactCollection.Entities.Count > 0)
                    return contactCollection.Entities.First();
                #endregion

                #region CreateContact
                Entity contact = new Entity("contact");
                contact["firstname"] = edContact.GetAttributeValue<string>("firstName");
                contact["lastname"] = edContact.GetAttributeValue<string>("lastName");

                contact["emailaddress1"] = edContact.GetAttributeValue<string>("email");
                contact["ts_emailvalidationstatus"] = new OptionSetValue(4);

                string tsContactId = getNextTsCustomerId(EnvVariables, tracingService);
                contact["new_contactaccountnumber"] = tsContactId;
                contact["adx_identity_username"] = edContact.Contains("userName") ? edContact.GetAttributeValue<string>("userName") : "contact." + tsContactId;

                contact["new_source"] = new OptionSetValue(101561); //101561 - web

                if (edContact.Contains("address"))
                {
                    Entity address = edContact.GetAttributeValue<Entity>("address");
                    contact["address1_country"] = address.GetAttributeValue<string>("countryCode");
                    contact["address1_stateorprovince"] = address.GetAttributeValue<string>("regionCode");
                    contact["address1_line1"] = address.GetAttributeValue<string>("address1");
                    contact["address1_line2"] = address.Contains("address2") ? address.GetAttributeValue<string>("address2") : null;
                    contact["address1_city"] = address.GetAttributeValue<string>("city");
                    contact["address1_postalcode"] = address.GetAttributeValue<string>("postalCode");


                    QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                    queryFieldMap.ColumnSet = new ColumnSet(true);
                    queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                    queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, address.GetAttributeValue<string>("countryCode"));
                    EntityCollection fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                    if (fieldMapCollection.Entities.Count > 0)
                    {
                        Entity fieldHierarchy = fieldMapCollection.Entities.First();
                        int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                        contact["ts_countrydesc"] = new OptionSetValue(countryOptionValue);
                    }

                    if (!string.IsNullOrEmpty(address.GetAttributeValue<string>("regionCode")))
                    {
                        queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                        queryFieldMap.ColumnSet = new ColumnSet(true);
                        queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                        queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, address.GetAttributeValue<string>("countryCode"));
                        queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, address.GetAttributeValue<string>("regionCode"));
                        fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                        if (fieldMapCollection.Entities.Count > 0)
                        {
                            Entity fieldHierarchy = fieldMapCollection.Entities.First();
                            int stateRegionOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                            contact["ts_stateprovdesc"] = new OptionSetValue(stateRegionOptionValue);
                        }
                    }
                }

                Guid contactId = service.Create(contact);

                contact = service.Retrieve(contact.LogicalName, contactId, new ColumnSet(true));


                return contact;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error in addContact(...). Exception message: " + Environment.NewLine + e.Message
                   + Environment.NewLine + "contact: " + Environment.NewLine + JsonConvert.SerializeObject(edContact)
                   ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
            #endregion
        }

        public static Entity findContact(string tsContactId
                                                          , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                #region FindIfExists
                QueryExpression queryContact = new QueryExpression("contact");
                queryContact.ColumnSet = new ColumnSet(true);
                queryContact.Criteria.AddCondition("new_contactaccountnumber", ConditionOperator.Equal, tsContactId);
                EntityCollection contactCollection = service.RetrieveMultiple(queryContact);

                if (contactCollection.Entities.Count == 0)
                    return null;

                return contactCollection.Entities.First();
                #endregion

            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error in findContact(...). Exception message: " + Environment.NewLine + e.Message
                   + Environment.NewLine + "tsContactId: " + tsContactId
                   ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
            #endregion
        }


        public static Entity getAccountByTsOrgId(string tsOrgId
                                                          , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            writeToTrace("Starting getAccountByTsOrgId"
                                             , tracingService);



            try
            {
                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(true);
                queryAccount.Criteria.AddCondition("accountnumber", ConditionOperator.Equal, tsOrgId);
                EntityCollection accountCollection = service.RetrieveMultiple(queryAccount);

                if (accountCollection.Entities.Count == 0)
                {
                    string error = "No account found with tsOrgId: " + tsOrgId;
                    writeToTrace(error, tracingService);
                    errorStack.Add(error);
                    return null;
                }

                Entity account = accountCollection.Entities.First();
                return account;
            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error in getAccountByTsOrgId(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "tsOrgId: " + tsOrgId
                    ;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;


            }
            #endregion


        }
        public static void processSystemNote(string noteTitle, string noteDesc, EntityReference annotationParentRef
                                                                                                    , IOrganizationService service, ITracingService tracingService)
        {
            try
            {

                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = service.RetrieveMultiple(queryAnnotation);

                Entity annotation = null;

                bool existsNote = false;
                if (annotationCollection.Entities.Count() > 0)
                {
                    existsNote = true;
                    annotation = annotationCollection.Entities.First();
                }
                else
                {
                    annotation = new Entity("annotation");
                    annotation["subject"] = noteTitle;
                    annotation["objectid"] = new EntityReference("incident", annotationParentRef.Id);
                }

                var noteDirectives = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                noteDirectives.Add("sectionStart", "NoteSpecialDirectives");
                noteDirectives.Add("systemNote", true);
                noteDirectives.Add("sectionEnd", "NoteSpecialDirectives");
                string noteDirectivesJson = JsonConvert.SerializeObject(noteDirectives);

                noteDesc += string.Concat(Enumerable.Repeat(Environment.NewLine, 8).ToArray()) + noteDirectivesJson;

                annotation["notetext"] = noteDesc;

                if (existsNote)
                {
                    service.Update(annotation);
                }
                else
                {
                    Guid annotationId = service.Create(annotation);
                }


            }
            catch (Exception e)
            {
                writeToTrace("Error in processSystemNote(). Exception message: " + Environment.NewLine + e.Message
                                                                                                                                , tracingService);
            }
        }
        public static dynamic makeHttpPostCall(string requestJson
                                                           , string requestUrl
                                                            , ITracingService tracingService
                                                            , List<string> errorStack
                                                            , Dictionary<string, string> extraHeaders = null
                                                            )
        {
            try
            {
                #region Initialize HttpClient and Headers
                dynamic respDynObject = null;

                HttpClient client = new HttpClient();
                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                if (extraHeaders != null)
                {
                    foreach (KeyValuePair<string, string> header in extraHeaders)
                    {
                        headers.Add(header.Key, header.Value);
                    }
                }
                #endregion


                #region Create Request Content & Send POST Request
                StringContent contentRequest = new StringContent(requestJson, Encoding.UTF8, "application/json");

                HttpResponseMessage response = client.PostAsync(
                                                                requestUrl
                                                                , contentRequest
                                                                 )
                                                                    .Result;
                #endregion


                #region Read Response & Return
                string responseTxt = response.Content.ReadAsStringAsync().Result;
                Console.WriteLine("Response: " + responseTxt);
                respDynObject = JsonConvert.DeserializeObject(responseTxt);
                client.Dispose();
                return respDynObject;
                #endregion

            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error in makeHttpPostCall(...). Exception message: " + Environment.NewLine + e.Message;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
            #endregion

        }

        public static dynamic makeMsAuthPostCall(string requestJson
                                                            , string baseUrl, string endPointPath
                                                            , string key, Dictionary<string, string> queryParams
                                                            , string clientId, string clientSecret
                                                            , ITracingService tracingService, List<string> errorStack)
        {
            dynamic respDynObject = null;

            try
            {
                #region Initialize HttpClient, AuthToken and Headers
                HttpClient client = new HttpClient();

                string accessToken = getMSAuthToken(baseUrl, tracingService, errorStack);

                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                headers.Add("OData-MaxVersion", "4.0");
                headers.Add("OData-Version", "4.0");
                headers.Accept.Add(
                                    new MediaTypeWithQualityHeaderValue("application/json")
                                    );

                #endregion




                #region Set Base URL, Endpoint Path, Key, and Query Parameters
                string queryString = "";
                if (queryParams != null && queryParams.Count > 0)
                {
                    queryString = string.Join("&", queryParams.Select(kvp => $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));
                    queryString = "?" + queryString;
                }
                string requestUrl = baseUrl + endPointPath + key + queryString;
                #endregion



                




                #region Create Request Content & Send POST Request
                StringContent contentRequest = new StringContent(requestJson, Encoding.UTF8, "application/json");

                writeToTrace("At makeMsAuthPostCall(...)  right before PostAsync with GetAwaiter() - "
                    + Environment.NewLine + "requestUrl: " + requestUrl + Environment.NewLine + "requestJson: " + requestJson
                    , tracingService);

                HttpResponseMessage response = client.PostAsync(
                                                                requestUrl
                                                                , contentRequest
                                                                )
                                                                .GetAwaiter().GetResult();

                #endregion


                writeToTrace("At makeMsAuthPostCall(...) right after PostAsync"
                                                                            , tracingService);



                #region Read Response & Return
                string responseTxt = response.Content.ReadAsStringAsync().Result;
                respDynObject = JsonConvert.DeserializeObject(responseTxt);
                //client.Dispose();
                #endregion

                writeToTrace("At makeMsAuthPostCall(...)  after PostAsync - "
                                + Environment.NewLine + "responseTxt: " + responseTxt
                                , tracingService);

                return respDynObject;
            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error in makeMsAuthPostCall(...). Exception message: " + Environment.NewLine + e.Message;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
            #endregion

            //return respDynObject;
        }



        public static string getMSAuthToken(string resource
                                                        , ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                #region Credentials & URL
                //string resource = DynamicsEnvironment;
                string clientId = EnvVariables["ts_TSDynamicsClientId"];
                string clientSecret = EnvVariables["ts_TSDynamicsClientSecret"];
                string tenantId = "d8ba2331-6b05-4303-9a60-36c58c3e272d";
                string url = "https://login.microsoftonline.com/" + tenantId + "/oauth2/v2.0/token";
                #endregion


                #region Create Dictionary for POST parameters
                Dictionary<string, string> parameters = new Dictionary<string, string>();
                parameters.Add("client_id", clientId);
                parameters.Add("scope", resource + "/.default");
                parameters.Add("client_secret", clientSecret);
                parameters.Add("grant_type", "client_credentials");
                #endregion


                #region Initialize HttpClient and Headers
                HttpClient clientAuth = new HttpClient();

                HttpRequestHeaders headersAuth = clientAuth.DefaultRequestHeaders;
                headersAuth.Accept.Add(
                                        new MediaTypeWithQualityHeaderValue("application/json")
                                        );
                #endregion


                #region Send POST Request and Read Response
                HttpResponseMessage responseAuth = clientAuth.PostAsync(url
                                                                            , new FormUrlEncodedContent(parameters)
                                                                            ).Result;


                string responseAuthTxt = responseAuth.Content.ReadAsStringAsync().Result;
                dynamic responseAuthObject = JsonConvert.DeserializeObject(responseAuthTxt);

                string token = responseAuthObject.access_token;
                return token;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                string error = "Error in getMSAuthToken(...). Exception message: " + Environment.NewLine + e.Message;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
                return null;
            }
            #endregion

        }

        
        public static Entity CallBoxApiServiceDirect(string jsonRequest
                                                                , ITracingService tracingService)
        {
            try
            {
                #region Initialization & Parameters
                string accessToken = GetBoxAccessToken(tracingService);

                EmbeddedBoxApiService boxService = new EmbeddedBoxApiService(accessToken, tracingService);
                #endregion


                #region Call BoxApiService.ProcessBoxRequestForDynamics
                EDServicesHelper.writeToTrace("Calling BoxApiService.ProcessBoxRequestForDynamics directly", tracingService);

                Entity resultEntity = boxService.ProcessBoxRequestForDynamics(jsonRequest);

                EDServicesHelper.writeToTrace("BoxApiService direct call completed", tracingService);
                #endregion

                return resultEntity;
            }
            #region Catch
            catch (Exception ex)
            {
                EDServicesHelper.writeToTrace($"Error calling BoxApiService direct: {ex.Message}", tracingService);

                Entity errorEntity = new Entity();
                if (!errorEntity.Attributes.Contains("success"))
                {
                    errorEntity.Attributes.Add("success", false);
                }
                if (!errorEntity.Attributes.Contains("message"))
                {
                    errorEntity.Attributes.Add("message", $"BoxApiService direct call failed: {ex.Message}");
                }
                if (!errorEntity.Attributes.Contains("error"))
                {
                    errorEntity.Attributes.Add("error", ex.ToString());
                }

                return errorEntity;
            }
            #endregion
        }

        public static string GetBoxAccessToken(
                                                 ITracingService tracingService)
        {
            try
            {
                writeToTrace("Starting Box Client Credentials Grant authentication", tracingService);

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    // Set up the token endpoint
                    string tokenEndpoint = "https://api.box.com/oauth2/token";

                    // Prepare the request body for Client Credentials Grant
                    var requestBody = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("grant_type", "client_credentials"),
                        new KeyValuePair<string, string>("client_id", BoxClientId),
                        new KeyValuePair<string, string>("client_secret", BoxClientSecret),
                        new KeyValuePair<string, string>("box_subject_type", "enterprise"),
                        new KeyValuePair<string, string>("box_subject_id", "555277")
                    };

                    writeToTrace("Sending Client Credentials Grant request to Box", tracingService);

                    // Create form-encoded content
                    var formContent = new System.Net.Http.FormUrlEncodedContent(requestBody);

                    // Set headers
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "BoxInterface/1.0");
                    httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

                    // Make the token request
                    var response = httpClient.PostAsync(tokenEndpoint, formContent).GetAwaiter().GetResult();
                    var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    writeToTrace("Box token response status: {response.StatusCode}", tracingService);

                    if (response.IsSuccessStatusCode)
                    {
                        // Parse the token response
                        var tokenResponse = JsonConvert.DeserializeObject<BoxTokenResponse>(responseContent);

                        if (tokenResponse != null && !string.IsNullOrEmpty(tokenResponse.AccessToken))
                        {
                            writeToTrace("Successfully obtained Box access token, expires in {tokenResponse.ExpiresIn} seconds", tracingService);
                            return tokenResponse.AccessToken;
                        }
                        else
                        {
                            throw new Exception("Invalid token response from Box API");
                        }
                    }
                    else
                    {
                        writeToTrace("Box token request failed: {responseContent}", tracingService);
                        throw new Exception($"Box authentication failed: {response.StatusCode} - {responseContent}");
                    }
                }
            }
            catch (Exception e)
            {
                writeToTrace("Error getting Box access token: " + e.Message, tracingService);

                return null;
            }
        }



        public static string regexReplace(string pattern, string expresion, string replaceWith)
        {
            string convExpresion = expresion;

            Regex regexObj = new Regex(pattern);
            convExpresion = regexObj.Replace(convExpresion, replaceWith);

            return convExpresion;
        }

        public static bool regexMatch(string pattern, string input)
        {
            Regex regex = new Regex(@pattern);
            return regex.IsMatch(input);
        }

        public static int regexMatchPos(string pattern, string input, int startAt)
        {
            Regex regexObj = new Regex(@pattern);

            Match match = regexObj.Match(input, startAt);

            return match.Index;
        }

        public static string regexMatchValue(string pattern, string input, int startAt)
        {
            Regex regexObj = new Regex(@pattern);

            Match match = regexObj.Match(input, startAt);

            return match.Value;
        }

        public static bool regexMatchSuccess(string pattern, string input, int startAt)
        {
            Regex regexObj = new Regex(@pattern);

            Match match = regexObj.Match(input, startAt);

            return match.Success;
        }


    }
}
