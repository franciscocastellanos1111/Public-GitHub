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
using static DynamicsProcesses.NetworkValidationService;
using HtmlAgilityPack;

namespace DynamicsProcesses
{
   

    internal class DynamicsProcessesValidationServices
    {

        public static Dictionary<string, string> EnvVariables;
        public static Guid OriginalCaller = Guid.Empty;

        public static IDictionary<string, Object> AutomatedValidationConfig;

        public static dynamic AutomatedValDefinition;

        public static dynamic ConfigJson
        {
            get { return AutomatedValDefinition; }
        }

        public static dynamic ConfigParams;

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

                if (!(automatedValSettings?.isAutomatedValidationActive ?? false))
                {
                    string isAutomatedValidationActive = ((bool)automatedValSettings?.isAutomatedValidationActive).ToString() ?? "null";
                    DynamicsInterface.writeToLog($"isAutomatedValidationActive: {isAutomatedValidationActive}. Exiting processValidationServices().");
                    return;
                }

                if (automatedValSettings?.automatedValConfigText == null)
                {
                    DynamicsInterface.writeToLog("automatedValConfigText is null. Exiting processValidationServices().");
                    return;
                }

                AutomatedValidationConfig = JsonConvert.DeserializeObject<ExpandoObject>(automatedValSettings.automatedValConfigText) as IDictionary<string, Object>;

                AutomatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);

                

                EnvVariables = DynamicsProcessesHelper.GetEnvironmentVariables();

                if (DynamicsEnvironments.ContainsKey(DynamicsInterface.DynamicsEnvironment))
                {
                    string DynamicsEnvironmentCurrentName = DynamicsEnvironments[DynamicsInterface.DynamicsEnvironment];
                    DynamicsEnvironments["DynamicsEnvironmentCurrent"] = DynamicsEnvironmentCurrentName;
                }


                ParallelProcessesHelper.SemaphoreClient = ParallelProcessesHelper.getTableClientAsync(AzureStorage7C);
                #endregion


                #region Identifying Queues To Process
                //"AutoValidation Email Outreach"
                //"AutoValidation Inconclusive"
                string queueNameCsv = "ValidationServices";
                if (DynamicsInterface.Args.Length > 1)
                    queueNameCsv = DynamicsInterface.Args[1];


                string[] queueNamesParameters = queueNameCsv.Split(',');


                List<string> queueNamesList = new List<string>();

                foreach (string queueParam in queueNamesParameters)
                {
                    string actualQueueName = queueParam;

                    switch (queueParam.ToLower())
                    {
                        case "emailoutreachresponded":
                            ValidationServicesUSNonC3.getCustomerHasRespondedEmailOutreach();
                            return;

                        case "valservicesoutreachresponded":
                            await getCustomerHasRespondedEmailOutreach();
                            return;

                        case "emailoutreachabandoned":
                            await setOldCasesInOutreachQueueToAbandoned();
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


                    DynamicsInterface.LogName += DynamicsInterface.Args.Length > 2 ? "_" + DynamicsInterface.Args[2] : "";

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

                int caseCategory = validationRequestCase.GetAttributeValue<OptionSetValue>("casetypecode")?.Value ?? -1;

                if (caseCategory != 5)
                    await DynamicsInterface.DataverseClient.DeleteAsync(queueItem.LogicalName, queueItem.Id);

                EntityReference customerIdRef = validationRequestCase.GetAttributeValue<EntityReference>("customerid");

                Entity validationRequestor = await DynamicsInterface.DataverseClient.RetrieveAsync("account", customerIdRef.Id, new ColumnSet(true));


                EntityReference orgDesigRef = validationRequestor.GetAttributeValue<EntityReference>("new_orgdesignation");
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

                getValidationConfigParameters(validationRequestCase, validationRequestor, validationReqTransactionId);

                #region Determine Behavior For Current Case
                await determineProcessBehavior(validationRequestCase, validationRequestor, validationReqTransactionId, queueName, queueItem, dispositionRequest);
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

        public static async Task determineProcessBehavior(Entity caseEntity, Entity validationRequestor, string validationReqTransactionId, string queueName, Entity queueItem, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region Parameters

                dynamic nonC3AutomatedValDefinition = (JObject)ConfigParams.nonC3AutomatedValDefinition;
                string nonC3PostAutoCloseQueue = nonC3AutomatedValDefinition?.postAutoCloseQueue ?? ConfigParams.postAutoCloseQueue;

                List<string> usAndTerritoriesCodes = new string[] { "AS", "FM", "GU", "MP", "PR", "UM", "US", "VI" }.ToList();

                string nonC3ClientName = nonC3AutomatedValDefinition?.clientName ?? "TechSoup Global";

                Entity qualCodeEntity = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid") == null ? null :
                                                            DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid").Id, new ColumnSet(true));

                string valReqQualCode = qualCodeEntity?.GetAttributeValue<string>("new_qualcode");

                string requestingClientName = validationRequestor.GetAttributeValue<string>("name");
                #endregion


                #region Check If It Is For US NonC3 Designation
                if (
                        ConfigParams.customerAutomatedValDefinition == null
                        && usAndTerritoriesCodes.Exists(code => code.ToLower() ==
                                                                                    caseEntity.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()
                                                        )
                        && requestingClientName == nonC3ClientName
                    )
                {
                    if ((bool)ConfigParams.isNonC3Validation)
                    {
                        DynamicsInterface.writeToLog($"US nonC3 orgDesignation: {valReqQualCode}"
                                                        + $"{Environment.NewLine}Eligible for Validation Services"
                                                        + $"{Environment.NewLine}Starting process"
                                                        );

                        await ValidationServicesUSNonC3.determineProcessBehavior(caseEntity, validationRequestor, validationReqTransactionId, queueName, dispositionRequest);

                        DynamicsInterface.writeToLog($"US nonC3 orgDesignation: {valReqQualCode}"
                                                        + $"{Environment.NewLine}End process"
                                                        );
                    }
                    else
                    {
                        DynamicsInterface.writeToLog($"US nonC3 orgDesignation: {valReqQualCode}"
                                                        + $"{Environment.NewLine}Not eligible for Validation Services"
                                                        );

                        string noteDesc = valReqQualCode + " is not eligible";
                        ValidationServicesHelper.processSystemNote("-- Subsection Not Eligible --", noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));


                        caseEntity["ts_casestatus"] = new OptionSetValue(102057);//OQ - Disqualified
                        DynamicsInterface.DataverseClient.Update(caseEntity);
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, nonC3PostAutoCloseQueue);
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
                    await determineAction(caseEntity, validationRequestor, validationReqTransactionId, queueName, queueItem, dispositionRequest);
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in determineProcessBehavior(). Exception message:{Environment.NewLine}{e.Message}"
                                                    );

                
            }
            #endregion
        }

