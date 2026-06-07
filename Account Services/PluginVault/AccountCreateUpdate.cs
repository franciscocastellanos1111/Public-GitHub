
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
using System.IdentityModel.Metadata;

namespace AccountServices
{
     
    public class AccountCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));            



            AccountServicesHelper.writeToTrace("Starting - AccountServices.AccountCreateUpdate"
                                                        , tracingService);


            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                Entity accountEntity = null;

                accountEntity = (Entity)context.InputParameters["Target"];

                if (accountEntity.LogicalName != "account")
                    return;


                Entity account = service.Retrieve("account", accountEntity.Id, new ColumnSet(true));
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                



                AccountServicesHelper.writeToTrace(context.MessageName + ": "
                                                        + Environment.NewLine + "accountId: " + account.Id.ToString() + "; tsOrgId: " + tsOrgId
                                                        , tracingService);

                updateOnyxOrg(account, tsOrgId
                                            , context, service, tracingService);


                if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                {
                    processAccountCreate(account
                                                , context, service, tracingService);
                }
                if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {

                    bool orgDesigChange = processOrgDesignationUpdate(account, tsOrgId
                                                                        , context, service, tracingService);

                    //if (!orgDesigChange)
                    //    processOrgRevalidation(account
                    //                                , context, service, tracingService);

                }
            }

            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error during AccountServices.AccountCreateUpdate: " + e.Message
                                                                                                                , tracingService);
            }

        }


        public static void processAccountCreate(Entity account
                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                Guid modifiedBy = account.GetAttributeValue<EntityReference>("createdby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;


                OptionSetValueCollection accountDirectivesCollection = account.GetAttributeValue<OptionSetValueCollection>("ts_accountdirectives");

                bool excludePostAccountCreateAutoLogic = accountDirectivesCollection != null && accountDirectivesCollection.Any(option => option.Value == 2); //2 - ExcludePostAccountCreateAutoLogic

                if (excludePostAccountCreateAutoLogic)
                {
                    AccountServicesHelper.writeToTrace("At processAccountCreate - Account directives contain 'ExcludePostAccountCreateAutoLogic'. Skipping processAccountCreate method"
                                                                                                                                                                                , tracingService);
                    return;
                }

                string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                AccountServicesHelper.writeToTrace("processAccountCreate - tsOrgId: " + tsOrgId
                                                                                , tracingService);

                EntityReference orgDesigEntRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesig = orgDesigEntRef == null ? Guid.Empty : orgDesigEntRef.Id;


                if (orgDesig != Guid.Empty)
                {
                    Entity qualCodeEntity = service.Retrieve("new_qualificationcode", orgDesig, new ColumnSet(true));

                    string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                    string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");

                    AccountServicesHelper.processOrgQualification(account.Id, orgDesig, 4, "Qualification Pending"
                                                                    , tsOrgId, qualCode
                                                                    , service, tracingService);

                    AccountServicesHelper.addLegalAddress(account
                                                            , context, service, tracingService);
                }

                
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in processAccountCreate(...). Exception message: "
                                                                + Environment.NewLine + e.Message
                                                                + Environment.NewLine + "accountId: " + account.Id.ToString()
                                                                , tracingService);
            }


        }



        public static void processOrgRevalidation(Entity account
                                                        , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                Entity accountBefore = (Entity)context.PreEntityImages["PreAccountImage"];


                QueryExpression queryAutoValidMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryAutoValidMap.ColumnSet = new ColumnSet(true);
                queryAutoValidMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "AutomatedValidation");
                EntityCollection autoValidMapCollection = service.RetrieveMultiple(queryAutoValidMap);
                if (autoValidMapCollection.Entities.Count == 0)
                    return;

                Entity autoValidationMap = autoValidMapCollection.Entities.First();
                int auomatedValidationValue = autoValidationMap.GetAttributeValue<int>("ts_valuecode");

                if (auomatedValidationValue != 1)
                    return;

                string automatedValConfigText = autoValidationMap.GetAttributeValue<string>("ts_configuration");

                dynamic automatedValDefinition = JsonConvert.DeserializeObject(automatedValConfigText);

                bool processRevalOnAccountUpdate = automatedValDefinition.config.revalidationOnAccounUpdate;

                if (!processRevalOnAccountUpdate)
                    return;

                List<string> fieldsTriggeringReval = ((JArray)automatedValDefinition.accountFieldUpdatesTriggeringReval).Select(fieldItem => (dynamic)fieldItem)
                                                                                                                .Select(dynObj => (string)dynObj.fieldName).ToList();

                bool initiateRevalidation = false;
                foreach (string fieldName in fieldsTriggeringReval)
                {
                    string[] entityReferences = { "new_activitycode" };


                    string fieldValueBefore = null;
                    string fieldValueAfter = null;
                    if (entityReferences.Contains(fieldName))
                    {
                        EntityReference fieldValueRefAfter = account.GetAttributeValue<EntityReference>(fieldName);
                        fieldValueAfter = fieldValueRefAfter == null ? Guid.Empty.ToString() : fieldValueRefAfter.Id.ToString();

                        EntityReference fieldValueRefBefore = accountBefore.GetAttributeValue<EntityReference>(fieldName);
                        fieldValueBefore = fieldValueRefBefore == null ? Guid.Empty.ToString() : fieldValueRefBefore.Id.ToString();

                    }
                    else
                    {
                        fieldValueAfter = account.GetAttributeValue<string>(fieldName);
                        fieldValueBefore = accountBefore.GetAttributeValue<string>(fieldName);
                    }


                    if (fieldValueBefore != fieldValueAfter)
                    {
                        AccountServicesHelper.writeToTrace("fieldName: " + fieldName + " got updated: "
                                                                            + Environment.NewLine + "fieldValueBefore: " + fieldValueBefore + "; fieldValueAfter: " + fieldValueAfter
                                                                            , tracingService);


                        initiateRevalidation = true;
                        break;
                    }
                }

                if (initiateRevalidation)
                {
                    
                }
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in processOrgRevalidation(...). Exception message: "
                                                                        + Environment.NewLine + e.Message
                                                                        + Environment.NewLine + "accountId: " + account.Id.ToString()
                                                                        , tracingService);
            }


        }




        public static bool processOrgDesignationUpdate(Entity account, string tsOrgId
                                                            , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            bool orgDesigChange = false;
            try
            {
                Guid modifiedBy = account.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return false;



                Entity accountBefore = (Entity)context.PreEntityImages["PreAccountImage"];

                EntityReference orgDesigEntRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                EntityReference orgDesigEntRefBefore = accountBefore.GetAttributeValue<EntityReference>("new_orgdesignation");


                if (orgDesigEntRef == null && orgDesigEntRefBefore == null)
                    return false;

                Guid orgDesig = orgDesigEntRef == null ? Guid.Empty : orgDesigEntRef.Id;
                Guid orgDesigBefore = orgDesigEntRefBefore == null ? Guid.Empty : orgDesigEntRefBefore.Id;


                AccountServicesHelper.writeToTrace("orgDesigBefore: " + orgDesigBefore + "; orgDesigAfter: " + orgDesig
                                                        , tracingService);


                Entity qualCodeEntity = service.Retrieve("new_qualificationcode", orgDesig, new ColumnSet(true));

                string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");

                if (orgDesigBefore == orgDesig)
                {
                    if (orgDesig != Guid.Empty)
                    {
                        string countryCode = account.GetAttributeValue<string>("address1_country");
                        string countryCodeBefore = accountBefore.GetAttributeValue<string>("address1_country");

                        if (countryCodeBefore != countryCode)
                        {
                            AccountServicesHelper.writeToTrace("countryCodeBefore: " + countryCodeBefore + "; countryCodeAfter: " + countryCode
                                                                                                                                    , tracingService);


                            AccountServicesHelper.updateOrgQualification(account.Id, orgDesigBefore, 13, DateTime.UtcNow
                                                                                                            , service, tracingService);

                            
                            AccountServicesHelper.updateQualCase(account.Id, orgDesigBefore, 102074 //102074 - OQ - Cancelled
                                                                                            , tsOrgId, qualCode
                                                                                            , service, tracingService);

                            account["new_orgdesignation"] = null;
                            service.Update(account);

                            AccountServicesHelper.addToSystemIntegrationLog("orgdesignationchange", "account", account.Id.ToString(), tsOrgId, false
                                                                                                                                            , "", service, tracingService);

                            return true;
                        }
                    }

                    return false;
                }

                

                if (orgDesigBefore != Guid.Empty)
                {
                    AccountServicesHelper.updateOrgQualification(account.Id, orgDesigBefore, 13, DateTime.UtcNow
                                                                                                            , service, tracingService);

                    
                    AccountServicesHelper.updateQualCase(account.Id, orgDesigBefore, 102074 //102074 - OQ - Cancelled
                                                                    , tsOrgId, qualCode
                                                                    , service, tracingService);
                    
                }

                if (orgDesig != Guid.Empty)
                {     
                    AccountServicesHelper.processOrgQualification(account.Id, orgDesig, 4, "Qualification Pending"
                                                                                            , tsOrgId, qualCode
                                                                                            , service, tracingService);

                    AccountServicesHelper.addLegalAddress(account
                                                                , context, service, tracingService);
                }

                if (orgDesigBefore != Guid.Empty || orgDesig != Guid.Empty)
                {
                    AccountServicesHelper.addToSystemIntegrationLog("orgdesignationchange", "account", account.Id.ToString(), tsOrgId, false
                                                                                                                                        , "", service, tracingService);

                    return true;
                }
            }
            catch (Exception e)
            {

                AccountServicesHelper.writeToTrace("Error in processOrgDesignationUpdate(...). Exception message: "
                                                                + Environment.NewLine + e.Message
                                                                + Environment.NewLine + "tsOrgId: " + tsOrgId
                                                                , tracingService);
            }


            return orgDesigChange;
        }

        public static void updateOnyxOrg(Entity account, string tsOrgId
                                                                , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                Guid modifiedBy = account.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;

                OptionSetValueCollection accountDirectivesCollection = account.GetAttributeValue<OptionSetValueCollection>("ts_accountdirectives");

                bool excludeDataIntegration = accountDirectivesCollection != null && accountDirectivesCollection.Any(option => option.Value == 1); //1 - ExcludeDataIntegration

                if (excludeDataIntegration)
                {
                    AccountServicesHelper.writeToTrace("At updateOnyxOrg - Account directives contain 'ExcludeDataIntegration'. Skipping Onyx integration"
                                                                                                                                                              , tracingService);
                    return;
                }


                EntityReference orgDesigEntRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                string orgDesignation = string.Empty;
                if (orgDesigEntRef != null)
                {
                    Entity qualCodeEntity = service.Retrieve("new_qualificationcode", orgDesigEntRef.Id, new ColumnSet("new_qualcode"));
                    orgDesignation = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                }

                string vchCompanyName = account.GetAttributeValue<string>("name");

                string customerTypeCode = string.Empty;
                if (account.Contains("customertypecode"))
                    customerTypeCode = account.FormattedValues["customertypecode"];

                OptionSetValue orgSourceOption = account.GetAttributeValue<OptionSetValue>("new_source");
                int orgSource = orgSourceOption == null ? 0 : orgSourceOption.Value;

                string countryCode = account.GetAttributeValue<string>("address1_country");
                string regionCode = account.GetAttributeValue<string>("address1_stateorprovince") ?? "";
                regionCode = regionCode.Length > 4 ? regionCode.Substring(0, 4) : regionCode;
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
                    Entity activityCodeEntity = service.Retrieve("new_activitycodes", activityCodeRef.Id, new ColumnSet("new_activitycode"));
                    activityCode = activityCodeEntity.GetAttributeValue<string>("new_activitycode");
                }

                EntityReference duplicateOfRef = account.GetAttributeValue<EntityReference>("ts_duplicateofid");
                string duplicateOf = string.Empty;
                if (duplicateOfRef != null)
                {
                    Entity accountDupeOf = service.Retrieve("account", duplicateOfRef.Id, new ColumnSet("accountnumber"));
                    duplicateOf = accountDupeOf.GetAttributeValue<string>("accountnumber");
                }

                int numberOfEmployees = account.GetAttributeValue<int>("numberofemployees");

                string partnerCode = string.Empty;
                EntityReference pngoRef = account.GetAttributeValue<EntityReference>("ts_orgppid");
                if (pngoRef != null)
                {
                    Entity pngoAccount = service.Retrieve("account", pngoRef.Id, new ColumnSet("ts_tspngoid", "ts_tspngocode", "accountnumber"));
                    partnerCode = pngoAccount.GetAttributeValue<string>("ts_tspngocode");
                }

                string orgRefId = account.GetAttributeValue<string>("ts_pporgid");



                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;


                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2kServer"];
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


                AccountServicesHelper.writeToTrace("updateOnyxOrg(...) ntext activityCode: " + activityCode
                                                        , tracingService);



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

                string elementsXml = string.Join(Environment.NewLine, elements.Select(e => e.OuterXml));
                AccountServicesHelper.writeToTrace("updateOnyxOrg(...) request elements:" + Environment.NewLine + elementsXml
                                                                                                                            , tracingService);


                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;


                string resultStatus = string.Empty;
                if (returnXml.Length > 0)
                    resultStatus = returnXml.First().Any[0].InnerText;


                AccountServicesHelper.writeToTrace("updateOnyxOrg(...) resultStatus: " + resultStatus
                                                        , tracingService);


                if (resultStatus.ToLower() != "success")
                {
                    string error = "Error in updateOnyxOrg(...). resultStatus: " + resultStatus;
                    AccountServicesHelper.createDynamicsoOnyxIntegrationLog(account.LogicalName, account.Id.ToString()
                                                                                    , error, service, tracingService);
                }

            }
            catch (Exception e)
            {
                string error = "Error in updateOnyxOrg(...). Exception message: "
                                + Environment.NewLine + e.Message
                                + Environment.NewLine + "tsOrgId: " + tsOrgId;   

                AccountServicesHelper.writeToTrace(error
                                                        , tracingService);


                AccountServicesHelper.createDynamicsoOnyxIntegrationLog(account.LogicalName, account.Id.ToString()
                                                                            , error, service, tracingService);

            }
        }



        public static void processOrgCountryUpdate(Entity account, string tsOrgId
                                                        , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {

            try
            {
                Guid modifiedBy = account.GetAttributeValue<EntityReference>("modifiedby").Id;
                Entity user = service.Retrieve("systemuser", modifiedBy, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;



                Entity accountBefore = (Entity)context.PreEntityImages["PreAccountImage"];

                EntityReference orgDesigEntRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                string countryCode = account.GetAttributeValue<string>("address1_country");


                EntityReference orgDesigEntRefBefore = accountBefore.GetAttributeValue<EntityReference>("new_orgdesignation");
                string countryCodeBefore = accountBefore.GetAttributeValue<string>("address1_country");

                if (countryCodeBefore == countryCode)
                    return;


                Guid orgDesig = orgDesigEntRef == null ? Guid.Empty : orgDesigEntRef.Id;
                Guid orgDesigBefore = orgDesigEntRefBefore == null ? Guid.Empty : orgDesigEntRefBefore.Id;


                
                if (orgDesigBefore != orgDesig) // The cases where orgDesigBefore != orgDesig will be taken care of by processOrgDesignationUpdate
                    return;

                Entity qualCodeEntity = service.Retrieve("new_qualificationcode", orgDesig, new ColumnSet(true));

                string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");

                if (orgDesig != Guid.Empty)
                {
                    AccountServicesHelper.updateOrgQualification(account.Id, orgDesigBefore, 13, DateTime.UtcNow
                                                                    , service, tracingService);

                    
                    AccountServicesHelper.updateQualCase(account.Id, orgDesigBefore, 102074 //102074 - OQ - Cancelled
                                                            , tsOrgId, qualCode
                                                            , service, tracingService);

                    account["new_orgdesignation"] = null;
                    service.Update(account);

                }

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in processOrgDesignationUpdate(...). Exception message: "
                                                        + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "tsOrgId: " + tsOrgId
                                                        , tracingService);

            }



        }


    }
}