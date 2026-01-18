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
using System.Drawing;
using System.Threading;
using DynamicsProcesses.Properties;
using System.IdentityModel;
using System.Security.Policy;
using System.IdentityModel.Metadata;
using System.Dynamic;
using System.Net.NetworkInformation;
using System.Web.UI.WebControls;
using System.Web.Util;
using System.Security.Cryptography;
using Azure.Data.Tables;

namespace DynamicsProcesses
{
    internal class ValidationServicesUSNonC3
    {

        public static void determineProcessBehavior(Entity caseEntity, Entity validationRequestor, string validationReqTransactionId, string queueName, Entity valReqOrgEntity, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region Disposition
                bool caseHasDisposition = DynamicsProcessesHelper.existsSystemNote(" --- Disposition Details --- ", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                if (!caseHasDisposition)
                {
                    
                    bool okToProcess = DynamicsProcessesValidationServices.getProcessingApproval(caseEntity, validationRequestor, queueName);

                    if (okToProcess)
                    {
                        ValidationServicesUSNonC3.getValidationScoreMatrix(caseEntity, validationRequestor, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest);
                        caseHasDisposition = DynamicsProcessesHelper.existsSystemNote(" --- Disposition Details --- ", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                    }
                }
                #endregion


                #region Determine Action
                if (caseHasDisposition)
                    ValidationServicesUSNonC3.determineAction(caseEntity, validationRequestor, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest);
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in determineProcessBehavior(). Exception message: " + Environment.NewLine + e.Message);
            }
            #endregion
        }
        public static void determineAction(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, Entity valReqOrgEntity, IDictionary<string, Object> dispositionRequest)
        {
            try
            {

                #region AutomatedValDefinition
                string postAutoValidationQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.config.postAutoValidationQueue == null ? "AutoValidation Inconclusive"
                                                                                                                                                    : DynamicsProcessesValidationServices.AutomatedValDefinition.config.postAutoValidationQueue;
                string postAutoValidationQueueHighPriority = DynamicsProcessesValidationServices.AutomatedValDefinition.config.postAutoValidationQueueHighPriority;
                string outreachQueueName = DynamicsProcessesValidationServices.AutomatedValDefinition.emailOutreachProcess.queueName;
                string outreachQueueHighPriority = DynamicsProcessesValidationServices.AutomatedValDefinition.emailOutreachProcess.queueNameHighPriority;

                string validationServicesInitialQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.initialQueue;

                string postAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postAutoCloseQueue;
                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;


                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        ((string)dispositionRequest["AddressCountryId"])?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();

                postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;

                                                                                      ;

                #endregion


                #region Parameters
                IDictionary<string, Object> valReqOrgAccountObj = ValidationServicesHelper.getValidationRequestOrgAccountInfo(validationReqTransactionId);
                #endregion


                #region InitialQueue
                if (queueName == validationServicesInitialQueue)
                {
                   

                    #region Catch Val Requests That Already Have Org Account
                    if (valReqOrgAccountObj["ctpOrgId"] != null)
                        hasOrgAccount_RouteOut(validationRequestCase, account, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest, valReqOrgAccountObj);
                    #endregion


                    #region Dupe Check
                    bool? existsAccount = ValidationServicesHelper.findExistingAccount(validationRequestCase, account, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest);
                    if (existsAccount == null)
                    {
                        validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                        DynamicsInterface.DataverseClient.Update(validationRequestCase);
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);
                        return;
                    }

                    if (existsAccount.Value)
                        return;

                    bool? matchesFound = ValidationServicesHelper.findValidationRequestAccountMatches(validationRequestCase, account, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest);

                    matchesFound = matchesFound ?? false;
                    if (matchesFound.Value)
                        return;
                    #endregion


                    #region Fraud Check
                    bool? potentialFraud = ValidationServicesHelper.evaluateForFraud(validationRequestCase, account, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest);

                    if (potentialFraud == null || potentialFraud.Value)
                        return;
                    #endregion


                    #region AutoClose
                    bool isAgentValid = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesagentvalid");
                    bool isTrustWorthy = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                    bool isActivityCodeValid = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesactivitycodevalid");


                    bool isAgentValidDisposition = validationRequestCase.GetAttributeValue<bool>("ts_validationagentdisposition");
                    bool isOrgValidDisposition = validationRequestCase.GetAttributeValue<bool>("ts_validationorgdisposition");

                    EntityReference validationDispositionActivityCodeRef = validationRequestCase.GetAttributeValue<EntityReference>("ts_validationdispositionactivitycode");

                    if (
                        (isOrgValidDisposition && isAgentValidDisposition && isTrustWorthy && isActivityCodeValid)

                        || (isOrgValidDisposition && isAgentValidDisposition && isTrustWorthy && !isActivityCodeValid && validationDispositionActivityCodeRef != null)

                        )
                    {
                        initiateOrgIncorporation(validationRequestCase, account, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest);
                        return;
                    }
                    #endregion


                    #region Routing & EmailOutreach
                    DynamicsProcessesValidationServices.applyRoutingRules(validationRequestCase, account, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest);
                    #endregion


                    #region If No ActionOrRouting Resolved
                    //validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                    //DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    //DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue)
                    #endregion

                    return;
                }
                #endregion

            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in determineAction(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
            }
            #endregion
        }




        public static void hasOrgAccount_RouteOut(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, Entity valReqOrgEntity, IDictionary<string, Object> dispositionRequest
                                                                                                                                                                , IDictionary<string, Object> valReqOrgAccountObj)
        {
            #region AutomatedValDefinition
            string postAutoValidationQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.config.postAutoValidationQueue == null ? "AutoValidation Inconclusive"
                                                                                                                                                : DynamicsProcessesValidationServices.AutomatedValDefinition.config.postAutoValidationQueue;
            string postAutoValidationQueueHighPriority = DynamicsProcessesValidationServices.AutomatedValDefinition.config.postAutoValidationQueueHighPriority;
            string outreachQueueName = DynamicsProcessesValidationServices.AutomatedValDefinition.emailOutreachProcess.queueName;
            string outreachQueueHighPriority = DynamicsProcessesValidationServices.AutomatedValDefinition.emailOutreachProcess.queueNameHighPriority;

            string validationServicesInitialQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.initialQueue;

            string postAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postAutoCloseQueue;
            string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;


            dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                   Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                   ((string)dispositionRequest["AddressCountryId"])?.ToLower()?.Replace("gb", "uk")
                                                                                                   )?.FirstOrDefault();


            postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;
            #endregion


            #region Temporarily Just Sending To PostNoAutoCloseQueue
            validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
            DynamicsInterface.DataverseClient.Update(validationRequestCase);
            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);
            #endregion
        }


