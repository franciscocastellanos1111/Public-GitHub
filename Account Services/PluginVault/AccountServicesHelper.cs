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



    }
}
