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
using static System.Windows.Forms.AxHost;
using System.Security.Policy;
using System.IdentityModel.Metadata;
using System.Dynamic;
using System.Net.NetworkInformation;
using System.Web.UI.WebControls;
using System.Web.Util;
using System.Security.Cryptography;
using Azure.Data.Tables;
using static DynamicsProcesses.NetworkValidationService;

namespace DynamicsProcesses
{
    internal class DynamicsProcessesValidationServices
    {

        public static Dictionary<string, string> EnvVariables;
        public static Guid OriginalCaller = Guid.Empty;

        public static IDictionary<string, Object> AutomatedValidationConfig;

        public static dynamic AutomatedValDefinition;

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

        public static string CTPUrl = ConfigurationManager.AppSettings["CTPUrl"];
        public static string CTPSessionKey = ConfigurationManager.AppSettings["CTPSessionKey"];

        public static string AzureStorage7C = ConfigurationManager.AppSettings["AzureStorage7C"];


        //"https://dev.tsgctp.org:45056/";
        //"https://tsvc.tsgctp.org/";
        public static async Task processValidationServices()
        {
            try
            {

                #region Initialization
                dynamic automatedValSettings = DynamicsProcessesAutomatedValidation.getAutomatedValidationConfig();

                if (!automatedValSettings.isAutomatedValidationActive)
                    return;

                //"AutoValidation Email Outreach"
                //"AutoValidation Inconclusive"
                string queueNameCsv = "ValidationServices";
                if (DynamicsInterface.Args.Length > 1)
                    queueNameCsv = DynamicsInterface.Args[1];


                string[] queueNamesParameters = queueNameCsv.Split(',');



                AutomatedValidationConfig  = JsonConvert.DeserializeObject<ExpandoObject>(automatedValSettings.automatedValConfigText) as IDictionary<string, Object>;

                AutomatedValDefinition  = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);


                EnvVariables = DynamicsProcessesHelper.GetEnvironmentVariables();

                if (DynamicsEnvironments.ContainsKey(DynamicsInterface.DynamicsEnvironment))
                {
                    string DynamicsEnvironmentCurrentName = DynamicsEnvironments[DynamicsInterface.DynamicsEnvironment];
                    DynamicsEnvironments["DynamicsEnvironmentCurrent"] = DynamicsEnvironmentCurrentName;
                }


                ParallelProcessesHelper.SemaphoreClient = ParallelProcessesHelper.getTableClientAsync(AzureStorage7C);
                #endregion


                #region Identifying Queues To Process
                List<string> queueNamesList = new List<string>();

                foreach(string queueParam in queueNamesParameters)
                {
                    string actualQueueName = queueParam;

                    switch (queueParam.ToLower())
                    {
                        case "emailoutreachresponded":
                            ValidationServicesUSNonC3.getCustomerHasRespondedEmailOutreach();
                            return;

                        default:
                            queueNamesList.Add(actualQueueName);
                            break;

                    }
                    
                }

                string[] queueNames = queueNamesList.ToArray();
                #endregion


                #region Getting Cases For The Queues In Question
                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.ColumnSet = new ColumnSet(true);
                queryEntity.Criteria.AddCondition("name", ConditionOperator.In, queueNames);
                EntityCollection entityCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryEntity);

                foreach (Entity queue in entityCollection.Entities)
                {
                    #region Processing Each Queue
                    Guid queueId = queue.Id;
                    string queueName = queue.GetAttributeValue<string>("name");

                    
                    QueryExpression queryQueueItem = new QueryExpression("queueitem");
                    queryQueueItem.ColumnSet = new ColumnSet(true);
                    queryQueueItem.Criteria.AddCondition("queueid", ConditionOperator.Equal, queueId);
                    queryQueueItem.AddOrder("enteredon", OrderType.Ascending);

                    
                    EntityCollection queueItemCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQueueItem);

                    DynamicsInterface.LogName += "_" + queueName.Replace(' ', '_');


                    DynamicsInterface.LogName += DynamicsInterface.Args.Length > 2 ? "_"  + DynamicsInterface.Args[2] : "";