        public static void initiateOrgIncorporation(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, Entity valReqOrgEntity, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region AutomatedValDefinition & Parameters
                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        ((string)dispositionRequest["AddressCountryId"])?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();


                string postAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postAutoCloseQueue;

                postAutoCloseQueue = countryAutomatedValDefinition?.postAutoCloseQueue ?? postAutoCloseQueue;

                #endregion


                #region Create Org Account
                Guid? qualCaseId = validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid")?.Id;
                Entity qualCase = qualCaseId == null ? null : DynamicsInterface.DataverseClient.Retrieve("incident", qualCaseId.Value, new ColumnSet(true));
                Guid? orgAccountId = qualCase?.GetAttributeValue<EntityReference>("customerid")?.Id;
                Entity orgAccount = orgAccountId == null ? null : DynamicsInterface.DataverseClient.Retrieve("account", orgAccountId.Value, new ColumnSet(true));

                if (orgAccount == null)
                {
                    orgAccount = ValidationServicesUSNonC3.createOrgFromCase(account, validationRequestCase, validationReqTransactionId, dispositionRequest);

                }
                #endregion


                #region CreateQualCase & Contact & Connection
                if (qualCase == null)
                {
                    qualCase = ValidationServicesHelper.processQualCaseFromValidationRequest(orgAccount, validationRequestCase);

                    validationRequestCase["parentcaseid"] = new EntityReference(qualCase.LogicalName, qualCase.Id);
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                }


                OptionSetValue agentVerificationStatusOption = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_validationrequestagentverification");

                Entity agentContact = ValidationServicesHelper.createAgentContact(validationRequestCase, account, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest);

                if (agentContact != null)
                    ValidationServicesHelper.connectAgentToAccount(orgAccount.Id, agentContact.Id, agentVerificationStatusOption, validationReqTransactionId);


                if (orgAccount == null || agentContact == null)
                {
                    string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;
                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);

                    return;
                }
                #endregion


