
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
using System.Security.Policy;
using System.Net;
using Microsoft.Xrm.Sdk.Messages;

namespace AccountServices
{
     
    public class GetOrgData : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public static List<string> ErrorStack;
        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));


            AccountServicesHelper.writeToTrace("Starting - AccountServices.GetOrgData"
                                                            , tracingService);

            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                /**********GET Custom API Request Parameters*********/
                Entity tsRequest = (Entity)context.InputParameters["ts_request"];
                /****************************************************/


                ErrorStack = new List<string>();




                string tsOrgId = tsRequest.Contains("TSOrgId") && !string.IsNullOrEmpty(tsRequest.GetAttributeValue<string>("TSOrgId")) ? tsRequest.GetAttributeValue<string>("TSOrgId")
                    : string.Empty;

                Entity tsResult = null;
                string resultStatus = "success";

                if (string.IsNullOrEmpty(tsOrgId))
                {
                    tsResult = new Entity();
                    tsResult.Attributes.Add("resultStatus", "failure");
                    tsResult.Attributes.Add("error", "TSOrgId not provided");
                    context.OutputParameters["ts_response"] = tsResult;
                    return;
                }



                Entity orgEntity = getOrgEntity(tsRequest, tsOrgId
                    , context, service, tracingService);


                string errorStackText = string.Empty;
                if (ErrorStack.Count > 0)
                    errorStackText = AccountServicesHelper.getErrorsFromStack(ErrorStack);


                if (!string.IsNullOrEmpty(errorStackText))
                {
                    tsResult = new Entity();
                    tsResult.Attributes.Add("TSOrgId", tsOrgId);
                    tsResult.Attributes.Add("resultStatus", "failure");
                    tsResult.Attributes.Add("error", errorStackText);                    
                }
                else
                {
                    tsResult = orgEntity;
                }
                /**********ASSIGN Custom API Response Parameters*********/
                context.OutputParameters["ts_response"] = tsResult;
                /********************************************************/

            }

            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error during AccountServices.GetOrgData: " + e.Message
                                                            , tracingService);
            }

        }

        public static Entity getOrgEntity(Entity tsRequest, string tsOrgId
                                            , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            Entity orgEntity = null;
            try
            {

                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(true);
                queryAccount.Criteria.AddCondition("accountnumber", ConditionOperator.Equal, tsOrgId);
                EntityCollection accountCollection = service.RetrieveMultiple(queryAccount);

                if (accountCollection.Entities.Count == 0)
                {
                    string error = "No organization found for TSOrgId: " + tsOrgId;


                    AccountServicesHelper.writeToTrace("At getOrgEntity(...). " + error
                                                            , tracingService);

                    ErrorStack.Add(error);
                    return null;
                }

                Entity account = accountCollection.Entities.First();
                Guid accountId = accountCollection.Entities.First().Id;

                orgEntity = new Entity();

                orgEntity.Attributes.Add("TSOrgId", tsOrgId);

                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                string orgDesignation = string.Empty;
                string orgDesignationDescription = string.Empty;
                Guid orgDesigId = Guid.Empty;
                if (orgDesigRef != null)
                {
                    Entity qualCodeEntity = service.Retrieve("new_qualificationcode", orgDesigRef.Id, new ColumnSet(true));
                    orgDesigId = orgDesigRef.Id;
                    orgDesignation = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                    orgDesignationDescription = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                }

                string vchCompanyName = account.GetAttributeValue<string>("name");

                string customerTypeCode = string.Empty;
                if (account.Contains("customertypecode"))
                    customerTypeCode = account.FormattedValues["customertypecode"];

                OptionSetValue orgSourceOption = account.GetAttributeValue<OptionSetValue>("new_source");
                int orgSource = orgSourceOption == null ? 0 : orgSourceOption.Value;

                //
                
                string companyTypeCode = account.Contains("ts_countrydesc") ? account.FormattedValues["ts_countrydesc"] : account.GetAttributeValue<string>("address1_country");
                orgEntity.Attributes.Add("name", vchCompanyName);
                orgEntity.Attributes.Add("companyTypeCode", companyTypeCode);
                orgEntity.Attributes.Add("orgDesignation", orgDesignation);
                orgEntity.Attributes.Add("orgDesignationDescription", orgDesignationDescription);
                orgEntity.Attributes.Add("customerTypeCode", customerTypeCode);
                orgEntity.Attributes.Add("sourceId", orgSource.ToString());


                string countryCode = account.GetAttributeValue<string>("address1_country");
                string regionCode = account.GetAttributeValue<string>("address1_stateorprovince");
                string address1 = account.GetAttributeValue<string>("address1_line1");
                string address2 = account.GetAttributeValue<string>("address1_line2");
                string address3 = account.GetAttributeValue<string>("address1_line3");
                string city = account.GetAttributeValue<string>("address1_city");
                string postalCode = account.GetAttributeValue<string>("address1_postalcode");



                Entity address = new Entity();

                address.Attributes.Add("address1", address1);
                if (!string.IsNullOrEmpty(address2))
                    address.Attributes.Add("address2", address2);
                if (!string.IsNullOrEmpty(address3))
                    address.Attributes.Add("address3", address3);

                address.Attributes.Add("city", city);
                address.Attributes.Add("regionCode", regionCode);
                address.Attributes.Add("postalCode", postalCode);
                address.Attributes.Add("countryCode", countryCode);

                orgEntity.Attributes.Add("address", address);



                string assignedId = account.GetAttributeValue<string>("new_platformid");
                string email = account.GetAttributeValue<string>("emailaddress1");
                string phone = account.GetAttributeValue<string>("telephone1");
                string legalIdentifier = account.GetAttributeValue<string>("new_legalidentifier");
                string url = account.GetAttributeValue<string>("websiteurl");
                string budget = account.GetAttributeValue<string>("new_budget");
                string isEmailValidCode = account.GetAttributeValue<bool>("new_isemailvalid") ? "0" : "1";
                string associationCode = account.GetAttributeValue<string>("new_associationcode");

                orgEntity.Attributes.Add("email", email);
                orgEntity.Attributes.Add("url", url);
                orgEntity.Attributes.Add("phone", phone);
                orgEntity.Attributes.Add("legalIdentifier", legalIdentifier);
                orgEntity.Attributes.Add("budget", budget);

                if (!string.IsNullOrEmpty(associationCode))
                    orgEntity.Attributes.Add("associationCode", associationCode);



                EntityReference activityCodeRef = account.GetAttributeValue<EntityReference>("new_activitycode");
                string activityCode = string.Empty;
                string activityCodeDescription = string.Empty;
                string activityCodeCategory = string.Empty;
                if (activityCodeRef != null)
                {
                    Entity activityCodeEntity = service.Retrieve("new_activitycodes", activityCodeRef.Id, new ColumnSet(true));
                    activityCode = activityCodeEntity.GetAttributeValue<string>("new_activitycode");
                    activityCodeDescription = activityCodeEntity.GetAttributeValue<string>("new_activitycodedescription"); 
                    activityCodeCategory = activityCodeEntity.GetAttributeValue<string>("new_activitycodecategory");

                }

                orgEntity.Attributes.Add("activityCode", activityCode);
                orgEntity.Attributes.Add("activityCodeDescription", activityCodeDescription);
                orgEntity.Attributes.Add("activityCodeCategory", activityCodeCategory);

                EntityReference duplicateOfRef = account.GetAttributeValue<EntityReference>("ts_duplicateofid");
                string duplicateOf = string.Empty;
                if (duplicateOfRef != null)
                {
                    Entity accountDupeOf = service.Retrieve("account", duplicateOfRef.Id, new ColumnSet("accountnumber"));
                    duplicateOf = accountDupeOf.GetAttributeValue<string>("accountnumber");
                }

                orgEntity.Attributes.Add("duplicateOf", duplicateOf);


                int numberOfEmployees = account.GetAttributeValue<int>("numberofemployees");
                orgEntity.Attributes.Add("numberOfEmployees", numberOfEmployees.ToString());


                string partnerCode = string.Empty;
                string pngoId = string.Empty;
                EntityReference pngoRef = account.GetAttributeValue<EntityReference>("ts_orgppid");
                if (pngoRef != null)
                {
                    Entity pngoAccount = service.Retrieve("account", pngoRef.Id, new ColumnSet("ts_tspngoid", "ts_tspngocode", "accountnumber"));
                    partnerCode = pngoAccount.GetAttributeValue<string>("ts_tspngocode");
                    pngoId = pngoAccount.GetAttributeValue<string>("ts_tspngoid");

                    if (!string.IsNullOrEmpty(pngoId))
                    {
                        orgEntity.Attributes.Add("pngoId", pngoId);
                        string orgRefId = account.GetAttributeValue<string>("ts_pporgid");
                        orgEntity.Attributes.Add("orgRefId", orgRefId);

                    }
                }


                string orgQualStatus = AccountServicesHelper.getOrgQualStatus(accountId, orgDesigId
                                                                                , tsOrgId, orgDesignation
                                                                                , service, tracingService);

                orgEntity.Attributes.Add("qualificationStatus", orgQualStatus);

                Entity caseEntity = AccountServicesHelper.getCaseEntity(2, 101996
                                                                            , accountId, orgDesigId, null
                                                                            , service, tracingService);

                string qualCaseStatus = string.Empty;                 
                if (caseEntity != null)
                    qualCaseStatus = caseEntity.FormattedValues["ts_casestatus"];

                orgEntity.Attributes.Add("qualificationCaseStatus", qualCaseStatus);

            }
            catch (Exception e)
            {
                string error = "Error in getOrgEntity(...). Exception message: "
                                        + Environment.NewLine + e.Message;
                
                AccountServicesHelper.writeToTrace(error
                                                        , tracingService);

                ErrorStack.Add(error);
            }

            return orgEntity;
        }
       
    }
}