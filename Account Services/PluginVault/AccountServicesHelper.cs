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
using System.Runtime.Remoting.Services;
using System.Runtime.Remoting.Contexts;
using AccountServices.DataAccessService;
using System.ServiceModel;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using System.Text.RegularExpressions;
using System.IdentityModel.Metadata;
using System.Xml;

namespace AccountServices
{
    public class AccountServicesHelper
    {
        //private Dictionary<string, string> _envVariables;
        //public  Dictionary<string, string> EnvVariables {
        //    get
        //    {  return _envVariables; }
        //    set 
        //    { _envVariables = value; }
        //}

        //public Dictionary<string, string> EnvVariables { get; set; }
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        public static Dictionary<string, string> GetEnvironmentVariables(IOrganizationService service, ITracingService tracingService)
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


                    //tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + $"schemaName: {schemaName}, value: {value}, defaultValue: {defaultValue}");
                    //tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                }
            }

            //EnvVariables = envVariables;

            return envVariables;


        }

        public static X509Certificate2 GetVaultCertificate(Dictionary<string, string> envVariables, ITracingService tracingService)
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
                AccountServicesHelper.writeToTrace("Error during GetVaultCertificate: " + e.Message
                                                                                                , tracingService);
            }

            return cer;
        }


        public static void updateOrgQualification(Guid accountId, Guid qualCodeId, int qualStatusCode, DateTime qualStatusDateUTC
                                                                                                                        , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet = new ColumnSet("ts_qualificationstatus");
                FilterExpression filterOrgQualification = new FilterExpression(LogicalOperator.And);
                filterOrgQualification.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                filterOrgQualification.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                queryOrgQualification.Criteria.AddFilter(filterOrgQualification);
                EntityCollection orgQualificationCollection = service.RetrieveMultiple(queryOrgQualification);


                foreach (Entity orgQualification in orgQualificationCollection.Entities)
                {
                    orgQualification["ts_qualificationstatus"] = new OptionSetValue(qualStatusCode);
                    orgQualification["ts_qualificationstatusdate"] = qualStatusDateUTC;
                    service.Update(orgQualification);

                    Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");
                    orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQualification.Id);
                    orgQualHistory["ts_qualificationstatusaction"] = new OptionSetValue(qualStatusCode);
                    orgQualHistory["ts_qualificationactiondate"] = DateTime.UtcNow;
                    //orgQualHistory["ts_onyxccaid"] = qualAction.CCA_primaryID;
                    orgQualHistory["ts_name"] = orgQualification.Id.ToString() + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                    service.Create(orgQualHistory);
                }




            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in updateOrgQualification(...). Exception message: "
                                                                    + Environment.NewLine + e.Message
                                                                    + Environment.NewLine + "accountId: " + accountId.ToString() + "; qualCodeId: " 
                                                                    + qualCodeId.ToString() + "; qualStatusCode: " + qualStatusCode.ToString()
                                                                    , tracingService);
            }
        }

        public static Guid createOrgQualification(Guid accountId, Guid qualCodeId, int qualStatusCode, DateTime qualStatusDateUTC
                                                                                , string tsOrgId, string qualCode
                                                                                , IOrganizationService service, ITracingService tracingService)
        {
            Guid orgQualId = Guid.Empty;
            try
            {
                Entity orgQualification = new Entity("ts_organizationqualification");



                orgQualification["ts_qualificationstatus"] = new OptionSetValue(qualStatusCode);
                orgQualification["ts_qualificationstatusdate"] = qualStatusDateUTC;
                orgQualification["ts_accountid"] = new EntityReference("account", accountId);
                orgQualification["ts_qualificationcodeid"] = new EntityReference("new_qualificationcode", qualCodeId);
                orgQualification["ts_name"] = tsOrgId.ToString() + " - " + qualCode;


                orgQualId = service.Create(orgQualification);



                Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");

                orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQualId);
                orgQualHistory["ts_qualificationstatusaction"] = new OptionSetValue(qualStatusCode);
                orgQualHistory["ts_qualificationactiondate"] = DateTime.UtcNow;
                //orgQualHistory["ts_onyxccaid"] = qualAction.CCA_primaryID;
                orgQualHistory["ts_name"] = orgQualId.ToString() + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                service.Create(orgQualHistory);

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in createOrgQualification(...). Exception message: "
                                                                    + Environment.NewLine + e.Message
                                                                    + Environment.NewLine + "accountId: " + accountId.ToString() + "; qualCodeId: " + qualCodeId.ToString()
                                                                    + Environment.NewLine + "tsOrgId: " + tsOrgId + "; qualCode: " + qualCode
                                                                    + "; qualStatusCode: " + qualStatusCode.ToString()
                                                                    , tracingService);
            }

            return orgQualId;
        }

        public static void processOrgQualification(Guid accountId, Guid qualCodeId, int qualStatusCode, string qualStatus
                                                                            , string tsOrgId, string qualCode
                                                                            , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                EntityCollection orgQualificationCollection = service.RetrieveMultiple(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                {
                    Entity orgQualification = orgQualificationCollection.Entities.First();


                    if (orgQualification.FormattedValues["ts_qualificationstatus"] != qualStatus)
                    {
                        orgQualification["ts_qualificationstatus"] = new OptionSetValue(qualStatusCode);
                        orgQualification["ts_qualificationstatusdate"] = DateTime.UtcNow;
                        service.Update(orgQualification);

                        Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");

                        orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQualification.Id);
                        orgQualHistory["ts_qualificationstatusaction"] = new OptionSetValue(qualStatusCode);
                        orgQualHistory["ts_qualificationactiondate"] = DateTime.UtcNow;
                        //orgQualHistory["ts_onyxccaid"] = qualAction.CCA_primaryID;
                        orgQualHistory["ts_name"] = orgQualification.Id.ToString() + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                        service.Create(orgQualHistory);
                    }
                }
                else
                {
                    createOrgQualification(accountId, qualCodeId, qualStatusCode, DateTime.UtcNow
                                                                    , tsOrgId, qualCode
                                                                    , service, tracingService);
                }

            }
            catch (Exception e)
            {

                AccountServicesHelper.writeToTrace("Error in processOrgQualification(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                        + Environment.NewLine + "accountId: " + accountId.ToString() + "; qualCodeId: " + qualCodeId.ToString()
                                                                        + Environment.NewLine + "tsOrgId: " + tsOrgId + "; qualCode: " + qualCode
                                                                        + "; qualStatusCode: " + qualStatusCode.ToString()
                                                                        , tracingService);
            }
        }


        public static void updateQualCase(Guid accountId, Guid qualCodeId, int caseStatus
                                                                    , string tsOrgId, string qualCode
                                                                    , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet("ts_casestatus");
                FilterExpression filterQualCase = new FilterExpression(LogicalOperator.And);
                filterQualCase.AddCondition("casetypecode", ConditionOperator.Equal, 2);
                filterQualCase.AddCondition("ts_type", ConditionOperator.Equal, 101996); //101996 - Organization Qualification
                filterQualCase.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                filterQualCase.AddCondition("accountid", ConditionOperator.Equal, accountId);
                queryQualCase.Criteria.AddFilter(filterQualCase);
                EntityCollection qualCaseCollection = service.RetrieveMultiple(queryQualCase);

                foreach (Entity caseEntity in qualCaseCollection.Entities)
                {
                    caseEntity["ts_casestatus"] = new OptionSetValue(caseStatus);

                    service.Update(caseEntity);
                }
            }
            catch (Exception e)
            {

                AccountServicesHelper.writeToTrace("Error in updateQualCase(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                        + Environment.NewLine + "accountId: " + accountId.ToString() + "; qualCodeId: " + qualCodeId.ToString()
                                                                        + Environment.NewLine + "tsOrgId: " + tsOrgId + "; qualCode: " + qualCode
                                                                        + "; caseStatus: " + caseStatus.ToString()
                                                                        , tracingService);
            }

        }

        public static void addToSystemIntegrationLog(string name, string entityName, string entityId, string tsOrgId, bool reloadOrg
                                                                                                            , string log, IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                Entity systemIntegrationLog = new Entity("ts_dynamicsonyxintegrationlog");
                systemIntegrationLog["ts_name"] = name;
                systemIntegrationLog["ts_entityname"] = entityName;
                systemIntegrationLog["ts_entityid"] = entityId;
                systemIntegrationLog["ts_tsorgid"] = tsOrgId;
                systemIntegrationLog["ts_log"] = log;
                systemIntegrationLog["ts_reloadorg"] = reloadOrg;

                Guid systemIntegrationLogId = service.Create(systemIntegrationLog);

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in addToSystemIntegrationLog(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                        + Environment.NewLine + "entityName: " + entityName + "; entityId: " + entityId
                                                                        , tracingService);
            }
        }
        public static void createDynamicsoOnyxIntegrationLog(string entityName, string entityId
                                                                                        , string error, IOrganizationService service, ITracingService tracingService)
        {

            try
            {
                Entity dynOnyxLog = new Entity("ts_dynamicsonyxintegrationlog");
                dynOnyxLog["ts_name"] = "dynamicstoonyx";
                dynOnyxLog["ts_entityname"] = entityName;
                dynOnyxLog["ts_entityid"] = entityId;
                dynOnyxLog["ts_log"] = error;

                Guid dynOnyxLogId = service.Create(dynOnyxLog);

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in createDynamicsoOnyxIntegrationLog(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                        + Environment.NewLine + "entityName: " + entityName + "; entityId: " + entityId
                                                                        , tracingService);
            }
        }


        public static string getNextTsCustomerId(Dictionary<string, string> envVariables, ITracingService tracingService)
        {
            string tsCustomerId = string.Empty;
            try
            {
                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(envVariables, tracingService);

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
                AccountServicesHelper.writeToTrace("Error in getNextTsCustomerId(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                        , tracingService);
            }

            return tsCustomerId;
        }


        public static void addLegalAddress(Entity account
                                                        , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                Entity address = null;

                QueryExpression queryEntity = new QueryExpression("customeraddress");
                FilterExpression filterEntity = new FilterExpression(LogicalOperator.And);
                filterEntity.AddCondition("parentid", ConditionOperator.Equal, account.Id);
                filterEntity.AddCondition("addresstypecode", ConditionOperator.Equal, 5);
                queryEntity.Criteria.AddFilter(filterEntity);
                EntityCollection entityCollection = service.RetrieveMultiple(queryEntity);


                if (entityCollection.Entities.Count > 0)
                    return;

                address = new Entity("customeraddress");
                address["addresstypecode"] = new OptionSetValue(5);
                address["parentid"] = new EntityReference("account", account.Id);
                address["objecttypecode"] = 1;


                address["country"] = account.GetAttributeValue<string>("address1_country");
                address["stateorprovince"] = account.GetAttributeValue<string>("address1_stateorprovince");
                address["line1"] = account.GetAttributeValue<string>("address1_line1");
                address["line2"] = account.GetAttributeValue<string>("address1_line2");
                address["line3"] = account.GetAttributeValue<string>("address1_line3");
                address["city"] = account.GetAttributeValue<string>("address1_city");
                address["postalcode"] = account.GetAttributeValue<string>("address1_postalcode");

                address["ts_countrydesc"] = account.GetAttributeValue<OptionSetValue>("ts_countrydesc");
                address["ts_stateprovdesc"] = account.GetAttributeValue<OptionSetValue>("ts_stateprovdesc");


                service.Create(address);

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in addLegalAddress(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                       , tracingService);
            }


        }

        public static int getAttributeOptionValue(string entityName, string entityAttribute, string choiceLabel
                                                                                                            , IOrganizationService service, ITracingService tracingService)
        {
            int optionSetValue = 0;
            try
            {
                RetrieveAttributeRequest retrieveRequest = new RetrieveAttributeRequest()
                {
                    EntityLogicalName = entityName,
                    LogicalName = entityAttribute
                };

                RetrieveAttributeResponse retrieveResponse = (RetrieveAttributeResponse)service.Execute(retrieveRequest);
                PicklistAttributeMetadata attributeMetadata = (PicklistAttributeMetadata)retrieveResponse.AttributeMetadata;
                OptionMetadataCollection attributeOptions = attributeMetadata.OptionSet.Options;
                OptionMetadata option = attributeOptions.ToList().Find(options => options.Label.UserLocalizedLabel.Label == choiceLabel);

                if (option != null)
                    optionSetValue = option.Value.Value;
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in getAttributeOptionValue(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                        + Environment.NewLine + "entityName: " + entityName + "; entityAttribute: " + entityAttribute + "; choiceLabel: " + choiceLabel
                                                                       , tracingService);
            }

            return optionSetValue;
        }


        public static bool existsAttributeOptionValue(string entityName, string entityAttribute, int optionSetValue
                                                                                                            , IOrganizationService service, ITracingService tracingService)
        {
            bool exists = false;
            try
            {
                RetrieveAttributeRequest retrieveRequest = new RetrieveAttributeRequest()
                {
                    EntityLogicalName = entityName,
                    LogicalName = entityAttribute
                };

                RetrieveAttributeResponse retrieveResponse = (RetrieveAttributeResponse)service.Execute(retrieveRequest);
                PicklistAttributeMetadata attributeMetadata = (PicklistAttributeMetadata)retrieveResponse.AttributeMetadata;
                OptionMetadataCollection attributeOptions = attributeMetadata.OptionSet.Options;
                OptionMetadata option = attributeOptions.ToList().Find(options => options.Value == optionSetValue);

                if (option != null)
                    exists = true;
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in existsAttributeOptionValue(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                        + Environment.NewLine + "entityName: " + entityName + "; entityAttribute: " + entityAttribute + "; optionSetValue: " + optionSetValue.ToString()
                                                                        , tracingService);
            }

            return exists;
        }



        public static Guid addAccountReference(string tsOrgId, Guid accountId, int refType, string refValue
                                                                                                    , IOrganizationService service, ITracingService tracingService)
        {
            Guid accountRefId = Guid.Empty;
            try
            {
                QueryExpression queryAccountReference = new QueryExpression("ts_accountreference");
                queryAccountReference.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                queryAccountReference.Criteria.AddCondition("ts_referencetype", ConditionOperator.Equal, refType);
                EntityCollection accountReferenceCollection = service.RetrieveMultiple(queryAccountReference);

                Entity accountReference = null;
                if (accountReferenceCollection.Entities.Count > 0)
                {
                    accountReference = accountReferenceCollection.Entities.First();
                    accountRefId = accountReference.Id;

                    accountReference["ts_referencevalue"] = refValue;
                    service.Update(accountReference);

                }
                else
                {
                    accountReference = new Entity("ts_accountreference");
                    accountReference["ts_accountid"] = new EntityReference("account", accountId);
                    accountReference["ts_referencetype"] = new OptionSetValue(refType);                    
                    accountReference["ts_referencevalue"] = refValue;

                    accountRefId = service.Create(accountReference);
                }

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in addAccountReference(...). Exception message: "
                                                                                    + Environment.NewLine + e.Message
                                                                                    + Environment.NewLine + "TSOrgId: " + tsOrgId
                                                                                    , tracingService);
            }

            return accountRefId;
        }


        public static Entity findMatchAccount(string name, string legalIdentifier, string addressLine1, string addressPostalCode, Guid accountId
                                                                                                                                            , IOrganizationService service, ITracingService tracingService)
        {
            Entity firstMatchAccount = null;
            try
            {
                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(true);
                queryAccount.Criteria.AddCondition("name", ConditionOperator.Equal, name);
                if (legalIdentifier == null)
                {
                    queryAccount.Criteria.AddCondition("new_legalidentifier", ConditionOperator.Null);
                }
                else
                {
                    queryAccount.Criteria.AddCondition("new_legalidentifier", ConditionOperator.Equal, legalIdentifier);
                }                
                queryAccount.Criteria.AddCondition("address1_line1", ConditionOperator.BeginsWith, addressLine1);
                queryAccount.Criteria.AddCondition("address1_postalcode", ConditionOperator.BeginsWith, addressPostalCode);
                queryAccount.Criteria.AddCondition("accountid", ConditionOperator.NotEqual, accountId);
                EntityCollection accountCollection = service.RetrieveMultiple(queryAccount);

                if (accountCollection.Entities.Count > 0)
                    firstMatchAccount = accountCollection.Entities.First();
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in findMatchAccount(...).Exception message: "
                                                                                    + Environment.NewLine + e.Message
                                                                                    , tracingService);
            }

            return firstMatchAccount;
        }

        public static string generateNewAssocCode()
        {
            string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            char[] stringChars = new char[12];
            Random random = new Random();

            for (int i = 0; i < stringChars.Length; i++)
            {
                stringChars[i] = chars[random.Next(chars.Length)];
            }

            string finalString = new String(stringChars);

            finalString = finalString.Insert(8, "-").Insert(4, "-");

            return finalString;
        }


        public static Entity getCaseEntity(int caseTypeCode, int type
                                                               , Guid accountId, Guid? qualCodeId, string tsOrderId
                                                               , IOrganizationService service, ITracingService tracingService)
        {
            Entity caseEntity = null;

            try
            {
                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet("ts_casestatus");
                FilterExpression filterQualCase = new FilterExpression(LogicalOperator.And);
                filterQualCase.AddCondition("casetypecode", ConditionOperator.Equal, caseTypeCode);
                filterQualCase.AddCondition("ts_type", ConditionOperator.Equal, type);
                filterQualCase.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                filterQualCase.AddCondition("accountid", ConditionOperator.Equal, accountId);
                if (tsOrderId != null)
                    filterQualCase.AddCondition("ts_tsorderid", ConditionOperator.Equal, tsOrderId);
                queryQualCase.Criteria.AddFilter(filterQualCase);
                queryQualCase.AddOrder("createdon", OrderType.Descending);
                queryQualCase.TopCount = 1;
                EntityCollection qualCaseCollection = service.RetrieveMultiple(queryQualCase);

                if (qualCaseCollection.Entities.Count > 0)
                {
                    caseEntity = qualCaseCollection.Entities.First();
                    return caseEntity;
                }
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in getCaseEntity(...). Exception message: "
                                                                            + Environment.NewLine + e.Message
                                                                            + Environment.NewLine + "caseTypeCode: " + caseTypeCode.ToString() + "; type: " + type.ToString() + "; accountId: " 
                                                                            + accountId.ToString() + "; qualCodeId: " + qualCodeId.ToString()
                                                                            , tracingService);
            }

            return caseEntity;
        }


        public static string getOrgQualStatus(Guid accountId, Guid qualCodeId
                                                                        , string tsOrgId, string qualCode
                                                                        , IOrganizationService service, ITracingService tracingService)
        {
            string orgQualStatus = string.Empty;
            try
            {
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
                AccountServicesHelper.writeToTrace("Error in getOrgQualStatus(...). Exception message: "
                                                                            + Environment.NewLine + e.Message
                                                                            + Environment.NewLine + "accountId: " + accountId.ToString() + "; qualCodeId: " + qualCodeId.ToString()
                                                                            + Environment.NewLine + "tsOrgId: " + tsOrgId + "; qualCode: " + qualCode
                                                                            , tracingService);



            }

            return orgQualStatus;
        }


        public static dynamic getBusinessUnitAndTeam(string businessUnit
                                                                        , IOrganizationService service, ITracingService tracingService)
        {
            AccountServicesHelper.writeToTrace($"getBusinessUnitAndTeam. Returning: dynamic businessUniTeam"
                                                                            , tracingService);
            dynamic businessUniTeam = new JObject();
            try
            {
                QueryExpression queryBusinessUnit = new QueryExpression("businessunit");
                queryBusinessUnit.ColumnSet = new ColumnSet("businessunitid", "name");
                queryBusinessUnit.Criteria.AddCondition("name", ConditionOperator.Equal, businessUnit);
                EntityCollection businessUnitCollection = service.RetrieveMultiple(queryBusinessUnit);

                if (businessUnitCollection.Entities.Count == 0)
                {
                    AccountServicesHelper.writeToTrace($"assignAccountsToBusUnit: Business unit '{businessUnit}' not found."
                                                                                                , tracingService);
                    return businessUniTeam;
                }

                Entity targetBU = businessUnitCollection.Entities.First();
                businessUniTeam.businessUnitId = targetBU.Id;


                QueryExpression queryTeam = new QueryExpression("team");
                queryTeam.ColumnSet = new ColumnSet("teamid", "name", "teamtype");
                queryTeam.Criteria.AddCondition("businessunitid", ConditionOperator.Equal, targetBU.Id);
                queryTeam.Criteria.AddCondition("name", ConditionOperator.Equal, businessUnit);
                queryTeam.Criteria.AddCondition("teamtype", ConditionOperator.Equal, 0); // 0 = Owner team (default team)
                EntityCollection teamCollection = service.RetrieveMultiple(queryTeam);


                if (teamCollection.Entities.Count == 0)
                    return businessUniTeam;

                businessUniTeam.teamId = teamCollection.Entities.First().Id;
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace($"Error in getBusinessUnitAndTeam. Exception message: {Environment.NewLine}{e.Message}"
                                                                            , tracingService);
            }


            return businessUniTeam;
        }

        public static dynamic getConfigFromFieldMapping(string fieldName
                                                                           , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryMapping.ColumnSet = new ColumnSet(true);
                queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, fieldName);
                EntityCollection mappingCollection = service.RetrieveMultiple(queryMapping);

                if (mappingCollection.Entities.Count == 0)
                {
                    writeToTrace($"Error in updateConfigOnFieldMapping(). ts_fieldname: '{fieldName}' was not found in ts_fieldhierarchyandmapping"
                                                                                                    , tracingService);
                    return null;
                }
                Entity fieldHierarchy = mappingCollection.Entities.First();

                string configText = fieldHierarchy.GetAttributeValue<string>("ts_configuration");

                dynamic configJson = JsonConvert.DeserializeObject(configText);

                return configJson;
            }
            catch (Exception e)
            {
                writeToTrace($"Error in getConfigFromFieldMapping(). Exception message:{Environment.NewLine}{e.Message}"
                                                    + $"{Environment.NewLine}fieldName: {fieldName}"
                                                                                                    , tracingService);
                return null;
            }
        }

        public static bool updateConfigOnFieldMapping(string fieldName, string configText
                                                                            , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryMapping.ColumnSet = new ColumnSet(true);
                queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, fieldName);
                EntityCollection mappingCollection = service.RetrieveMultiple(queryMapping);

                if (mappingCollection.Entities.Count == 0)
                {
                    writeToTrace($"Error in updateConfigOnFieldMapping(). ts_fieldname: '{fieldName}' was not found in ts_fieldhierarchyandmapping"
                                                                                                    , tracingService);
                    return false;
                }
                Entity fieldHierarchy = mappingCollection.Entities.First();

                fieldHierarchy["ts_configuration"] = configText;

                service.Update(fieldHierarchy);

                return true;

            }
            catch (Exception e)
            {
                writeToTrace($"Error in updateConfigOnFieldMapping(). Exception message:{Environment.NewLine}{e.Message}"
                                                    + $"{Environment.NewLine}fieldName: {fieldName}"
                                                                                                    , tracingService);
                return false;
            }


        }

        public static Guid getTeamIdForGroup(string groupName
                                                           , IOrganizationService service, ITracingService tracingService)
        {
            Guid teamId = Guid.Empty;
            try
            {
                string businessUnitName = "TSValidationsGlobal";

                Guid targetBUId;
                QueryExpression queryTargetBU = new QueryExpression("businessunit");
                queryTargetBU.ColumnSet = new ColumnSet("businessunitid", "name");
                queryTargetBU.Criteria.AddCondition("name", ConditionOperator.Equal, businessUnitName);
                EntityCollection targetBUCollection = service.RetrieveMultiple(queryTargetBU);

                if (targetBUCollection.Entities.Count == 0)
                {
                    writeToTrace($"getTeamIdForGroup(). Business Unit: {businessUnitName}, not found"
                                                                                                     , tracingService);
                    return Guid.Empty;
                }
                targetBUId = targetBUCollection.Entities.First().Id;


                QueryExpression queryExistingTeam = new QueryExpression("team");
                queryExistingTeam.ColumnSet = new ColumnSet("teamid", "name");
                queryExistingTeam.Criteria.AddCondition("name", ConditionOperator.Equal, groupName);
                //queryExistingTeam.Criteria.AddCondition("businessunitid", ConditionOperator.Equal, targetBUId);
                EntityCollection existingTeamCollection = service.RetrieveMultiple(queryExistingTeam);

                if (existingTeamCollection.Entities.Count > 0)
                {
                    teamId = existingTeamCollection.Entities.First().Id;
                }
                else
                {

                    Entity newTeam = new Entity("team");
                    newTeam["name"] = groupName;
                    newTeam["businessunitid"] = new EntityReference("businessunit", targetBUId);
                    newTeam["teamtype"] = new OptionSetValue(0); // 0 = Owner team

                    teamId = service.Create(newTeam);
                }
            }
            catch (Exception e)
            {
                writeToTrace($"Error in getTeamIdForGroup(...). Exception message:{Environment.NewLine}{e.Message}"
                                                                                                                , tracingService);
            }

            return teamId;
        }

        public static bool existsTeamName(string teamName
                                                           , IOrganizationService service, ITracingService tracingService)
        {
            bool existsTeam = false;
            try
            {
                string businessUnitName = "TSValidationsGlobal";

                Guid targetBUId;
                QueryExpression queryTargetBU = new QueryExpression("businessunit");
                queryTargetBU.ColumnSet = new ColumnSet("businessunitid", "name");
                queryTargetBU.Criteria.AddCondition("name", ConditionOperator.Equal, businessUnitName);
                EntityCollection targetBUCollection = service.RetrieveMultiple(queryTargetBU);

                if (targetBUCollection.Entities.Count == 0)
                {
                    writeToTrace($"existsTeamName(). Business Unit: {businessUnitName}, not found"
                                                                                                     , tracingService);
                    return false;
                }
                targetBUId = targetBUCollection.Entities.First().Id;


                QueryExpression queryExistingTeam = new QueryExpression("team");
                queryExistingTeam.ColumnSet = new ColumnSet("teamid", "name");
                queryExistingTeam.Criteria.AddCondition("name", ConditionOperator.Equal, teamName);
                queryExistingTeam.Criteria.AddCondition("businessunitid", ConditionOperator.Equal, targetBUId);
                EntityCollection existingTeamCollection = service.RetrieveMultiple(queryExistingTeam);

                if (existingTeamCollection.Entities.Count > 0)
                    existsTeam = true;
            }
            catch (Exception e)
            {
                writeToTrace($"Error in existsTeamName(). Exception message:{Environment.NewLine}{e.Message}"
                                                                                                                , tracingService);
            }
            return existsTeam;
        }


        public static bool createTeamRoleAssoc(Guid teamId, string roleName
                                                                    , IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                QueryExpression queryRole = new QueryExpression("role");
                queryRole.ColumnSet = new ColumnSet("roleid", "name");
                queryRole.Criteria.AddCondition("name", ConditionOperator.Equal, roleName);
                EntityCollection roleCollection = service.RetrieveMultiple(queryRole);

                if (roleCollection.Entities.Count == 0)
                {
                    writeToTrace($"createTeamRoleAssoc: Role '{roleName}' not found"
                                                                                      , tracingService);
                    return false;
                }

                Guid roleId = roleCollection.Entities.First().Id;

                QueryExpression queryTeamRole = new QueryExpression("teamroles");
                queryTeamRole.Criteria.AddCondition("teamid", ConditionOperator.Equal, teamId);
                queryTeamRole.Criteria.AddCondition("roleid", ConditionOperator.Equal, roleId);
                EntityCollection teamRoleCollection = service.RetrieveMultiple(queryTeamRole);

                if (teamRoleCollection.Entities.Count > 0)
                    return true;

                EntityReferenceCollection roleRefCollection = new EntityReferenceCollection();
                roleRefCollection.Add(new EntityReference("role", roleId));
                service.Associate("team", teamId, new Relationship("teamroles_association"), roleRefCollection);

                return true;
            }
            catch (Exception e)
            {
                writeToTrace($"createTeamRoleAssoc: Error associating role '{roleName}' to team '{teamId}': {e.Message}"
                                                                                                                , tracingService);
                return false;
            }
        }

        public static dynamic findCountryRegion(string value
                                                        , string description
                                                        , string optionSetName
                                                        , IOrganizationService service, ITracingService tracingService
                                                        , bool createNewIfNotFound = true)
        {
            Entity fieldMapping = null;
            dynamic response = new JObject();
            try
            {
                FilterExpression fieldNameFilter = new FilterExpression(LogicalOperator.Or);
                fieldNameFilter.AddCondition("ts_fieldname", ConditionOperator.Equal, "CountryRegionGlobalSupport");
                fieldNameFilter.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");


                FilterExpression valueFilter = new FilterExpression(LogicalOperator.And);
                valueFilter.AddCondition("ts_value", ConditionOperator.Equal, value);

                FilterExpression queryValueFilter = new FilterExpression(LogicalOperator.And);
                queryValueFilter.AddFilter(fieldNameFilter);
                queryValueFilter.AddFilter(valueFilter);

                QueryExpression queryValueMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryValueMapping.ColumnSet = new ColumnSet(true);
                queryValueMapping.Criteria.AddFilter(queryValueFilter);
                EntityCollection mappingValueCollection = service.RetrieveMultiple(queryValueMapping);

                if (mappingValueCollection.Entities.Count > 0)
                {
                    fieldMapping = mappingValueCollection.Entities.First();
                    response.valueCode = fieldMapping.GetAttributeValue<int>("ts_valuecode");
                    response.value = fieldMapping.GetAttributeValue<string>("ts_value");
                    response.isNew = false;

                    return response;
                }


                FilterExpression descriptionFilter = new FilterExpression(LogicalOperator.And);
                descriptionFilter.AddCondition("ts_valuedescription", ConditionOperator.Equal, description);

                FilterExpression queryDescriptionFilter = new FilterExpression(LogicalOperator.And);
                queryDescriptionFilter.AddFilter(fieldNameFilter);
                queryDescriptionFilter.AddFilter(descriptionFilter);



                QueryExpression queryDescriptionMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryDescriptionMapping.ColumnSet = new ColumnSet(true);
                queryDescriptionMapping.Criteria.AddFilter(queryDescriptionFilter);
                EntityCollection mappingDescriptionCollection = service.RetrieveMultiple(queryDescriptionMapping);

                if (mappingDescriptionCollection.Entities.Count > 0)
                {
                    fieldMapping = mappingDescriptionCollection.Entities.First();
                    response.valueCode = fieldMapping.GetAttributeValue<int>("ts_valuecode");
                    response.value = fieldMapping.GetAttributeValue<string>("ts_value");
                    response.isNew = false;

                    return response;
                }

                if (!createNewIfNotFound)
                    return response;


                QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryMapping.ColumnSet = new ColumnSet(true);
                queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "CountryRegionGlobalSupport");
                queryMapping.TopCount = 1;
                queryMapping.AddOrder("ts_valuecode", OrderType.Descending);
                EntityCollection queryMappingCollection = service.RetrieveMultiple(queryMapping);

                if (queryMappingCollection.Entities.Count == 0)
                    return response;

                Entity latestFieldMapping = queryMappingCollection.Entities.First();

                int valueCode = latestFieldMapping.GetAttributeValue<int>("ts_valuecode") + 1;


                Entity newFieldMapping = new Entity("ts_fieldhierarchyandmapping");

                newFieldMapping["ts_fieldname"] = "CountryRegionGlobalSupport";

                newFieldMapping["ts_valuecode"] = valueCode;

                newFieldMapping["ts_value"] = value;

                newFieldMapping["ts_name"] = value;



                newFieldMapping["ts_valueseq"] = valueCode;

                //newFieldMapping["ts_mappedfieldvalue"] = 
                //newFieldMapping["ts_mappedfieldvaluecode"] = 

                newFieldMapping["ts_valuedescription"] = description;

                Guid newFieldMappingId = service.Create(newFieldMapping);


                addOptionSet(optionSetName, valueCode, description
                                                                    , service, tracingService);

                response.valueCode = valueCode;
                response.value = value;
                response.isNew = true;
            }
            catch (Exception e)
            {
                writeToTrace($"Error in findCountryRegion(). Exception message:{Environment.NewLine}{e.Message}"
                                                                                                            , tracingService);
            }


            return response;
        }


        public static dynamic identifyCountryRegionInName(string name
                                                                    , IOrganizationService service, ITracingService tracingService)
        {
            name = name.Replace("UK", "GB");
            string[] nameWords = name.Split(' ');
            int wordsCount = nameWords.Length;

            List<string> wordList = nameWords.ToList();

            if (wordsCount >= 2)
            {
                string lastTwoWords = nameWords[wordsCount - 2] + " " + nameWords[wordsCount - 1];
                wordList.Add(lastTwoWords);
            }

            nameWords = wordList.ToArray();

            dynamic response = new JObject();
            try
            {
                FilterExpression fieldNameFilter = new FilterExpression(LogicalOperator.Or);
                fieldNameFilter.AddCondition("ts_fieldname", ConditionOperator.Equal, "CountryRegionGlobalSupport");
                fieldNameFilter.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");


                QueryExpression queryCountryRegion = new QueryExpression("ts_fieldhierarchyandmapping");
                queryCountryRegion.ColumnSet = new ColumnSet(true);
                queryCountryRegion.Criteria.AddFilter(fieldNameFilter);
                EntityCollection countryRegionCollection = service.RetrieveMultiple(queryCountryRegion);

                List<Entity> countryRegions = countryRegionCollection.Entities.ToList();

                Entity countryRegion = countryRegions.Where(countryRegionItem =>
                                    nameWords.Contains(countryRegionItem.GetAttributeValue<string>("ts_value"))
                                    || nameWords.Contains(countryRegionItem.GetAttributeValue<string>("ts_valuedescription"))
                )?.FirstOrDefault();

                if (countryRegion == null)
                    return response;

                response.valueCode = countryRegion.GetAttributeValue<int>("ts_valuecode");
                response.value = countryRegion.GetAttributeValue<string>("ts_value");

                return response;
            }
            catch (Exception e)
            {
                writeToTrace($"Error in IdentifyCountryRegionInName(). Exception message:{Environment.NewLine}{e.Message}"
                                                                                                                            , tracingService);


            }

            return response;
        }
        public static bool addOptionSet(string optionSetName, int value, string choice
                                                                            , IOrganizationService service, ITracingService tracingService)
        {
            bool success = true;
            try
            {

                RetrieveOptionSetRequest retrieveOptionSetRequest = new RetrieveOptionSetRequest
                {
                    Name = optionSetName
                };
                //CreateOptionSetRequest createOptionSetRequest = new CreateOptionSetRequest();

                RetrieveOptionSetResponse retrieveOptionSetResponse = (RetrieveOptionSetResponse)service.Execute(retrieveOptionSetRequest);
                OptionSetMetadata retrievedOptionSetMetadata = (OptionSetMetadata)retrieveOptionSetResponse.OptionSetMetadata;
                OptionMetadataCollection options = retrievedOptionSetMetadata.Options;
                OptionMetadata option = options.ToList().Find(item => item.Value == value);

                if (option == null)
                {
                    InsertOptionValueRequest request = new InsertOptionValueRequest();
                    request.OptionSetName = optionSetName;
                    request.Value = value;
                    request.Label = new Label(choice, 1033);
                    //request.SolutionUniqueName = solutionName;

                    InsertOptionValueResponse response = (InsertOptionValueResponse)service.Execute(request);
                }
                else
                {
                    UpdateOptionValueRequest request = new UpdateOptionValueRequest();
                    request.OptionSetName = optionSetName;
                    request.Value = value;
                    request.Label = new Label(choice, 1033);
                    request.MergeLabels = true;

                    UpdateOptionValueResponse response = (UpdateOptionValueResponse)service.Execute(request);
                }
            }
            catch (Exception e)
            {
                writeToTrace("Error in addOptionSet(). Exception message: " + "\n" + e.Message
                                                                                                , tracingService);
                success = false;
            }

            return success;
        }



        public static EntityReference resolveCustomerForEmail(string emailAddress
                                                                            , IOrganizationService service, ITracingService tracingService)
        {
            EntityReference customerRef = null;
            try
            {
                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, emailAddress);
                EntityCollection accountCollection = service.RetrieveMultiple(queryAccount);

                if (accountCollection.Entities.Count == 1)
                {
                    customerRef = new EntityReference("account", accountCollection.Entities.First().Id);
                    return customerRef;
                }

                QueryExpression queryContact = new QueryExpression("contact");
                queryContact.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, emailAddress);
                EntityCollection contactCollection = service.RetrieveMultiple(queryContact);

                if (contactCollection.Entities.Count > 0)
                {
                    Guid contactId = contactCollection.Entities.First().Id;

                    QueryExpression queryConnection = new QueryExpression("connection");
                    queryConnection.ColumnSet = new ColumnSet("record2id");
                    FilterExpression filterConnection = new FilterExpression(LogicalOperator.And);
                    filterConnection.AddCondition("record1id", ConditionOperator.Equal, contactId);
                    filterConnection.AddCondition("statecode", ConditionOperator.Equal, 0);
                    filterConnection.AddCondition("record2objecttypecode", ConditionOperator.Equal, 1);
                    queryConnection.Criteria.AddFilter(filterConnection);
                    EntityCollection connectionCollection = service.RetrieveMultiple(queryConnection);

                    if (connectionCollection.Entities.Count == 1)
                    {
                        Entity connectionEntity = connectionCollection.Entities.First();
                        Guid accountId = connectionEntity.GetAttributeValue<EntityReference>("record2id").Id;
                        customerRef = new EntityReference("account", accountId);
                        return customerRef;
                    }

                    customerRef = new EntityReference("contact", contactId);
                    return customerRef;
                }
            }
            catch (Exception e)
            {
                writeToTrace($"Error in resolveCustomerForEmail(). Exception message:{Environment.NewLine}{e.Message}"
                                                    + $"{Environment.NewLine}emailAddress: {emailAddress}"
                                                    , tracingService);
            }

            return customerRef;
        }

        public static bool isOneWordAllCapitals(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Trim().Contains(' '))
                return false;

            return input.Trim() == input.Trim().ToUpper();
        }
        public static string regexReplace(string pattern, string expresion, string replaceWith, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            string convExpresion = expresion;

            Regex regexObj = new Regex(pattern, regexOptions);
            convExpresion = regexObj.Replace(expresion, replaceWith);

            return convExpresion;
        }

        public static bool regexMatch(string pattern, string input, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            Regex regex = new Regex(pattern, regexOptions);
            return regex.IsMatch(input);
        }

        public static int regexMatchPos(string pattern, string input, int startAt, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            Regex regexObj = new Regex(pattern, regexOptions);

            Match match = regexObj.Match(input, startAt);

            return match.Index;
        }

        public static string regexMatchValue(string pattern, string input, int startAt, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            Regex regexObj = new Regex(pattern, regexOptions);

            Match match = regexObj.Match(input, startAt);

            return match.Value;
        }

        public static bool regexMatchSuccess(string pattern, string input, int startAt, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            Regex regexObj = new Regex(pattern, regexOptions);

            Match match = regexObj.Match(input, startAt);

            return match.Success;
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

        public static void writeToTrace(string message
                                                    , ITracingService tracingService)
        {
            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": "
                + "\n" + message
                    );
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
        }

        public static Guid getUserIdByFullName(string fullName
                                                            ,IOrganizationService service, ITracingService tracingService)
        {
            Guid userId = Guid.Empty;
            try
            {
                QueryExpression queryUser = new QueryExpression("systemuser");
                queryUser.ColumnSet = new ColumnSet("fullname");
                queryUser.Criteria.AddCondition("fullname", ConditionOperator.Equal, fullName);
                EntityCollection userCollection = service.RetrieveMultiple(queryUser);


                if (userCollection.Entities.Count == 0)
                    return Guid.Empty;

                Entity userEntity = userCollection.Entities.First();
                userId = userEntity.Id;
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in getUserIdByFullName(). Exception message: " + "\n" + e.Message
                                                                                                                        , tracingService);                
            }

            return userId;
        }



        public static Guid createCase(string title
                                       , int caseTypeCode
                                       , int? type
                                       , EntityReference customerRef
                                       , int caseStatus
                                       , Guid? qualCodeId
                                       , Dictionary<string, string> extraCaseFields
                                       , IOrganizationService service, ITracingService tracingService)
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

                caseEntity["ts_casestatus"] = new OptionSetValue(caseStatus);

                if (qualCodeId != null)
                    caseEntity["ts_qualificationcodeid"] = new EntityReference("new_qualificationcode", qualCodeId.Value);


                if (extraCaseFields != null)
                    foreach (KeyValuePair<string, string> caseField in extraCaseFields)
                    {
                        caseEntity[caseField.Key] = caseField.Value;
                    }

                caseId = service.Create(caseEntity);

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in createCase(...). Exception message: " + Environment.NewLine + e.Message
                                                                                                                                    , tracingService);
            }

            return caseId;
        }

        public static Guid createContact(string tsContactId, Dictionary<string, string> envVariables
                                                                   , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            Guid contactId = Guid.Empty;
            try
            {

                Entity contact = getOnyxIndividualInfo(tsContactId, envVariables
                                                                   , service, tracingService, errorStack);



                Entity contactEntity = new Entity("contact");

                contactEntity["firstname"] = contact.GetAttributeValue<string>("vchFirstName");
                contactEntity["lastname"] = contact.GetAttributeValue<string>("vchLastName");
                contactEntity["emailaddress1"] = contact.GetAttributeValue<string>("chEmailAddress");
                contactEntity["ts_emailvalidationstatus"] = new OptionSetValue(int.Parse(contact.GetAttributeValue<string>("emailValidationStatus")));
                contactEntity["new_contactaccountnumber"] = contact.GetAttributeValue<string>("iIndividualId");


                if (!string.IsNullOrEmpty(contact.GetAttributeValue<string>("iSourceId")))
                {
                    contactEntity["new_source"] = new OptionSetValue(int.Parse(contact.GetAttributeValue<string>("iSourceId")));
                }


                if (!string.IsNullOrEmpty(contact.GetAttributeValue<string>("iUserSubTypeId")))
                    contactEntity["ts_contactsubtype"] = new OptionSetValue(1);


                contactEntity["new_ctpverificationcode"] = contact.GetAttributeValue<string>("ctpVerificationCode");
                contactEntity["adx_identity_username"] = contact.GetAttributeValue<string>("vchAssignedId");

                string countryCode = contact.GetAttributeValue<string>("chCountryCode")?.Trim();
                string regionCode = contact.GetAttributeValue<string>("chRegionCode")?.Trim();
                if (!string.IsNullOrEmpty(countryCode))
                    contactEntity["address1_country"] = countryCode;
                if (!string.IsNullOrEmpty(regionCode))
                    contactEntity["address1_stateorprovince"] = regionCode;

                if (!string.IsNullOrEmpty(contact.GetAttributeValue<string>("vchAddress1")))
                    contactEntity["address1_addresstypecode"] = new OptionSetValue(3);

                contactEntity["address1_line1"] = contact.GetAttributeValue<string>("vchAddress1");
                contactEntity["address1_line2"] = contact.GetAttributeValue<string>("vchAddress2");
                contactEntity["address1_line3"] = contact.GetAttributeValue<string>("vchAddress3");
                contactEntity["address1_city"] = contact.GetAttributeValue<string>("vchCity");


                string postalCode = contact.GetAttributeValue<string>("vchPostCode");
                if (!string.IsNullOrEmpty(postalCode))
                    contactEntity["address1_postalcode"] = postalCode.Length > 20 ? postalCode.Substring(0, 19) : postalCode;

                if (!string.IsNullOrEmpty(contact.GetAttributeValue<string>("dynCountryValueCode")))
                    contactEntity["ts_countrydesc"] = new OptionSetValue(int.Parse(contact.GetAttributeValue<string>("dynCountryValueCode")));

                if (!string.IsNullOrEmpty(contact.GetAttributeValue<string>("dynStateProvValueCode")))
                    contactEntity["ts_stateprovdesc"] = new OptionSetValue(int.Parse(contact.GetAttributeValue<string>("dynStateProvValueCode")));

                contactEntity["ts_userlockedstatus"] = contact.GetAttributeValue<string>("vchUser7") == "1" ? true : false;

                DateTime result;
                if (DateTime.TryParse(contact.GetAttributeValue<string>("vchUser8"), out result))
                    contactEntity["ts_userlockeddate"] = TimeZoneInfo.ConvertTimeToUtc(result, pstZone);



                DateTime createdDateUTC = TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(contact.GetAttributeValue<string>("dtInsertDate")), pstZone);
                contactEntity["overriddencreatedon"] = createdDateUTC;

                contactId = service.Create(contactEntity);

            }
            catch (Exception e)
            {

                string error = "Error in createContact(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "tsContactId: " + tsContactId;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
            }

            return contactId;
        }

        public static Guid getUserIdByFullName(string fullName
                                                             , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            Guid userId = Guid.Empty;
            try
            {
                QueryExpression queryUser = new QueryExpression("systemuser");
                queryUser.ColumnSet = new ColumnSet("fullname");
                queryUser.Criteria.AddCondition("fullname", ConditionOperator.Equal, fullName);
                EntityCollection userCollection = service.RetrieveMultiple(queryUser);


                if (userCollection.Entities.Count == 0)
                    return Guid.Empty;

                Entity userEntity = userCollection.Entities.First();
                userId = userEntity.Id;
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                writeToTrace("Error in getUserIdByFullName(). Exception message: " + "\n" + e.Message
                                                                                                    , tracingService);
                errorStack.Add(error);
            }

            return userId;
        }

    
        public static Entity getOnyxIndividualInfo(string tsContactId, Dictionary<string, string> envVariables
                                                                   , IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {

            Entity individual = null;
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
                objRequest.DBName = "DBAdmin";
                objRequest.SPName = "usp_getContactInfo";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("TSContactId");
                param1.InnerText = tsContactId;
                elements.Add(param1);




                objRequest.@params.Any = elements.ToArray();


                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                if (returnXml.Length == 0)
                {
                    string error = "At getOnyxIndividualInfo(...). No record was returned";
                    writeToTrace(error, tracingService);
                    errorStack.Add(error);
                    return null;

                }

                rowType row = returnXml.First();

                individual = new Entity();

                individual.Attributes.Add("iIndividualId", row.Any[0].InnerText);
                individual.Attributes.Add("iSiteId", row.Any[1].InnerText);
                individual.Attributes.Add("chLanguageCode", row.Any[2].InnerText);
                individual.Attributes.Add("vchAssignedId", row.Any[3].InnerText);
                individual.Attributes.Add("vchSalutation", row.Any[4].InnerText);
                individual.Attributes.Add("vchFirstName", row.Any[5].InnerText);
                individual.Attributes.Add("vchMiddleName", row.Any[6].InnerText);
                individual.Attributes.Add("vchLastName", row.Any[7].InnerText);
                individual.Attributes.Add("vchSuffix", row.Any[8].InnerText);
                individual.Attributes.Add("vchAddress1", row.Any[9].InnerText);
                individual.Attributes.Add("vchAddress2", row.Any[10].InnerText);
                individual.Attributes.Add("vchAddress3", row.Any[11].InnerText);
                individual.Attributes.Add("vchCity", row.Any[12].InnerText);
                individual.Attributes.Add("chRegionCode", row.Any[13].InnerText);
                individual.Attributes.Add("chCountryCode", row.Any[14].InnerText);
                individual.Attributes.Add("vchPostCode", row.Any[15].InnerText);
                individual.Attributes.Add("vchPhoneNumber", row.Any[16].InnerText);
                individual.Attributes.Add("vchEmailAddress", row.Any[17].InnerText);
                individual.Attributes.Add("vchURL", row.Any[18].InnerText);
                individual.Attributes.Add("chGender", row.Any[19].InnerText);
                individual.Attributes.Add("iUserTypeId", row.Any[20].InnerText);
                individual.Attributes.Add("iUserSubTypeId", row.Any[21].InnerText);
                individual.Attributes.Add("iCompanyId", row.Any[22].InnerText);
                individual.Attributes.Add("vchCompanyName", row.Any[23].InnerText);
                individual.Attributes.Add("chTitleCode", row.Any[24].InnerText);
                individual.Attributes.Add("vchTitleDesc", row.Any[25].InnerText);
                individual.Attributes.Add("chDepartmentCode", row.Any[26].InnerText);
                individual.Attributes.Add("vchDepartmentDesc", row.Any[27].InnerText);
                individual.Attributes.Add("iPhoneTypeId", row.Any[28].InnerText);
                individual.Attributes.Add("iAddressTypeId", row.Any[29].InnerText);
                individual.Attributes.Add("iSourceId", row.Any[30].InnerText);
                individual.Attributes.Add("iStatusId", row.Any[31].InnerText);
                individual.Attributes.Add("bValidAddress", row.Any[32].InnerText);
                individual.Attributes.Add("iAccessCode", row.Any[33].InnerText);
                individual.Attributes.Add("bPrivate", row.Any[34].InnerText);
                individual.Attributes.Add("vchUser1", row.Any[35].InnerText);
                individual.Attributes.Add("vchUser2", row.Any[36].InnerText);
                individual.Attributes.Add("vchUser3", row.Any[37].InnerText);
                individual.Attributes.Add("vchUser4", row.Any[38].InnerText);
                individual.Attributes.Add("vchUser5", row.Any[39].InnerText);
                individual.Attributes.Add("vchUser6", row.Any[40].InnerText);
                individual.Attributes.Add("vchUser7", row.Any[41].InnerText);
                individual.Attributes.Add("vchUser8", row.Any[42].InnerText);
                individual.Attributes.Add("vchUser9", row.Any[43].InnerText);
                individual.Attributes.Add("vchUser10", row.Any[44].InnerText);
                individual.Attributes.Add("chInsertBy", row.Any[45].InnerText);
                individual.Attributes.Add("dtInsertDate", row.Any[46].InnerText);
                individual.Attributes.Add("chUpdateBy", row.Any[47].InnerText);
                individual.Attributes.Add("dtUpdateDate", row.Any[48].InnerText);
                individual.Attributes.Add("tiRecordStatus", row.Any[49].InnerText);
                individual.Attributes.Add("dtModifiedDate", row.Any[50].InnerText);
                individual.Attributes.Add("customerType", row.Any[51].InnerText);
                individual.Attributes.Add("emailValidationStatus", row.Any[52].InnerText);
                individual.Attributes.Add("ctpVerificationCode", row.Any[53].InnerText);
                individual.Attributes.Add("dynCountryValueCode", row.Any[54].InnerText);
                individual.Attributes.Add("dynStateProvValueCode", row.Any[55].InnerText);




            }
            catch (Exception e)
            {
                string error = "Error in getOnyxIndividualInfo(...). Exception message: " + Environment.NewLine + e.Message;
                writeToTrace(error, tracingService);
                errorStack.Add(error);
            }


            return individual;

        }

    }
}