                #region CTPOrgId Exists Check
                string ctpOrgId = orgAccount.GetAttributeValue<string>("ts_ctporgid");
                if (string.IsNullOrEmpty(ctpOrgId))
                {
                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104701); //104701 - OQ - Validated - Awaiting CTPOrgId Provisioning
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    return;
                }
                #endregion


                #region Qualify
                bool success = ValidationServicesHelper.updateValidationRequestCaseStatus(validationRequestCase, 102056, validationReqTransactionId); //102056 - OQ - Qualified

                if (success)
                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseQueue);
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in initiateOrgIncorporation(...). Exception message: " + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                        );
            }
            #endregion

        }


        public static Entity createOrgFromCase(Entity requestingAccount, Entity caseEntity, string validationReqTransactionId, IDictionary<string, Object> dispositionRequest)
        {
            #region Parameters
            Guid accountId = Guid.Empty;
            string tsOrgId = string.Empty;
            Entity newAccount = null;
            #endregion

            try
            {
                #region New Account Entity
                Entity account = new Entity("account");
                #endregion


                #region PngoId & OrgRefId
                //string orgRefId = DynamicsProcessesHelper.regexMatchValue(@"(?<=\w+_)(\w+)(?=_\w+)", validationReqTransactionId, 0);

                //account["ts_pporgid"] = orgRefId;
                //account["ts_orgppid"] = new EntityReference("account", requestingAccount.Id);
                #endregion


                #region Name, Org Designation, Mission Statement...
                account["name"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.LegalName)];
                account["customertypecode"] = new OptionSetValue(3); //Customer
                account["new_source"] = new OptionSetValue(101892); //TSS Web Site 101892

                EntityReference qualCodeRef = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid");
                account["new_orgdesignation"] = qualCodeRef;

                if (
                   !ValidationServicesHelper.hasNullEmptyValues(dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.MissionStatement)].ToString())
                   )
                    account["ts_missionstatement"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.MissionStatement)].ToString();

                if (
                     !ValidationServicesHelper.hasNullEmptyValues(dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.Phone)].ToString())
                     )
                    account["telephone1"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.Phone)];
                #endregion


                #region Address
                account["address1_country"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressCountryId)];

                account["address1_stateorprovince"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressStateRegion)];

                account["address1_line1"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressLine1)];

                if (!ValidationServicesHelper.hasNullEmptyValues(
                                        dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressOther)].ToString()
                                        )
                       )
                    account["address1_line2"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressOther)];

                account["address1_city"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressCity)];
                account["address1_postalcode"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressPostalCode)];


                #region Country And State Hierarchy Mapping
                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressCountryId)]);
                EntityCollection fieldMapCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    account["ts_countrydesc"] = new OptionSetValue(countryOptionValue);
                }


                queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressCountryId)]);
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.AddressStateRegion)]);
                fieldMapCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    account["ts_stateprovdesc"] = new OptionSetValue(countryOptionValue);
                }
                #endregion
                #endregion


                #region Email, Url, Budget, Legal Identifier & Activity Code
                if (
                     !ValidationServicesHelper.hasNullEmptyValues(dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.Email)].ToString())
                     )
                    account["emailaddress1"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.Email)];

                if (!ValidationServicesHelper.hasNullEmptyValues(
                                        dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.Website)].ToString()
                                        )
                    )
                    account["websiteurl"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.Website)].ToString();

                if (!ValidationServicesHelper.hasNullEmptyValues(
                                        dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.OperatingBudget)].ToString()
                                        )
                    )
                    account["new_budget"] = dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.OperatingBudget)].ToString();




                List<IDictionary<string, Object>> registrationIdentifiers = JsonConvert.DeserializeObject<List<ExpandoObject>>(
                                                                                                                                 JsonConvert.SerializeObject(dispositionRequest["RegistrationIdentifiers"])
                                                                                                                                 ).ToList<IDictionary<string, Object>>();



                if (registrationIdentifiers != null && registrationIdentifiers.Count() > 0
                    && !ValidationServicesHelper.hasNullEmptyValues(registrationIdentifiers.First()[nameof(ValidationServicesHelper.RegIdentifierClass.LegalIdentifier)].ToString())
                    )
                {

                    account["new_legalidentifier"] = registrationIdentifiers.First()[nameof(ValidationServicesHelper.RegIdentifierClass.LegalIdentifier)].ToString();
                }
                else
                {
                    //"error"
                }


                if (!ValidationServicesHelper.hasNullEmptyValues(
                                        dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.ActivityCode)].ToString()
                                        )
                    )
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, dispositionRequest[nameof(ValidationServicesHelper.DispositionClass.ActivityCode)].ToString());
                    EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                    if (entityCollection.Entities.Count > 0)
                        account["new_activitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);
                }
                #endregion


                #region Account Create & Retrieve
                accountId = DynamicsInterface.DataverseClient.Create(account);

                if (accountId == Guid.Empty)
                {
                    string error = "Error in createOrgFromCase(). Account was not created";

                    DynamicsInterface.errorStack.Add(error);
                    return null;
                }

                newAccount = DynamicsInterface.DataverseClient.Retrieve(account.LogicalName, accountId, new ColumnSet(true));
                tsOrgId = newAccount.GetAttributeValue<string>("accountnumber");
                #endregion


                #region Extra - Commented Out
                //if (tsRequest.Contains("associations"))
                //{
                //    EntityCollection associations = tsRequest.GetAttributeValue<EntityCollection>("associations");

                //    foreach (Entity association in associations.Entities)
                //    {
                //        string tsContactId = association.GetAttributeValue<string>("contactTSID");
                //        string tsContactType = association.GetAttributeValue<string>("contactTypeTSID");

                //        queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                //        queryFieldMap.ColumnSet = new ColumnSet(true);
                //        queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "tsContactType");
                //        queryFieldMap.Criteria.AddCondition("ts_valuecode", ConditionOperator.Equal, int.Parse(tsContactType));
                //        fieldMapCollection = service.RetrieveMultiple(queryFieldMap);

                //        if (fieldMapCollection.Entities.Count > 0)
                //        {
                //            Entity fieldHierarchy = fieldMapCollection.Entities.First();
                //            string contactTypeTo = fieldHierarchy.GetAttributeValue<string>("ts_value");

                //            addConnectionToContact(accountId, tsOrgId, tsContactId, "Employer", contactTypeTo
                //                , context, service, tracingService);
                //        }
                //    }
                //}



                if (registrationIdentifiers.Count() > 1)
                {

                    int counter = 0;
                    foreach (IDictionary<string, Object> registrationIdentifier in registrationIdentifiers)
                    {
                        counter++;
                        if (counter > 1)
                        {
                            //string legalIdentifierType = legalIdentifier.GetAttributeValue<string>("type");
                            //string legalIdentifierValue = legalIdentifier.GetAttributeValue<string>("identifier");

                            //string legalIdentifierOptionLabel = "Legal Identifier " + counter.ToString();
                            //int refType = AccountServicesHelper.getAttributeOptionValue("ts_accountreference", "ts_referencetype", legalIdentifierOptionLabel
                            //    , service, tracingService);

                            //if (refType != 0)
                            //    AccountServicesHelper.addAccountReference(tsOrgId, accountId, refType, legalIdentifierType + ":" + legalIdentifierValue
                            //        , service, tracingService);

                        }
                    }
                }
                #endregion

            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in createOrgFromCase(string validationReqTransactionId). Exception message: " + Environment.NewLine + e.Message
                                                  + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                  );


                DynamicsProcessesHelper.processSystemNote(" --- Error Creating Org --- ", "Error in createOrgFromCase(string validationReqTransactionId). Exception message: " + Environment.NewLine + e.Message
                                                                , new EntityReference(caseEntity.LogicalName, caseEntity.Id));


            }
            #endregion

            return newAccount;
        }



        public static void getValidationScoreMatrix(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName, Entity valReqOrgEntity, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region Calling Score Matrix API
                string CTPUrl = "https://tsvc.tsgctp.org/";
                string CTPSessionKey = "61695af7-1652-4b08-b786-192de1884f61";
                string endPointPath = "services/vsscorematrix/v_001/";

                Dictionary<string, string> queryParams = new Dictionary<string, string>();
                queryParams.Add("transaction_id", validationReqTransactionId);


                dynamic dispositionResponse = ValidationServicesHelper.makeHttpGetCall(
                                                                                        CTPUrl, endPointPath
                                                                                        , CTPSessionKey, queryParams
                                                                                        );



                string dispositionResponseText = JsonConvert.SerializeObject(dispositionResponse);
                dynamic automatedValSettings = ValidationServicesHelper.getAutomatedValidationConfig();

                dynamic automatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);
                #endregion









                #region Get Disposition Data & Disposition Transaction Id
                dynamic dispositionData = dispositionResponse.returnStatus.data;


                string dispositionDataText = JsonConvert.SerializeObject(dispositionData);
                caseEntity["ts_validationdispositiondata"] = dispositionDataText;


                string dispositionTransactionId = dispositionData.transaction_id;
                if (validationReqTransactionId != dispositionTransactionId)
                    return;
                #endregion












                #region  Disposition Details
                var scoreMatrixObj = JsonConvert.DeserializeObject<System.Dynamic.ExpandoObject>(dispositionResponseText) as IDictionary<string, Object>;
                var dispositionScores = (IDictionary<string, Object>)((IDictionary<string, Object>)scoreMatrixObj["returnStatus"])["data"];









                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;


                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        (
                                                                                                                                       dispositionRequest["AddressCountryId"]?.ToString() ?? ""
                                                                                                                                       )?
                                                                                                                                       .ToLower()?.Replace("gb", "uk")
                                                                                                                       )?.FirstOrDefault();

                postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;





                string dispositionStatus = dispositionData.score_matrix_status == null ? "" : dispositionData.score_matrix_status;
                caseEntity["ts_validationdispositionstatus"] = dispositionStatus;

                string noteDesc = "";
                string noteTitle = "";
                if (dispositionStatus != "completed")
                {
                    int checkCountsForManagedQueue = automatedValDefinition.config.checkCountsForManagedQueue;

                    caseEntity["ts_validationrequestlaststatuscheck"] = DateTime.UtcNow;
                    int dispositionRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");
                    dispositionRequestChecksCount++;
                    caseEntity["ts_validationstatuscheckscount"] = dispositionRequestChecksCount;

                    if (dispositionRequestChecksCount >= checkCountsForManagedQueue)
                    {
                        caseEntity["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                        //DynamicsInterface.DataverseClient.Update(caseEntity);
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, postNoAutoCloseQueue);

                        ValidationServicesHelper.processSystemNote("No Disposition Received", "There was no validation resolution after the conclusion of the time allotted for this process", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                    }
                    DynamicsInterface.DataverseClient.Update(caseEntity);
                    return;
                }





                string scoreItems = "";
                foreach (var dispositionScore in dispositionScores)
                {
                    string elementType = dispositionScore.Value.GetType().Name;

                    string value = dispositionScore.Value.ToString();
                    if (elementType.StartsWith("List") || elementType.ToLower().Contains("object"))
                        value = JsonConvert.SerializeObject(dispositionScore.Value);

                    scoreItems += dispositionScore.Key + " : " + value;

                    scoreItems += Environment.NewLine + string.Concat(Enumerable.Repeat("-", 30).ToArray()) + Environment.NewLine;
                }
                #endregion













                #region Org, Agent, Trustworthy, Activity Code, Legal Equivalence Dispositions
                string orgDisposition = ValidationServicesHelper.regexMatchValue("\\w+(\\s\\w+)*", (string)dispositionData.org_disposition, 0);
                float orgDispScore = dispositionData.org_disposition_confidence;

                string agentDisposition = ValidationServicesHelper.regexMatchValue("\\w+(\\s\\w+)*", (string)dispositionData.agent_disposition, 0);
                float agentDispScore = dispositionData.agent_disposition_confidence;

                string trustworthyDisposition = ValidationServicesHelper.regexMatchValue("\\w+(\\s\\w+)*", (string)dispositionData.risk_disposition, 0);
                float trustworthyConfidence = dispositionData.risk_disposition_confidence;

                string activityCodeFinal = dispositionData.act_code_final;

                string legalEquivalenceDisposition = ValidationServicesHelper.regexMatchValue("\\w+(\\s\\w+)*", (string)dispositionData.led_disposition, 0);

                string legalEquivalenceDispositionLower = legalEquivalenceDisposition == null ? "" : legalEquivalenceDisposition.ToLower();


                string rulesEvaluatedAction = dispositionData.rules_evaluated_action;
                #endregion










                #region IRS
                string externalDBName = dispositionData.ext_db_name;

                bool isIRSPresent = false;
                if (externalDBName != null && externalDBName.ToLower().Contains("irs"))
                    isIRSPresent = true;


                #region Org Name Matching
                string dispositionOrgName = dispositionData.org_name; 
                caseEntity["ts_validationdispositionorgname"] = dispositionOrgName;

                string validationRequestLegalName = caseEntity.GetAttributeValue<string>("ts_validationrequestlegalname");

                string IRSOrgName = dispositionData.ext_db_org_data.NAME;
                caseEntity["ts_validationdispositionirsorgname"] = IRSOrgName;

                IRSOrgName = IRSOrgName == null ? "" : IRSOrgName;


                float levenshteinDistance = Fastenshtein.Levenshtein.Distance(validationRequestLegalName.ToLower(), IRSOrgName.ToLower());
                float topLength = Math.Max(validationRequestLegalName.Length, IRSOrgName.Length);

                float levenshteinIndex = (topLength - levenshteinDistance) / topLength;

                bool orgNamesMatch = levenshteinIndex >= 0.60 ? true : false;
                #endregion


                #region SubSection Matching
                string IRSSubSection = dispositionData.ext_db_org_data.SUBSECTION;
                IRSSubSection = IRSSubSection.PadLeft(2, '0');

                IRSSubSection = IRSSubSection == "00" ? "" : IRSSubSection;

                Entity qualCodeEntity = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid") == null ? null :
                                                            DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid").Id, new ColumnSet(true));

                string validationRequestNonC3OrgDesig = qualCodeEntity?.GetAttributeValue<string>("new_qualcode");

                string valReqNonC3SubSection = validationRequestNonC3OrgDesig.Substring(validationRequestNonC3OrgDesig.Length - 2, 2);
                valReqNonC3SubSection = DynamicsProcessesHelper.regexMatch(@"^\d+$", valReqNonC3SubSection) ? valReqNonC3SubSection :
                                                                                                    validationRequestNonC3OrgDesig.Substring(validationRequestNonC3OrgDesig.Length - 1, 1).PadLeft(2, '0');


                caseEntity["ts_validationdispositionirssubsection"] = IRSSubSection;
                bool valReqIrsSubSectionMatch = IRSSubSection == valReqNonC3SubSection ? true : false;
                #endregion


                #region Legal Identifier Matching
                string IRSOrgEIN = dispositionData.ext_db_org_data.EIN;
                string legalIdentifier = caseEntity.GetAttributeValue<string>("ts_validationrequestlegalidentifier");

                legalIdentifier = legalIdentifier == null ? "" : legalIdentifier;
                bool legalIdentifiersMatch = IRSOrgEIN == legalIdentifier.Replace("-", "") ? true : false;
                #endregion

                #region IRS Revocation Check
                string IRSRevocationCode = dispositionData.ext_db_org_data.REV_CD;

                bool isOnIRSRevokeList = IRSRevocationCode == null || IRSRevocationCode.ToLower() == "na" ? false : true;

                caseEntity["ts_validationdispositiononirsrevokelist"] = isOnIRSRevokeList;
                #endregion

                #endregion





                #region Org Rules
                bool isOrgValid = false;
                if (isIRSPresent && orgNamesMatch &&
                    valReqIrsSubSectionMatch && 
                    legalIdentifiersMatch && !isOnIRSRevokeList)
                    isOrgValid = true;

                caseEntity["ts_validationdispositionrulesorgvalid"] = isOrgValid;


                caseEntity["ts_validationorgdisposition"] = orgDisposition.ToLower() == "is" ? true : false;
                caseEntity["ts_orgvalidationdispositionscore"] = orgDispScore.ToString();
                #endregion








                #region Agent Details
                string agentName = caseEntity.GetAttributeValue<string>("ts_validationagentname");

                float levenshteinDistOrgAgent = Fastenshtein.Levenshtein.Distance(validationRequestLegalName.ToLower(), agentName.ToLower());
                float topLengthOrgAgent = Math.Max(validationRequestLegalName.Length, agentName.Length);
                float levenshteinOrgAgentIndex = (topLengthOrgAgent - levenshteinDistOrgAgent) / topLengthOrgAgent;

                bool isAgentNameValid = levenshteinOrgAgentIndex < 0.60 ? true : false;


                List<dynamic> ctpOrgDataList = ((JArray)dispositionData.ctp_db_match_set).ToList<dynamic>();


                dynamic orgData = null;

                if (ctpOrgDataList.Count > 0)
                    orgData = ctpOrgDataList.First();


                string dispositionOrgWebsite = orgData == null ? "" : (orgData.org_website == null ? "" : orgData.org_website);

                caseEntity["ts_validationdispositionorgwebsite"] = dispositionOrgWebsite;


                string validationRequestWebsite = caseEntity.GetAttributeValue<string>("ts_validationrequestwebsite");

                string validationRequestAgentEmail = caseEntity.GetAttributeValue<string>("ts_validationrequestagentemail");

                string valReqAgentEmailDomain = ValidationServicesHelper.regexMatchValue("(?<=@)(.+)", validationRequestAgentEmail, 0);

                bool agentOrgCommonDomain = ValidationServicesHelper.regexMatch(valReqAgentEmailDomain, validationRequestWebsite);
                #endregion











                /**Agent Rules**/


                caseEntity["ts_validationdispositionrulesagentvalid"] = agentDisposition.ToLower() == "is" ? true : false;
                if (isAgentNameValid && agentOrgCommonDomain)
                    caseEntity["ts_validationdispositionrulesagentvalid"] = true;


                caseEntity["ts_validationagentdisposition"] = agentDisposition.ToLower() == "is" ? true : false;
                caseEntity["ts_agentvalidationdispositionscore"] = agentDispScore.ToString();


                //initial value for Agent Verification
                caseEntity["ts_validationrequestagentverification"] = caseEntity.GetAttributeValue<bool>("ts_validationagentdisposition") ? new OptionSetValue(1)
                                                                                                                                        : new OptionSetValue(0);






                /**Trustworthy**/


                caseEntity["ts_validationdispositiontrustworthy"] = trustworthyDisposition.ToLower() == "is" ? true : false;
                caseEntity["ts_validationdispositiontrustworthyconfidence"] = trustworthyConfidence.ToString();







                /**Legal Equivalency**/


                caseEntity["ts_validationlegalequivalencedisposition"] = legalEquivalenceDispositionLower == "does" ? true : false;











                /**Activity Code Rules**/

                string selfReportedActivityCode = caseEntity.GetAttributeValue<string>("ts_validationselfreportedactivitycode");

                string IRSNteeCode = dispositionData.ext_db_org_data.NTEE_CD == null ? "" : dispositionData.ext_db_org_data.NTEE_CD;

                caseEntity["ts_validationdispositionirsnteecode"] = IRSNteeCode;

                List<dynamic> activityCodes = ((JArray)dispositionData.act_codes).ToList<dynamic>();


                string dispositionSystemNteeCodesCsv = "";
                string dispositionSystemActivityCodesCsv = "";
                if (activityCodes.Count() > 0)
                {
                    string[] dispositionSystemNteeCodes = activityCodes.ToList().Select(item => (string)item.ntee).Distinct().ToArray();
                    dispositionSystemNteeCodesCsv = string.Join(",", dispositionSystemNteeCodes);


                    string[] dispositionSystemActivityCodes = activityCodes.ToList().Select(item => (string)item.act_sub).Distinct().ToArray();
                    dispositionSystemActivityCodesCsv = string.Join(",", dispositionSystemActivityCodes);
                }

                caseEntity["ts_validationdispositionsystemreportednteecodes"] = dispositionSystemNteeCodesCsv;
                caseEntity["ts_validationdispositionsystemactivitycodes"] = dispositionSystemActivityCodesCsv;




                dynamic IRSNteeActivityCodeObj = activityCodes.Find(item => item.ntee == IRSNteeCode);
                string IRSMappedActivityCode = IRSNteeActivityCodeObj == null ? "" : IRSNteeActivityCodeObj.act_sub;

                var nonIRSActivityCodesQuery = activityCodes.Where(item => item.ntee != IRSNteeCode);

                string nonIRSActivityNteesCsv = "";
                string nonIRSActivityCodesCsv = "";
                if (nonIRSActivityCodesQuery.Count() > 0)
                {
                    string[] nonIRSActivityNtees = nonIRSActivityCodesQuery.ToList().Select(item => (string)item.ntee).ToArray();
                    nonIRSActivityNteesCsv = string.Join(",", nonIRSActivityNtees);

                    string[] nonIRSActivityCodes = nonIRSActivityCodesQuery.ToList().Select(item => (string)item.act_sub).ToArray();
                    nonIRSActivityCodesCsv = string.Join(",", nonIRSActivityCodes);

                }


                string sensitiveListNtee = dispositionData.ntee_code_sensitive_list_value == null ? "" : dispositionData.ntee_code_sensitive_list_value;
                dynamic sensitiveListcAtivityCodeObj = sensitiveListNtee == "" ? null : activityCodes.Find(item => item.ntee == sensitiveListNtee);
                string sensitiveListActivityCode = sensitiveListcAtivityCodeObj == null ? "" : sensitiveListcAtivityCodeObj.act_sub;

                caseEntity["ts_validationsensitivitylistactivitycode"] = sensitiveListActivityCode;





                string[] whiteListedActivityCodes = { "030", "046", "205", "317", "318" };


                bool isActivityCodeWhiteListed = whiteListedActivityCodes.Contains(selfReportedActivityCode) ? true : false;

                bool isActivityCodeInInternalList = activityCodes.Exists(item => item.act_sub == IRSNteeCode); //IRS NTEE found in internal reference

                bool nteeSensitiveListFound = dispositionData.ntee_code_sensitive_list_found;

                if (
                    (isActivityCodeWhiteListed || isActivityCodeInInternalList || selfReportedActivityCode == activityCodeFinal)
                    && !nteeSensitiveListFound
                    )
                    caseEntity["ts_validationdispositionrulesactivitycodevalid"] = true;












                /***/


                caseEntity["ts_validationdispositionactivitycodematch"] = activityCodeFinal;


                if (!string.IsNullOrEmpty(activityCodeFinal) && activityCodeFinal != sensitiveListActivityCode)
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, activityCodeFinal);
                    EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                    if (entityCollection.Entities.Count > 0)
                        caseEntity["ts_validationdispositionactivitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);

                }









                /**Rules Evaluation**/
                List<dynamic> autoCloseCustomeRules = ((JArray)automatedValDefinition.config.autoCloseCustomRules).ToList<dynamic>();

                bool isAgentValid = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesagentvalid");
                bool isTrustWorthy = caseEntity.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                bool isActivityCodeValid = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesactivitycodevalid");


                bool isAgentValidDisposition = caseEntity.GetAttributeValue<bool>("ts_validationagentdisposition");
                bool isOrgValidDisposition = caseEntity.GetAttributeValue<bool>("ts_validationorgdisposition");



                caseEntity["ts_validationdispositionrulesautoclosequalify"] = false;

                caseEntity["ts_validationactionfromdispositionrules"] = "Manual - Further Evaluation Needed";

                if (isOrgValidDisposition && isAgentValidDisposition && isTrustWorthy && isActivityCodeValid)
                {
                    caseEntity["ts_validationdispositionrulesautoclosequalify"] = true;

                    caseEntity["ts_validationactionfromdispositionrules"] = "Qualify - AutoClose";

                    /*Setting "Validation Disposition Activity Code Final" back to null*/
                    caseEntity["ts_validationdispositionactivitycode"] = null;
                }
                else if (autoCloseCustomeRules.Exists(item => (string)item.rule == "ValidOrgAgentTrustACUpdate"))
                {

                    string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                    if (isOrgValidDisposition && isAgentValidDisposition && isTrustWorthy && !isActivityCodeValid && caseEntity.GetAttributeValue<EntityReference>("ts_validationdispositionactivitycode") != null)
                    {

                        caseEntity["ts_validationactionfromdispositionrules"] = "Change Activity Code To " + activityCodeFinal + " - Qualify - AutoClose";// - No Open Orders";
                    }

                }











                /***/


                rulesEvaluatedAction = rulesEvaluatedAction.ToLower();
                switch (rulesEvaluatedAction)
                {
                    case "manual":
                        rulesEvaluatedAction = "Manual - Further Evaluation Needed";
                        break;

                    case "autoclose":
                        rulesEvaluatedAction = "AutoClose - Disqualify";
                        if (caseEntity.GetAttributeValue<bool>("ts_validationorgdisposition"))
                            rulesEvaluatedAction = "AutoClose - Qualify";
                        break;

                    default:
                        rulesEvaluatedAction = string.IsNullOrEmpty(rulesEvaluatedAction) ? "" : Char.ToUpper(rulesEvaluatedAction[0]) + rulesEvaluatedAction.Substring(1);
                        break;
                }

                caseEntity["ts_validationdispositionaction"] = rulesEvaluatedAction;








                /***/

                DynamicsInterface.DataverseClient.Update(caseEntity);



                /***/


                string dispositionReference = dispositionData.reference_id == null ? "" : dispositionData.reference_id;
                string dispositionUrl = ValidationServicesHelper.regexMatchValue("https:.+?html", dispositionReference, 0);


                noteTitle = " --- Disposition Details --- ";

                noteDesc = "Full Disposition:" + Environment.NewLine + Environment.NewLine;
                noteDesc += dispositionUrl;
                noteDesc += Environment.NewLine + Environment.NewLine + "Score Matrix: ";
                noteDesc += Environment.NewLine + Environment.NewLine + scoreItems;

                ValidationServicesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));

                Entity validationRequestCase = ValidationServicesHelper.getCaseForTransactionId(validationReqTransactionId);

                ValidationServicesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getValidationScoreMatrix(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                    );
            }

        }

    }
}