        public static void getValidationConfigParameters(Entity validationRequestCase, Entity validationRequestor, string validationReqTransactionId)
        {
            try
            {

                dynamic validationServices = ConfigJson.validationServices;


                dynamic countryAutomatedValDefinition = ((JArray)validationServices?.countries)?.ToList<dynamic>()?
                                                                                                        .Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();


                dynamic validationServicesCustomers = validationServices.validationServicesCustomers;

                string requestingClientName = validationRequestor.GetAttributeValue<string>("name");
                dynamic customerAutomatedValDefinition = ((JArray)validationServices?.customers)?.ToList<dynamic>()?
                                                                                                                    .Where(customer => ((string)customer.name)?.ToLower() == requestingClientName.ToLower()
                                                                                                                            )?.FirstOrDefault();

               
                dynamic customerCountryAutomatedValDefinition = ((JArray)customerAutomatedValDefinition?.countries)?.ToList<dynamic>()?
                                                                                                        .Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();

                int maxDispositionRetrievalAttempts = (validationServices.maxDispositionRetrievalAttempts ?? ConfigJson.config.checkCountsForManagedQueue) ?? 100;

                string initialQueue = validationServices.initialQueue;

                bool autoCloseEnabled = validationServices.autoCloseEnabled;
                autoCloseEnabled = countryAutomatedValDefinition?.autoCloseEnabled ?? autoCloseEnabled;
                autoCloseEnabled = customerAutomatedValDefinition == null ? autoCloseEnabled : (
                                                                                                    (customerCountryAutomatedValDefinition?.autoCloseEnabled ?? customerAutomatedValDefinition?.autoCloseEnabled) ?? validationServicesCustomers?.autoCloseEnabled
                                                                                                ) ?? autoCloseEnabled;

                

                string postAutoCloseQueue = validationServices.postAutoCloseQueue;
                postAutoCloseQueue = countryAutomatedValDefinition?.postAutoCloseQueue ?? postAutoCloseQueue;
                postAutoCloseQueue = customerAutomatedValDefinition == null ? postAutoCloseQueue : (
                                                                                                        (customerCountryAutomatedValDefinition?.postAutoCloseQueue ?? customerAutomatedValDefinition?.postAutoCloseQueue) ?? validationServicesCustomers?.postAutoCloseQueue
                                                                                                    ) ?? postAutoCloseQueue;

                string postNoAutoCloseQueue = validationServices.postNoAutoCloseQueue;
                postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;
                postNoAutoCloseQueue = customerAutomatedValDefinition == null ? postNoAutoCloseQueue : (
                                                                                                            (customerCountryAutomatedValDefinition?.postNoAutoCloseQueue ?? customerAutomatedValDefinition?.postNoAutoCloseQueue) ?? validationServicesCustomers?.postNoAutoCloseQueue
                                                                                                        ) ?? postNoAutoCloseQueue;

                string duplicateReviewQueue = validationServices.duplicateReviewQueue;
                duplicateReviewQueue = countryAutomatedValDefinition?.duplicateReviewQueue ?? duplicateReviewQueue;
                duplicateReviewQueue = customerAutomatedValDefinition == null ? duplicateReviewQueue : (
                                                                                                        (customerCountryAutomatedValDefinition?.duplicateReviewQueue ?? customerAutomatedValDefinition?.duplicateReviewQueue) ?? validationServicesCustomers?.duplicateReviewQueue
                                                                                                    ) ?? duplicateReviewQueue;

                bool fraudReviewEnabled = validationServices.fraudReviewEnabled ?? false;
                fraudReviewEnabled = countryAutomatedValDefinition?.fraudReviewEnabled ?? fraudReviewEnabled;
                fraudReviewEnabled = customerAutomatedValDefinition == null ? fraudReviewEnabled : (
                                                                                                        (customerCountryAutomatedValDefinition?.fraudReviewEnabled ?? customerAutomatedValDefinition?.fraudReviewEnabled) ?? validationServicesCustomers?.fraudReviewEnabled
                                                                                                    ) ?? fraudReviewEnabled;

                string fraudReviewQueue = validationServices.fraudReviewQueue;
                fraudReviewQueue = countryAutomatedValDefinition?.fraudReviewQueue ?? fraudReviewQueue;
                fraudReviewQueue = customerAutomatedValDefinition == null ? fraudReviewQueue : (
                                                                                                        (customerCountryAutomatedValDefinition?.fraudReviewQueue ?? customerAutomatedValDefinition?.fraudReviewQueue) ?? validationServicesCustomers?.fraudReviewQueue
                                                                                                    ) ?? fraudReviewQueue;


                bool autoOrgOutreachEnabled = validationServices.emailOutreachProcess.autoOrgOutreachEnabled ?? false;
                autoOrgOutreachEnabled = countryAutomatedValDefinition?.emailOutreachProcess?.autoOrgOutreachEnabled ?? autoOrgOutreachEnabled;
                autoOrgOutreachEnabled = customerAutomatedValDefinition == null ? autoOrgOutreachEnabled : (
                                                                                                        (customerCountryAutomatedValDefinition?.emailOutreachProcess?.autoOrgOutreachEnabled ?? customerAutomatedValDefinition?.emailOutreachProcess?.autoOrgOutreachEnabled) ?? validationServicesCustomers?.emailOutreachProcess?.autoOrgOutreachEnabled
                                                                                                    ) ?? autoOrgOutreachEnabled;

                string outreachSenderMailboxQueue = validationServices.emailOutreachProcess.senderMailboxQueue;
                outreachSenderMailboxQueue = countryAutomatedValDefinition?.emailOutreachProcess?.senderMailboxQueue ?? outreachSenderMailboxQueue;
                outreachSenderMailboxQueue = customerAutomatedValDefinition == null ? outreachSenderMailboxQueue : (
                                                                                                        (customerCountryAutomatedValDefinition?.emailOutreachProcess?.senderMailboxQueue ?? customerAutomatedValDefinition?.emailOutreachProcess?.senderMailboxQueue) ?? validationServicesCustomers?.emailOutreachProcess?.senderMailboxQueue
                                                                                                    ) ?? outreachSenderMailboxQueue;

                string outreachEmailTemplate = validationServices.emailOutreachProcess.emailTemplate;
                outreachEmailTemplate = countryAutomatedValDefinition?.emailOutreachProcess?.emailTemplate ?? outreachEmailTemplate;
                outreachEmailTemplate = customerAutomatedValDefinition == null ? outreachEmailTemplate : (
                                                                                                        (customerCountryAutomatedValDefinition?.emailOutreachProcess?.emailTemplate ?? customerAutomatedValDefinition?.emailOutreachProcess?.emailTemplate) ?? validationServicesCustomers?.emailOutreachProcess?.emailTemplate
                                                                                                    ) ?? outreachEmailTemplate;

                string outreachQueueName = validationServices.emailOutreachProcess.queueName;
                outreachQueueName = countryAutomatedValDefinition?.emailOutreachProcess?.queueName ?? outreachQueueName;
                outreachQueueName = customerAutomatedValDefinition == null ? outreachQueueName : (
                                                                                                        (customerCountryAutomatedValDefinition?.emailOutreachProcess?.queueName ?? customerAutomatedValDefinition?.emailOutreachProcess?.queueName) ?? validationServicesCustomers?.emailOutreachProcess?.queueName
                                                                                                    ) ?? outreachQueueName;

                string outreachQueueHighPriority = validationServices.emailOutreachProcess.queueNameHighPriority;
                outreachQueueHighPriority = countryAutomatedValDefinition?.emailOutreachProcess?.queueNameHighPriority ?? outreachQueueHighPriority;
                outreachQueueHighPriority = customerAutomatedValDefinition == null ? outreachQueueHighPriority : (
                                                                                                        (customerCountryAutomatedValDefinition?.emailOutreachProcess?.queueNameHighPriority ?? customerAutomatedValDefinition?.emailOutreachProcess?.queueNameHighPriority) ?? validationServicesCustomers?.emailOutreachProcess?.queueNameHighPriority
                                                                                                    ) ?? outreachQueueHighPriority;


                bool skipEmailOutreachIfArtifactPresent = validationServices.emailOutreachProcess.skipEmailOutreachIfArtifactPresent ?? false;
                skipEmailOutreachIfArtifactPresent = countryAutomatedValDefinition?.emailOutreachProcess?.skipEmailOutreachIfArtifactPresent ?? skipEmailOutreachIfArtifactPresent;
                skipEmailOutreachIfArtifactPresent = customerAutomatedValDefinition == null ? skipEmailOutreachIfArtifactPresent : (
                                                                                                        (customerCountryAutomatedValDefinition?.emailOutreachProcess?.skipEmailOutreachIfArtifactPresent ?? customerAutomatedValDefinition?.emailOutreachProcess?.skipEmailOutreachIfArtifactPresent) ?? validationServicesCustomers?.emailOutreachProcess?.skipEmailOutreachIfArtifactPresent
                                                                                                    ) ?? skipEmailOutreachIfArtifactPresent;

                int artifactWaitTimeMinutes = validationServices.emailOutreachProcess.artifactWaitTimeMinutes ?? 0;
                artifactWaitTimeMinutes = countryAutomatedValDefinition?.emailOutreachProcess?.artifactWaitTimeMinutes ?? artifactWaitTimeMinutes;
                artifactWaitTimeMinutes = customerAutomatedValDefinition == null ? artifactWaitTimeMinutes : (
                                                                                                        (customerCountryAutomatedValDefinition?.emailOutreachProcess?.artifactWaitTimeMinutes ?? customerAutomatedValDefinition?.emailOutreachProcess?.artifactWaitTimeMinutes) ?? validationServicesCustomers?.emailOutreachProcess?.artifactWaitTimeMinutes
                                                                                                    ) ?? artifactWaitTimeMinutes;

                bool includeCtpOrgMatch = validationServices.includeCtpOrgMatch ?? true;
                includeCtpOrgMatch = countryAutomatedValDefinition?.includeCtpOrgMatch ?? includeCtpOrgMatch;
                includeCtpOrgMatch = customerAutomatedValDefinition == null ? includeCtpOrgMatch : (
                                                                                                        (customerCountryAutomatedValDefinition?.includeCtpOrgMatch ?? customerAutomatedValDefinition?.includeCtpOrgMatch) ?? validationServicesCustomers?.includeCtpOrgMatch
                                                                                                    ) ?? includeCtpOrgMatch;


                bool closoeAbandonedRequests = validationServices.closeAbandonedRequests ?? false;
                closoeAbandonedRequests = countryAutomatedValDefinition?.closeAbandonedRequests ?? closoeAbandonedRequests;
                closoeAbandonedRequests = customerAutomatedValDefinition == null ? closoeAbandonedRequests : (
                                                                                                        (customerCountryAutomatedValDefinition?.closeAbandonedRequests ?? customerAutomatedValDefinition?.closeAbandonedRequests) ?? validationServicesCustomers?.closeAbandonedRequests
                                                                                                    ) ?? closoeAbandonedRequests;


                int numberOfDaysInQueueToCloseAbandoned = validationServices.numberOfDaysInQueueToCloseAbandoned ?? 30;
                numberOfDaysInQueueToCloseAbandoned = countryAutomatedValDefinition?.numberOfDaysInQueueToCloseAbandoned ?? numberOfDaysInQueueToCloseAbandoned;
                numberOfDaysInQueueToCloseAbandoned = customerAutomatedValDefinition == null ? numberOfDaysInQueueToCloseAbandoned : (
                                                                                                        (customerCountryAutomatedValDefinition?.numberOfDaysInQueueToCloseAbandoned ?? customerAutomatedValDefinition?.numberOfDaysInQueueToCloseAbandoned) ?? validationServicesCustomers?.numberOfDaysInQueueToCloseAbandoned
                                                                                                    ) ?? numberOfDaysInQueueToCloseAbandoned;   

                
                bool createAccountOnValReqCreation = validationServices.createAccountOnValReqCreation ?? false;
                createAccountOnValReqCreation = countryAutomatedValDefinition?.createAccountOnValReqCreation ?? createAccountOnValReqCreation;
                createAccountOnValReqCreation = customerAutomatedValDefinition == null ? createAccountOnValReqCreation : (
                                                                                                        (customerCountryAutomatedValDefinition?.createAccountOnValReqCreation ?? customerAutomatedValDefinition?.createAccountOnValReqCreation) ?? validationServicesCustomers?.createAccountOnValReqCreation
                                                                                                    ) ?? createAccountOnValReqCreation;


                string clientName = validationServices.clientName ?? "";
                clientName = countryAutomatedValDefinition?.clientName ?? clientName;
                clientName = customerAutomatedValDefinition?.name ?? clientName;


                List<dynamic> targetedValidations = ((JArray)customerAutomatedValDefinition?.targetedValidations)?.ToList<dynamic>();


                List<string> nonC3Designations = ((JArray)(
                                                             ((JArray)validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                           Where(country => ((string)country.country)?.ToLower() == "us"
                                                                                                                           )?.FirstOrDefault()?.orgDesignations
                                                            ))?.ToList<dynamic>()?.Select(item => (string)item)?.ToList();


                Entity qualCodeEntity = validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid") == null ? null :
                                                            DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid").Id, new ColumnSet(true));

                string valReqQualCode = qualCodeEntity?.GetAttributeValue<string>("new_qualcode");

                dynamic nonC3AutomatedValDefinition = (
                                                            ((JArray)validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                            Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                                            );
                bool nonC3Validation = false;
                if (
                        customerAutomatedValDefinition == null
                        && nonC3Designations.Count > 0 && !string.IsNullOrEmpty(valReqQualCode)
                        && nonC3Designations.Exists(designation => designation.ToLower() == valReqQualCode.ToLower())
                        && requestingClientName == ((string)nonC3AutomatedValDefinition?.clientName ?? "TechSoup Global")
                    )
                {
                    nonC3Validation = true;

                    autoCloseEnabled = validationServices.autoCloseEnabled;
                    autoCloseEnabled = nonC3AutomatedValDefinition?.autoCloseEnabled ?? autoCloseEnabled;

                    postAutoCloseQueue = validationServices.postAutoCloseQueue;
                    postAutoCloseQueue = nonC3AutomatedValDefinition?.postAutoCloseQueue ?? postAutoCloseQueue;

                    postNoAutoCloseQueue = validationServices.postNoAutoCloseQueue;
                    postNoAutoCloseQueue = nonC3AutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;

                    duplicateReviewQueue = validationServices.duplicateReviewQueue;
                    duplicateReviewQueue = nonC3AutomatedValDefinition?.duplicateReviewQueue ?? duplicateReviewQueue;

                    fraudReviewEnabled = validationServices.fraudReviewEnabled ?? false;
                    fraudReviewEnabled = nonC3AutomatedValDefinition?.fraudReviewEnabled ?? fraudReviewEnabled;

                    fraudReviewQueue = validationServices.fraudReviewQueue;
                    fraudReviewQueue = nonC3AutomatedValDefinition?.fraudReviewQueue ?? fraudReviewQueue;

                    autoOrgOutreachEnabled = validationServices.emailOutreachProcess.autoOrgOutreachEnabled ?? false;
                    autoOrgOutreachEnabled = nonC3AutomatedValDefinition?.emailOutreachProcess?.autoOrgOutreachEnabled ?? autoOrgOutreachEnabled;

                    outreachSenderMailboxQueue = validationServices.emailOutreachProcess.senderMailboxQueue;
                    outreachSenderMailboxQueue = nonC3AutomatedValDefinition?.emailOutreachProcess?.senderMailboxQueue ?? outreachSenderMailboxQueue;

                    outreachEmailTemplate = validationServices.emailOutreachProcess.emailTemplate;
                    outreachEmailTemplate = nonC3AutomatedValDefinition?.emailOutreachProcess?.emailTemplate ?? outreachEmailTemplate;

                    outreachQueueName = validationServices.emailOutreachProcess.queueName;
                    outreachQueueName = nonC3AutomatedValDefinition?.emailOutreachProcess?.queueName ?? outreachQueueName;

                    outreachQueueHighPriority = validationServices.emailOutreachProcess.queueNameHighPriority;
                    outreachQueueHighPriority = nonC3AutomatedValDefinition?.emailOutreachProcess?.queueNameHighPriority ?? outreachQueueHighPriority;

                    skipEmailOutreachIfArtifactPresent = false;

                    artifactWaitTimeMinutes = 0;
                    
                    clientName = nonC3AutomatedValDefinition?.clientName ?? "TechSoup Global";
                }

                IDictionary<string, Object> automatedValDefinitionParam = new ExpandoObject() as IDictionary<string, Object>;

                automatedValDefinitionParam["validationServicesCustomers"] = validationServicesCustomers;
                automatedValDefinitionParam["customerAutomatedValDefinition"] = customerAutomatedValDefinition;
                automatedValDefinitionParam["customerCountryAutomatedValDefinition"] = customerCountryAutomatedValDefinition;
                automatedValDefinitionParam["maxDispositionRetrievalAttempts"] = maxDispositionRetrievalAttempts;
                automatedValDefinitionParam["initialQueue"] = initialQueue;
                automatedValDefinitionParam["autoCloseEnabled"] = autoCloseEnabled;
                automatedValDefinitionParam["postAutoCloseQueue"] = postAutoCloseQueue;
                automatedValDefinitionParam["postNoAutoCloseQueue"] = postNoAutoCloseQueue;
                automatedValDefinitionParam["duplicateReviewQueue"] = duplicateReviewQueue;
                automatedValDefinitionParam["fraudReviewEnabled"] = fraudReviewEnabled;
                automatedValDefinitionParam["fraudReviewQueue"] = fraudReviewQueue;
                automatedValDefinitionParam["autoOrgOutreachEnabled"] = autoOrgOutreachEnabled;
                automatedValDefinitionParam["outreachSenderMailboxQueue"] = outreachSenderMailboxQueue;
                automatedValDefinitionParam["outreachEmailTemplate"] = outreachEmailTemplate;
                automatedValDefinitionParam["outreachQueueName"] = outreachQueueName;
                automatedValDefinitionParam["outreachQueueHighPriority"] = outreachQueueHighPriority;
                automatedValDefinitionParam["targetedValidations"] = targetedValidations;
                automatedValDefinitionParam["skipEmailOutreachIfArtifactPresent"] = skipEmailOutreachIfArtifactPresent;
                automatedValDefinitionParam["artifactWaitTimeMinutes"] = artifactWaitTimeMinutes;
                automatedValDefinitionParam["includeCtpOrgMatch"] = includeCtpOrgMatch;
                automatedValDefinitionParam["closeAbandonedRequests"] = closoeAbandonedRequests;
                automatedValDefinitionParam["numberOfDaysInQueueToCloseAbandoned"] = numberOfDaysInQueueToCloseAbandoned;
                automatedValDefinitionParam["createAccountOnValReqCreation"] = createAccountOnValReqCreation;
                automatedValDefinitionParam["clientName"] = clientName;


                automatedValDefinitionParam["nonC3Designations"] = nonC3Designations;
                automatedValDefinitionParam["nonC3AutomatedValDefinition"] = nonC3AutomatedValDefinition;
                automatedValDefinitionParam["isNonC3Validation"] = nonC3Validation;

                ConfigParams = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(automatedValDefinitionParam));
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in getValidationConfigParameters(). Exception message:{Environment.NewLine}{e.Message}"
                    + $"{Environment.NewLine}validationReqTransactionId: {validationReqTransactionId}"
                    );
                
            }
        }
        public static async Task determineAction(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, Entity queueItem, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                
                #region AutomatedValDefinition
                bool autoCloseEnabled = ConfigParams.autoCloseEnabled;
                string initialQueue = ConfigParams.initialQueue;
                string postAutoCloseQueue = ConfigParams.postAutoCloseQueue;
                string postNoAutoCloseQueue = ConfigParams.postNoAutoCloseQueue; 
                string outreachQueueName = ConfigParams.outreachQueueName;

                bool closoeAbandonedRequests = ConfigParams.closeAbandonedRequests;
                int numberOfDaysInQueueToCloseAbandoned = ConfigParams.numberOfDaysInQueueToCloseAbandoned;
                #endregion


                #region Parameters
                IDictionary<string, Object> valReqOrgAccountObj = await ValidationServicesHelper.getValidationRequestOrgAccountInfo(validationReqTransactionId);
                #endregion



                #region InitialQueue
                if (queueName == initialQueue)
                {
                    #region CTPProvisioning
                    int? tsCaseStatus = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_casestatus")?.Value; //OQ - Validated - Awaiting CTPOrgId Provisioning
                    string ctpOrgId = ValidationServicesHelper.getParentCaseAccountCtpOrgId(validationRequestCase.Id);

                    DynamicsInterface.writeToLog("caseStatus: " + validationRequestCase.FormattedValues["ts_casestatus"]
                                                                   + Environment.NewLine + "ctpOrgId: " + ctpOrgId
                                                                   );
                    if (tsCaseStatus == 104701 || tsCaseStatus == 104704) //OQ - Validated - Awaiting CTPOrgId Provisioning; 104704 - OQ - Disqualified - Awaiting CTPOrgId Provisioning
                    {
                        if (!string.IsNullOrEmpty(ctpOrgId))
                        {
                            //bool success = ValidationServicesHelper.updateValidationRequestCaseStatus(validationRequestCase, 102056, validationReqTransactionId);

                            EntityReference parentCaseRef = validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid");

                            if (parentCaseRef == null)
                            {
                                DynamicsInterface.writeToLog("Validation Request Transaction Id: " + validationReqTransactionId + " does not have a parent case associated. Cannot update status");
                                DynamicsProcessesHelper.processSystemNote("Error Updating Status", $"Validation Request does not have a parent case associated.", new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                                return;
                            }

                            Entity qualCase = await DynamicsInterface.DataverseClient.RetrieveAsync(parentCaseRef.LogicalName, parentCaseRef.Id, new ColumnSet("ts_casestatus"));

                            validationRequestCase["ts_casestatus"] = qualCase.GetAttributeValue<OptionSetValue>("ts_casestatus");
                            await DynamicsInterface.DataverseClient.UpdateAsync(validationRequestCase);
                            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseQueue);

                            await ValidationServicesHelper.sendValidationRequestStatusToRequestor(validationReqTransactionId, validationRequestCase, account);




                            IDictionary<string, Object> ctpOrgEntity = await ValidationServicesHelper.getCTPOrgObjects(ctpOrgId);


                            if (ctpOrgEntity == null)
                                DynamicsInterface.writeToLog($"At determineAction(). After verifying receipt of the ctpOrgId and setting the val request to qualified - there was a problem getting the ctpOrgEntity");


                            bool existsTransactionIdExternalReference = ((List<dynamic>)ctpOrgEntity["transactionIdExternalReferences"]).Any(tranRef => (string)tranRef.typeValue == validationReqTransactionId);

                            if (!existsTransactionIdExternalReference)
                                await ProcessHelper.addObject_001ToCtpOrg("ExternalReferenceObject_001", "transaction", validationReqTransactionId, ctpOrgEntity, "nil");


                            await ProcessHelper.addAgentToCtpOrgObjects(validationRequestCase, ctpOrgEntity);
                        }
                        return;
                    }
                    #endregion                



                    #region Finding Existing Accounts
                    IDictionary<string, System.Object> accountMatchesResponse = await ValidationServicesHelper.findValidationRequestAccountMatches(validationRequestCase, account, validationReqTransactionId);
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



                    #region Initiate Nonprofit Verification Agent
                    await ValidationServicesHelper.initiateNonprofitVerificationAgent(validationRequestCase.Id.ToString());
                    #endregion




                    #region Fraud Check
                    bool potentialFraud = await validationServicesEvaluateForFraud(validationRequestCase, account, validationReqTransactionId);

                    if (potentialFraud)
                        return;
                    #endregion



                    #region AutoClose
                    if (autoCloseEnabled)
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
                            await initiateOrgIncorporation(validationRequestCase, account, validationReqTransactionId, queueName, true);
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
                else if (queueName == outreachQueueName)
                {
                    DateTime enteredQueue = queueItem.GetAttributeValue<DateTime?>("enteredon") ?? DateTime.MaxValue;

                    TimeSpan timeSpanInQueue = DateTime.UtcNow - enteredQueue;

                    if (closoeAbandonedRequests && timeSpanInQueue.TotalDays >= numberOfDaysInQueueToCloseAbandoned)
                    {
                        validationRequestCase["ts_casestatus"] = new OptionSetValue(102059);//OQ - Abandoned
                        await DynamicsInterface.DataverseClient.UpdateAsync(validationRequestCase);
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseQueue);
                        return;
                    }

                }

            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in determineAction(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}validationReqTransactionId: {validationReqTransactionId}"
                                                );


                
            }
            #endregion
        }



        public static void hasOrgAccount_RouteOut(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest
                                                                                                                                                                , IDictionary<string, Object> valReqOrgAccountObj)
        {
            #region AutomatedValDefinition
            string postAutoCloseQueue = ConfigParams.postAutoCloseQueue;
            string postNoAutoCloseQueue = ConfigParams.postNoAutoCloseQueue;
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
                List<dynamic> autoCloseCustomeRules = ((JArray)ConfigJson.config.autoCloseCustomRules)?.ToList<dynamic>();
                List<dynamic> queueRoutingRules = ((JArray)ConfigJson.queueRoutingRules)?.ToList<dynamic>();

                List<dynamic> currentQueueRoutingRules = queueRoutingRules?.Where(rule => rule.routeFromQueue == queueName)?.ToList();


                string postNoAutoCloseQueue = ConfigParams.postNoAutoCloseQueue;
                #endregion

                //
                #region EmailOutreach
                IDictionary<string, System.Object> outreachCriteriaResponse = evaluateValidationRequestCriteriaEmailOutreach(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
                if (outreachCriteriaResponse.ContainsKey("emailOutreachCriteria") && (bool)outreachCriteriaResponse["emailOutreachCriteria"])
                {
                    processValidationRequestEmailOutreach(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
                    return;
                }
                else
                {
                    if (outreachCriteriaResponse.ContainsKey("validationProcessAction") && (string)outreachCriteriaResponse["validationProcessAction"] == "terminate")
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


        public static IDictionary<string, System.Object> evaluateValidationRequestCriteriaEmailOutreach(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            IDictionary<string, System.Object> response = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;
            response["emailOutreachCriteria"] = false;
            try
            {
                #region AutomatedValDefinition

                bool autoOrgEmailOutreachEnabled = ConfigParams.autoOrgOutreachEnabled;
                bool skipEmailOutreachIfArtifactPresent = ConfigParams.skipEmailOutreachIfArtifactPresent;
                int artifactWaitTimeMinutes = ConfigParams.artifactWaitTimeMinutes;

                if (!autoOrgEmailOutreachEnabled)
                    return response;
                #endregion

                if (skipEmailOutreachIfArtifactPresent)
                {
                    QueryExpression queryEntityAttachment = new QueryExpression("msdyn_entityattachment");
                    queryEntityAttachment.ColumnSet = new ColumnSet(true);//"msdyn_name",ts_referencevalue
                    queryEntityAttachment.Criteria.AddCondition("msdyn_relatedentity", ConditionOperator.Equal, validationRequestCase.Id);
                    EntityCollection entityAttachmentCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntityAttachment);

                    if (entityAttachmentCollection.Entities.Count > 0
                        && entityAttachmentCollection.Entities.Any(attach => !string.IsNullOrEmpty(attach.GetAttributeValue<string>("ts_referencevalue"))
                                                                    )
                        )
                    {
                        DynamicsProcessesHelper.removeSystemNote(" --- Waiting For Potential Artifacts --- ", validationRequestCase.ToEntityReference());

                        string artifactReceivedNote = $"Artifacts have been received for Validation Request Case. Skipping Email Outreach process";
                        DynamicsProcessesHelper.processSystemNote("Artifacts Received", artifactReceivedNote, validationRequestCase.ToEntityReference());

                        return response;
                    }
                    else
                    {
                        DateTime modifiedOnUtc = validationRequestCase.GetAttributeValue<DateTime>("modifiedon");

                        TimeSpan timeSinceLastUpdate = DateTime.UtcNow - modifiedOnUtc;

                        if (timeSinceLastUpdate.TotalMinutes <= artifactWaitTimeMinutes)
                        {
                            DynamicsProcessesHelper.processSystemNote(" --- Waiting For Potential Artifacts --- ", "Waiting to see if artifacts are provided", validationRequestCase.ToEntityReference());
                            response["validationProcessAction"] = "terminate";
                            return response;
                        }
                        else
                        {
                            DynamicsProcessesHelper.removeSystemNote(" --- Waiting For Potential Artifacts --- ", validationRequestCase.ToEntityReference());
                        }
                    }
                }
                


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
                    response["emailOutreachCriteria"] = true;
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

            return response;
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

        public static async Task initiateOrgIncorporation(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, bool setToQualified = true)
        {
            try
            {
                #region AutomatedValDefinition & Parameters
                string postAutoCloseQueue = ConfigParams.postAutoCloseQueue;
                string postNoAutoCloseQueue = ConfigParams.postNoAutoCloseQueue;

                List<dynamic> targetedValidations = ((JArray)ConfigParams.targetedValidations)?.ToList<dynamic>();

                bool agentValidation = targetedValidations == null
                                                                || targetedValidations.Where(item => ((string)item)?.ToLower() == "agent")?.FirstOrDefault() != null;
                #endregion


                #region Create Org Account
                Guid? qualCaseId = validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid")?.Id;
                Entity qualCase = qualCaseId == null ? null : DynamicsInterface.DataverseClient.Retrieve("incident", qualCaseId.Value, new ColumnSet(true));
                Guid? orgAccountId = qualCase?.GetAttributeValue<EntityReference>("customerid")?.Id;
                Entity orgAccount = orgAccountId == null ? null : DynamicsInterface.DataverseClient.Retrieve("account", orgAccountId.Value, new ColumnSet(true));

                if (orgAccount == null)
                    orgAccount = ValidationServicesHelper.createOrgFromCase(account, validationRequestCase, validationReqTransactionId);
                
                #endregion


                #region CreateQualCase & Contact & Connection
                if (qualCase == null)
                {
                    qualCase = ValidationServicesHelper.processQualCaseFromValidationRequest(orgAccount, validationRequestCase);

                    if (qualCase != null)
                    {
                        validationRequestCase["parentcaseid"] = qualCase.ToEntityReference();
                        DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    }
                }


                OptionSetValue agentVerificationStatusOption = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_validationrequestagentverification");

                Entity agentContact = ValidationServicesHelper.createAgentContact(validationRequestCase, account, validationReqTransactionId, queueName);

                if (agentContact != null)
                    ValidationServicesHelper.connectAgentToAccount(orgAccount.Id, agentContact.Id, agentVerificationStatusOption, validationReqTransactionId);


                if (orgAccount == null 
                    || (agentContact == null && agentValidation)
                    )
                {
                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);

                    return;
                }
                #endregion

                if (setToQualified)
                {
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





                    IDictionary<string, Object> ctpOrgEntity = await ValidationServicesHelper.getCTPOrgObjects(ctpOrgId);


                    if (ctpOrgEntity == null)
                        DynamicsInterface.writeToLog($"At initiateOrgIncorporation(). There was a problem getting the ctpOrgEntity");


                    bool existsTransactionIdExternalReference = ((List<dynamic>)ctpOrgEntity["transactionIdExternalReferences"]).Any(tranRef => (string)tranRef.typeValue == validationReqTransactionId);

                    if (!existsTransactionIdExternalReference)
                        await ProcessHelper.addObject_001ToCtpOrg("ExternalReferenceObject_001", "transaction", validationReqTransactionId, ctpOrgEntity, "nil");


                    await ProcessHelper.addAgentToCtpOrgObjects(validationRequestCase, ctpOrgEntity);
                    #endregion
                }
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in initiateOrgIncorporation(). Exception message:{Environment.NewLine}{e.Message}"
                                                        + $"{Environment.NewLine}validationReqTransactionId: {validationReqTransactionId}"
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
                string templateName = ConfigParams.outreachEmailTemplate;
                string senderMailboxQueue = ConfigParams.outreachSenderMailboxQueue;
                string outreachQueueName = ConfigParams.outreachQueueName;
                string outreachQueueHighPriority = ConfigParams.outreachQueueHighPriority;
                

                Entity template = DynamicsProcessesHelper.getTemplateEntity(templateName);
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
                
                EntityCollection fromParties = new EntityCollection();

                Guid? queueId = ValidationServicesHelper.getMailBoxQueueId(senderMailboxQueue);

                if (queueId == null)
                {
                    DynamicsInterface.writeToLog($"At processValidationRequestEmailOutreach(). No maibox queue found with name: {senderMailboxQueue}, a default mailbox queue was not found");
                    return;
                }


                Entity fromQueue = new Entity("activityparty");
                fromQueue["partyid"] = new EntityReference("queue", queueId.Value);
                fromParties.Entities.Add(fromQueue);

                EntityCollection toParties = new EntityCollection();

                string validationRequestEmail = validationRequestCase.GetAttributeValue<string>("ts_validationrequestemail");
                Entity toparty = new Entity("activityparty");
                toparty["addressused"] = DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] == "prod" ? validationRequestEmail : "test@example.com";
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
        public static async Task getCustomerHasRespondedEmailOutreach()
        {
            try
            {
                dynamic validationServices = ConfigJson.validationServices;
                dynamic validationServicesCustomers = validationServices.validationServicesCustomers;

                string validationServicesOutreachQueue = validationServices.emailOutreachProcess.queueName;
                string valServicesCustomersOutreachQueue = validationServicesCustomers?.emailOutreachProcess?.queueName ?? "NoQueueName";

                string fetchExpressionQuery = @"
                        <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""5000"" no-lock=""false"">
	                        <entity name=""queueitem"">
		                        <attribute name=""statecode""/>
		                        <attribute name=""title""/>
		                        <attribute name=""enteredon""/>
		                        <attribute name=""objecttypecode""/>
		                        <attribute name=""objectid""/>
		                        <attribute name=""queueid""/>
		                        <attribute name=""workerid""/>
		                        <order attribute=""enteredon"" descending=""true""/>
		                        <attribute name=""queueitemid""/>
		                        <link-entity alias=""inc"" name=""incident"" to=""objectid"" from=""incidentid"" link-type=""inner"">
			                        <attribute name=""casetypecode""/>
			                        <attribute name=""customerid""/>
			                        <attribute name=""ts_tsorderid""/>
			                        <attribute name=""ts_casestatus""/>
                                    <attribute name=""ts_validationrequesttransactionid""/>
                                    <filter type=""and"">
                                        <condition attribute=""ts_casestatus"" operator=""eq"" value=""104699""/>
                                    </filter>
			                        <link-entity name=""account"" alias=""aa"" link-type=""inner"" from=""accountid"" to=""customerid"">
				                        <attribute name=""accountnumber""/>
			                        </link-entity>
		                        </link-entity>
		                        <link-entity name=""queue"" alias=""qu"" link-type=""inner"" from=""queueid"" to=""queueid"">
                                    <attribute name=""name""/>
			                        <filter type=""and"">
				                        <condition attribute=""name"" operator=""in"">
                                            <value>" + validationServicesOutreachQueue + @"</value>
                                            <value>" + valServicesCustomersOutreachQueue + @"</value>
                                        </condition>
			                        </filter>
		                        </link-entity>
	                        </entity>
                        </fetch>";

                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery);
                string newfetchxml = fetchXmlDoc.ToString();

                EntityCollection queueItemCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(new FetchExpression(newfetchxml));

                foreach (Entity queueItem in queueItemCollection.Entities)
                {
                    try
                    {
                        string transactionId = (string)queueItem.GetAttributeValue<AliasedValue>("inc.ts_validationrequesttransactionid")?.Value ?? "";

                        EntityReference queueItemObjRef = queueItem.GetAttributeValue<EntityReference>("objectid");
                        if (queueItemObjRef == null || queueItemObjRef.LogicalName != "incident")
                        {
                            await DynamicsInterface.DataverseClient.DeleteAsync(queueItem.LogicalName, queueItem.Id);
                            continue;
                        }

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

                        Entity validationRequestCase = await DynamicsInterface.DataverseClient.RetrieveAsync("incident", caseId, new ColumnSet(true));

                        int caseCategory = validationRequestCase.GetAttributeValue<OptionSetValue>("casetypecode")?.Value ?? -1;

                        if (caseCategory != 5)
                            await DynamicsInterface.DataverseClient.DeleteAsync(queueItem.LogicalName, queueItem.Id);

                        EntityReference customerIdRef = validationRequestCase.GetAttributeValue<EntityReference>("customerid");

                        Entity validationRequestor = await DynamicsInterface.DataverseClient.RetrieveAsync("account", customerIdRef.Id, new ColumnSet(true));


                        string validationReqTransactionId = validationRequestCase.GetAttributeValue<string>("ts_validationrequesttransactionid");



                        getValidationConfigParameters(validationRequestCase, validationRequestor, validationReqTransactionId);

                        string postAutoCloseQueue = ConfigParams.postAutoCloseQueue;
                        string postNoAutoCloseQueue = ConfigParams.postNoAutoCloseQueue;

                        DynamicsProcessesHelper.addCaseToQueue(caseId, postNoAutoCloseQueue);




                        #region Release Semaphore
                        await ParallelProcessesHelper.releaseSemaphoreAsync(resourceId);
                        #endregion
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog($"Error in getCustomerHasRespondedEmailOutreach() - ForEach QueueItem. Exception message:{Environment.NewLine}{ex.Message}"
                            + $"{Environment.NewLine}transactionId: {(string)queueItem.GetAttributeValue<AliasedValue>("inc.ts_validationrequesttransactionid")?.Value ?? ""}"
                            );
                    }
                }


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in getCustomerHasRespondedEmailOutreach(). Exception message:{Environment.NewLine}{e.Message}");
            }
        }

        public static async Task setOldCasesInOutreachQueueToAbandoned()
        {
            try
            {
                dynamic validationServices = ConfigJson.validationServices;
                dynamic validationServicesCustomers = validationServices.validationServicesCustomers;

                string validationServicesOutreachQueue = validationServices.emailOutreachProcess.queueName;
                string valServicesCustomersOutreachQueue = validationServicesCustomers?.emailOutreachProcess?.queueName ?? "NoQueueName";

                string validationServicesPostNoAutoCloseQueue =  validationServices.postNoAutoCloseQueue;
                string validationServicesCustomersPostNoAutoCloseQueue = validationServicesCustomers?.postNoAutoCloseQueue ?? "NoQueueName";

                bool closoeAbandonedRequests = validationServices.closeAbandonedRequests ?? false;
                if (!closoeAbandonedRequests)
                    return;

                int numberOfDaysInQueueToCloseAbandoned = validationServices.numberOfDaysInQueueToCloseAbandoned ?? 0;

                DateTime referenceDate = DateTime.UtcNow.AddDays(-1 * numberOfDaysInQueueToCloseAbandoned);
                string referenceDateText = referenceDate.ToString("yyyy-MM-ddTHH:mm:ssZ");

                string fetchExpressionQuery = @"
                        <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""5000"" no-lock=""false"">
	                        <entity name=""queueitem"">
		                        <attribute name=""statecode""/>
		                        <attribute name=""title""/>
		                        <attribute name=""enteredon""/>
		                        <attribute name=""objecttypecode""/>
		                        <attribute name=""objectid""/>
		                        <attribute name=""queueid""/>
		                        <attribute name=""workerid""/>
		                        <order attribute=""enteredon"" descending=""true""/>
		                        <attribute name=""queueitemid""/>
                                <filter type=""and"">
                                    <condition attribute=""enteredon"" operator=""lt"" value=""" + referenceDateText + @"""/>
                                </filter>    
		                        <link-entity alias=""inc"" name=""incident"" to=""objectid"" from=""incidentid"" link-type=""inner"">
			                        <attribute name=""casetypecode""/>
			                        <attribute name=""customerid""/>
			                        <attribute name=""ts_tsorderid""/>
			                        <attribute name=""ts_casestatus""/>
                                    <attribute name=""ts_validationrequesttransactionid""/>                                    
			                        <link-entity name=""account"" alias=""aa"" link-type=""inner"" from=""accountid"" to=""customerid"">
				                        <attribute name=""accountnumber""/>
			                        </link-entity>
		                        </link-entity>
		                        <link-entity name=""queue"" alias=""qu"" link-type=""inner"" from=""queueid"" to=""queueid"">
                                    <attribute name=""name""/>
			                        <filter type=""and"">
				                        <condition attribute=""name"" operator=""in"">
                                            <value>" + validationServicesOutreachQueue + @"</value>
                                            <value>" + valServicesCustomersOutreachQueue + @"</value>
                                            <value>" + validationServicesPostNoAutoCloseQueue + @"</value>
                                            <value>" + validationServicesCustomersPostNoAutoCloseQueue  + @"</value>
                                        </condition>
			                        </filter>
		                        </link-entity>
	                        </entity>
                        </fetch>";

                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery);
                string newfetchxml = fetchXmlDoc.ToString();

                EntityCollection queueItemCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(new FetchExpression(newfetchxml));

                foreach (Entity queueItem in queueItemCollection.Entities)
                {
                    try
                    {
                        string transactionId = (string)queueItem.GetAttributeValue<AliasedValue>("inc.ts_validationrequesttransactionid")?.Value ?? "";

                        EntityReference queueItemObjRef = queueItem.GetAttributeValue<EntityReference>("objectid");
                        if (queueItemObjRef == null || queueItemObjRef.LogicalName != "incident")
                        {
                            await DynamicsInterface.DataverseClient.DeleteAsync(queueItem.LogicalName, queueItem.Id);
                            continue;
                        }

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

                        Entity validationRequestCase = await DynamicsInterface.DataverseClient.RetrieveAsync("incident", caseId, new ColumnSet(true));

                        int caseCategory = validationRequestCase.GetAttributeValue<OptionSetValue>("casetypecode")?.Value ?? -1;

                        if (caseCategory != 5)
                            await DynamicsInterface.DataverseClient.DeleteAsync(queueItem.LogicalName, queueItem.Id);

                        EntityReference customerIdRef = validationRequestCase.GetAttributeValue<EntityReference>("customerid");

                        Entity validationRequestor = await DynamicsInterface.DataverseClient.RetrieveAsync("account", customerIdRef.Id, new ColumnSet(true));


                        string validationReqTransactionId = validationRequestCase.GetAttributeValue<string>("ts_validationrequesttransactionid");



                        //getValidationConfigParameters(validationRequestCase, validationRequestor, validationReqTransactionId);

                        //string postAutoCloseQueue = ConfigParams.postAutoCloseQueue;
                        //string postNoAutoCloseQueue = ConfigParams.postNoAutoCloseQueue;

                        string tsCaseStatusText = validationRequestCase.Contains("ts_casestatus") ? validationRequestCase.FormattedValues["ts_casestatus"] : "";

                        if (tsCaseStatusText.ToLower().Contains("awaiting"))
                        {
                            validationRequestCase["ts_casestatus"] = new OptionSetValue(102059); //OQ - Abandoned
                            await DynamicsInterface.DataverseClient.UpdateAsync(validationRequestCase);
                        }




                        #region Release Semaphore
                        await ParallelProcessesHelper.releaseSemaphoreAsync(resourceId);
                        #endregion
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog($"Error in setOldCasesInOutreachQueueToAbandoned() - ForEach QueueItem. Exception message:{Environment.NewLine}{ex.Message}"
                            + $"{Environment.NewLine}transactionId: {(string)queueItem.GetAttributeValue<AliasedValue>("inc.ts_validationrequesttransactionid")?.Value ?? ""}"
                            );
                    }
                }


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in setOldCasesInOutreachQueueToAbandoned(). Exception message:{Environment.NewLine}{e.Message}");
            }
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


        public static async Task<bool> validationServicesEvaluateForFraud(Entity validationRequestCase, Entity requestingAccount, string validationReqTransactionId)
        {
            var fraudFlags = new List<string>();
            bool isFraudulent = false;
            string fraudReviewQueue = ConfigParams.fraudReviewQueue;
            bool fraudReviewEnabled = ConfigParams.fraudReviewEnabled;

            try
            {
                string name = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalname");
                string website = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite") ?? "";
                string countryCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid") ?? "";

                string address1 = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressline1") ?? "";
                string address2 = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressother") ?? "";
                string city = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscity") ?? "";
                string regionCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressstateregion") ?? "";
                string postalCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresspostalcode") ?? "";

                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentemail") ?? "";



                string whoisDataJson = "";

                if (!string.IsNullOrEmpty(website))
                {

                    #region Website Content Analysis (includes Generic/Social Media Domain Detection)
                    // Perform sophisticated website content analysis using WebsiteAnalysisService
                    // This service includes generic/social media domain detection as part of Phase 1 (Domain Analysis)
                    try
                    {
                        DynamicsInterface.writeToLog($"[analyzeWebsite] Starting comprehensive analysis for {website}");
                        DynamicsInterface.writeToLog($"[analyzeWebsite] Input: orgName='{name}', address1='{address1}', city='{city}', country='{countryCode}'");

                        WebsiteAnalysisResult websiteAnalysisResult = await analyzeWebsite(website, name, address1, address2, city, regionCode, countryCode, validationReqTransactionId);

                        // Phase 1: Generic/Social Media Domain Detection (handled by WebsiteAnalysisService)
                        if (websiteAnalysisResult.IsGenericOrSocialMediaDomain)
                        {
                            fraudFlags.Add($"Website uses a generic/free platform instead of an independently owned domain: {websiteAnalysisResult.DomainType}");
                            isFraudulent = true;
                            DynamicsInterface.writeToLog($"Generic domain detected for {validationReqTransactionId}: {websiteAnalysisResult.DomainType}");
                        }

                        // Phase 2: Accessibility Check
                        if (!websiteAnalysisResult.IsAccessible)
                        {
                            fraudFlags.Add($"Website is not accessible after multiple attempts: {websiteAnalysisResult.AccessError}");
                            isFraudulent = true;
                        }
                        else
                        {
                            // Phase 3: Organization Name Match
                            if (websiteAnalysisResult.OrganizationNameMatchScore < 0.2) // Less than 20% match is suspicious
                            {
                                fraudFlags.Add($"Organization name '{name}' not found on website (match score: {websiteAnalysisResult.OrganizationNameMatchScore:P0})");
                                if (!string.IsNullOrEmpty(websiteAnalysisResult.OrganizationNameMatchDetails))
                                {
                                    DynamicsInterface.writeToLog($"Org name match details: {websiteAnalysisResult.OrganizationNameMatchDetails}");
                                }
                                isFraudulent = true;
                            }

                            // Phase 4: Address Match
                            if (websiteAnalysisResult.AddressMatchScore < 0.2) // Less than 20% address match is suspicious
                            {
                                fraudFlags.Add($"Organization address not found on website (match score: {websiteAnalysisResult.AddressMatchScore:P0})");
                                if (!string.IsNullOrEmpty(websiteAnalysisResult.AddressMatchDetails))
                                {
                                    DynamicsInterface.writeToLog($"Address match details: {websiteAnalysisResult.AddressMatchDetails}");
                                }
                                // Note: Not marking as fraudulent alone - combined with other factors
                            }

                            // Phase 5: Trust Signal Analysis
                            if (websiteAnalysisResult.TrustScore < 0.25) // Less than 25% trust score is concerning
                            {
                                fraudFlags.Add($"Website has low trust score: {websiteAnalysisResult.TrustScore:P0}");
                                isFraudulent = true;
                            }
                            else if (websiteAnalysisResult.TrustSignals?.Count > 0)
                            {
                                DynamicsInterface.writeToLog($"Trust signals detected: {string.Join(", ", websiteAnalysisResult.TrustSignals.Take(5))}");
                            }

                            // Phase 6: Red Flag Detection
                            if (websiteAnalysisResult.RedFlagCount > 0)
                            {
                                foreach (var redFlag in websiteAnalysisResult.RedFlags)
                                {
                                    if (!fraudFlags.Contains(redFlag))
                                    {
                                        fraudFlags.Add($"Red flag: {redFlag}");
                                    }
                                }
                                if (websiteAnalysisResult.RedFlagCount >= 3) // Multiple red flags indicate fraud
                                {
                                    isFraudulent = true;
                                }
                            }

                            // Phase 7: Content Quality Assessment
                            if (!websiteAnalysisResult.HasMeaningfulContent)
                            {
                                fraudFlags.Add("Website lacks meaningful organizational content (no mission statements, programs, or team information found)");
                                isFraudulent = true;
                            }

                            if (websiteAnalysisResult.ContentQualityScore < 0.2) // Very low content quality
                            {
                                fraudFlags.Add($"Website has very low content quality score: {websiteAnalysisResult.ContentQualityScore:P0}");
                                isFraudulent = true;
                            }

                            // Phase 8: Overall Score Assessment
                            if (websiteAnalysisResult.OverallContentScore < 0.3) // Less than 30% overall score is suspicious
                            {
                                fraudFlags.Add($"Website has low overall credibility score: {websiteAnalysisResult.OverallContentScore:P0}");
                                isFraudulent = true;
                            }

                            // Add any additional analysis flags from the service
                            foreach (var flag in websiteAnalysisResult.AnalysisFlags)
                            {
                                if (!fraudFlags.Contains(flag) && !fraudFlags.Any(f => f.Contains(flag)))
                                {
                                    fraudFlags.Add($"Analysis: {flag}");
                                }
                            }
                        }

                        // Comprehensive logging
                        DynamicsInterface.writeToLog($"Website analysis for {website}: " +
                                                        $"Accessible={websiteAnalysisResult.IsAccessible}, " +
                                                        $"Generic={websiteAnalysisResult.IsGenericOrSocialMediaDomain}, " +
                                                        $"OrgNameScore={websiteAnalysisResult.OrganizationNameMatchScore:P0}, " +
                                                        $"AddressScore={websiteAnalysisResult.AddressMatchScore:P0}, " +
                                                        $"TrustScore={websiteAnalysisResult.TrustScore:P0}, " +
                                                        $"QualityScore={websiteAnalysisResult.ContentQualityScore:P0}, " +
                                                        $"RedFlags={websiteAnalysisResult.RedFlagCount}, " +
                                                        $"OverallScore={websiteAnalysisResult.OverallContentScore:P0}");

                        // Create comprehensive system note
                        var noteBuilder = new StringBuilder();
                        noteBuilder.AppendLine($"Website Analysis for {website}");
                        noteBuilder.AppendLine();
                        noteBuilder.AppendLine($"=== Accessibility ===");
                        noteBuilder.AppendLine($"Accessible: {websiteAnalysisResult.IsAccessible}");
                        if (!websiteAnalysisResult.IsAccessible)
                            noteBuilder.AppendLine($"Error: {websiteAnalysisResult.AccessError}");
                        noteBuilder.AppendLine();
                        noteBuilder.AppendLine($"=== Domain Analysis ===");
                        noteBuilder.AppendLine($"Generic/Social Media Domain: {websiteAnalysisResult.IsGenericOrSocialMediaDomain}");
                        if (websiteAnalysisResult.IsGenericOrSocialMediaDomain)
                            noteBuilder.AppendLine($"Domain Type: {websiteAnalysisResult.DomainType}");
                        noteBuilder.AppendLine();
                        noteBuilder.AppendLine($"=== Identity Verification ===");
                        noteBuilder.AppendLine($"Organization Name Match: {websiteAnalysisResult.OrganizationNameMatchScore:P0}");
                        noteBuilder.AppendLine($"Address Match: {websiteAnalysisResult.AddressMatchScore:P0}");
                        noteBuilder.AppendLine();
                        noteBuilder.AppendLine($"=== Trust & Quality ===");
                        noteBuilder.AppendLine($"Trust Score: {websiteAnalysisResult.TrustScore:P0}");
                        noteBuilder.AppendLine($"Content Quality: {websiteAnalysisResult.ContentQualityScore:P0}");
                        noteBuilder.AppendLine($"Structural Score: {websiteAnalysisResult.StructuralScore:P0}");
                        noteBuilder.AppendLine($"Meaningful Content: {websiteAnalysisResult.HasMeaningfulContent}");
                        noteBuilder.AppendLine();
                        noteBuilder.AppendLine($"=== Red Flags ({websiteAnalysisResult.RedFlagCount}) ===");
                        if (websiteAnalysisResult.RedFlags?.Count > 0)
                            foreach (var rf in websiteAnalysisResult.RedFlags.Take(10))
                                noteBuilder.AppendLine($"• {rf}");                        
                        else
                            noteBuilder.AppendLine("None detected");                        
                        noteBuilder.AppendLine();
                        noteBuilder.AppendLine($"=== Trust Signals ({websiteAnalysisResult.TrustSignals?.Count ?? 0}) ===");
                        if (websiteAnalysisResult.TrustSignals?.Count > 0)
                            foreach (var ts in websiteAnalysisResult.TrustSignals.Take(10))
                                noteBuilder.AppendLine($"✓ {ts}");
                        else
                            noteBuilder.AppendLine("None detected");
                        noteBuilder.AppendLine();
                        noteBuilder.AppendLine($"=== Overall ===");
                        noteBuilder.AppendLine($"Overall Score: {websiteAnalysisResult.OverallContentScore:P0}");
                        if (!string.IsNullOrEmpty(websiteAnalysisResult.OverallAssessment))
                            noteBuilder.AppendLine($"Assessment: {websiteAnalysisResult.OverallAssessment}");

                        ValidationServicesHelper.processSystemNote("Website Analysis", noteBuilder.ToString()
                                                                                                                , new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));
                    }
                    catch (Exception websiteEx)
                    {
                        DynamicsInterface.writeToLog($"Error in website content analysis for {validationReqTransactionId}: {websiteEx.Message}\n{websiteEx.StackTrace}");
                        // Don't fail the entire fraud check due to website analysis error
                    }
                    #endregion



                    try
                    {
                        EnhancedDomainValidator enhancedDomainValidator = new EnhancedDomainValidator();
                        EnhancedDomainValidationResult enhancedResult = await enhancedDomainValidator.ValidateDomainAsync(website);


                        if (!enhancedResult.IsValid)
                        {
                            fraudFlags.Add("Website is not valid");
                            isFraudulent = true;
                        }

                        if (enhancedResult != null && enhancedResult.WhoisData != null)
                        {
                            WhoisResult whoisData = enhancedResult.WhoisData;
                            whoisDataJson = JsonConvert.SerializeObject(whoisData, Newtonsoft.Json.Formatting.Indented);

                            if (whoisData.CreatedDate != null && whoisData.IsRecentlyRegistered)
                            {
                                fraudFlags.Add($"Domain was recently registered ({whoisData.DomainAgeInDays} days ago) - potential indicator of fraudulent activity");
                                isFraudulent = true;
                            }


                            // Compare registrar phone country against the expected country code (case insensitive)
                            string registrarPhoneCountry = whoisData.Registrar?.GetPhoneCountryCode();
                            if (!string.IsNullOrEmpty(registrarPhoneCountry) &&
                                !string.Equals(registrarPhoneCountry, countryCode, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(registrarPhoneCountry, countryCode?.Replace("GB", "UK")?.Replace("gb", "uk"), StringComparison.OrdinalIgnoreCase))
                            {
                                fraudFlags.Add($"Registrar phone indicates location outside expected country ({countryCode}): {whoisData.Registrar.Name} (Phone country: {registrarPhoneCountry})");
                                isFraudulent = true;
                            }


                            if (enhancedResult.WhoisData.IpAddresses.Count > 0)
                            {
                                bool hasExpectedCountryIP = false;
                                bool isSuspiciousIPFound = false;
                                var ipCountryDetails = new List<string>();
                                foreach (string ipAddress in enhancedResult.WhoisData.IpAddresses)
                                {
                                    try
                                    {
                                        IPAddressValidationResult ipValidation = await NetworkValidationService.ValidateIPAddressAsync(ipAddress);
                                        if (ipValidation != null)
                                        {
                                            ipCountryDetails.Add($"{ipAddress} ({ipValidation.CountryCode})");
                                            // Check if IP is in expected country (case insensitive)
                                            if (string.Equals(ipValidation.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(ipValidation.CountryCode, countryCode?.Replace("GB", "UK")?.Replace("gb", "uk"), StringComparison.OrdinalIgnoreCase))
                                                hasExpectedCountryIP = true;

                                            if (ipValidation.IsSuspicious)
                                                isSuspiciousIPFound = true;
                                        }
                                    }
                                    catch (Exception ipEx)
                                    {
                                        DynamicsInterface.writeToLog($"Error validating WHOIS IP {ipAddress} for {validationReqTransactionId}: {ipEx.Message}");
                                    }
                                }

                                if (enhancedResult.WhoisData.IpAddresses.Count > 0 && !hasExpectedCountryIP && countryCode.ToLower() == "us")
                                {
                                    fraudFlags.Add($"Domain registration data has no IP addresses located in the expected country ({countryCode}). IPs found: {string.Join(", ", ipCountryDetails)}");
                                    isFraudulent = true;
                                    DynamicsInterface.writeToLog($"Domain registration data has no IP addresses located in expected country ({countryCode}) for {validationReqTransactionId}");
                                }
                            }
                            else
                            {
                                //fraudFlags.Add("No IP addresses found for domain");
                                //isFraudulent = true;
                            }

                            if (enhancedResult.DnsData != null && enhancedResult.DnsData.ARecords.Count > 0)
                            {
                                bool hasExpectedCountryIP = false;
                                List<string> ipCountries = new List<string>();
                                bool isSuspiciousIPFound = false;

                                foreach (var ipAddress in enhancedResult.DnsData.ARecords)
                                {
                                    try
                                    {
                                        var ipValidation = await NetworkValidationService.ValidateIPAddressAsync(ipAddress);
                                        if (ipValidation != null)
                                        {
                                            ipCountries.Add($"{ipAddress} ({ipValidation.Country})");

                                            // Check if IP is in expected country (case insensitive)
                                            if (string.Equals(ipValidation.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(ipValidation.CountryCode, countryCode?.Replace("GB", "UK")?.Replace("gb", "uk"), StringComparison.OrdinalIgnoreCase))
                                                hasExpectedCountryIP = true;

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

                                if (!hasExpectedCountryIP && ipCountries.Count > 0
                                     && countryCode.ToLower() == "us"
                                    )
                                {
                                    fraudFlags.Add($"Domain has no IP addresses located in the expected country ({countryCode}). All IPs are outside {countryCode}: {string.Join(", ", ipCountries)}");
                                    isFraudulent = true;

                                    DynamicsInterface.writeToLog($"IP addresses outside expected country ({countryCode}) detected for {validationReqTransactionId}: {string.Join(", ", ipCountries)}");
                                }
                                else if (ipCountries.Count > 0)
                                {
                                    DynamicsInterface.writeToLog($"IP geolocation check for {validationReqTransactionId}: {string.Join(", ", ipCountries)} - Expected country ({countryCode}) IP found: {hasExpectedCountryIP}");
                                }
                            }


                            if (whoisData.Contacts != null)
                            {
                                List<string> countries = whoisData.Contacts.GetAllCountries();
                                // Check for expected country contact (case insensitive, with common variations)
                                bool hasExpectedCountryContact = countries.Any(c =>
                                                                                    string.Equals(c, countryCode, StringComparison.OrdinalIgnoreCase) ||
                                                                                    string.Equals(c, countryCode?.Replace("GB", "UK")?.Replace("gb", "uk"), StringComparison.OrdinalIgnoreCase) ||
                                                                                    IsCountryMatch(c, countryCode)
                                                                                );

                                if (!hasExpectedCountryContact && countries.Any())
                                {
                                    fraudFlags.Add($"Domain registration has no contacts in expected country ({countryCode}). Countries found: {string.Join(", ", countries)}");
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
                                !string.Equals(domainValResult.DomainRegistrationCountryCode, countryCode, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(domainValResult.DomainRegistrationCountryCode, countryCode?.Replace("GB", "UK")?.Replace("gb", "uk"), StringComparison.OrdinalIgnoreCase))
                            {
                                fraudFlags.Add($"Domain registration is not in the expected country ({countryCode}): {domainValResult.DomainRegistrationCountry} ({domainValResult.DomainRegistrationCountryCode})");
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

                            // Check if registration IP is in expected country (case insensitive)
                            bool isInExpectedCountry = ipValidation != null && 
                                (string.Equals(ipValidation.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(ipValidation.CountryCode, countryCode?.Replace("GB", "UK")?.Replace("gb", "uk"), StringComparison.OrdinalIgnoreCase));

                            if (ipValidation != null && ipValidation.IsSuspicious && !isInExpectedCountry)
                            {
                                fraudFlags.Add($"IP address used during registration, {userRegistrationIP}, is not in the expected country ({countryCode}). Country of IP Address: {ipValidation.Country} (country code: {ipValidation.CountryCode})");
                                isFraudulent = true;

                                DynamicsInterface.writeToLog($"Registration IP outside expected country ({countryCode}) detected for {validationReqTransactionId}: {userRegistrationIP} -> {ipValidation.Country}");
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
                    if (!fraudReviewEnabled)
                        return false;

                    string noteDesc = "Fraud analysis findings:\n\n" + string.Join("\n", fraudFlags.Select((flag, index) => $"{index + 1}. {flag}"));

                    noteDesc += $"\n\n\nWHOIS Data:\n{whoisDataJson}";
                  

                    ValidationServicesHelper.processSystemNote("-- Potential Fraud --", noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                    DynamicsInterface.writeToLog($"Case {validationReqTransactionId} flagged as potential fraud with {fraudFlags.Count} violations");

                    

                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104602); //104602 - OQ - Fraud Review
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, fraudReviewQueue);                    
                }

                return isFraudulent;
            }
            catch (Exception ex)
            {

                DynamicsInterface.writeToLog($"Error in evaluateForFraud for {validationReqTransactionId}: {ex.Message}");

                if (!fraudReviewEnabled)
                    return false;

                ValidationServicesHelper.processSystemNote("-- Potential Fraud --", $"Error during fraud evaluation: {ex.Message}\n\nCase requires manual review due to technical issues during automated validation."
                                    , new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                
                
                validationRequestCase["ts_casestatus"] = new OptionSetValue(104602);//104602 - OQ - Fraud Review
                DynamicsInterface.DataverseClient.Update(validationRequestCase);

                fraudReviewQueue = fraudReviewQueue ?? DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.fraudReviewQueue;
                DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, fraudReviewQueue);

                return true;
            }
        }




        #region Website Analysis Methods

        /// <summary>
        /// Performs comprehensive website analysis using the WebsiteAnalysisService.
        /// This method delegates to the sophisticated analysis service for LLM-level understanding.
        /// </summary>
        public static async Task<WebsiteAnalysisResult> analyzeWebsite(
                                                                        string website,
                                                                        string organizationName,
                                                                        string address1,
                                                                        string address2,
                                                                        string city,
                                                                        string regionCode,
                                                                        string countryCode,
                                                                        string validationReqTransactionId)
        {
            DynamicsInterface.writeToLog($"[analyzeWebsite] Starting analysis for {website}, TransactionId: {validationReqTransactionId}");
            DynamicsInterface.writeToLog($"[analyzeWebsite] Input: orgName='{organizationName}', address1='{address1}', address2='{address2}', city='{city}', country='{countryCode}'");

            using (var analysisService = new WebsiteAnalysisService())
            {
                var request = new WebsiteAnalysisRequest
                {
                    Website = website,
                    OrganizationName = organizationName,
                    Address1 = address1,
                    Address2 = address2,
                    City = city,
                    RegionCode = regionCode,
                    CountryCode = countryCode,
                    TransactionId = validationReqTransactionId
                };

                var result = await analysisService.AnalyzeAsync(request);

                DynamicsInterface.writeToLog($"[analyzeWebsite] Completed: OrgScore={result.OrganizationNameMatchScore:P0}, " +
                    $"AddrScore={result.AddressMatchScore:P0}, TrustScore={result.TrustScore:P0}, " +
                    $"QualityScore={result.ContentQualityScore:P0}, OverallScore={result.OverallContentScore:P0}");

                return result;
            }
        }


        private static void ExtractSchemaOrgData(JToken schema, StringBuilder textBuilder)
        {
            if (schema == null) return;

            try
            {
                // Handle arrays
                if (schema is JArray arr)
                {
                    foreach (var item in arr)
                        ExtractSchemaOrgData(item, textBuilder);
                    return;
                }

                // Extract organization-related fields
                var type = schema["@type"]?.ToString();
                if (type != null && (type.Contains("Organization") || type.Contains("LocalBusiness") ||
                                      type.Contains("NGO") || type.Contains("Corporation") || type.Contains("NonProfit")))
                {
                    var name = schema["name"]?.ToString();
                    var description = schema["description"]?.ToString();
                    var address = schema["address"];

                    if (!string.IsNullOrEmpty(name)) textBuilder.AppendLine("SCHEMA_ORG_NAME: " + name);
                    if (!string.IsNullOrEmpty(description)) textBuilder.AppendLine("SCHEMA_ORG_DESC: " + description);

                    if (address != null)
                    {
                        var streetAddress = address["streetAddress"]?.ToString();
                        var addressLocality = address["addressLocality"]?.ToString();
                        var addressRegion = address["addressRegion"]?.ToString();
                        var postalCode = address["postalCode"]?.ToString();
                        var addressCountry = address["addressCountry"]?.ToString();

                        textBuilder.AppendLine("SCHEMA_ADDRESS: " +
                            $"{streetAddress} {addressLocality} {addressRegion} {postalCode} {addressCountry}");
                    }
                }
            }
            catch { /* Ignore parsing errors */ }
        }


        private static string CleanHtmlText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            // Decode HTML entities
            text = WebUtility.HtmlDecode(text);

            // Remove script and style content that might have been included
            text = Regex.Replace(text, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);

            // Remove any remaining HTML tags
            text = Regex.Replace(text, @"<[^>]+>", " ");

            // Normalize whitespace
            text = Regex.Replace(text, @"\s+", " ");

            return text.Trim();
        }


        private static bool AnalyzeContentMeaningfulness(string content, HtmlDocument htmlDoc, WebsiteAnalysisResult result)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;

            string normalizedContent = content.ToLowerInvariant();
            int meaningfulIndicators = 0;

            // Check for mission/purpose statements (multilingual)
            var purposeKeywords = new[] { 
                // English
                "mission", "vision", "purpose", "goal", "objective", "about us", "who we are", "what we do", "our story", "our work", "our impact",
                // German
                "über uns", "wer wir sind", "was wir tun", "unsere mission", "unsere vision", "unsere geschichte", "leitbild", "zielsetzung",
                // French
                "à propos", "qui sommes-nous", "notre mission", "notre vision", "notre histoire", "nos objectifs", "notre but",
                // Spanish
                "sobre nosotros", "quiénes somos", "nuestra misión", "nuestra visión", "nuestra historia", "nuestros objetivos",
                // Italian
                "chi siamo", "la nostra missione", "la nostra visione", "la nostra storia", "i nostri obiettivi",
                // Dutch
                "over ons", "wie zijn wij", "onze missie", "onze visie", "ons verhaal",
                // Portuguese
                "sobre nós", "quem somos", "nossa missão", "nossa visão", "nossa história"
            };
            if (purposeKeywords.Any(k => normalizedContent.Contains(k)))
            {
                meaningfulIndicators++;
                result.MatchedElements.Add("Contains mission/purpose statements");
            }

            // Check for nonprofit/organization indicators (multilingual)
            var nonprofitKeywords = new[] { 
                // English
                "nonprofit", "non-profit", "501(c)(3)", "501c3", "charity", "charitable", "tax-exempt", "tax exempt",
                "foundation", "donate", "donation", "volunteer", "community", "service", "cause", "impact", "beneficiaries",
                // German
                "gemeinnützig", "gemeinnütziger verein", "e.v.", "eingetragener verein", "stiftung", "spenden", "spende",
                "ehrenamt", "ehrenamtlich", "freiwillig", "gemeinschaft", "wohltätigkeit", "förderverein",
                // French
                "association", "but non lucratif", "sans but lucratif", "asbl", "fondation", "don", "faire un don",
                "bénévole", "bénévolat", "communauté", "charité", "caritative", "ong",
                // Spanish
                "sin fines de lucro", "sin ánimo de lucro", "ong", "fundación", "donar", "donación", "voluntario",
                "voluntariado", "comunidad", "caridad", "benéfico",
                // Italian
                "senza scopo di lucro", "no profit", "onlus", "fondazione", "donazione", "donare", "volontariato",
                "volontario", "comunità", "beneficenza",
                // Dutch
                "stichting", "vereniging", "anbi", "goede doel", "doneren", "donatie", "vrijwilliger",
                "vrijwilligerswerk", "gemeenschap", "liefdadigheid",
                // Portuguese
                "sem fins lucrativos", "organização não governamental", "ong", "fundação", "doar", "doação",
                "voluntário", "voluntariado", "comunidade", "caridade"
            };
            if (nonprofitKeywords.Count(k => normalizedContent.Contains(k)) >= 2)
            {
                meaningfulIndicators++;
                result.MatchedElements.Add("Contains nonprofit/charitable indicators");
            }

            // Check for program/service descriptions (multilingual)
            var programKeywords = new[] { 
                // English
                "program", "service", "initiative", "project", "campaign", "event", "activity", "workshop", "training", "outreach",
                // German
                "programm", "dienstleistung", "projekt", "initiative", "kampagne", "veranstaltung", "aktivität", "workshop", "schulung", "angebot",
                // French
                "programme", "service", "projet", "initiative", "campagne", "événement", "activité", "atelier", "formation",
                // Spanish
                "programa", "servicio", "proyecto", "iniciativa", "campaña", "evento", "actividad", "taller", "capacitación",
                // Italian
                "programma", "servizio", "progetto", "iniziativa", "campagna", "evento", "attività", "laboratorio", "formazione",
                // Dutch
                "programma", "dienst", "project", "initiatief", "campagne", "evenement", "activiteit", "workshop", "training",
                // Portuguese
                "programa", "serviço", "projeto", "iniciativa", "campanha", "evento", "atividade", "oficina", "treinamento"
            };
            if (programKeywords.Any(k => normalizedContent.Contains(k)))
            {
                meaningfulIndicators++;
                result.MatchedElements.Add("Contains program/service descriptions");
            }

            // Check for team/staff information (multilingual)
            var teamKeywords = new[] { 
                // English
                "team", "staff", "board", "director", "leadership", "founder", "ceo", "executive", "president", "member",
                // German
                "vorstand", "geschäftsführer", "gründer", "leitung", "mitarbeiter", "mitglied", "team", "beirat", "vorsitzender",
                // French
                "équipe", "personnel", "conseil", "directeur", "direction", "fondateur", "président", "membre", "comité",
                // Spanish
                "equipo", "personal", "junta", "director", "liderazgo", "fundador", "presidente", "miembro", "directiva",
                // Italian
                "squadra", "personale", "consiglio", "direttore", "leadership", "fondatore", "presidente", "membro",
                // Dutch
                "bestuur", "medewerkers", "directeur", "oprichter", "voorzitter", "lid", "team", "leiding",
                // Portuguese
                "equipe", "pessoal", "conselho", "diretor", "liderança", "fundador", "presidente", "membro"
            };
            if (teamKeywords.Any(k => normalizedContent.Contains(k)))
            {
                meaningfulIndicators++;
                result.MatchedElements.Add("Contains team/leadership information");
            }

            // Check for contact information patterns
            var hasPhone = Regex.IsMatch(content, @"[\(\+]?\d{1,3}[\s\-\.]?\(?\d{3}\)?[\s\-\.]?\d{3}[\s\-\.]?\d{4}");
            var hasEmail = Regex.IsMatch(content, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
            if (hasPhone || hasEmail)
            {
                meaningfulIndicators++;
                result.MatchedElements.Add("Contains contact information");
            }

            // Check content length (meaningful sites usually have substantial content)
            if (content.Length > 2000)
            {
                meaningfulIndicators++;
            }

            // Check for multiple pages/sections (indicates a real website)
            var internalLinks = htmlDoc.DocumentNode.SelectNodes("//a[starts-with(@href, '/') or contains(@href, '" +
                Regex.Match(htmlDoc.DocumentNode.OuterHtml, @"(https?://[^/""']+)").Groups[1].Value + "')]");
            if (internalLinks != null && internalLinks.Count >= 3)
            {
                meaningfulIndicators++;
                result.MatchedElements.Add($"Has {internalLinks.Count} internal links");
            }

            // Check for images (real org sites typically have images)
            var images = htmlDoc.DocumentNode.SelectNodes("//img[@src]");
            if (images != null && images.Count >= 2)
            {
                meaningfulIndicators++;
            }

            // Check for headings (real sites have structured content)
            var headings = htmlDoc.DocumentNode.SelectNodes("//h1 | //h2 | //h3");
            if (headings != null && headings.Count >= 1)
            {
                meaningfulIndicators++;
            }

            // Check for any substantial paragraphs
            var paragraphs = htmlDoc.DocumentNode.SelectNodes("//p");
            if (paragraphs != null && paragraphs.Count(p => (p.InnerText?.Trim().Length ?? 0) > 50) >= 2)
            {
                meaningfulIndicators++;
            }

            // Minimum threshold: at least 2 meaningful indicators (lowered from 3)
            bool isMeaningful = meaningfulIndicators >= 2;
            DynamicsInterface.writeToLog($"Content meaningfulness check: {meaningfulIndicators} indicators found, isMeaningful={isMeaningful}");
            return isMeaningful;
        }


        private static double CalculateOverallContentScore(WebsiteAnalysisResult result)
        {
            double score = 0.0;

            // Accessibility (20% weight)
            if (result.IsAccessible)
                score += 0.2;

            // Organization name match (25% weight)
            score += result.OrganizationNameMatchScore * 0.25;

            // Address match (20% weight)
            score += result.AddressMatchScore * 0.20;

            // Meaningful content (25% weight)
            if (result.HasMeaningfulContent)
                score += 0.25;

            // Penalty for generic/social media domains (10% reduction)
            if (result.IsGenericOrSocialMediaDomain)
                score -= 0.10;

            // Bonus for matched elements
            score += Math.Min(result.MatchedElements.Count * 0.02, 0.10);

            return Math.Max(0, Math.Min(1, score)); // Clamp between 0 and 1
        }


        private static bool IsCountryMatch(string country, string expectedCountryCode)
        {
            if (string.IsNullOrWhiteSpace(country) || string.IsNullOrWhiteSpace(expectedCountryCode))
                return false;

            country = country.Trim().ToUpperInvariant();
            expectedCountryCode = expectedCountryCode.Trim().ToUpperInvariant();

            // Direct match
            if (country == expectedCountryCode)
                return true;

            // Handle GB/UK variations
            if ((expectedCountryCode == "GB" || expectedCountryCode == "UK") &&
                (country == "GB" || country == "UK" || country == "UNITED KINGDOM" || country == "GREAT BRITAIN"))
                return true;

            // Common country code to full name mappings (including native language variations)
            var countryMappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "US", new[] { "UNITED STATES", "UNITED STATES OF AMERICA", "USA", "U.S.", "U.S.A.", "ÉTATS-UNIS", "ESTADOS UNIDOS", "STATI UNITI", "VEREINIGTE STAATEN" } },
                { "UK", new[] { "UNITED KINGDOM", "GREAT BRITAIN", "GB", "ENGLAND", "SCOTLAND", "WALES", "NORTHERN IRELAND", "ROYAUME-UNI", "REINO UNIDO", "GROßBRITANNIEN", "VEREINIGTES KÖNIGREICH" } },
                { "GB", new[] { "UNITED KINGDOM", "GREAT BRITAIN", "UK", "ENGLAND", "SCOTLAND", "WALES", "NORTHERN IRELAND", "ROYAUME-UNI", "REINO UNIDO", "GROßBRITANNIEN", "VEREINIGTES KÖNIGREICH" } },
                { "CA", new[] { "CANADA", "KANADA" } },
                { "AU", new[] { "AUSTRALIA", "AUSTRALIE", "AUSTRALIEN" } },
                { "DE", new[] { "GERMANY", "DEUTSCHLAND", "ALLEMAGNE", "ALEMANIA", "GERMANIA", "DUITSLAND" } },
                { "FR", new[] { "FRANCE", "FRANKREICH", "FRANCIA", "FRANKRIJK" } },
                { "NL", new[] { "NETHERLANDS", "HOLLAND", "NEDERLAND", "PAYS-BAS", "PAÍSES BAJOS", "PAESI BASSI", "NIEDERLANDE" } },
                { "BE", new[] { "BELGIUM", "BELGIQUE", "BELGIË", "BELGIEN", "BÉLGICA", "BELGIO" } },
                { "ES", new[] { "SPAIN", "ESPAÑA", "ESPAGNE", "SPANIEN", "SPAGNA", "SPANJE" } },
                { "IT", new[] { "ITALY", "ITALIA", "ITALIE", "ITALIEN", "ITALIË" } },
                { "PT", new[] { "PORTUGAL" } },
                { "BR", new[] { "BRAZIL", "BRASIL", "BRÉSIL", "BRASILIEN", "BRASILE", "BRAZILIË" } },
                { "MX", new[] { "MEXICO", "MÉXICO", "MEXIQUE", "MEXIKO", "MESSICO" } },
                { "IN", new[] { "INDIA", "INDE", "INDIEN" } },
                { "CN", new[] { "CHINA", "PEOPLES REPUBLIC OF CHINA", "PRC", "CHINE", "CINA" } },
                { "JP", new[] { "JAPAN", "JAPON", "GIAPPONE", "JAPÓN" } },
                { "KR", new[] { "SOUTH KOREA", "KOREA", "REPUBLIC OF KOREA", "CORÉE DU SUD", "COREA DEL SUR", "SÜDKOREA" } },
                { "NZ", new[] { "NEW ZEALAND", "NOUVELLE-ZÉLANDE", "NUEVA ZELANDA", "NUOVA ZELANDA", "NEUSEELAND" } },
                { "ZA", new[] { "SOUTH AFRICA", "AFRIQUE DU SUD", "SUDÁFRICA", "SUDAFRICA", "SÜDAFRIKA" } },
                { "IE", new[] { "IRELAND", "REPUBLIC OF IRELAND", "IRLANDE", "IRLAND", "IRLANDA", "IERLAND" } },
                { "CH", new[] { "SWITZERLAND", "SUISSE", "SCHWEIZ", "SVIZZERA", "SUIZA", "ZWITSERLAND" } },
                { "AT", new[] { "AUSTRIA", "ÖSTERREICH", "AUTRICHE", "OOSTENRIJK" } },
                { "SE", new[] { "SWEDEN", "SVERIGE", "SUÈDE", "SCHWEDEN", "SUECIA", "SVEZIA" } },
                { "NO", new[] { "NORWAY", "NORGE", "NORVÈGE", "NORWEGEN", "NORUEGA", "NORVEGIA" } },
                { "DK", new[] { "DENMARK", "DANMARK", "DANEMARK", "DÄNEMARK", "DINAMARCA", "DANIMARCA" } },
                { "FI", new[] { "FINLAND", "SUOMI", "FINLANDE", "FINNLAND", "FINLANDIA" } },
                { "PL", new[] { "POLAND", "POLSKA", "POLOGNE", "POLEN", "POLONIA" } },
                { "CZ", new[] { "CZECH REPUBLIC", "CZECHIA", "ČESKO", "RÉPUBLIQUE TCHÈQUE", "TSCHECHIEN", "CHEQUIA" } },
                { "RU", new[] { "RUSSIA", "RUSSIAN FEDERATION", "RUSSIE", "RUSSLAND", "RUSIA" } },
                { "SG", new[] { "SINGAPORE", "SINGAPOUR", "SINGAPUR", "SINGAPURA" } },
                { "HK", new[] { "HONG KONG" } },
                { "TW", new[] { "TAIWAN", "TAÏWAN" } },
                { "PH", new[] { "PHILIPPINES", "FILIPINAS", "FILIPPINE" } },
                { "TH", new[] { "THAILAND", "THAÏLANDE", "TAILANDIA" } },
                { "MY", new[] { "MALAYSIA", "MALAISIE", "MALASIA" } },
                { "ID", new[] { "INDONESIA", "INDONÉSIE", "INDONESIEN" } },
                { "VN", new[] { "VIETNAM", "VIET NAM", "VIÊT NAM" } },
                { "AE", new[] { "UNITED ARAB EMIRATES", "UAE", "ÉMIRATS ARABES UNIS", "EMIRATOS ÁRABES UNIDOS" } },
                { "IL", new[] { "ISRAEL", "ISRAËL" } },
                { "EG", new[] { "EGYPT", "ÉGYPTE", "ÄGYPTEN", "EGIPTO", "EGITTO" } },
                { "NG", new[] { "NIGERIA", "NIGÉRIA" } },
                { "KE", new[] { "KENYA", "KENIA" } },
                { "GH", new[] { "GHANA" } },
                { "AR", new[] { "ARGENTINA", "ARGENTINE", "ARGENTINIEN" } },
                { "CL", new[] { "CHILE", "CHILI" } },
                { "CO", new[] { "COLOMBIA", "COLOMBIE", "KOLUMBIEN" } },
                { "PE", new[] { "PERU", "PÉROU" } }
            };

            if (countryMappings.TryGetValue(expectedCountryCode, out string[] variations))
            {
                return variations.Contains(country);
            }

            return false;
        }

        #endregion



    }


    #region Website Analysis Classes


    /// <summary>
    /// Comprehensive result object for website analysis
    /// </summary>
    public class WebsiteAnalysisResult
    {
        // Core accessibility
        public bool IsAccessible { get; set; }
        public string AccessError { get; set; }
        public int RetryAttempts { get; set; }

        // Domain analysis
        public bool IsGenericOrSocialMediaDomain { get; set; }
        public string DomainType { get; set; }

        // Organization name matching
        public bool OrganizationNameMatched { get; set; }
        public double OrganizationNameMatchScore { get; set; }
        public string OrganizationNameMatchDetails { get; set; }

        // Address matching
        public bool AddressMatched { get; set; }
        public double AddressMatchScore { get; set; }
        public string AddressMatchDetails { get; set; }

        // Trust analysis
        public double TrustScore { get; set; }
        public List<string> TrustSignals { get; set; } = new List<string>();

        // Content quality
        public bool HasMeaningfulContent { get; set; }
        public double ContentQualityScore { get; set; }
        public List<string> ContentQualityIndicators { get; set; } = new List<string>();

        // Structural analysis
        public double StructuralScore { get; set; }
        public StructuralMetrics StructuralMetrics { get; set; }

        // Red flags
        public List<string> RedFlags { get; set; } = new List<string>();
        public int RedFlagCount { get; set; }

        // Overall scores
        public double OverallContentScore { get; set; }
        public string OverallAssessment { get; set; }

        // Analysis details
        public List<string> AnalysisFlags { get; set; } = new List<string>();
        public List<string> MatchedElements { get; set; } = new List<string>();
        public string ExtractedContent { get; set; }
    }


    public static class TextMatchingUtility
    {

        public static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Convert to lowercase
            text = text.ToLowerInvariant();

            // Decode HTML entities
            text = WebUtility.HtmlDecode(text);

            // Remove special characters and punctuation but keep spaces
            text = Regex.Replace(text, @"[^\w\s\-]", " ");

            // Normalize hyphens and dashes
            text = Regex.Replace(text, @"[\-–—]", " ");

            // Remove extra whitespace
            text = Regex.Replace(text, @"\s+", " ").Trim();

            return text;
        }

        /// <summary>
        /// Normalizes organization name for matching, removing common suffixes (multilingual)
        /// </summary>
        public static string NormalizeOrgName(string orgName)
        {
            if (string.IsNullOrWhiteSpace(orgName))
                return string.Empty;

            string text = NormalizeText(orgName);

            // Remove common org suffixes for flexible matching (multilingual)
            var orgSuffixes = new[] { 
                // English
                "inc", "incorporated", "llc", "corp", "corporation", "company", "co", "ltd", "limited",
                "foundation", "org", "organization", "nonprofit", "non profit", "ngo", "association",
                "assoc", "society", "institute", "group", "trust", "charity", "the", "a", "an",
                // German
                "ev", "e.v.", "eingetragener verein", "ggmbh", "gmbh", "ag", "ohg", "kg", "stiftung",
                "verein", "gemeinnützig", "gemeinnützige", "gemeinnütziger",
                // French  
                "sarl", "sas", "sa", "sasu", "eurl", "association", "fondation", "ong",
                // Spanish
                "sl", "sa", "slu", "fundación", "fundacion", "asociación", "asociacion", "ong",
                // Italian
                "srl", "spa", "sas", "snc", "fondazione", "associazione", "onlus", "ong",
                // Dutch
                "bv", "nv", "vof", "stichting", "vereniging",
                // Portuguese
                "ltda", "sa", "fundação", "fundacao", "associação", "associacao", "ong"
            };

            foreach (var suffix in orgSuffixes)
            {
                // Remove as suffix
                text = Regex.Replace(text, $@"\s+{Regex.Escape(suffix)}$", "", RegexOptions.IgnoreCase);
                // Remove as prefix (like "the", "der", "le", etc.)
                text = Regex.Replace(text, $@"^{Regex.Escape(suffix)}\s+", "", RegexOptions.IgnoreCase);
            }

            return text.Trim();
        }

        /// <summary>
        /// Extracts significant words from text (words that matter for matching)
        /// </summary>
        public static List<string> ExtractSignificantWords(string text, int minLength = 3)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // English
                "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
                "by", "from", "as", "is", "was", "are", "were", "been", "be", "have", "has", "had",
                "do", "does", "did", "will", "would", "could", "should", "may", "might", "must",
                "shall", "can", "need", "dare", "ought", "used", "this", "that", "these", "those",
                "i", "you", "he", "she", "it", "we", "they", "what", "which", "who", "whom",
                "your", "our", "their", "its", "my", "his", "her", "inc", "llc", "ltd", "org",
                // German
                "der", "die", "das", "ein", "eine", "und", "oder", "aber", "in", "auf", "an", "zu", "für", "von", "mit",
                "bei", "aus", "als", "ist", "war", "sind", "waren", "sein", "haben", "hat", "hatte",
                "werden", "wird", "wurde", "kann", "können", "muss", "müssen", "soll", "sollte",
                "dieser", "diese", "dieses", "jener", "jene", "ich", "du", "er", "sie", "es", "wir", "ihr",
                "mein", "dein", "sein", "unser", "euer", "ev", "gmbh", "ag",
                // French
                "le", "la", "les", "un", "une", "des", "et", "ou", "mais", "dans", "sur", "à", "pour", "de", "avec",
                "par", "comme", "est", "était", "sont", "étaient", "être", "avoir", "ai", "eu",
                "ce", "cette", "ces", "cet", "je", "tu", "il", "elle", "nous", "vous", "ils", "elles",
                "mon", "ton", "son", "notre", "votre", "leur", "qui", "que", "quoi", "sarl", "sas",
                // Spanish
                "el", "la", "los", "las", "un", "una", "unos", "unas", "y", "o", "pero", "en", "sobre", "a", "para", "de", "con",
                "por", "como", "es", "era", "son", "eran", "ser", "estar", "tener", "tiene", "tenía",
                "este", "esta", "estos", "estas", "ese", "esa", "yo", "tú", "él", "ella", "nosotros", "ellos", "ellas",
                "mi", "tu", "su", "nuestro", "vuestro", "quien", "que", "cual", "sl", "sa",
                // Italian
                "il", "lo", "la", "i", "gli", "le", "uno", "una", "e", "ed", "ma", "in", "su", "per", "di", "con",
                "da", "come", "è", "era", "sono", "erano", "essere", "avere", "ho", "ha", "aveva",
                "questo", "questa", "questi", "queste", "quello", "quella", "io", "tu", "lui", "lei", "noi", "voi", "loro",
                "mio", "tuo", "suo", "nostro", "vostro", "chi", "che", "quale", "srl", "spa",
                // Dutch
                "de", "het", "een", "en", "of", "maar", "in", "op", "aan", "te", "voor", "van", "met",
                "door", "uit", "als", "is", "was", "zijn", "waren", "worden", "wordt", "werd", "hebben", "heeft", "had",
                "dit", "dat", "deze", "die", "ik", "jij", "hij", "zij", "wij", "jullie",
                "mijn", "jouw", "zijn", "haar", "ons", "hun", "wie", "wat", "welke", "bv", "nv",
                // Portuguese
                "o", "a", "os", "as", "um", "uma", "uns", "umas", "e", "ou", "mas", "em", "sobre", "para", "de", "com",
                "por", "como", "é", "era", "são", "eram", "ser", "estar", "ter", "tem", "tinha",
                "este", "esta", "estes", "estas", "esse", "essa", "eu", "tu", "ele", "ela", "nós", "vós", "eles", "elas",
                "meu", "teu", "seu", "nosso", "vosso", "quem", "que", "qual", "ltda", "sa"
            };

            return NormalizeText(text)
                .Split(' ')
                .Where(w => w.Length >= minLength && !stopWords.Contains(w))
                .Distinct()
                .ToList();
        }

        public static int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source))
                return string.IsNullOrEmpty(target) ? 0 : target.Length;
            if (string.IsNullOrEmpty(target))
                return source.Length;

            int[,] distance = new int[source.Length + 1, target.Length + 1];

            for (int i = 0; i <= source.Length; i++)
                distance[i, 0] = i;
            for (int j = 0; j <= target.Length; j++)
                distance[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                    distance[i, j] = Math.Min(
                        Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                        distance[i - 1, j - 1] + cost);
                }
            }

            return distance[source.Length, target.Length];
        }

        /// <summary>
        /// Calculates how well an organization name is found within website content.
        /// This searches FOR the name IN the content, not compares them as equals.
        /// </summary>
        public static double CalculateOrgNameMatchScore(string orgName, string websiteContent)
        {
            if (string.IsNullOrWhiteSpace(orgName) || string.IsNullOrWhiteSpace(websiteContent))
                return 0.0;

            string normalizedOrgName = NormalizeOrgName(orgName);
            string normalizedContent = NormalizeText(websiteContent);

            // Strategy 1: Direct containment check (best match)
            if (normalizedContent.Contains(normalizedOrgName))
            {
                return 1.0; // Perfect match - org name found exactly
            }

            // Strategy 2: Check if all significant words from org name appear in content
            var orgWords = ExtractSignificantWords(normalizedOrgName, 2);
            if (orgWords.Count == 0)
                return 0.0;

            int exactWordMatches = 0;
            int fuzzyWordMatches = 0;

            foreach (var word in orgWords)
            {
                if (normalizedContent.Contains(word))
                {
                    exactWordMatches++;
                }
                else
                {
                    // Try fuzzy matching for this word - look for similar words in content
                    var contentWords = normalizedContent.Split(' ').Where(w => w.Length >= word.Length - 2 && w.Length <= word.Length + 2);
                    foreach (var contentWord in contentWords)
                    {
                        int distance = LevenshteinDistance(word, contentWord);
                        double similarity = 1.0 - ((double)distance / Math.Max(word.Length, contentWord.Length));
                        if (similarity >= 0.75) // 75% similar
                        {
                            fuzzyWordMatches++;
                            break;
                        }
                    }
                }
            }

            double exactScore = (double)exactWordMatches / orgWords.Count;
            double fuzzyScore = (double)fuzzyWordMatches / orgWords.Count * 0.7; // Fuzzy matches count less

            double wordMatchScore = exactScore + fuzzyScore;

            // Strategy 3: Check for partial phrase matches (e.g., "Workshop Aberfeldy" within "The Workshop Aberfeldy")
            // Try matching subsequences of the org name
            double phraseScore = 0.0;
            if (orgWords.Count >= 2)
            {
                // Try pairs of consecutive words
                for (int i = 0; i < orgWords.Count - 1; i++)
                {
                    string phrase = orgWords[i] + " " + orgWords[i + 1];
                    if (normalizedContent.Contains(phrase))
                    {
                        phraseScore = Math.Max(phraseScore, 0.7);
                    }
                }

                // Try triplets if available
                if (orgWords.Count >= 3)
                {
                    for (int i = 0; i < orgWords.Count - 2; i++)
                    {
                        string phrase = orgWords[i] + " " + orgWords[i + 1] + " " + orgWords[i + 2];
                        if (normalizedContent.Contains(phrase))
                        {
                            phraseScore = Math.Max(phraseScore, 0.85);
                        }
                    }
                }
            }

            // Strategy 4: Check page title and headings specifically (higher weight)
            // These are marked in extracted content with special prefixes
            double titleBonus = 0.0;
            var titleMatch = Regex.Match(normalizedContent, @"title[:\s]+([^\n]+)", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                string title = titleMatch.Groups[1].Value;
                int titleWordMatches = orgWords.Count(w => title.Contains(w));
                if (titleWordMatches > 0)
                {
                    titleBonus = (double)titleWordMatches / orgWords.Count * 0.3;
                }
            }

            // Combine scores with weights
            double finalScore = Math.Max(wordMatchScore, phraseScore) + titleBonus;

            return Math.Min(1.0, finalScore); // Cap at 100%
        }

        /// <summary>
        /// Calculates how well an address matches content on a website
        /// </summary>
        public static double CalculateAddressMatchScore(string websiteContent, string address1, string address2, string city, string regionCode, string countryCode, string postalCode)
        {
            if (string.IsNullOrWhiteSpace(websiteContent))
                return 0.0;

            string normalizedContent = NormalizeText(websiteContent);
            double totalScore = 0.0;
            int componentsChecked = 0;
            var matchedComponents = new List<string>();

            // Combine address lines for comprehensive street matching
            string combinedAddress = $"{address1} {address2}".Trim();

            // Check city - most important
            if (!string.IsNullOrWhiteSpace(city))
            {
                componentsChecked++;
                string normalizedCity = NormalizeText(city);
                if (normalizedContent.Contains(normalizedCity))
                {
                    totalScore += 1.0;
                    matchedComponents.Add($"City: {city}");
                }
                else
                {
                    // Try individual words for multi-word cities
                    var cityWords = normalizedCity.Split(' ').Where(w => w.Length > 2).ToList();
                    int matchedCityWords = cityWords.Count(w => normalizedContent.Contains(w));
                    if (matchedCityWords > 0)
                    {
                        totalScore += (double)matchedCityWords / cityWords.Count * 0.7;
                        matchedComponents.Add($"City partial: {matchedCityWords}/{cityWords.Count} words");
                    }
                }
            }

            // Check region/state - support both abbreviations and full names
            if (!string.IsNullOrWhiteSpace(regionCode))
            {
                componentsChecked++;
                string normalizedRegion = NormalizeText(regionCode);

                // Common region/state mappings (US states + UK regions + other)
                var regionMappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    // US States
                    {"AL", new[] {"alabama"}}, {"AK", new[] {"alaska"}}, {"AZ", new[] {"arizona"}},
                    {"AR", new[] {"arkansas"}}, {"CA", new[] {"california"}}, {"CO", new[] {"colorado"}},
                    {"CT", new[] {"connecticut"}}, {"DE", new[] {"delaware"}}, {"FL", new[] {"florida"}},
                    {"GA", new[] {"georgia"}}, {"HI", new[] {"hawaii"}}, {"ID", new[] {"idaho"}},
                    {"IL", new[] {"illinois"}}, {"IN", new[] {"indiana"}}, {"IA", new[] {"iowa"}},
                    {"KS", new[] {"kansas"}}, {"KY", new[] {"kentucky"}}, {"LA", new[] {"louisiana"}},
                    {"ME", new[] {"maine"}}, {"MD", new[] {"maryland"}}, {"MA", new[] {"massachusetts"}},
                    {"MI", new[] {"michigan"}}, {"MN", new[] {"minnesota"}}, {"MS", new[] {"mississippi"}},
                    {"MO", new[] {"missouri"}}, {"MT", new[] {"montana"}}, {"NE", new[] {"nebraska"}},
                    {"NV", new[] {"nevada"}}, {"NH", new[] {"new hampshire"}}, {"NJ", new[] {"new jersey"}},
                    {"NM", new[] {"new mexico"}}, {"NY", new[] {"new york"}}, {"NC", new[] {"north carolina"}},
                    {"ND", new[] {"north dakota"}}, {"OH", new[] {"ohio"}}, {"OK", new[] {"oklahoma"}},
                    {"OR", new[] {"oregon"}}, {"PA", new[] {"pennsylvania"}}, {"RI", new[] {"rhode island"}},
                    {"SC", new[] {"south carolina"}}, {"SD", new[] {"south dakota"}}, {"TN", new[] {"tennessee"}},
                    {"TX", new[] {"texas"}}, {"UT", new[] {"utah"}}, {"VT", new[] {"vermont"}},
                    {"VA", new[] {"virginia"}}, {"WA", new[] {"washington"}}, {"WV", new[] {"west virginia"}},
                    {"WI", new[] {"wisconsin"}}, {"WY", new[] {"wyoming"}}, {"DC", new[] {"district of columbia", "washington dc"}},
                    // UK regions
                    {"SCT", new[] {"scotland", "scottish"}}, {"ENG", new[] {"england", "english"}},
                    {"WLS", new[] {"wales", "welsh"}}, {"NIR", new[] {"northern ireland"}},
                    // Scottish regions/council areas
                    {"PERTH", new[] {"perth", "perthshire", "perth and kinross"}},
                    {"HIGHLAND", new[] {"highland", "highlands"}},
                    {"ABERDEENSHIRE", new[] {"aberdeenshire", "aberdeen"}}
                };

                bool regionFound = false;

                // Check direct match
                if (normalizedContent.Contains(normalizedRegion))
                {
                    regionFound = true;
                }
                // Check mapped names
                else if (regionMappings.TryGetValue(regionCode.ToUpperInvariant(), out string[] fullNames))
                {
                    if (fullNames.Any(fn => normalizedContent.Contains(fn)))
                    {
                        regionFound = true;
                    }
                }
                // Check if the region code itself appears
                else if (regionCode.Length >= 2 && normalizedContent.Contains(regionCode.ToLowerInvariant()))
                {
                    regionFound = true;
                }

                if (regionFound)
                {
                    totalScore += 1.0;
                    matchedComponents.Add($"Region: {regionCode}");
                }
            }

            // Check postal code
            if (!string.IsNullOrWhiteSpace(postalCode))
            {
                componentsChecked++;
                string normalizedPostal = postalCode.ToLowerInvariant().Trim();

                // Try various postal code formats
                if (normalizedContent.Contains(normalizedPostal) ||
                    normalizedContent.Contains(normalizedPostal.Replace(" ", "")) ||
                    normalizedContent.Contains(Regex.Replace(normalizedPostal, @"[^\w]", "")))
                {
                    totalScore += 1.0;
                    matchedComponents.Add($"Postal: {postalCode}");
                }
                else
                {
                    // For US ZIP codes, try just first 5 digits
                    string zipMatch = Regex.Match(postalCode, @"\d{5}").Value;
                    if (!string.IsNullOrEmpty(zipMatch) && normalizedContent.Contains(zipMatch))
                    {
                        totalScore += 0.8;
                        matchedComponents.Add($"Postal partial: {zipMatch}");
                    }
                    // For UK postcodes, try outward code (first part)
                    else
                    {
                        var ukPostcodeMatch = Regex.Match(postalCode.ToUpperInvariant(), @"^([A-Z]{1,2}\d{1,2}[A-Z]?)");
                        if (ukPostcodeMatch.Success && normalizedContent.Contains(ukPostcodeMatch.Groups[1].Value.ToLowerInvariant()))
                        {
                            totalScore += 0.7;
                            matchedComponents.Add($"Postal outward: {ukPostcodeMatch.Groups[1].Value}");
                        }
                    }
                }
            }

            // Check street address (partial matching with flexibility) - use combined address1 + address2
            if (!string.IsNullOrWhiteSpace(combinedAddress))
            {
                componentsChecked++;
                string normalizedAddress = NormalizeText(combinedAddress);

                // Remove common street suffixes for flexible matching
                var streetSuffixes = new[] { "street", "st", "avenue", "ave", "road", "rd", "drive", "dr",
                                             "lane", "ln", "boulevard", "blvd", "way", "court", "ct",
                                             "circle", "cir", "place", "pl", "terrace", "close", "crescent",
                                             "unit", "suite", "ste", "apt", "apartment", "floor", "fl" };

                string addressForMatching = normalizedAddress;
                foreach (var suffix in streetSuffixes)
                {
                    addressForMatching = Regex.Replace(addressForMatching, $@"\b{suffix}\b", " ");
                }
                // Also remove unit/apartment numbers like "1b", "1c", "1b 1c"
                addressForMatching = Regex.Replace(addressForMatching, @"\b\d+[a-z]?\b", " ");
                addressForMatching = Regex.Replace(addressForMatching, @"\s+", " ").Trim();

                // Extract significant parts (words that are meaningful for matching)
                var addressParts = addressForMatching.Split(' ')
                    .Where(p => p.Length > 2 && !Regex.IsMatch(p, @"^\d+$")) // Skip short words and numbers
                    .ToList();

                if (addressParts.Count > 0)
                {
                    int matchedParts = addressParts.Count(p => normalizedContent.Contains(p));
                    double partScore = (double)matchedParts / addressParts.Count;
                    totalScore += partScore;

                    if (matchedParts > 0)
                    {
                        matchedComponents.Add($"Address: {matchedParts}/{addressParts.Count} parts ({string.Join(", ", addressParts.Where(p => normalizedContent.Contains(p)))})");
                    }
                }
            }

            // Check country
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                // Country check is informational, doesn't add to score but we log it
                var countryMappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    {"US", new[] {"united states", "usa", "america"}},
                    {"UK", new[] {"united kingdom", "britain", "england", "scotland", "wales"}},
                    {"GB", new[] {"united kingdom", "britain", "great britain", "england", "scotland", "wales"}},
                    {"CA", new[] {"canada"}},
                    {"AU", new[] {"australia"}}
                };

                bool countryFound = normalizedContent.Contains(countryCode.ToLowerInvariant());
                if (!countryFound && countryMappings.TryGetValue(countryCode.ToUpperInvariant(), out string[] countryNames))
                {
                    countryFound = countryNames.Any(cn => normalizedContent.Contains(cn));
                }
            }

            return componentsChecked > 0 ? totalScore / componentsChecked : 0.0;
        }

        /// <summary>
        /// Legacy method for backward compatibility - redirects to new implementation
        /// </summary>
        public static double CalculateSimilarityScore(string source, string target)
        {
            return CalculateOrgNameMatchScore(source, target);
        }
    }

    #endregion
}
