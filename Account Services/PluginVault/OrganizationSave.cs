
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
using System.Web.Configuration;
using System.Data.Common;

namespace AccountServices
{
     
    public class OrganizationSave : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public static List<string> errorStack;
        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));


            AccountServicesHelper.writeToTrace($"Starting - AccountServices.OrganizationSave"
                                                                                            , tracingService);

            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                /**********GET Custom API Request Parameters*********/
                Entity tsRequest = (Entity)context.InputParameters["ts_request"];
                /****************************************************/



                errorStack = new List<string>();



             


                Entity tsResult = new Entity();
                tsResult.Attributes.Add("duplicateOrg", false);

                Entity postActionAccount = null;
                string tsOrgId = string.Empty;
                if (tsRequest.Contains("TSOrgId"))
                {
                    tsOrgId = tsRequest.GetAttributeValue<string>("TSOrgId");

                    postActionAccount = updateOrg(tsRequest, tsOrgId, tsResult
                                                                            , context, service, tracingService, serviceFactory);
                }
                else
                {
                    postActionAccount = createOrg(tsRequest, tsResult
                                                                    , context, service, tracingService, serviceFactory);
                    if (postActionAccount != null)
                        tsOrgId = postActionAccount.GetAttributeValue<string>("accountnumber");
                }

                



                string errorStackText = string.Empty;
                if (errorStack.Count > 0)
                    errorStackText = getErrorsFromStack();

                string resultStatus = "success";
                if (postActionAccount == null || !string.IsNullOrEmpty(errorStackText))
                    resultStatus = "failure";

                
                tsResult.Attributes.Add("resultStatus", resultStatus);
                if (!string.IsNullOrEmpty(errorStackText))
                    tsResult.Attributes.Add("error", errorStackText);
                
                if (!string.IsNullOrEmpty(tsOrgId))
                    tsResult.Attributes.Add("TSOrgId", tsOrgId);

                /**********ASSIGN Custom API Response Parameters*********/
                context.OutputParameters["ts_response"] = tsResult;
                /********************************************************/

            }

            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace($"Error during AccountServices.OrganizationSave:{Environment.NewLine}{e.Message}"
                                                                                                                                , tracingService);
            }

        }

        public static Entity updateOrg(Entity tsRequest, string tsOrgId, Entity tsResult
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService, IOrganizationServiceFactory serviceFactory)
        {
            Entity postActionAccount = null;


            try
            {

                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(true);
                queryAccount.Criteria.AddCondition("accountnumber", ConditionOperator.Equal, tsOrgId);
                EntityCollection accountCollection = service.RetrieveMultiple(queryAccount);

                if (accountCollection.Entities.Count == 0)
                {
                    string error = "No organization found for TSOrgId: " + tsOrgId;
                    AccountServicesHelper.writeToTrace(error
                                                            , tracingService);
                    errorStack.Add(error);
                    return null;
                }


                AccountServicesHelper.writeToTrace($"updateOrg(). tsOrgId: {tsOrgId}"
                                                                                        , tracingService);


                Entity accountCurrent = accountCollection.Entities.First();
                Guid accountId = accountCollection.Entities.First().Id;

                Entity account = service.Retrieve("account", accountId, new ColumnSet(false));

                if (tsRequest.Contains("name"))
                    account["name"] = tsRequest.GetAttributeValue<string>("name");

               
                if (tsRequest.Contains("sourceId"))
                {
                    string sourceId = tsRequest.GetAttributeValue<string>("sourceId");
                    account["new_source"] = new OptionSetValue(int.Parse(sourceId));
                }



                string qualName = "";
                string qualCode = "";
                Guid qualCodeId = Guid.Empty;
                if (tsRequest.Contains("orgDesignation"))
                {
                    string orgDesigCode = tsRequest.GetAttributeValue<string>("orgDesignation");

                    QueryExpression queryQualCode = new QueryExpression("new_qualificationcode");
                    queryQualCode.ColumnSet = new ColumnSet(true);
                    queryQualCode.Criteria.AddCondition("new_qualcode", ConditionOperator.Equal, orgDesigCode);
                    EntityCollection qualCodeCollection = service.RetrieveMultiple(queryQualCode);
                    if (qualCodeCollection.Entities.Count > 0)
                    {
                        qualCodeId = qualCodeCollection.Entities.First().Id;
                        Entity qualCodeEntity = qualCodeCollection.Entities.First();
                        qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                        qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");

                        EntityReference currentOrgDesigEntRef = accountCurrent.GetAttributeValue<EntityReference>("new_orgdesignation");
                        Guid currentOrgDesigId = currentOrgDesigEntRef == null ? Guid.Empty : currentOrgDesigEntRef.Id;

                        if (qualCodeId != currentOrgDesigId)
                            account["new_orgdesignation"] = new EntityReference("new_qualificationcode", qualCodeId);
                    }
                    else
                    {
                        string error = "The orgDesignation is invalid";
                        AccountServicesHelper.writeToTrace(error
                                                                , tracingService);
                        errorStack.Add(error);
                        return null;
                    }
                }




                if (tsRequest.Contains("address"))
                {
                    Entity address = tsRequest.GetAttributeValue<Entity>("address");

                    account["address1_country"] = address.GetAttributeValue<string>("countryCode");

                    if (address.GetAttributeValue<string>("countryCode").ToLower() == "us" && !address.Contains("regionCode"))
                    {
                        string error = "For 'US' countryCode, a regionCode must be provided";
                        AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                        errorStack.Add(error);
                        return null;
                    }

                    string regionCode = address.Contains("regionCode") ? address.GetAttributeValue<string>("regionCode") : "--";
                    account["address1_stateorprovince"] = regionCode;

                    account["address1_line1"] = address.GetAttributeValue<string>("address1");
                    account["address1_line2"] = address.Contains("address2") ? address.GetAttributeValue<string>("address2") : null;
                    account["address1_line3"] = address.Contains("address3") ? address.GetAttributeValue<string>("address3") : null;

                    account["address1_city"] = address.GetAttributeValue<string>("city");
                    account["address1_postalcode"] = address.GetAttributeValue<string>("postalCode");



                    QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                    queryFieldMap.ColumnSet = new ColumnSet(true);
                    queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                    queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, address.GetAttributeValue<string>("countryCode"));
                    EntityCollection fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                    if (fieldMapCollection.Entities.Count > 0)
                    {
                        Entity fieldHierarchy = fieldMapCollection.Entities.First();
                        int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                        account["ts_countrydesc"] = new OptionSetValue(countryOptionValue);
                    }


                    queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                    queryFieldMap.ColumnSet = new ColumnSet(true);
                    queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                    queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, address.GetAttributeValue<string>("countryCode"));
                    queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, regionCode);
                    fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                    if (fieldMapCollection.Entities.Count > 0)
                    {
                        Entity fieldHierarchy = fieldMapCollection.Entities.First();
                        int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                        account["ts_stateprovdesc"] = new OptionSetValue(countryOptionValue);
                    }
                }


                if (tsRequest.Contains("email"))
                    account["emailaddress1"] = tsRequest.GetAttributeValue<string>("email");

                if (tsRequest.Contains("phone"))
                    account["telephone1"] = tsRequest.GetAttributeValue<string>("phone");



                if (tsRequest.Contains("legalIdentifiers"))
                {
                    EntityCollection legalIdentifiers = tsRequest.GetAttributeValue<EntityCollection>("legalIdentifiers");

                    if (legalIdentifiers.Entities.Count > 0)
                    {
                        Entity legalIdEntity = legalIdentifiers.Entities.First();
                        account["new_legalidentifier"] = legalIdEntity.GetAttributeValue<string>("identifier");
                    }
                }


                if (tsRequest.Contains("url"))
                    account["websiteurl"] = tsRequest.GetAttributeValue<string>("url");

                if (tsRequest.Contains("budget"))
                {
                    Entity budget = tsRequest.GetAttributeValue<Entity>("budget");
                    account["new_budget"] = budget.GetAttributeValue<string>("value");
                }

                if (tsRequest.Contains("associationCode"))
                    account["new_associationcode"] = tsRequest.GetAttributeValue<string>("associationCode");

                if (tsRequest.Contains("activityCode"))
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, tsRequest.GetAttributeValue<string>("activityCode"));
                    EntityCollection entityCollection = service.RetrieveMultiple(queryEntity);

                    if (entityCollection.Entities.Count > 0)
                        account["new_activitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);
                }



                string pngoId = tsRequest.Contains("pngoId") ? tsRequest.GetAttributeValue<string>("pngoId") : string.Empty;

                EntityReference pngoRef = accountCurrent.GetAttributeValue<EntityReference>("ts_orgppid");

                if (pngoRef != null)
                {
                    Entity pngoAccount = service.Retrieve("account", pngoRef.Id, new ColumnSet("ts_tspngoid", "ts_tspngocode", "accountnumber"));
                    string tsPngoId = pngoAccount.GetAttributeValue<string>("ts_tspngoid");
                    string tsPngoCode = pngoAccount.GetAttributeValue<string>("ts_tspngocode");

                    if (tsPngoId != pngoId)
                    {
                        string error = "The pngoId provided does not match the one on the Org";
                        AccountServicesHelper.writeToTrace(error
                                                                  , tracingService);
                        errorStack.Add(error);
                        return null;
                    }
                }


                string orgRefId = tsRequest.Contains("orgRefId") ? tsRequest.GetAttributeValue<string>("orgRefId") : string.Empty;
                string ppOrgId = accountCurrent.GetAttributeValue<string>("ts_pporgid");

                if (tsRequest.Contains("orgRefId") && orgRefId != ppOrgId)
                    account["ts_pporgid"] = orgRefId;


                if (tsRequest.Contains("numberOfEmployees"))
                {
                    string numberOfEmployees = tsRequest.GetAttributeValue<string>("numberOfEmployees");
                    int numberOfEmployeesNumber = 0;
                    if (int.TryParse(numberOfEmployees, out numberOfEmployeesNumber))
                    {
                        account["numberofemployees"] = numberOfEmployeesNumber;
                    }
                    else
                    {
                        string error = "numberOfEmployees must a numerical value";
                        tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "At createOrg(...). " + error
                        );
                        tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
                        errorStack.Add(error);
                        return null;
                    }
                }

                if (account.Attributes.Count > 1)
                    service.Update(account);


                postActionAccount = service.Retrieve(account.LogicalName, account.Id, new ColumnSet(true));

                EntityReference orgDesigRef = postActionAccount.GetAttributeValue<EntityReference>("new_orgdesignation");

                if (tsRequest.Contains("qualificationStatus") && orgDesigRef != null)
                {
                    qualCodeId = orgDesigRef.Id;

                    string qualStatus = tsRequest.GetAttributeValue<string>("qualificationStatus");

                    QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                    queryMapping.ColumnSet = new ColumnSet("ts_value", "ts_valuecode");
                    queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "ts_casestatus");
                    queryMapping.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, "Organization Qualification");
                    queryMapping.Criteria.AddCondition("ts_mappedfieldvalue", ConditionOperator.Equal, qualStatus);
                    EntityCollection mappingCollection = service.RetrieveMultiple(queryMapping);

                    if (mappingCollection.Entities.Count == 0)
                    {
                        string error = "The qualificationStatus is invalid";
                        AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                        errorStack.Add(error);
                    }
                    else
                    {
                        Entity fieldMapping = mappingCollection.Entities.First();
                        string tsCaseStatus = fieldMapping.GetAttributeValue<string>("ts_value");//Case status
                        int tsCaseStatusCode = fieldMapping.GetAttributeValue<int>("ts_valuecode");//Case status option value

                        //2 - Qualification Case; 101996 - Organization Qualification; 
                        Entity caseEntity = AccountServicesHelper.getCaseEntity(2, 101996
                                                                                    , accountId, qualCodeId, null
                                                                                    , service, tracingService);

                        if (caseEntity != null)
                        {
                            caseEntity["ts_casestatus"] = new OptionSetValue(tsCaseStatusCode);
                            service.Update(caseEntity);
                        }
                        else
                        {
                            Entity qualCodeEntity = service.Retrieve("new_qualificationcode", orgDesigRef.Id, new ColumnSet(true));
                            qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                            qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");

                            Guid caseId = AccountServicesHelper.createCase(title: qualCode + " - " + qualName
                                                                                , caseTypeCode: 2
                                                                                , type: 101996
                                                                                , customerRef: new EntityReference(account.LogicalName, accountId)
                                                                                , caseStatus: tsCaseStatusCode
                                                                                , qualCodeId: qualCodeId
                                                                                , extraCaseFields: null
                                                                                , service, tracingService);
                        }
                    }
                }



                if (tsRequest.Contains("associations"))
                {
                    EntityCollection associations = tsRequest.GetAttributeValue<EntityCollection>("associations");

                    foreach (Entity association in associations.Entities)
                    {
                        string tsContactId = association.GetAttributeValue<string>("contactTSID");
                        string tsContactType = association.GetAttributeValue<string>("contactTypeTSID");

                        QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                        queryFieldMap.ColumnSet = new ColumnSet(true);
                        queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "tsContactType");
                        queryFieldMap.Criteria.AddCondition("ts_valuecode", ConditionOperator.Equal, int.Parse(tsContactType));
                        EntityCollection fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                        if (fieldMapCollection.Entities.Count > 0)
                        {
                            Entity fieldHierarchy = fieldMapCollection.Entities.First();
                            string contactTypeTo = fieldHierarchy.GetAttributeValue<string>("ts_value");

                            addConnectionToContact(accountId, tsOrgId, tsContactId, "Employer", contactTypeTo
                                                                                                            , context, service, tracingService, serviceFactory);
                        }
                    }
                }


                if (tsRequest.Contains("organizationReferences"))
                {
                    EntityCollection organizationReferences = null;

                    if (!tsRequest.TryGetAttributeValue<EntityCollection>("organizationReferences", out organizationReferences))
                    {
                        organizationReferences = new EntityCollection();
                        Entity organizationReference = tsRequest.GetAttributeValue<Entity>("organizationReferences");
                        organizationReferences.Entities.Add(organizationReference);
                    }

                    foreach (Entity organizationReference in organizationReferences.Entities)
                    {
                        string referenceType = organizationReference.GetAttributeValue<string>("referenceType");
                        string referenceValue = organizationReference.GetAttributeValue<string>("referenceValue");

                        int refType = AccountServicesHelper.getAttributeOptionValue("ts_accountreference", "ts_referencetype", referenceType
                            , service, tracingService);

                        if (refType != 0)
                        {
                            AccountServicesHelper.addAccountReference(tsOrgId, accountId, refType, referenceValue
                                , service, tracingService);
                        }
                        else
                        {
                            string error = "Invalid referenceType '" + referenceType + "' in organizationReferences";
                            AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                            errorStack.Add(error);
                            //return null;
                        }
                    }
                }


                if (tsRequest.Contains("legalIdentifiers"))
                {
                    EntityCollection legalIdentifiers = tsRequest.GetAttributeValue<EntityCollection>("legalIdentifiers");

                    int counter = 0;
                    foreach (Entity legalIdentifier in legalIdentifiers.Entities)
                    {
                        counter++;
                        if (counter > 1)
                        {
                            string legalIdentifierType = legalIdentifier.GetAttributeValue<string>("type");
                            string legalIdentifierValue = legalIdentifier.GetAttributeValue<string>("identifier");

                            string legalIdentifierOptionLabel = "Legal Identifier " + counter.ToString();
                            int refType = AccountServicesHelper.getAttributeOptionValue("ts_accountreference", "ts_referencetype", legalIdentifierOptionLabel
                                , service, tracingService);

                            if (refType != 0)
                                AccountServicesHelper.addAccountReference(tsOrgId, accountId, refType, legalIdentifierType + ":" + legalIdentifierValue
                                                                                                                                                    , service, tracingService);

                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (e.Message.StartsWith("Another account already exists"))
                    tsResult.Attributes["duplicateOrg"] = true;


                string error = $"Error in updateOrg(). Exception message:{Environment.NewLine}{e.Message}"
                                ;

                AccountServicesHelper.writeToTrace(error
                                                            , tracingService);

                errorStack.Add(error);
            }

            return postActionAccount;
        }
        public static Entity createOrg(Entity tsRequest, Entity tsResult
                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService, IOrganizationServiceFactory serviceFactory)
        {
            Entity newAccount = null;
            Guid accountId = Guid.Empty;
            string tsOrgId = string.Empty;
            try
            {

                
                Entity account = new Entity("account");


                account["name"] = tsRequest.GetAttributeValue<string>("name");

                account["customertypecode"] = new OptionSetValue(3); //Customer


                if (tsRequest.Contains("sourceId"))
                {
                    string sourceId = tsRequest.GetAttributeValue<string>("sourceId");
                    account["new_source"] = new OptionSetValue(int.Parse(sourceId));
                }
                else
                {
                    account["new_source"] = new OptionSetValue(101892); //TSS Web Site 101892
                }




                Guid qualCodeId = Guid.Empty;
                if (tsRequest.Contains("orgDesignation"))
                {
                    string orgDesigCode = tsRequest.GetAttributeValue<string>("orgDesignation");
                    QueryExpression queryQualCode = new QueryExpression("new_qualificationcode");
                    queryQualCode.Criteria.AddCondition("new_qualcode", ConditionOperator.Equal, orgDesigCode);
                    EntityCollection qualCodeCollection = service.RetrieveMultiple(queryQualCode);
                    if (qualCodeCollection.Entities.Count > 0)
                    {
                        qualCodeId = qualCodeCollection.Entities.First().Id;
                        account["new_orgdesignation"] = new EntityReference("new_qualificationcode", qualCodeId);
                    }
                    else
                    {
                        string error = "The orgDesignation is invalid";
                        AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                        errorStack.Add(error);
                        return null;
                    }
                }



                Entity address = tsRequest.GetAttributeValue<Entity>("address");

                account["address1_country"] = address.GetAttributeValue<string>("countryCode"); 

                if (address.GetAttributeValue<string>("countryCode").ToLower() == "us" && !address.Contains("regionCode"))
                {
                    string error = "For 'US' countryCode, a regionCode must be provided";
                    AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                    errorStack.Add(error);
                    return null;
                }

                string regionCode = address.Contains("regionCode") ? address.GetAttributeValue<string>("regionCode") : "--";
                account["address1_stateorprovince"] = regionCode;

                if (address.Contains("address1"))
                    account["address1_line1"] = address.GetAttributeValue<string>("address1");
                if (address.Contains("address2"))
                    account["address1_line2"] = address.GetAttributeValue<string>("address2");
                if (address.Contains("address3"))
                    account["address1_line3"] = address.GetAttributeValue<string>("address3");
                if (address.Contains("city"))
                    account["address1_city"] = address.GetAttributeValue<string>("city");
                if (address.Contains("postalCode"))
                    account["address1_postalcode"] = address.GetAttributeValue<string>("postalCode");



                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, address.GetAttributeValue<string>("countryCode"));
                EntityCollection fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    account["ts_countrydesc"] = new OptionSetValue(countryOptionValue);
                }


                queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, address.GetAttributeValue<string>("countryCode"));
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, regionCode);
                fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    account["ts_stateprovdesc"] = new OptionSetValue(countryOptionValue);
                }






                //account["new_platformid"] = org.vchAssignedId;

                if (tsRequest.Contains("email"))
                    account["emailaddress1"] = tsRequest.GetAttributeValue<string>("email");

                if (tsRequest.Contains("phone"))
                    account["telephone1"] = tsRequest.GetAttributeValue<string>("phone");



                if (tsRequest.Contains("legalIdentifiers"))
                {
                    EntityCollection legalIdentifiers = tsRequest.GetAttributeValue<EntityCollection>("legalIdentifiers");

                    if (legalIdentifiers.Entities.Count > 0)
                    {
                        Entity legalIdEntity = legalIdentifiers.Entities.First();
                        account["new_legalidentifier"] = legalIdEntity.GetAttributeValue<string>("identifier");
                    }

                }
                else
                {
                    string error = "No legal identifier provided";
                    AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                    errorStack.Add(error);
                    return null;
                }


                

                if (tsRequest.Contains("url"))
                    account["websiteurl"] = tsRequest.GetAttributeValue<string>("url");

                if (tsRequest.Contains("budget"))
                {
                    Entity budget = tsRequest.GetAttributeValue<Entity>("budget");
                    account["new_budget"] = budget.GetAttributeValue<string>("value");
                }
                

                if (tsRequest.Contains("associationCode"))
                    account["new_associationcode"] = tsRequest.GetAttributeValue<string>("associationCode");

                if (tsRequest.Contains("activityCode"))
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, tsRequest.GetAttributeValue<string>("activityCode"));
                    EntityCollection entityCollection = service.RetrieveMultiple(queryEntity);

                    if (entityCollection.Entities.Count > 0)
                        account["new_activitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);

                }

                if (tsRequest.Contains("pngoId") && !tsRequest.Contains("orgRefId")
                    || tsRequest.Contains("orgRefId") && !tsRequest.Contains("pngoId")

                    )
                {
                    string error = "pngoId and orgRefId are mutually required";
                    AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                    errorStack.Add(error);
                    return null;

                }

                
                if (tsRequest.Contains("pngoId"))
                {
                    string pngoId = tsRequest.GetAttributeValue<string>("pngoId");
                    string orgRefId = tsRequest.GetAttributeValue<string>("orgRefId");

                    if (pngoId == "Batch")
                    {
                        account["new_platformid"] = "Batch." + orgRefId;
                    }                    
                    else if (pngoId.ToLower() == "techsoup") { }
                    else 
                    {
                        QueryExpression queryPNGOAccount = new QueryExpression("account");
                        queryPNGOAccount.Criteria.AddCondition("ts_tspngoid", ConditionOperator.Equal, pngoId);
                        EntityCollection PNGOAccountCollection = service.RetrieveMultiple(queryPNGOAccount);
                        if (PNGOAccountCollection.Entities.Count == 0)
                        {
                            string error = "pngoId is invalid";
                            AccountServicesHelper.writeToTrace(error
                                                                    , tracingService);
                            errorStack.Add(error);
                            return null;
                        }

                        Guid pngoAccountId = PNGOAccountCollection.Entities.First().Id;
                        account["ts_orgppid"] = new EntityReference("account", pngoAccountId);
                        account["ts_pporgid"] = orgRefId;
                    }
                }


                if (tsRequest.Contains("numberOfEmployees"))
                {
                    string numberOfEmployees = tsRequest.GetAttributeValue<string>("numberOfEmployees");
                    int numberOfEmployeesNumber = 0;
                    if (int.TryParse(numberOfEmployees, out numberOfEmployeesNumber))
                    {
                        account["numberofemployees"] = numberOfEmployeesNumber;                        
                    }
                    else
                    {
                        string error = "numberOfEmployees must a numerical value";
                        AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                        errorStack.Add(error);
                        return null;
                    }
                }

                

                /***************************************************************/
                    accountId = service.Create(account);
                /***************************************************************/

                


                if (accountId == Guid.Empty)
                {
                    string error = "No account Id was created";
                    AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                    errorStack.Add(error);
                    return null;
                }

                newAccount = service.Retrieve(account.LogicalName, accountId, new ColumnSet(true));
                tsOrgId = newAccount.GetAttributeValue<string>("accountnumber");



                if (tsRequest.Contains("qualificationStatus"))
                {
                    string qualStatus = tsRequest.GetAttributeValue<string>("qualificationStatus");

                    QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                    queryMapping.ColumnSet = new ColumnSet("ts_value", "ts_valuecode");
                    queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "ts_casestatus");
                    queryMapping.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, "Organization Qualification");
                    queryMapping.Criteria.AddCondition("ts_mappedfieldvalue", ConditionOperator.Equal, qualStatus);
                    EntityCollection mappingCollection = service.RetrieveMultiple(queryMapping);

                    if (mappingCollection.Entities.Count == 0)
                    {
                        string error = "The qualificationStatus is invalid";
                        AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                        errorStack.Add(error);
                        /*Will not do return; will capture and add error to stack, but will not prevent other actions 
                         * (associations, accountReferences) to be performed
                         */
                        //return null;
                    }
                    else
                    {
                        Entity fieldMapping = mappingCollection.Entities.First();
                        string tsCaseStatus = fieldMapping.GetAttributeValue<string>("ts_value");//Case status
                        int tsCaseStatusCode = fieldMapping.GetAttributeValue<int>("ts_valuecode");//Case status option value

                        //2 - Qualification Case; 101996 - Organization Qualification; 
                        Entity caseEntity = AccountServicesHelper.getCaseEntity(2, 101996
                                                                                , accountId, qualCodeId, null
                                                                                , service, tracingService);

                        if (caseEntity != null)
                        {
                            caseEntity["ts_casestatus"] = new OptionSetValue(tsCaseStatusCode);
                            service.Update(caseEntity);
                        }
                    }
                }


                if (tsRequest.Contains("associations"))
                {
                    EntityCollection associations = tsRequest.GetAttributeValue<EntityCollection>("associations");

                    foreach (Entity association in associations.Entities)
                    {
                        string tsContactId = association.GetAttributeValue<string>("contactTSID");
                        string tsContactType = association.GetAttributeValue<string>("contactTypeTSID");

                        queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                        queryFieldMap.ColumnSet = new ColumnSet(true);
                        queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "tsContactType");
                        queryFieldMap.Criteria.AddCondition("ts_valuecode", ConditionOperator.Equal, int.Parse(tsContactType));
                        fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                        if (fieldMapCollection.Entities.Count > 0)
                        {
                            Entity fieldHierarchy = fieldMapCollection.Entities.First();
                            string contactTypeTo = fieldHierarchy.GetAttributeValue<string>("ts_value");

                            addConnectionToContact(accountId, tsOrgId, tsContactId, "Employer", contactTypeTo
                                                                                                            , context, service, tracingService, serviceFactory);
                        }
                    }
                }

                if (tsRequest.Contains("organizationReferences"))
                {
                    EntityCollection organizationReferences = null;

                    if (!tsRequest.TryGetAttributeValue<EntityCollection>("organizationReferences", out organizationReferences))
                    {
                        organizationReferences = new EntityCollection();
                        Entity organizationReference = tsRequest.GetAttributeValue<Entity>("organizationReferences");
                        organizationReferences.Entities.Add(organizationReference);
                    }

                    foreach (Entity organizationReference in organizationReferences.Entities)
                    {
                        string referenceType = organizationReference.GetAttributeValue<string>("referenceType");
                        string referenceValue = organizationReference.GetAttributeValue<string>("referenceValue");

                        int refType = AccountServicesHelper.getAttributeOptionValue("ts_accountreference", "ts_referencetype", referenceType
                                                                                                                                        , service, tracingService);

                        if (refType != 0)
                        {
                            AccountServicesHelper.addAccountReference(tsOrgId, accountId, refType, referenceValue
                                                                                                                , service, tracingService);
                        }
                        else
                        {
                            string error = $"Invalid referenceType '{referenceType}' in organizationReferences";
                            AccountServicesHelper.writeToTrace(error
                                                                 , tracingService);
                            errorStack.Add(error);
                            //return null;
                        }
                    }
                }


                if (tsRequest.Contains("legalIdentifiers"))
                {
                    EntityCollection legalIdentifiers = tsRequest.GetAttributeValue<EntityCollection>("legalIdentifiers");

                    int counter = 0;
                    foreach (Entity legalIdentifier in legalIdentifiers.Entities)
                    {
                        counter++;
                        if (counter > 1)
                        {
                            string legalIdentifierType = legalIdentifier.GetAttributeValue<string>("type");
                            string legalIdentifierValue = legalIdentifier.GetAttributeValue<string>("identifier");

                            string legalIdentifierOptionLabel = $"Legal Identifier {counter.ToString()}";
                            int refType = AccountServicesHelper.getAttributeOptionValue("ts_accountreference", "ts_referencetype", legalIdentifierOptionLabel
                                                                                                                                                            , service, tracingService);

                            if (refType != 0)
                                AccountServicesHelper.addAccountReference(tsOrgId, accountId, refType, $"{legalIdentifierType} : {legalIdentifierValue}"
                                                                                                                                                    , service, tracingService);

                        }
                    }
                }



            }
            catch (Exception e)
            {
                if (e.Message.StartsWith("Another account already exists"))
                    tsResult.Attributes["duplicateOrg"] = true;

                string error = $"Error in createOrg(). Exception message:{Environment.NewLine}{e.Message}"
                                ;

                AccountServicesHelper.writeToTrace(error
                                                        , tracingService);
                errorStack.Add(error);
            }
            return newAccount;
        }

        public static void addConnectionToContact(Guid accountId, string tsOrgId, string tsContactId, string contactTypeFrom, string contactTypeTo
                                                                                                                                , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService, IOrganizationServiceFactory serviceFactory)
        {
            Guid connectionId = Guid.Empty;

            try
            {

                Entity connectionEntity = null;
                bool connectionExists = false;

                QueryExpression queryContact = new QueryExpression("contact");
                queryContact.Criteria.AddCondition("new_contactaccountnumber", ConditionOperator.Equal, tsContactId);
                EntityCollection contactCollection = service.RetrieveMultiple(queryContact);
                Guid contactId = Guid.Empty;
                if (contactCollection.Entities.Count > 0)
                {
                    contactId = contactCollection.Entities.First().Id;

                    QueryExpression queryConnection = new QueryExpression("connection");
                    queryConnection.ColumnSet = new ColumnSet(true);
                    FilterExpression filterConnection = new FilterExpression(LogicalOperator.And);
                    filterConnection.AddCondition("record1id", ConditionOperator.Equal, accountId);
                    filterConnection.AddCondition("record2id", ConditionOperator.Equal, contactId);
                    queryConnection.Criteria.AddFilter(filterConnection);
                    EntityCollection connectionCollection = service.RetrieveMultiple(queryConnection);

                    if (connectionCollection.Entities.Count > 0)
                    {
                        connectionExists = true;
                        connectionEntity = connectionCollection.Entities.First();
                        connectionId = connectionCollection.Entities.First().Id;
                    }
                    else
                    {
                        connectionEntity = new Entity("connection");
                    }
                }
                else
                {
                    Guid dynamicsUser = AccountServicesHelper.getUserIdByFullName("# TSDynamicsOnyx"
                                                                                                    , service, tracingService, errorStack);

                    AccountServicesHelper.writeToTrace($"Impersonating user: # TSDynamicsOnyx: {dynamicsUser.ToString()}"
                                                                                                                    , tracingService);


                    service = serviceFactory.CreateOrganizationService(dynamicsUser);
                    contactId = AccountServicesHelper.createContact(tsContactId, EnvVariables
                                                                                            , service, tracingService, errorStack);
                    service = serviceFactory.CreateOrganizationService(null);


                    if (contactId == Guid.Empty)
                    {
                        string error = $"No Contact record found for tsContactId: {tsContactId}";
                        AccountServicesHelper.writeToTrace($"At addConnectionToContact(). {error}"
                                                                                                , tracingService);
                        errorStack.Add(error);
                        return;
                    }

                    connectionEntity = new Entity("connection");
                }



                QueryExpression queryConnectionRole = new QueryExpression("connectionrole");
                queryConnectionRole.Criteria.AddCondition("name", ConditionOperator.Equal, contactTypeTo);
                EntityCollection connectionRoleCollection = service.RetrieveMultiple(queryConnectionRole);
                Guid connectionRoleToId = Guid.Empty;
                if (connectionRoleCollection.Entities.Count > 0)
                {
                    connectionRoleToId = connectionRoleCollection.Entities.First().Id;
                }
                else
                {
                    connectionRoleToId = createConnectionRole(contactTypeFrom
                                                                        , service, tracingService);
                }



                QueryExpression queryConnectionFromRole = new QueryExpression("connectionrole");
                queryConnectionFromRole.Criteria.AddCondition("name", ConditionOperator.Equal, contactTypeFrom);
                EntityCollection connectionRoleFromCollection = service.RetrieveMultiple(queryConnectionFromRole);
                Guid connectionRoleFromId = Guid.Empty;
                if (connectionRoleFromCollection.Entities.Count > 0)
                {
                    connectionRoleFromId = connectionRoleFromCollection.Entities.First().Id;

                }
                else
                {
                    connectionRoleFromId = createConnectionRole(contactTypeFrom
                                                                            , service, tracingService);
                }

                associateConnectionRole(connectionRoleFromId, connectionRoleToId
                    , service, tracingService);
                connectionEntity["record1id"] = new EntityReference("account", accountId);
                connectionEntity["record1objecttypecode"] = new OptionSetValue(1);
                connectionEntity["record2id"] = new EntityReference("contact", contactId);
                connectionEntity["record2objecttypecode"] = new OptionSetValue(2);

                connectionEntity["record1roleid"] = new EntityReference("connectionrole", connectionRoleFromId);
                connectionEntity["record2roleid"] = new EntityReference("connectionrole", connectionRoleToId);

                if (connectionExists)
                {
                    int stateCode = connectionEntity.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 1;
                    if (stateCode == 1) //stateCode = 1: inactive
                    {
                        connectionEntity["statecode"] = new OptionSetValue(0);
                        connectionEntity["statuscode"] = new OptionSetValue(1);
                    }

                    service.Update(connectionEntity);
                }
                else
                {
                    connectionId = service.Create(connectionEntity);
                }
                    
            }
            catch (Exception e)
            {
                string error = $"Error in addConnectionToContact(). Exception message:{Environment.NewLine}{e.Message}"
                                ;
                AccountServicesHelper.writeToTrace(error
                                                        , tracingService);
                errorStack.Add(error);
            }
        }

        public static Guid createConnectionRole(string name
                                                            , IOrganizationService service, ITracingService tracingService)
        {
            Guid connectionRoleId = Guid.Empty;

            try
            {
                Entity connectionRole = new Entity("connectionrole");


                connectionRole["name"] = name;
                connectionRole["category"] = new OptionSetValue(1);

                CreateRequest request = new CreateRequest();
                request.Target = connectionRole;
                request.Parameters.Add("SolutionUniqueName", "DataverseArchitectureModifications");

                CreateResponse response = (CreateResponse)service.Execute(request);

                connectionRoleId = response.id;
            }
            catch (Exception e)
            {
                string error = $"Error in createConnectionRole(). Exception message:{Environment.NewLine}{e.Message}"
                                    ;                
                AccountServicesHelper.writeToTrace(error
                                                        , tracingService);
                errorStack.Add(error);
            }

            return connectionRoleId;
        }
        public static void associateConnectionRole(Guid connectionRoleFromId, Guid connectionRoleToId
                                                                                            , IOrganizationService service, ITracingService tracingService)
        {

            try
            {
                AssociateRequest associateConnectionRoles = new AssociateRequest
                {
                    Target = new EntityReference("connectionrole", connectionRoleFromId),
                    RelatedEntities = new EntityReferenceCollection()
                        {
                            new EntityReference("connectionrole", connectionRoleToId)
                        },
                    Relationship = new Relationship()
                    {
                        PrimaryEntityRole = EntityRole.Referenced,//EntityRole.Referencing, // Referencing or Referenced based on N:1 or 1:N reflexive relationship.
                        SchemaName = "connectionroleassociation_association"
                    }
                };
                service.Execute(associateConnectionRoles);
            }
            catch (Exception e)
            {
                string error = $"Error in associateConnectionRole(). Exception message:{Environment.NewLine}{e.Message}"
                                    ;                
                AccountServicesHelper.writeToTrace(error
                                                        , tracingService);
                errorStack.Add(error);
            }

        }


        public static string getErrorsFromStack()
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
    }
}