                    #region Processing Queue Items
                    foreach (Entity queueItem in queueItemCollection.Entities)
                    {
                        DynamicsInterface.errorStack = new List<string>();

                        #region Check If Queue Item Is For Case
                        EntityReference queueItemObjRef = queueItem.GetAttributeValue<EntityReference>("objectid");
                        if (queueItemObjRef == null || queueItemObjRef.LogicalName != "incident")
                        {
                            DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                            continue;
                        }
                        #endregion

                        Guid caseId = queueItemObjRef.Id;

                        #region Get Semaphore
                        string resourceId = "CaseId_" + caseId.ToString() + "_" + DynamicsEnvironments["DynamicsEnvironmentCurrent"];
                        bool semaphoreAcquired = await ParallelProcessesHelper.tryAcquireSemaphoreAsync(resourceId, ParallelProcessesHelper.MaxConcurrentProcesses);
                        #endregion


                        #region Go To Next Queue Item If Semaphore Not Acquired
                        if (!semaphoreAcquired)
                        {
                            //await ParallelProcessesHelper.releaseSemaphoreAsync(resourceId);
                            continue;
                        }
                        #endregion



                        await processValidationRequest(queueItem, queueName, caseId);

                        #region Release Semaphore
                        if (semaphoreAcquired)
                            await ParallelProcessesHelper.releaseSemaphoreAsync(resourceId);
                        #endregion


                    }
                    #endregion
                    #endregion
                }
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processValidationServices(). Exception message: " + Environment.NewLine + e.Message);
            }
            #endregion
        }


        public static async Task processValidationRequest(Entity queueItem, string queueName, Guid caseId)
        {
            try
            {
                #region Entities & Parameters   
                Entity validationRequestCase = await DynamicsInterface.DataverseClient.RetrieveAsync("incident", caseId, new ColumnSet(true));            

                EntityReference customerIdRef = validationRequestCase.GetAttributeValue<EntityReference>("customerid");
               
                Entity account = await DynamicsInterface.DataverseClient.RetrieveAsync("account", customerIdRef.Id, new ColumnSet(true));


                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                EntityReference qualCodeRef = validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid");

                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;
                Guid qualCodeId = qualCodeRef == null ? Guid.Empty : qualCodeRef.Id;
                #endregion


                #region GetDispositionRequest & OrgEntity For Validation Request Case
                string validationReqTransactionId = validationRequestCase.GetAttributeValue<string>("ts_validationrequesttransactionid");

                string dispositionRequestText = ValidationServicesHelper.getDispositionRequestFromCaseNote("DispositionRequest:" + validationReqTransactionId,
                                                                                                                            new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                IDictionary<string, Object> dispositionRequest = JsonConvert.DeserializeObject<ExpandoObject>(dispositionRequestText) as IDictionary<string, Object>;

                //Entity valReqOrgEntity = ValidationServicesHelper.getAccountEntityFromValidationRequestCase(account, validationRequestCase, validationReqTransactionId, dispositionRequest);
                #endregion



                #region Determine Behavior For Current Case
                await determineProcessBehavior(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
                #endregion

            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processAutoValidationCase(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "queueItemId: " + queueItem.Id.ToString()
                    );
            }
            #endregion
        }

        public static async Task determineProcessBehavior(Entity caseEntity, Entity validationRequestor, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region AutomatedValDefinition & Parameters
                string postAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postAutoCloseQueue;

                List<string> usAndTerritoriesCodes = new string[] { "AS", "FM", "GU", "MP", "PR", "UM", "US", "VI" }.ToList();


                string postAutoCloseNonC3Queue =    
                                            (
                                                ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                        Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                            )?.postAutoCloseQueue;


                

                string requestingClientName = validationRequestor.GetAttributeValue<string>("name");

                dynamic customerAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.customers)?.ToList<dynamic>()?.
                                                                                                                                                                                Where(customer => ((string)customer.name)?.ToLower() == requestingClientName.ToLower()
                                                                                                                                                                                        )?.FirstOrDefault();

                postAutoCloseNonC3Queue = postAutoCloseNonC3Queue ?? postAutoCloseQueue;
                #endregion



                #region Check If It Is For US NonC3 Designation
                if (customerAutomatedValDefinition == null && usAndTerritoriesCodes.Exists(code => code.ToLower() ==
                                                                                                                    caseEntity.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()
                                                                                            )
                    )
                {
                    List<string> nonC3Designations = ((JArray)(
                                         ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                       Where(country => ((string)country.country)?.ToLower() == "us"
                                                                                                       )?.FirstOrDefault()?.orgDesignations
                                        ))?.ToList<dynamic>()?.Select(item => (string)item)?.ToList();

                    Entity qualCodeEntity = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid") == null ? null :
                                                            DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid").Id, new ColumnSet(true));

                    string nonC3OrgDesignation = qualCodeEntity?.GetAttributeValue<string>("new_qualcode");


                    if (
                        nonC3Designations.Count > 0 && !string.IsNullOrEmpty(nonC3OrgDesignation)
                            && nonC3Designations.Exists(designation => designation.ToLower() == nonC3OrgDesignation.ToLower())
                        )
                    {
                        DynamicsInterface.writeToLog("US nonC3 orgDesignation: " + nonC3OrgDesignation 
                                                        + Environment.NewLine + "Eligible for Validation Services"
                                                        + Environment.NewLine + "Starting process"
                                                        );

                        await ValidationServicesUSNonC3.determineProcessBehavior(caseEntity, validationRequestor, validationReqTransactionId, queueName, dispositionRequest);

                        DynamicsInterface.writeToLog("US nonC3 orgDesignation: " + nonC3OrgDesignation
                                                        + Environment.NewLine + "End process"
                                                        );
                    }
                    else
                    {

                        DynamicsInterface.writeToLog("US nonC3 orgDesignation: " + nonC3OrgDesignation
                                                        + Environment.NewLine + "Not eligible for Validation Services"
                                                        );

                        string noteDesc = nonC3OrgDesignation + " is not eligible";
                        ValidationServicesHelper.processSystemNote("-- Subsection Not Eligible --", noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));


                        caseEntity["ts_casestatus"] = new OptionSetValue(102057);//OQ - Disqualified
                        DynamicsInterface.DataverseClient.Update(caseEntity);
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, postAutoCloseNonC3Queue);
                    }


                    return;
                }
                #endregion

                #region Disposition
                bool caseHasDisposition = DynamicsProcessesHelper.existsSystemNote(" --- Disposition Details --- ", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                if (!caseHasDisposition)
                {
                    bool okToProcess = await getProcessingApproval(caseEntity, validationRequestor, queueName);

                    if (okToProcess)
                    {
                        await ValidationServicesHelper.getValidationScoreMatrix(caseEntity, validationRequestor, validationReqTransactionId, queueName, dispositionRequest);
                        caseHasDisposition = DynamicsProcessesHelper.existsSystemNote(" --- Disposition Details --- ", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                    }
                }
                #endregion


                #region Determine Action
                if (caseHasDisposition)
                    await determineAction(caseEntity, validationRequestor, validationReqTransactionId, queueName, dispositionRequest);
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in determineProcessBehavior(). Exception message: " + Environment.NewLine + e.Message);
            }
            #endregion
        }


        public static async Task determineAction(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
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

                


                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();

                string requestingClientName = account.GetAttributeValue<string>("name");
                dynamic customerAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.customers)?.ToList<dynamic>()?.
                                                                                                                                                                                Where(customer => ((string)customer.name)?.ToLower() == requestingClientName.ToLower()
                                                                                                                                                                                        )?.FirstOrDefault();
                dynamic validationServicesCustomers = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.validationServicesCustomers;


                string postAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postAutoCloseQueue;
                postAutoCloseQueue = countryAutomatedValDefinition?.postAutoCloseQueue ?? postAutoCloseQueue;
                postAutoCloseQueue = customerAutomatedValDefinition == null ? postAutoCloseQueue : (customerAutomatedValDefinition?.postAutoCloseQueue ?? validationServicesCustomers?.postAutoCloseQueue) ?? postAutoCloseQueue;

                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;
                postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;
                postNoAutoCloseQueue = customerAutomatedValDefinition == null ? postNoAutoCloseQueue : (customerAutomatedValDefinition?.postNoAutoCloseQueue ?? validationServicesCustomers?.postNoAutoCloseQueue) ?? postNoAutoCloseQueue;

                bool validationServicesAutoCloseEnabled = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.autoCloseEnabled;
                validationServicesAutoCloseEnabled = countryAutomatedValDefinition?.autoCloseEnabled ?? validationServicesAutoCloseEnabled;
                validationServicesAutoCloseEnabled = customerAutomatedValDefinition == null ? validationServicesAutoCloseEnabled : (customerAutomatedValDefinition?.autoCloseEnabled  ?? validationServicesCustomers?.autoCloseEnabled) ?? validationServicesAutoCloseEnabled;                
                #endregion


                #region Parameters
                IDictionary<string, Object> valReqOrgAccountObj = await ValidationServicesHelper.getValidationRequestOrgAccountInfo(validationReqTransactionId);
                #endregion


                #region InitialQueue
                if (queueName == validationServicesInitialQueue)
                {
                    #region CTPProvisioning
                    int ? tsCaseStatus = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_casestatus")?.Value; //OQ - Validated - Awaiting CTPOrgId Provisioning
                    string ctpOrgId = ValidationServicesHelper.getParentCaseAccountCtpOrgId(validationRequestCase.Id);

                    DynamicsInterface.writeToLog("caseStatus: " + validationRequestCase.FormattedValues["ts_casestatus"]
                                                                   + Environment.NewLine + "ctpOrgId: " + ctpOrgId
                                                                   );
                    if (tsCaseStatus == 104701) //OQ - Validated - Awaiting CTPOrgId Provisioning
                    {
                        if (!string.IsNullOrEmpty(ctpOrgId))
                        {
                            bool success = ValidationServicesHelper.updateValidationRequestCaseStatus(validationRequestCase, 102056, validationReqTransactionId);

                            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseQueue);
                        }
                        return;
                    }
                    #endregion


                    //#region Catch Val Requests That Already Have Org Account
                    //if (valReqOrgAccountObj["ctpOrgId"] != null)
                    //    hasOrgAccount_RouteOut(validationRequestCase, account, validationReqTransactionId, queueName, valReqOrgEntity, dispositionRequest, valReqOrgAccountObj);
                    //#endregion


                    #region Finding Existing Accounts
                    IDictionary<string, System.Object> accountMatchesResponse = ValidationServicesHelper.findValidationRequestAccountMatches(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
                    if (accountMatchesResponse.ContainsKey("validationProcessAction"))
                    {
                        if ((string)accountMatchesResponse["validationProcessAction"] == "ValidationRequestStatusUpdate-RouteToQueue")
                        {
                            validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                            DynamicsInterface.DataverseClient.Update(validationRequestCase);
                            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);
                            return;
                        }
                        else if ((string)accountMatchesResponse["validationProcessAction"] == "terminate")
                        {
                            return;
                        }
                    }
                    #endregion

                    

                    #region Fraud Check
                    bool? potentialFraud = ValidationServicesHelper.evaluateForFraud(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);

                    if (potentialFraud == null || potentialFraud.Value)
                        return;
                    #endregion


                    #region AutoClose
                    if (validationServicesAutoCloseEnabled)
                    {
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
                            initiateOrgIncorporation(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
                            return;
                        }
                    }
                    #endregion


                    #region Routing & EmailOutreach
                    DynamicsProcessesValidationServices.applyRoutingRules(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
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



        public static void hasOrgAccount_RouteOut(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName,IDictionary<string, Object> dispositionRequest
                                                                                                                                                                , IDictionary<string, Object> valReqOrgAccountObj)
        {
            #region AutomatedValDefinition
            string validationServicesInitialQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.initialQueue;

            dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                   Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                   validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
                                                                                                   )?.FirstOrDefault();

            string requestingClientName = account.GetAttributeValue<string>("name");
            dynamic customerAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.customers)?.ToList<dynamic>()?.
                                                                                                                                                                            Where(customer => ((string)customer.name)?.ToLower() == requestingClientName.ToLower()
                                                                                                                                                                                    )?.FirstOrDefault();
            dynamic validationServicesCustomers = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.validationServicesCustomers;


            string postAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postAutoCloseQueue;
            postAutoCloseQueue = countryAutomatedValDefinition?.postAutoCloseQueue ?? postAutoCloseQueue;
            postAutoCloseQueue = customerAutomatedValDefinition == null ? postAutoCloseQueue : (customerAutomatedValDefinition?.postAutoCloseQueue ?? validationServicesCustomers?.postAutoCloseQueue) ?? postAutoCloseQueue;

            string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;
            postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;
            postNoAutoCloseQueue = customerAutomatedValDefinition == null ? postNoAutoCloseQueue : (customerAutomatedValDefinition?.postNoAutoCloseQueue ?? validationServicesCustomers?.postNoAutoCloseQueue) ?? postNoAutoCloseQueue;
            #endregion


            #region Temporarily Just Sending To PostNoAutoCloseQueue
            validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
            DynamicsInterface.DataverseClient.Update(validationRequestCase);
            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);
            #endregion
        }




        public static void applyRoutingRules(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            try
            {

                #region AutomatedValDefinition & Parameters
                List<dynamic> autoCloseCustomeRules = ((JArray)AutomatedValDefinition.config.autoCloseCustomRules)?.ToList<dynamic>();
                List<dynamic> queueRoutingRules = ((JArray)AutomatedValDefinition.queueRoutingRules)?.ToList<dynamic>();

                List<dynamic> currentQueueRoutingRules = queueRoutingRules?.Where(rule => rule.routeFromQueue == queueName)?.ToList();

                

                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                       Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                       validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
                                                                                                       )?.FirstOrDefault();

                string requestingClientName = account.GetAttributeValue<string>("name");
                dynamic customerAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.customers)?.ToList<dynamic>()?.
                                                                                                                                                                                Where(customer => ((string)customer.name)?.ToLower() == requestingClientName.ToLower()
                                                                                                                                                                                        )?.FirstOrDefault();
                dynamic validationServicesCustomers = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.validationServicesCustomers;


                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;
                postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;
                postNoAutoCloseQueue = customerAutomatedValDefinition == null ? postNoAutoCloseQueue : (customerAutomatedValDefinition?.postNoAutoCloseQueue ?? validationServicesCustomers?.postNoAutoCloseQueue) ?? postNoAutoCloseQueue;
                #endregion


                #region EmailOutreach
                bool emailOutreachCriteria = evaluateValidationRequestCriteriaEmailOutreach(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
                if (emailOutreachCriteria)
                {
                    processValidationRequestEmailOutreach(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
                    return;

                }
                #endregion


                #region ForEach CurrentQueueRoutingRule
                foreach (dynamic rule in currentQueueRoutingRules)
                {
                    //evaluateRoutingRule(rule, caseEntity, account, validationReqTransactionId, queueName, isHighPriority);
                }
                #endregion

                #region Default Routing Rule
                validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                DynamicsInterface.DataverseClient.Update(validationRequestCase);
                DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);
                #endregion

            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in applyRoutingRules(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
            }
            #endregion
        }


        public static bool evaluateValidationRequestCriteriaEmailOutreach(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            bool emailOutreachCriteria = false;
            try
            {
                #region AutomatedValDefinition
                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                       Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                       validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
                                                                                                       )?.FirstOrDefault();

                string requestingClientName = account.GetAttributeValue<string>("name");
                dynamic customerAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.customers)?.ToList<dynamic>()?.
                                                                                                                                                                                Where(customer => ((string)customer.name)?.ToLower() == requestingClientName.ToLower()
                                                                                                                                                                                        )?.FirstOrDefault();
                dynamic validationServicesCustomers = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.validationServicesCustomers;



                bool autoOrgEmailOutreachEnabled = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.autoOrgOutreachEnabled == null ? false 
                                                                                                            : DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.autoOrgOutreachEnabled;
                autoOrgEmailOutreachEnabled = countryAutomatedValDefinition?.emailOutreachProcess?.autoOrgOutreachEnabled ?? autoOrgEmailOutreachEnabled;
                autoOrgEmailOutreachEnabled = customerAutomatedValDefinition == null ? autoOrgEmailOutreachEnabled : (customerAutomatedValDefinition?.emailOutreachProcess?.autoOrgOutreachEnabled ?? validationServicesCustomers?.emailOutreachProcess?.autoOrgOutreachEnabled) ?? autoOrgEmailOutreachEnabled;


                if (!autoOrgEmailOutreachEnabled)
                    return false;
                #endregion

                #region Parameters
                bool isOrgValidDispostion = validationRequestCase.GetAttributeValue<bool>("ts_validationorgdisposition");
                bool isAgentValidDisposition = validationRequestCase.GetAttributeValue<bool>("ts_validationagentdisposition");
                bool isDispositionTrustworthy = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                bool isLegalEquivalencyDisp = validationRequestCase.GetAttributeValue<bool>("ts_validationlegalequivalencedisposition");
                #endregion

                #region EmailOutreach Criteria
                if (
                    !(isOrgValidDispostion && isAgentValidDisposition && isDispositionTrustworthy && isLegalEquivalencyDisp)
                    )
                    emailOutreachCriteria = true;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in evaluateValidationRequestCriteriaEmailOutreach(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                    );
            }
            #endregion

            return emailOutreachCriteria;
        }


        public static void evaluateRoutingRule(dynamic rule, Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, Entity valReqOrgEntity)
        {
            try
            {
                #region Parameters
                bool isOrgValiDispRules = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesorgvalid");
                bool isAgentValidDispRules = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesagentvalid");

                bool isOrgValidDisposition = validationRequestCase.GetAttributeValue<bool>("ts_validationorgdisposition");
                bool isAgentValidDisposition = validationRequestCase.GetAttributeValue<bool>("ts_validationagentdisposition");

                bool isTrustWorthy = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                bool isActivityCodeValid = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesactivitycodevalid");

                string IRSOrgName = validationRequestCase.GetAttributeValue<string>("ts_validationdispositionirsorgname");
                #endregion

                #region EvaluatingRoutingRule
                string routeToQueue = rule.routeToQueue;
                string routingCriteria = rule.routingCriteria;
                switch (routingCriteria)
                {
                    


                }
                #endregion


            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in evaluateRoutingRule(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
            }
            #endregion
        }

        public static void initiateOrgIncorporation(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region AutomatedValDefinition & Parameters
                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();

                string requestingClientName = account.GetAttributeValue<string>("name");
                dynamic customerAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.customers)?.ToList<dynamic>()?.
                                                                                                                                                                                Where(customer => ((string)customer.name)?.ToLower() == requestingClientName.ToLower()
                                                                                                                                                                                        )?.FirstOrDefault();
                dynamic validationServicesCustomers = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.validationServicesCustomers;


                string postAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postAutoCloseQueue;
                postAutoCloseQueue = countryAutomatedValDefinition?.postAutoCloseQueue ?? postAutoCloseQueue;
                postAutoCloseQueue = customerAutomatedValDefinition == null ? postAutoCloseQueue : (customerAutomatedValDefinition?.postAutoCloseQueue ?? validationServicesCustomers?.postAutoCloseQueue) ?? postAutoCloseQueue;

                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;
                postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;
                postNoAutoCloseQueue = customerAutomatedValDefinition == null ? postNoAutoCloseQueue : (customerAutomatedValDefinition?.postNoAutoCloseQueue ?? validationServicesCustomers?.postNoAutoCloseQueue) ?? postNoAutoCloseQueue;
                #endregion


                #region Create Org Account
                Guid? qualCaseId = validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid")?.Id;
                Entity qualCase = qualCaseId == null ? null : DynamicsInterface.DataverseClient.Retrieve("incident", qualCaseId.Value, new ColumnSet(true));
                Guid? orgAccountId = qualCase?.GetAttributeValue<EntityReference>("customerid")?.Id;
                Entity orgAccount = orgAccountId == null ? null : DynamicsInterface.DataverseClient.Retrieve("account", orgAccountId.Value, new ColumnSet(true));

                if (orgAccount == null)
                {
                    orgAccount = ValidationServicesHelper.createOrgFromCase(account, validationRequestCase, validationReqTransactionId);
                  
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

                Entity agentContact = ValidationServicesHelper.createAgentContact(validationRequestCase, account, validationReqTransactionId, queueName);

                if (agentContact!= null)
                    ValidationServicesHelper.connectAgentToAccount(orgAccount.Id, agentContact.Id, agentVerificationStatusOption, validationReqTransactionId);


                if (orgAccount == null || agentContact == null)
                {
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



        public static void processValidationRequestEmailOutreach(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            bool hasOpenOrders = true;


            try
            {
                #region Parameters
                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();


                string requestingClientName = account.GetAttributeValue<string>("name");
                dynamic customerAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.customers)?.ToList<dynamic>()?.
                                                                                                                                                                                Where(customer => ((string)customer.name)?.ToLower() == requestingClientName.ToLower()
                                                                                                                                                                                        )?.FirstOrDefault();

                dynamic validationServicesCustomers = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.validationServicesCustomers;


                string templateName = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.emailTemplate;
                templateName = countryAutomatedValDefinition?.emailOutreachProcess?.emailTemplate ?? templateName;
                templateName = customerAutomatedValDefinition == null ? templateName : (customerAutomatedValDefinition?.emailOutreachProcess?.emailTemplate ?? validationServicesCustomers?.emailOutreachProcess?.emailTemplate) ?? templateName;

                Entity template = DynamicsProcessesHelper.getTemplateEntity(templateName);

                string outreachQueueName = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.queueName;
                string outreachQueueHighPriority = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.queueNameHighPriority;

                outreachQueueName = countryAutomatedValDefinition?.emailOutreachProcess?.queueName ?? outreachQueueName;
                outreachQueueName = customerAutomatedValDefinition == null ? outreachQueueName : 
                                                                                                (customerAutomatedValDefinition?.emailOutreachProcess?.queueName ?? validationServicesCustomers?.emailOutreachProcess?.queueName) ?? outreachQueueName;

                outreachQueueHighPriority = countryAutomatedValDefinition?.emailOutreachProcess?.queueNameHighPriority ?? outreachQueueHighPriority;
                outreachQueueHighPriority = customerAutomatedValDefinition == null ? outreachQueueHighPriority : 
                                                                                                (customerAutomatedValDefinition?.emailOutreachProcess?.queueNameHighPriority ?? validationServicesCustomers?.emailOutreachProcess?.queueNameHighPriority) ?? outreachQueueHighPriority;

                string senderMailboxQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.senderMailboxQueue ?? "";
                senderMailboxQueue = countryAutomatedValDefinition?.emailOutreachProcess?.senderMailboxQueue ?? senderMailboxQueue;
                senderMailboxQueue = customerAutomatedValDefinition == null ? senderMailboxQueue : (customerAutomatedValDefinition?.emailOutreachProcess?.senderMailboxQueue ?? validationServicesCustomers?.emailOutreachProcess?.senderMailboxQueue) ?? senderMailboxQueue;
                #endregion


                #region Check If Email Already Sent
                DateTime validationRequestDate = validationRequestCase.GetAttributeValue<DateTime>("ts_validationrequestdate");
                QueryExpression queryEmail = new QueryExpression("email");
                queryEmail.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, validationRequestCase.Id);
                //queryEmail.Criteria.AddCondition("createdon", ConditionOperator.GreaterThan, validationRequestDate);
                queryEmail.Criteria.AddCondition("subject", ConditionOperator.Equal, "TechSoup: Action Required for TechSoup Validation");
                EntityCollection emailCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEmail);

                if (emailCollection.Entities.Count > 0)
                    return;
                #endregion


                #region ProcessingEmail
                Entity email = new Entity("email");

                //QueryExpression queryEntity = new QueryExpression("queue");
                //queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, "Support");
                //EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                //if (entityCollection.Entities.Count == 0)
                //{
                //    DynamicsInterface.writeToLog("At processEmailOutreach(). No maibox queue found with name: " + "Support");
                //    return;
                //}

                //Guid queueId = entityCollection.Entities.First().Id;

                EntityCollection fromParties = new EntityCollection();

                Guid? queueId = ValidationServicesHelper.getMailBoxQueueId(senderMailboxQueue);

                if (queueId == null)
                {
                    DynamicsInterface.writeToLog("At processValidationRequestEmailOutreach(). No maibox queue found with name: " + senderMailboxQueue + ", a default mailbox queue was not found");
                    return;
                }


                Entity fromQueue = new Entity("activityparty");
                fromQueue["partyid"] = new EntityReference("queue", queueId.Value);
                fromParties.Entities.Add(fromQueue);

                EntityCollection toParties = new EntityCollection();

                string validationRequestEmail = validationRequestCase.GetAttributeValue<string>("ts_validationrequestemail");
                Entity toparty = new Entity("activityparty");
                toparty["addressused"] = DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] == "prod" ? validationRequestEmail : "franciscocastellanos@yahoo.com";
                toParties.Entities.Add(toparty);

                email["from"] = fromParties;
                email["to"] = toParties;

                email["subject"] = "To be replaced";
                email["description"] = "To be replaced";
                email["directioncode"] = true;                

                SendEmailFromTemplateRequest request = new SendEmailFromTemplateRequest()
                {
                    Target = email,
                    TemplateId = template.Id,
                    RegardingId = validationRequestCase.Id,
                    RegardingType = "incident"
                    //,issue
                };

                //request.
                SendEmailFromTemplateResponse response = (SendEmailFromTemplateResponse)DynamicsInterface.DataverseClient.Execute(request);
                #endregion


                #region Updating Case & Routing
                if (response.Id != Guid.Empty)
                {
                    email = DynamicsInterface.DataverseClient.Retrieve("email", response.Id, new ColumnSet(true));
                    email["regardingobjectid"] = new EntityReference("incident", validationRequestCase.Id);
                    DynamicsInterface.DataverseClient.Update(email);


                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104698); //OQ - AutoValidation - Awaiting Customer Response
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);                    

                    string nextQueue = hasOpenOrders ? outreachQueueHighPriority : outreachQueueName;
                    if (nextQueue != queueName)
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, nextQueue);
                }
                #endregion

            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processValidationRequestEmailOutreach(...). Exception message: " + Environment.NewLine + e.Message
                                                                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                                                                );
            }
            #endregion

        }
        public static bool determineIfDuplicate(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName)
        {
            bool isDuplicate = false;
            try
            {
                string tsCaseStatusDesc = caseEntity.Contains("ts_casestatus") ? caseEntity.FormattedValues["ts_casestatus"] : "";

                if (tsCaseStatusDesc != "OQ - Not Started")
                    return false;


                string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                ServiceAdminDataContext context = new ServiceAdminDataContext();

                IEnumerable<usp_findOrgMatchesResult> orgMatchQeury = null;

                orgMatchQeury = from table in context.usp_findOrgMatches(int.Parse(tsOrgId))
                                select table;

                List<usp_findOrgMatchesResult> orgMatchResult = orgMatchQeury.ToList<usp_findOrgMatchesResult>();

                if (orgMatchResult.Count == 0)
                    return false;


                isDuplicate = true;

                string orgMatches = orgMatchResult.First().orgMatches;

                string dupesNoteDesc = "TSOrgIds of matching orgs: " + Environment.NewLine + Environment.NewLine;
                dupesNoteDesc += orgMatches + Environment.NewLine + Environment.NewLine;
                dupesNoteDesc += "Routing case to Duplicate Review queue";
                DynamicsProcessesHelper.processSystemNote("Initial Duplicate Check - Org Matches Found", dupesNoteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));


                caseEntity["ts_casestatus"] = new OptionSetValue(104696); //OQ - AutoValidation - Duplicate Review
                DynamicsInterface.DataverseClient.Update(caseEntity);


                string duplicateReviewQueue = AutomatedValDefinition.config.duplicateReviewQueue;
                if (!string.IsNullOrEmpty(duplicateReviewQueue))
                    DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, duplicateReviewQueue);
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in determineIfDuplicate(...). Exception message: " + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                        );
            }

            return isDuplicate;
        }
        
        public static async Task<bool> getProcessingApproval(Entity caseEntity, Entity account, string queueName)
        {
            bool okToProcess = false;

            try
            {
                int validationRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");

                int topCount = AutomatedValDefinition.config.maximumCount;

                if (validationRequestChecksCount >= topCount && topCount != -1)
                {
                    string validationReqTransactionId = caseEntity.GetAttributeValue<string>("ts_validationrequesttransactionid");

                    //removeCaseFromAutomatedValidation(caseEntity, account, validationReqTransactionId, queueName);

                    return false;
                }

                DateTime validationRequestDate = caseEntity.GetAttributeValue<DateTime>("ts_validationrequestdate");
                DateTime validationLastStatusCheck = validationRequestChecksCount == 0 ? validationRequestDate : caseEntity.GetAttributeValue<DateTime>("ts_validationrequestlaststatuscheck");
                TimeSpan spanSinceLastProcessing = DateTime.UtcNow - validationLastStatusCheck;
                double minutesSinceLastProcessing = spanSinceLastProcessing.TotalMinutes;


                var segments = AutomatedValidationConfig.Where(item => item.Key.StartsWith("segment"));

                KeyValuePair<string, object> topLowerBoundSeg = new KeyValuePair<string, object>();

                foreach (KeyValuePair<string, object> segment in segments)
                {
                    dynamic segValue = segment.Value;
                    if (validationRequestChecksCount >= segValue.lowerBoundCount)
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
                DynamicsInterface.writeToLog("Error in getProcessingApproval(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "caseId: " + caseEntity.Id.ToString()
                    );
            }
            return okToProcess;
        }


        public static async Task<bool> validationServicesEvaluateForFraudSimplified(Entity validationRequestCase, Entity requestingAccount, string validationReqTransactionId)
        {
            var fraudFlags = new List<string>();
            bool isFraudulent = false;


            //string orgDomainRegistrationCountry = string.Empty;


            try
            {
                string website = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite");
                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentemail");
                string countryCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid");

                if (!string.IsNullOrEmpty(website))
                {
                    try
                    {
                        var enhancedDomainValidator = new EnhancedDomainValidator();
                        var enhancedResult = await enhancedDomainValidator.ValidateDomainAsync(website);


                        if (!enhancedResult.IsValid)
                        {
                            fraudFlags.Add("Website is not valid");
                            isFraudulent = true;
                        }

                        if (enhancedResult != null && enhancedResult.WhoisData != null)
                        {
                            var whoisData = enhancedResult.WhoisData;


                            if (whoisData.IsRecentlyRegistered)
                            {
                                fraudFlags.Add($"Domain was recently registered ({whoisData.DomainAgeInDays} days ago) - potential indicator of fraudulent activity");
                                isFraudulent = true;
                            }


                            if (whoisData.Registrar?.GetPhoneCountryCode() != null &&
                                whoisData.Registrar.GetPhoneCountryCode() != "US")
                            {
                                fraudFlags.Add($"Registrar phone indicates non-US location: {whoisData.Registrar.Name} (Phone country: {whoisData.Registrar.GetPhoneCountryCode()})");
                                isFraudulent = true;
                            }


                            if (enhancedResult.WhoisData.IpAddresses.Count > 0)
                            {
                                bool hasUSIP = false;
                                bool isSuspiciousIPFound = false;
                                foreach (string ipAddress in enhancedResult.WhoisData.IpAddresses)
                                {
                                    try
                                    {
                                        IPAddressValidationResult ipValidation = await NetworkValidationService.ValidateIPAddressAsync(ipAddress);
                                        if (ipValidation != null && ipValidation.IsInUS)
                                            hasUSIP = true;

                                        if (ipValidation != null && ipValidation.IsSuspicious)
                                            isSuspiciousIPFound = true;

                                    }
                                    catch (Exception ipEx)
                                    {
                                        DynamicsInterface.writeToLog($"Error validating WHOIS IP {ipAddress} for {validationReqTransactionId}: {ipEx.Message}");
                                    }
                                }

                                if (enhancedResult.WhoisData.IpAddresses.Count > 0 && !hasUSIP && isSuspiciousIPFound)
                                {
                                    fraudFlags.Add("Domain registration data has no IP addresses located in the US");
                                    isFraudulent = true;
                                    DynamicsInterface.writeToLog($"Domain registration data has no IP addresses located in the US for {validationReqTransactionId}");
                                }
                            }
                            else
                            {
                                //fraudFlags.Add("No IP addresses found for domain");
                                //isFraudulent = true;
                            }

                            if (enhancedResult.DnsData != null && enhancedResult.DnsData.ARecords.Count > 0)
                            {
                                bool hasUSIP = false;
                                var ipCountries = new List<string>();
                                bool isSuspiciousIPFound = false;

                                foreach (var ipAddress in enhancedResult.DnsData.ARecords)
                                {
                                    try
                                    {
                                        var ipValidation = await NetworkValidationService.ValidateIPAddressAsync(ipAddress);
                                        if (ipValidation != null)
                                        {
                                            ipCountries.Add($"{ipAddress} ({ipValidation.Country})");

                                            if (ipValidation.IsInUS)
                                                hasUSIP = true;

                                            if (ipValidation.IsSuspicious)
                                                isSuspiciousIPFound = true;
                                        }
                                    }
                                    catch (Exception ipEx)
                                    {
                                        DynamicsInterface.writeToLog($"Error validating IP {ipAddress} for {validationReqTransactionId}: {ipEx.Message}");
                                        ipCountries.Add($"{ipAddress} (validation error)");
                                    }
                                }

                                if (!hasUSIP && ipCountries.Count > 0
                                    //&& isSuspiciousIPFound
                                    )
                                {
                                    fraudFlags.Add($"Domain has no IP addresses located in the US. All IPs are outside US: {string.Join(", ", ipCountries)}");
                                    isFraudulent = true;

                                    DynamicsInterface.writeToLog($"Non-US IP addresses detected for {validationReqTransactionId}: {string.Join(", ", ipCountries)}");
                                }
                                else if (ipCountries.Count > 0)
                                {
                                    DynamicsInterface.writeToLog($"IP geolocation check for {validationReqTransactionId}: {string.Join(", ", ipCountries)} - US IP found: {hasUSIP}");
                                }
                            }


                            if (whoisData.Contacts != null)
                            {
                                var countries = whoisData.Contacts.GetAllCountries();
                                bool hasUSContact = countries.Any(c =>
                                    string.Equals(c, "US", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(c, "UNITED STATES", StringComparison.OrdinalIgnoreCase));

                                if (!hasUSContact && countries.Any())
                                {
                                    fraudFlags.Add($"Domain registration has no US contacts. Countries found: {string.Join(", ", countries)}");
                                    isFraudulent = true;
                                }
                            }
                        }

                        enhancedDomainValidator.Dispose();
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog($"Error in domain validation for {validationReqTransactionId}: {ex.Message}");


                        try
                        {
                            var domainValResult = await DomainValidator.ValidateDomainRegistrationAsync(website);
                            if (domainValResult != null && domainValResult.DomainRegistrationCountryCode != null &&
                                domainValResult.DomainRegistrationCountryCode.ToUpper() != "US")
                            {
                                fraudFlags.Add($"Domain registration is not in the US: {domainValResult.DomainRegistrationCountry} ({domainValResult.DomainRegistrationCountryCode})");
                                isFraudulent = true;
                            }
                        }
                        catch (Exception ex2)
                        {
                            DynamicsInterface.writeToLog($"Error in fallback domain validation for {validationReqTransactionId}: {ex2.Message}");
                        }
                    }
                }

                if (!string.IsNullOrEmpty(agentEmail))
                {
                    try
                    {
                        var userRegistrationIP = DynamicsProcessesHelper.getUserRegistrationIpAddress(agentEmail);
                        if (!string.IsNullOrEmpty(userRegistrationIP))
                        {
                            var ipValidation = await NetworkValidationService.ValidateIPAddressAsync(userRegistrationIP);

                            if (ipValidation != null && ipValidation.IsSuspicious && !ipValidation.IsInUS)
                            {
                                //fraudFlags.Add($"User registration IP address is not in the US: {ipValidation.Country} ({ipValidation.CountryCode})");
                                fraudFlags.Add($"IP address used during registration, {userRegistrationIP}, is not in the US. Country of IP Address: {ipValidation.Country} (country code: {ipValidation.CountryCode})");
                                isFraudulent = true;

                                DynamicsInterface.writeToLog($"Non-US registration IP detected for {validationReqTransactionId}: {userRegistrationIP} -> {ipValidation.Country}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog($"Error in registration IP validation for {validationReqTransactionId}: {ex.Message}");
                    }
                }


                if (isFraudulent && fraudFlags.Count > 0)
                {

                    //string noteDesc = "Simplified fraud analysis findings (US vs Non-US activity detection):\n\n" + string.Join("\n", fraudFlags.Select((flag, index) => $"{index + 1}. {flag}"));
                    //processSystemNote("-- Potential Fraud (Simplified Detection) --", noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                    string noteDesc = "Fraud analysis findings:\n\n" + string.Join("\n", fraudFlags.Select((flag, index) => $"{index + 1}. {flag}"));
                    ValidationServicesHelper.processSystemNote("-- Potential Fraud --", noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104602); //104602 - OQ - Fraud Review
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);


                    dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        countryCode?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();

                    string fraudReviewQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.fraudReviewQueue;

                    fraudReviewQueue = countryAutomatedValDefinition?.fraudReviewQueue ?? fraudReviewQueue;

                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, fraudReviewQueue);

                    DynamicsInterface.writeToLog($"Case {validationReqTransactionId} flagged as potential fraud with {fraudFlags.Count} simplified violations");
                }

                return isFraudulent;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error in evaluateForFraudSimplified for {validationReqTransactionId}: {ex.Message}");

                ValidationServicesHelper.processSystemNote("-- Potential Fraud --", $"Error during fraud evaluation: {ex.Message}\n\nCase requires manual review due to technical issues during automated validation."
                                    , new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                validationRequestCase["ts_casestatus"] = new OptionSetValue(104602);//104602 - OQ - Fraud Review
                DynamicsInterface.DataverseClient.Update(validationRequestCase);

                string fraudReviewQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.config.fraudReviewQueue;
                DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, fraudReviewQueue);

                return true;
            }
        }

        



    }
}
