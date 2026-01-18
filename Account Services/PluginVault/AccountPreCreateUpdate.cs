
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
using System.Runtime.Remoting.Contexts;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace AccountServices
{
     
    public class AccountPreCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));            


            AccountServicesHelper.writeToTrace("Starting - AccountServices.AccountPreCreateUpdate"
                                                    , tracingService);


            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                Entity accountTargetEntity = null;

                accountTargetEntity = (Entity)context.InputParameters["Target"];

                if (accountTargetEntity.LogicalName != "account")
                    return;



                AccountServicesHelper.writeToTrace("accountTargetEntity.Id: " + accountTargetEntity.Id.ToString()
                                                        , tracingService);


                //string preTsOrgId = string.Empty;
                if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                {                   
                    
                    //preTsOrgId = getTSOrgId(accountTargetEntity
                    //    , context, service, tracingService);

                    //accountTargetEntity["accountnumber"] = preTsOrgId;

                    accountTargetEntity = validateAccountCreate(accountTargetEntity
                                                                    , context, service, tracingService);

                }
                if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    Entity account = service.Retrieve("account", accountTargetEntity.Id, new ColumnSet(true));
                    string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                    //validateOrgDesignationUpdate(accountTargetEntity, account, tsOrgId
                    //    , context, service, tracingService);

                    validateAccountUpdate(accountTargetEntity, account, tsOrgId
                                                , context, service, tracingService);
                }

            }

            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);


                AccountServicesHelper.writeToTrace("Error during AccountServices.AccountPreCreateUpdate: " + e.Message
                                                        , tracingService);

            }

        }

        public static Entity validateAccountCreate(Entity accountTargetEntity
                                                        , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {

            try
            {
                //Guid modifiedBy = accountTargetEntity.GetAttributeValue<EntityReference>("createdby");
                Entity user = service.Retrieve("systemuser", context.InitiatingUserId, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                AccountServicesHelper.writeToTrace("InitiatingUser: " + fullName
                                                        , tracingService);


                if (fullName.Contains("TSDynamicsOnyx"))
                {
                    AccountServicesHelper.writeToTrace("validateAccountCreate(...) - Bypassing the whole validation"
                                                                                                            , tracingService);
                    return accountTargetEntity;
                }

                EntityReference pngoRef = null;
                Entity pngoAccount = null;
                string tsPngoId = string.Empty;
                string tsPngoCode = string.Empty;

                if (accountTargetEntity.Contains("ts_orgppid"))
                {
                    pngoRef = accountTargetEntity.GetAttributeValue<EntityReference>("ts_orgppid");

                    if (pngoRef == null)
                        throw new InvalidPluginExecutionException("'Org's Partner Platform' contains a value, but it is not a reference to an Account record");

                    pngoAccount = service.Retrieve("account", pngoRef.Id, new ColumnSet("ts_tspngoid", "ts_tspngocode", "accountnumber"));
                    tsPngoId = pngoAccount.GetAttributeValue<string>("ts_tspngoid");
                    tsPngoCode = pngoAccount.GetAttributeValue<string>("ts_tspngocode");
                }


                if (fullName.Contains("TSDynamics"))
                {
                    AccountServicesHelper.writeToTrace("validateAccountUpdate(...) - Bypassing validation, but generating tsOrgId"
                                                                                                            , tracingService);
                }
                else
                {
                    if (!accountTargetEntity.Contains("address1_country"))
                        throw new InvalidPluginExecutionException("'Country' must be provided");
                    if (!accountTargetEntity.Contains("address1_stateorprovince"))
                        throw new InvalidPluginExecutionException("'State/Province' must be provided");
                    if (!accountTargetEntity.Contains("address1_line1"))
                        throw new InvalidPluginExecutionException("'Street 1' must be provided");
                    if (!accountTargetEntity.Contains("address1_city"))
                        throw new InvalidPluginExecutionException("'City' must be provided");
                    if (!accountTargetEntity.Contains("address1_postalcode"))
                        throw new InvalidPluginExecutionException("'Postal Code' must be provided");
                    if (!accountTargetEntity.Contains("emailaddress1"))
                        throw new InvalidPluginExecutionException("Organization Email must be provided");
                    if (!accountTargetEntity.Contains("new_source"))
                        throw new InvalidPluginExecutionException("Source must be provided");



                    if (accountTargetEntity.Contains("new_orgdesignation"))
                    {
                        EntityReference orgDesigEntRef = accountTargetEntity.GetAttributeValue<EntityReference>("new_orgdesignation");

                        Entity qualCodeEntity = service.Retrieve("new_qualificationcode", orgDesigEntRef.Id, new ColumnSet(true));
                        string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                        string qualCategory = qualCodeEntity.GetAttributeValue<string>("new_qualcategory");

                        string accountCountryCode = accountTargetEntity.GetAttributeValue<string>("address1_country");

                        if (accountCountryCode.ToLower() == "gb")
                            accountCountryCode = "uk";

                        if (qualCategory != "QualOrg" || !qualCode.StartsWith(accountCountryCode.ToLower()))
                            throw new InvalidPluginExecutionException("The Organization Designation must be of 'Organization Qualification' type, and must correspond to the organization country");

                        if (!accountTargetEntity.Contains("new_legalidentifier") || !accountTargetEntity.Contains("new_activitycode") || !accountTargetEntity.Contains("new_budget") || !accountTargetEntity.Contains("telephone1"))
                            throw new InvalidPluginExecutionException("If an Organization Designation is provided:  'Legal Identifier', 'Activity Code', 'Budget', and 'Phone Number' are required");

                        string[] USCountries = { "AS", "FM", "GU", "MP", "PR", "UM", "US", "VI" };
                        if (USCountries.Contains(accountCountryCode) && !accountTargetEntity.Contains("new_associationcode"))
                        {
                            string associationCode = AccountServicesHelper.generateNewAssocCode();
                            accountTargetEntity["new_associationcode"] = associationCode;
                        }
                    }

                    string budget = string.Empty;
                    if (accountTargetEntity.Contains("new_budget"))
                    {
                        budget = accountTargetEntity.GetAttributeValue<string>("new_budget");

                        if (!AccountServicesHelper.regexMatch(@"^\d+$", budget))
                            throw new InvalidPluginExecutionException("Budget must be a numerical value");
                    }


                    //accountTargetEntity.GetAttributeValue<string>("");



                    if (accountTargetEntity.Contains("ts_orgppid"))
                    {

                        if (string.IsNullOrEmpty(tsPngoId))
                            throw new InvalidPluginExecutionException("The Organization selected for 'Org's Partner Platform' is not a PNGO account");
                        if (!accountTargetEntity.Contains("ts_pporgid"))
                            throw new InvalidPluginExecutionException("If 'Org's Partner Platform' is provided, 'OrgId in Partner Platform' must also be provided");
                        if (!accountTargetEntity.Contains("new_orgdesignation"))
                            throw new InvalidPluginExecutionException("If 'Org's Partner Platform' is provided, and Org Designation must also be provided");
                    }

                    if (accountTargetEntity.Contains("ts_pporgid") && !accountTargetEntity.Contains("ts_orgppid"))
                        throw new InvalidPluginExecutionException("If 'OrgId in Partner Platform' is provided, 'Org's Partner Platform' must also be provided");






                    string name = accountTargetEntity.GetAttributeValue<string>("name");
                    //name = name.Length < 20 ? name.Substring(0, name.Length) : name.Substring(0, 20);

                    string legalIdentifier = accountTargetEntity.GetAttributeValue<string>("new_legalidentifier");

                    string addressLine1 = accountTargetEntity.GetAttributeValue<string>("address1_line1");
                    //addressLine1 = addressLine1.Length < 20 ? addressLine1.Substring(0, addressLine1.Length) : addressLine1.Substring(0, 20);

                    string addressPostalCode = accountTargetEntity.GetAttributeValue<string>("address1_postalcode");
                    addressPostalCode = addressPostalCode.Length < 5 ? addressPostalCode.Substring(0, addressPostalCode.Length) : addressPostalCode.Substring(0, 5);


                    Entity matchingAccount = AccountServicesHelper.findMatchAccount(name, legalIdentifier, addressLine1, addressPostalCode, accountTargetEntity.Id
                                                                                        , service, tracingService);

                    if (matchingAccount != null)
                    {
                        string matchingTsOrgId = matchingAccount.GetAttributeValue<string>("accountnumber");
                        throw new InvalidPluginExecutionException("Another account already exists. TSOrgId: " + matchingTsOrgId);
                    }

                }


                string preTsOrgId = getTSOrgId(accountTargetEntity
                                                                , context, service, tracingService);

                accountTargetEntity["accountnumber"] = preTsOrgId;


                if (accountTargetEntity.Contains("ts_orgppid"))
                {
                    accountTargetEntity["new_platformid"] = tsPngoCode + "." + accountTargetEntity.GetAttributeValue<string>("ts_pporgid");
                }
                else
                {
                    if (!accountTargetEntity.Contains("new_platformid"))
                        accountTargetEntity["new_platformid"] = "TechSoup." + preTsOrgId;
                }

                
            }
            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);


                AccountServicesHelper.writeToTrace("Error in validateAccountCreate(...). Exception message: "
                                                        + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "accountTargetEntityId: " + accountTargetEntity.Id.ToString()
                                                        , tracingService);

            }

            return accountTargetEntity;
        }
        public static Entity validateAccountUpdate(Entity accountTargetEntity, Entity account, string tsOrgId
                                                        , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {

            try
            {
                //Guid modifiedBy = accountTargetEntity.GetAttributeValue<EntityReference>("createdby");
                Entity user = service.Retrieve("systemuser", context.InitiatingUserId, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                AccountServicesHelper.writeToTrace("validateAccountUpdate(...) InitiatingUserId: " + fullName
                                                        , tracingService);

                if (fullName.Contains("TSDynamicsOnyx") || fullName.Contains("TSDynamics"))
                {
                    AccountServicesHelper.writeToTrace("validateAccountUpdate(...) - Bypassing validation"
                                                                                                            , tracingService);
                    return accountTargetEntity;
                }

                if (accountTargetEntity.Contains("address1_country"))
                { 
                    if (string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("address1_country")))
                        throw new InvalidPluginExecutionException("'Country' must be provided");
                }
                if (accountTargetEntity.Contains("address1_stateorprovince"))                    
                {
                    if (string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("address1_stateorprovince")))
                        throw new InvalidPluginExecutionException("'State/Province' must be provided");
                }
                if (accountTargetEntity.Contains("address1_line1"))                    
                {
                    if (string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("address1_line1")))
                        throw new InvalidPluginExecutionException("'Street 1' must be provided");
                }
                if (accountTargetEntity.Contains("address1_city"))                    
                {
                    if (string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("address1_city")))
                        throw new InvalidPluginExecutionException("'City' must be provided");
                }
                if (accountTargetEntity.Contains("address1_postalcode"))
                {
                    if (string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("address1_postalcode")))
                        throw new InvalidPluginExecutionException("'Postal Code' must be provided");
                }
                if (accountTargetEntity.Contains("emailaddress1"))
                {
                    if (string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("emailaddress1")))
                        throw new InvalidPluginExecutionException("Organization Email must be provided");
                }
                if (accountTargetEntity.Contains("new_source"))                    
                {
                    OptionSetValue param = accountTargetEntity.GetAttributeValue<OptionSetValue>("new_source");
                    if (param == null)
                        throw new InvalidPluginExecutionException("Source must be provided");
                }


                AccountServicesHelper.writeToTrace("Contains orgDesignation: " + accountTargetEntity.Contains("new_orgdesignation").ToString()
                                                        , tracingService);



                EntityReference orgDesigTargetRef = accountTargetEntity.GetAttributeValue<EntityReference>("new_orgdesignation");
                if (accountTargetEntity.Contains("new_orgdesignation") && accountTargetEntity.GetAttributeValue<EntityReference>("new_orgdesignation") != null)
                {
                    Entity qualCodeEntity = service.Retrieve("new_qualificationcode", orgDesigTargetRef.Id, new ColumnSet(true));
                    string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                    string qualCategory = qualCodeEntity.GetAttributeValue<string>("new_qualcategory");

                    string accountCountryCode = account.GetAttributeValue<string>("address1_country");

                    if (accountCountryCode.ToLower() == "gb")
                        accountCountryCode = "uk";

                    if (qualCategory != "QualOrg" || !qualCode.StartsWith(accountCountryCode.ToLower()))
                        throw new InvalidPluginExecutionException("The Organization Designation must be of 'Organization Qualification' type, and must correspond to the organization country");

                }



                EntityReference orgDesigCurrentRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                if (accountTargetEntity.Contains("new_orgdesignation") && accountTargetEntity.GetAttributeValue<EntityReference>("new_orgdesignation") != null
                    || !accountTargetEntity.Contains("new_orgdesignation") && account.GetAttributeValue<EntityReference>("new_orgdesignation") != null
                    )
                {

                    string legalId = accountTargetEntity.GetAttributeValue<string>("new_legalidentifier");
                    EntityReference activityCode = accountTargetEntity.GetAttributeValue<EntityReference>("new_activitycode");
                    string budget = accountTargetEntity.GetAttributeValue<string>("new_budget");

                    Guid activityCodeId = activityCode == null ? Guid.Empty : activityCode.Id;

                   

                    AccountServicesHelper.writeToTrace("accountTargetEntity.Contains(\"new_legalidentifier\"): " + accountTargetEntity.Contains("new_legalidentifier").ToString()
                                                            + Environment.NewLine + "accountTargetEntity.Contains(\"new_activitycode\"): " + accountTargetEntity.Contains("new_activitycode").ToString()
                                                            + Environment.NewLine + "accountTargetEntity.Contains(\"new_budget\"): " + accountTargetEntity.Contains("new_budget").ToString()
                                                            , tracingService);


                    AccountServicesHelper.writeToTrace("budget: " + budget
                                                            + Environment.NewLine + "legalId: " + legalId
                                                            + Environment.NewLine + "activityCodeId: " + activityCodeId.ToString()
                                                            , tracingService);



                    if (accountTargetEntity.Contains("new_legalidentifier") && string.IsNullOrEmpty(legalId) || !accountTargetEntity.Contains("new_legalidentifier") && string.IsNullOrEmpty(account.GetAttributeValue<string>("new_legalidentifier"))
                           || accountTargetEntity.Contains("new_activitycode") && activityCode == null || !accountTargetEntity.Contains("new_activitycode") && account.GetAttributeValue<EntityReference>("new_activitycode") == null
                               || accountTargetEntity.Contains("new_budget") && string.IsNullOrEmpty(budget) || !accountTargetEntity.Contains("new_budget") && string.IsNullOrEmpty(account.GetAttributeValue<string>("new_budget"))
                               || accountTargetEntity.Contains("telephone1") && string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("telephone1")) || !accountTargetEntity.Contains("telephone1") && string.IsNullOrEmpty(account.GetAttributeValue<string>("telephone1"))
                           )
                        throw new InvalidPluginExecutionException("If an Organization Designation is provided:  'Legal Identifier', 'Activity Code', 'Budget', and 'Phone Number' are required");

                }

                
                if (accountTargetEntity.Contains("new_budget"))
                {
                    string budget = accountTargetEntity.GetAttributeValue<string>("new_budget");

                    if (!AccountServicesHelper.regexMatch(@"^\d+$", budget))
                        throw new InvalidPluginExecutionException("Budget must be a numerical value");
                }


                EntityReference pngoRef = accountTargetEntity.GetAttributeValue<EntityReference>("ts_orgppid");

                if (pngoRef == null)
                    pngoRef = account.GetAttributeValue<EntityReference>("ts_orgppid");

                Entity pngoAccount = null;
                string tsPngoId = string.Empty;
                string tsPngoCode = string.Empty;

                if (pngoRef != null)
                {
                    pngoAccount = service.Retrieve("account", pngoRef.Id, new ColumnSet("ts_tspngoid", "ts_tspngocode", "accountnumber"));
                    tsPngoId = pngoAccount.GetAttributeValue<string>("ts_tspngoid");
                    tsPngoCode = pngoAccount.GetAttributeValue<string>("ts_tspngocode");
                }

                if (accountTargetEntity.Contains("ts_orgppid") && accountTargetEntity.GetAttributeValue<EntityReference>("ts_orgppid") != null)
                {
                    if (string.IsNullOrEmpty(tsPngoId))
                        throw new InvalidPluginExecutionException("The Organization selected for 'Org's Partner Platform' is not a PNGO account");
                }



                if (
                    accountTargetEntity.Contains("ts_orgppid") && accountTargetEntity.GetAttributeValue<EntityReference>("ts_orgppid") != null
                    ||
                    !accountTargetEntity.Contains("ts_orgppid") && account.GetAttributeValue<EntityReference>("ts_orgppid") != null      
                    )
                {
                    if (accountTargetEntity.Contains("ts_pporgid") && string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("ts_pporgid"))
                        || !accountTargetEntity.Contains("ts_pporgid") && string.IsNullOrEmpty(account.GetAttributeValue<string>("ts_pporgid"))
                        )
                    {
                        throw new InvalidPluginExecutionException("If 'Org's Partner Platform' is provided, 'OrgId in Partner Platform' must also be provided");
                    }


                    if (
                        accountTargetEntity.Contains("new_orgdesignation") && accountTargetEntity.GetAttributeValue<EntityReference>("new_orgdesignation") == null
                        ||
                        !accountTargetEntity.Contains("new_orgdesignation") && account.GetAttributeValue<EntityReference>("new_orgdesignation") == null
                        )

                    {
                        throw new InvalidPluginExecutionException("If 'Org's Partner Platform' is provided, and Org Designation must also be provided");
                    }
                }

                if (
                    accountTargetEntity.Contains("ts_pporgid") && !string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("ts_pporgid"))
                    || !accountTargetEntity.Contains("ts_pporgid") && !string.IsNullOrEmpty(account.GetAttributeValue<string>("ts_pporgid"))

                    )
                {
                    if (
                        accountTargetEntity.Contains("ts_orgppid") && accountTargetEntity.GetAttributeValue<EntityReference>("ts_orgppid") == null
                        ||
                        !accountTargetEntity.Contains("ts_orgppid") && account.GetAttributeValue<EntityReference>("ts_orgppid") == null
                    )
                    {
                        throw new InvalidPluginExecutionException("If 'OrgId in Partner Platform' is provided, 'Org's Partner Platform' must also be provided");
                    }
                }



                if (
                    accountTargetEntity.Contains("ts_orgppid") && accountTargetEntity.GetAttributeValue<EntityReference>("ts_orgppid") != null
                    || accountTargetEntity.Contains("ts_pporgid") && !string.IsNullOrEmpty(accountTargetEntity.GetAttributeValue<string>("ts_pporgid"))
                    )
                {
                    string platformId = accountTargetEntity.Contains("ts_pporgid") ? accountTargetEntity.GetAttributeValue<string>("ts_pporgid") : account.GetAttributeValue<string>("ts_pporgid");

                    accountTargetEntity["new_platformid"] = tsPngoCode + "." + platformId;
                }





                string name = accountTargetEntity.Contains("name") ? accountTargetEntity.GetAttributeValue<string>("name") : account.GetAttributeValue<string>("name");
                //name = name.Length < 20 ? name.Substring(0, name.Length) : name.Substring(0, 20);

                string legalIdentifier = accountTargetEntity.Contains("new_legalidentifier") ? accountTargetEntity.GetAttributeValue<string>("new_legalidentifier") : account.GetAttributeValue<string>("new_legalidentifier");

                string addressLine1 = accountTargetEntity.Contains("address1_line1") ? accountTargetEntity.GetAttributeValue<string>("address1_line1") : account.GetAttributeValue<string>("address1_line1");
                //addressLine1 = addressLine1.Length < 20 ? addressLine1.Substring(0, addressLine1.Length) : addressLine1.Substring(0, 20);

                string addressPostalCode = accountTargetEntity.Contains("address1_postalcode") ? accountTargetEntity.GetAttributeValue<string>("address1_postalcode") : account.GetAttributeValue<string>("address1_postalcode");
                addressPostalCode = addressPostalCode.Length < 5 ? addressPostalCode.Substring(0, addressPostalCode.Length) : addressPostalCode.Substring(0, 5);



                Entity matchingAccount = AccountServicesHelper.findMatchAccount(name, legalIdentifier, addressLine1, addressPostalCode, accountTargetEntity.Id
                                                                                    , service, tracingService);

                if (matchingAccount != null)
                {
                    string matchingTsOrgId = matchingAccount.GetAttributeValue<string>("accountnumber");
                    throw new InvalidPluginExecutionException("Another account already exists. TSOrgId: " + matchingTsOrgId);
                }


                if (fullName.Contains("TSDynamics") || fullName.Contains("TSDynamicsOnyx") || fullName.Contains("DynamicsClient") || fullName.Contains("DynamicsESBIntegration") || fullName.Contains("SYSTEM"))
                    return accountTargetEntity;


                EntityReference duplicateOfRef = account.GetAttributeValue<EntityReference>("ts_duplicateofid");
                Guid duplicateOfId = duplicateOfRef == null ? Guid.Empty : duplicateOfRef.Id;

                if (
                accountTargetEntity.Contains("ts_duplicateofid") && accountTargetEntity.GetAttributeValue<EntityReference>("ts_duplicateofid") != null
                    )
                {
                    EntityReference duplicateOfRefTarget = accountTargetEntity.GetAttributeValue<EntityReference>("ts_duplicateofid");
                    Guid duplicateOfIdTarget = duplicateOfRefTarget == null ? Guid.Empty : duplicateOfRefTarget.Id;

                    if (duplicateOfIdTarget != duplicateOfId)
                        throw new InvalidPluginExecutionException("Please use a Qualification Case to set this account as duplicate");
                }
            }
            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);


                AccountServicesHelper.writeToTrace("Error in validateAccountUpdate(...). Exception message: "
                                                        + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "accountTargetEntityId: " + accountTargetEntity.Id.ToString()
                                                        , tracingService);

            }

            return accountTargetEntity;
        }
        public static string getTSOrgId(Entity accountTargetEntity
                                            , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            string preTsOrgId = string.Empty;
            try
            {
                if (accountTargetEntity.Contains("accountnumber"))
                {
                    preTsOrgId = accountTargetEntity.GetAttributeValue<string>("accountnumber");                   


                    AccountServicesHelper.writeToTrace("contains tsorgId: "
                                                            + Environment.NewLine + preTsOrgId
                                                            , tracingService);

                    return preTsOrgId;
                }

                preTsOrgId = AccountServicesHelper.getNextTsCustomerId(EnvVariables, tracingService);

                if (!string.IsNullOrEmpty(preTsOrgId))
                    return preTsOrgId;

                throw new InvalidPluginExecutionException("TSOrgId could not be generated");

            }
            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);


                AccountServicesHelper.writeToTrace("Error in getTSOrgId(...). Exception message: "
                                                        + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "accountTargetEntityId: " + accountTargetEntity.Id.ToString()
                                                        , tracingService);


            }

            return preTsOrgId;
        }
        public static void validateOrgDesignationUpdate(Entity accountTargetEntity, Entity account, string tsOrgId
                                                            , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {

            try
            {
                if (!accountTargetEntity.Contains("new_orgdesignation"))
                {
                    AccountServicesHelper.writeToTrace("Does not contain new_orgdesignation"
                                                            , tracingService);
                    return;
                }

                Entity user = service.Retrieve("systemuser", context.InitiatingUserId, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");


                AccountServicesHelper.writeToTrace("User: " + fullName
                                                        , tracingService);

                if (fullName.Contains("TSDynamicsOnyx"))
                    return;

               

                EntityReference orgDesigEntRef = accountTargetEntity.GetAttributeValue<EntityReference>("new_orgdesignation");

                Entity qualCodeEntity = service.Retrieve("new_qualificationcode", orgDesigEntRef.Id, new ColumnSet(true));
                string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                string qualCategory = qualCodeEntity.GetAttributeValue<string>("new_qualcategory");


                string accountCountryCode = account.GetAttributeValue<string>("address1_country");

                if (accountCountryCode.ToLower() == "gb")
                    accountCountryCode = "uk";

                if (qualCategory != "QualOrg" || !qualCode.StartsWith(accountCountryCode.ToLower()))
                {
                    throw new InvalidPluginExecutionException("The Organization Designation must be of 'Organization Qualification' type, and must correspond to the organization country");
                }

            }
            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);


                AccountServicesHelper.writeToTrace("Error in validateOrgDesignation(...). Exception message: "
                                                        + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "tsOrgId: " + tsOrgId
                                                        , tracingService);
            }



        }
        //var ine = context.InputParameters.Select(x => x.Key + " | " + x.Value).ToArray();

        //var paranmcsv = string.Join(";", ine);
        //AccountServicesHelper.writeToTrace("inputparameters: " + paranmcsv
                                                        //, tracingService);

    }
}