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
//using static System.Windows.Forms.AxHost;
using System.Security.Policy;
using System.IdentityModel.Metadata;
using System.Dynamic;
using System.Net.NetworkInformation;
using System.Web.UI.WebControls;
using System.Web.Util;
using System.Security.Cryptography;
using static DynamicsProcesses.NetworkValidationService;

namespace DynamicsProcesses
{
    internal class DynamicsProcessesAutomatedValidation
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
        public static async Task  processAutomatedValidation()
        {
            try
            {


                var automatedValSettings = getAutomatedValidationConfig();

                if (!automatedValSettings.isAutomatedValidationActive)
                    return;

                //"AutoValidation Email Outreach"
                //"AutoValidation Inconclusive"
                string queueNameCsv = "AutoValidation";
                if (DynamicsInterface.Args.Length > 1)
                    queueNameCsv = DynamicsInterface.Args[1];


                string[] queueNamesParameters = queueNameCsv.Split(',');


                AutomatedValidationConfig  = JsonConvert.DeserializeObject<ExpandoObject>(automatedValSettings.automatedValConfigText) as IDictionary<string, Object>;

                AutomatedValDefinition  = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);



                string postAutoValidationQueue = AutomatedValDefinition.config.postAutoValidationQueue;
                string postAutoValidationQueueHighPriority = AutomatedValDefinition.config.postAutoValidationQueueHighPriority;
                string outreachQueueName = AutomatedValDefinition.emailOutreachProcess.queueName;
                string outreachQueueHighPriority = AutomatedValDefinition.emailOutreachProcess.queueNameHighPriority;
                string duplicateReviewQueue = AutomatedValDefinition.config.duplicateReviewQueue;
                string pendingActivityCodeDecisionQueue = ((JArray)AutomatedValDefinition.queueRoutingRules)?
                                                                                                .ToList<dynamic>()?.Where(rule => 
                                                                                                rule.routingCriteria == "ValDisposition-OrgAgentValid-Trustworthy-OrgNameFromIRSAvailable-ActivityCodeInvalid")?
                                                                                               .Select(rule => (string)rule.routeToQueue)?.FirstOrDefault();

                string not03IRSSubsectionnQueue = ((JArray)AutomatedValDefinition.queueRoutingRules)?
                                                                                                .ToList<dynamic>()?.Where(rule =>
                                                                                                rule.routingCriteria == "Not03IRSSubsection")?
                                                                                               .Select(rule => (string)rule.routeToQueue)?.FirstOrDefault();


                EnvVariables = DynamicsProcessesHelper.GetEnvironmentVariables();

                if (DynamicsEnvironments.ContainsKey(DynamicsInterface.DynamicsEnvironment))
                {
                    string DynamicsEnvironmentCurrentName = DynamicsEnvironments[DynamicsInterface.DynamicsEnvironment];
                    DynamicsEnvironments["DynamicsEnvironmentCurrent"] = DynamicsEnvironmentCurrentName;
                }



                ParallelProcessesHelper.SemaphoreClient = ParallelProcessesHelper.getTableClientAsync(AzureStorage7C);


                List<string> queueNamesList = new List<string>();

                foreach(string queueParam in queueNamesParameters)
                {
                    string actualQueueName = queueParam;

                    switch (queueParam.ToLower())
                    {
                        case "manualvalidationqueues":
                            queueNamesList.Add(postAutoValidationQueue);
                            queueNamesList.Add(postAutoValidationQueueHighPriority);
                            queueNamesList.Add(pendingActivityCodeDecisionQueue);
                            queueNamesList.Add(not03IRSSubsectionnQueue);                            
                            break;

                        case "postdisposition":
                            queueNamesList.Add(postAutoValidationQueue);
                            queueNamesList.Add(postAutoValidationQueueHighPriority);

                            demoteCasesFromHighPriority();
                            promoteCasesToHighPriority();

                            return;

                        case "emailoutreach":
                            queueNamesList.Add(outreachQueueName);
                            queueNamesList.Add(outreachQueueHighPriority);
                            break;

                        case "emailoutreachresponded":
                            await getCustomerHasRespondedEmailOutreach();
                            return;

                        case "emailoutreachabandoned":
                            await setOldCasesInOutreachQueueToAbandoned();
                            return;

                        case "duplicatereview":
                            queueNamesList.Add(duplicateReviewQueue);
                            break;

                        case "initiatedispostionrequest":
                            initiateValidationRquestFoQualCases();
                            return;

                        case "scorematrix":
                            bool forceGetScoreMatrix = false;
                            if (DynamicsInterface.Args.Length > 1)
                            {
                                string scoreMatrixConditional = DynamicsInterface.Args[1];

                                if (scoreMatrixConditional.ToLower() == "unconditional")
                                    forceGetScoreMatrix = true;
                            }

                            scoreMatrixFoQualCases(forceGetScoreMatrix);
                            return;

                        case "processautoqualify":
                            processAutoCloseQualifyFoQualCases();
                            return;

                        case "processmanual":
                            processAutoValidationQueueManual();
                            return;

                        case "recyclebin":
                            int daysOld = 0;
                            if (DynamicsInterface.Args.Length > 2)
                            {
                                string daysOldText = DynamicsInterface.Args[2];

                                if (int.TryParse(daysOldText, out daysOld)) {}

                                recycleBin(daysOld);
                            }
                            return;

                        default:
                            queueNamesList.Add(actualQueueName);
                            break;

                    }
                    
                }

                string[] queueNames = queueNamesList.ToArray();


                




                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.ColumnSet = new ColumnSet(true);
                //queryEntity.Criteria.AddCondition("name", ConditionOperator.In, new object[] { "AutoValidation","AutoValidation Inconclusive" });
                queryEntity.Criteria.AddCondition("name", ConditionOperator.In, queueNames);
                //queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
                EntityCollection entityCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryEntity);


                string baseLogName = DynamicsInterface.LogName;
                foreach (Entity queue in entityCollection.Entities)
                {
                    Guid queueId = queue.Id;
                    string queueName = queue.GetAttributeValue<string>("name");

                    
                    QueryExpression queryQueueItem = new QueryExpression("queueitem");
                    queryQueueItem.ColumnSet = new ColumnSet(true);
                    queryQueueItem.Criteria.AddCondition("queueid", ConditionOperator.Equal, queueId);
                    //queryQueueItem.Criteria.AddCondition("objectid", ConditionOperator.Equal, Guid.Parse("DB7FDE24-9CBF-F011-BBD3-000D3A9A573F"));

                    /*change this back to OrderType.Ascending*/
                    queryQueueItem.AddOrder("enteredon", OrderType.Ascending);

                    
                    EntityCollection queueItemCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryQueueItem);

                    DynamicsInterface.LogName = baseLogName + "_" + queueName.Replace(' ', '_').Replace('/', '_');

                    DynamicsInterface.LogName += DynamicsInterface.Args.Length > 3 ? "_" + DynamicsInterface.Args[3] : "";

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
                        await ParallelProcessesHelper.releaseSemaphoreAsync(resourceId);
                        #endregion

                    }
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processAutomatedValidation(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static void recycleBin(int daysOld)
        {
            try
            {
                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.ColumnSet = new ColumnSet(true);
                queryEntity.Criteria.AddCondition("name", ConditionOperator.In, "Recycle Bin");
                EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                foreach (Entity queue in entityCollection.Entities)
                {
                    Guid queueId = queue.Id;
                    string queueName = queue.GetAttributeValue<string>("name");


                    QueryExpression queryQueueItem = new QueryExpression("queueitem");
                    queryQueueItem.ColumnSet = new ColumnSet(true);
                    queryQueueItem.Criteria.AddCondition("queueid", ConditionOperator.Equal, queueId);
                    queryQueueItem.Criteria.AddCondition("enteredon", ConditionOperator.LessEqual, DateTime.UtcNow.AddDays(-1 * daysOld));


                    queryQueueItem.AddOrder("enteredon", OrderType.Ascending);


                    EntityCollection queueItemCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQueueItem);

                    DynamicsInterface.LogName += "_" + queueName.Replace(' ', '_');

                    foreach (Entity queueItem in queueItemCollection.Entities)
                    {
                        DynamicsInterface.errorStack = new List<string>();

                        processRecycleBin(queueItem, queueName);
                    }
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in recycleBin(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static void processRecycleBin(Entity queueItem, string queueName)
        {
            try
            {
                EntityReference queueItemObjRef = queueItem.GetAttributeValue<EntityReference>("objectid");

                if (queueItemObjRef == null || queueItemObjRef.LogicalName != "incident")
                {
                    DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                   
                }

                Guid caseId = queueItemObjRef.Id;

                Entity caseEntity = DynamicsInterface.DataverseClient.Retrieve("incident", caseId, new ColumnSet(true));



                QueryExpression queryEmail = new QueryExpression("email");
                queryEmail.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, caseEntity.Id);
                EntityCollection emailCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEmail);

                foreach(Entity email in emailCollection.Entities)
                    DynamicsInterface.DataverseClient.Delete(email.LogicalName, email.Id);

                DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                DynamicsInterface.DataverseClient.Delete(caseEntity.LogicalName, caseEntity.Id);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processRecycleBin(). Exception message: " + Environment.NewLine + e.Message
                                                                                             + Environment.NewLine + "queueItemId: " + queueItem.Id.ToString()
                                                                                            );
            }

        }


        public static async Task getCustomerHasRespondedEmailOutreach()
        {
            try
            {
                string outreachQueueName = AutomatedValDefinition.emailOutreachProcess.queueName;
                string outreachQueueHighPriority = AutomatedValDefinition.emailOutreachProcess.queueNameHighPriority;


                string postAutoValidationQueue = AutomatedValDefinition.config.postAutoValidationQueue;
                string postAutoValidationQueueHighPriority = AutomatedValDefinition.config.postAutoValidationQueueHighPriority;



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
                                    <attribute name=""ticketnumber""/>
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
                                            <value>" + outreachQueueName + @"</value>
                                            <value>" + outreachQueueHighPriority + @"</value>
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

                    EntityReference queueItemObjRef = queueItem.GetAttributeValue<EntityReference>("objectid");
                    if (queueItemObjRef == null || queueItemObjRef.LogicalName != "incident")
                    {
                        DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
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


                    string linkQueueName = (string)queueItem.GetAttributeValue<AliasedValue>("qu.name")?.Value;
                    string caseNumber = (string)queueItem.GetAttributeValue<AliasedValue>("inc.ticketnumber")?.Value;

                    string nextQueue = linkQueueName == outreachQueueName ? postAutoValidationQueue : postAutoValidationQueueHighPriority;
                    DynamicsProcessesHelper.addCaseToQueue(caseId, nextQueue);




                    #region Release Semaphore
                    await ParallelProcessesHelper.releaseSemaphoreAsync(resourceId);
                    #endregion

                }
                return;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getCustomerHasRespondedEmailOutreach(). Exception message: " + Environment.NewLine + e.Message);
            }
        }
        public static async Task setOldCasesInOutreachQueueToAbandoned()
        {
            try
            {
                string outreachQueueName = AutomatedValDefinition.emailOutreachProcess.queueName;
                string outreachQueueHighPriority = AutomatedValDefinition.emailOutreachProcess.queueNameHighPriority;


                string postAutoValidationQueue = AutomatedValDefinition.config.postAutoValidationQueue;
                string postAutoValidationQueueHighPriority = AutomatedValDefinition.config.postAutoValidationQueueHighPriority;

            
                DateTime referenceDate = DateTime.UtcNow.AddDays(-45);
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
                                <attribute name=""ticketnumber""/>
		                        <link-entity name=""account"" alias=""aa"" link-type=""inner"" from=""accountid"" to=""customerid"">
			                        <attribute name=""accountnumber""/>
		                        </link-entity>
	                        </link-entity>
	                        <link-entity name=""queue"" alias=""qu"" link-type=""inner"" from=""queueid"" to=""queueid"">
                             <attribute name=""name""/>
		                        <filter type=""and"">
			                        <condition attribute=""name"" operator=""in"">
                                     <value>" + outreachQueueName + @"</value>
                                     <value>" + outreachQueueHighPriority + @"</value>
                                 </condition>
		                        </filter>
	                        </link-entity>
                     </entity>
                 </fetch>";

                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery);
                string newfetchxml = fetchXmlDoc.ToString();

                EntityCollection queueItemCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(new FetchExpression(newfetchxml));

                foreach (Entity queueItem in queueItemCollection.Entities)
                {

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

                    Entity caseEntity = await DynamicsInterface.DataverseClient.RetrieveAsync("incident", caseId, new ColumnSet("ts_casestatus"));

                    string tsCaseStatusText = caseEntity.Contains("ts_casestatus") ? caseEntity.FormattedValues["ts_casestatus"] : "";

                    if (tsCaseStatusText.ToLower().Contains("awaiting"))
                    {
                        caseEntity["ts_casestatus"] = new OptionSetValue(102059); //OQ - Abandoned
                        await DynamicsInterface.DataverseClient.UpdateAsync(caseEntity);
                    }

                    #region Release Semaphore
                    await ParallelProcessesHelper.releaseSemaphoreAsync(resourceId);
                    #endregion

                }
           


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getCustomerHasRespondedEmailOutreach(). Exception message: " + Environment.NewLine + e.Message);
            }
        }
        public static void promoteCasesToHighPriority()
        {
            try
            {
                string postAutoValidationQueue = AutomatedValDefinition.config.postAutoValidationQueue;
                string postAutoValidationQueueHighPriority = AutomatedValDefinition.config.postAutoValidationQueueHighPriority;
                string dateRef = DateTime.UtcNow.AddMinutes(-240).ToString("yyyy-MM-ddTHH:mm:ssZ");

                string fetchExpressionQuery2 = @"
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
			                        <condition attribute=""enteredon"" operator=""gt"" value=""" + dateRef + @"""/>
		                        </filter>
		                        <link-entity alias=""inc"" name=""incident"" to=""objectid"" from=""incidentid"" link-type=""inner"">
			                        <attribute name=""casetypecode""/>
			                        <attribute name=""customerid""/>
			                        <attribute name=""ts_tsorderid""/>
			                        <attribute name=""ts_casestatus""/>
			                        <link-entity name=""account"" alias=""aa"" link-type=""inner"" from=""accountid"" to=""customerid"">
				                        <attribute name=""accountnumber""/>
			                        </link-entity>
		                        </link-entity>
		                        <link-entity name=""queue"" alias=""qu"" link-type=""inner"" from=""queueid"" to=""queueid"">
			                        <filter type=""and"">
				                        <condition attribute=""name"" operator=""in"">
                                <value>" + postAutoValidationQueue + @"</value>
                              </condition>
			                        </filter>
		                        </link-entity>
	                        </entity>
                        </fetch>
                        ";
                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery2);

                string newfetchxml = fetchXmlDoc.ToString();


                EntityCollection accountCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(new FetchExpression(newfetchxml));


                string[] tsOrgIds = accountCollection.Entities.ToList().Select(item => (string)item.GetAttributeValue<AliasedValue>("aa.accountnumber").Value).ToArray();
                string tsOrgIdscsv = string.Join(",", tsOrgIds);

                
                Dictionary<string, bool> orgOrders = DynamicsProcessesHelper.orgOpenOrders(tsOrgIdscsv);

                var casesWithOrders = accountCollection.Entities.Where(item => orgOrders[(string)item.GetAttributeValue<AliasedValue>("aa.accountnumber").Value]);
                foreach (Entity caseItem in casesWithOrders)
                {
                    EntityReference caseRef = caseItem.GetAttributeValue<EntityReference>("objectid");
                    Guid caseId = caseRef.Id;
                    DynamicsProcessesHelper.addCaseToQueue(caseId, postAutoValidationQueueHighPriority);
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in promoteCasesToHighPriority(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static void demoteCasesFromHighPriority()
        {
            try
            {
                string postAutoValidationQueue = AutomatedValDefinition.config.postAutoValidationQueue;
                string postAutoValidationQueueHighPriority = AutomatedValDefinition.config.postAutoValidationQueueHighPriority;
                string dateRef = DateTime.UtcNow.AddMinutes(-240).ToString("yyyy-MM-ddTHH:mm:ssZ");

                string fetchExpressionQuery2 = @"
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
			                        <condition attribute=""enteredon"" operator=""gt"" value=""" + dateRef + @"""/>
		                        </filter>
		                        <link-entity alias=""inc"" name=""incident"" to=""objectid"" from=""incidentid"" link-type=""inner"">
			                        <attribute name=""casetypecode""/>
			                        <attribute name=""customerid""/>
			                        <attribute name=""ts_tsorderid""/>
			                        <attribute name=""ts_casestatus""/>
			                        <link-entity name=""account"" alias=""aa"" link-type=""inner"" from=""accountid"" to=""customerid"">
				                        <attribute name=""accountnumber""/>
			                        </link-entity>
		                        </link-entity>
		                        <link-entity name=""queue"" alias=""qu"" link-type=""inner"" from=""queueid"" to=""queueid"">
			                        <filter type=""and"">
				                        <condition attribute=""name"" operator=""in"">
                                 <value>" + postAutoValidationQueueHighPriority + @"</value>
                              </condition>
			                        </filter>
		                        </link-entity>
	                        </entity>
                        </fetch>
                        ";
                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery2);

                string newfetchxml = fetchXmlDoc.ToString();


                EntityCollection accountCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(new FetchExpression(newfetchxml));


                string[] tsOrgIds = accountCollection.Entities.ToList().Select(item => (string)item.GetAttributeValue<AliasedValue>("aa.accountnumber").Value).ToArray();
                string tsOrgIdscsv = string.Join(",", tsOrgIds);


                Dictionary<string, bool> orgOrders = DynamicsProcessesHelper.orgOpenOrders(tsOrgIdscsv);

                var casesWithOrders = accountCollection.Entities.Where(item => !orgOrders[(string)item.GetAttributeValue<AliasedValue>("aa.accountnumber").Value]);
                foreach (Entity caseItem in casesWithOrders)
                {
                    EntityReference caseRef = caseItem.GetAttributeValue<EntityReference>("objectid");
                    Guid caseId = caseRef.Id;
                    Entity caseEntity = DynamicsInterface.DataverseClient.Retrieve("incident", caseId, new ColumnSet(true));

                    EntityReference customerIdRef = caseEntity.GetAttributeValue<EntityReference>("customerid");

                    if (customerIdRef != null && customerIdRef.LogicalName == "account")
                    {
                        Entity account = DynamicsInterface.DataverseClient.Retrieve("account", customerIdRef.Id, new ColumnSet(true));

                        if (!DynamicsProcessesHelper.isValidationServices(account))
                            DynamicsProcessesHelper.addCaseToQueue(caseId, postAutoValidationQueue);
                    }
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in demoteCasesFromHighPriority(). Exception message: " + Environment.NewLine + e.Message);
            }
        }


        public static string pickNextValTransaction()
        {
            string transactionId = null;
            try
            {

                DBAdminDataContext context = new DBAdminDataContext();
                IEnumerable<usp_pickValTransResult> sprocQuery = null;
                context.Connection.Open();

                sprocQuery = from table in context.usp_pickValTrans()
                             select table;
                List<usp_pickValTransResult> sprocQueryResult = sprocQuery.ToList<usp_pickValTransResult>();

                if (sprocQueryResult.Count == 0)
                    return null;

                usp_pickValTransResult org = sprocQueryResult.First();
                transactionId = org.tsId;

                context.Connection.Close();
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in pickNextValTransaction(). Exception message: " + Environment.NewLine + e.Message);
            }

            return transactionId;
        }
        public static Guid getSystemUserId()
        {
            Guid userId = Guid.Empty;
            try
            {
                QueryExpression queryUser = new QueryExpression("systemuser");
                queryUser.ColumnSet = new ColumnSet(true);
                queryUser.Criteria.AddCondition("fullname", ConditionOperator.Equal, "SYSTEM");
                EntityCollection userCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryUser);


                if (userCollection.Entities.Count == 0)
                    return Guid.Empty;

                Entity userEntity = userCollection.Entities.First();
                userId = userEntity.Id;


                string fullName = userEntity.GetAttributeValue<string>("fullname");
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getSystemUserId(). Exception message: " + Environment.NewLine + e.Message);
            }

            return userId;
        }
        public static dynamic getAutomatedValidationConfig()
        {
            dynamic settings = new System.Dynamic.ExpandoObject();

            settings.isAutomatedValidationActive = false;

            try
            {
                QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryMapping.ColumnSet = new ColumnSet(true);
                queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "AutomatedValidation");
                EntityCollection mappingCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryMapping);

                if (mappingCollection.Entities.Count() == 0)
                {
                    DynamicsInterface.writeToLog("Error in getAutomatedValidationConfig(...). ts_fieldname = 'AutomatedValidation' was not found in ts_fieldhierarchyandmapping"  );
                    return settings;
                }
                Entity autoValidationMap = mappingCollection.Entities.First();

                string automatedValConfigText = autoValidationMap.GetAttributeValue<string>("ts_configuration");
                int auomatedValidationValue = autoValidationMap.GetAttributeValue<int>("ts_valuecode");

                settings.automatedValConfigText = automatedValConfigText;

                if (auomatedValidationValue == 1)
                    settings.isAutomatedValidationActive = true;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getAutomatedValidationConfig(). Exception message: " + Environment.NewLine + e.Message);
            }

            return settings;
        }


        public static void addPreexistingValidationTransactionToCase(Entity caseEntity)
        {
            try
            {
                string validationReqTransactionId = caseEntity.GetAttributeValue<string>("ts_validationrequesttransactionid");

                if (!string.IsNullOrEmpty(validationReqTransactionId))
                    return;


                string transactionId = pickNextValTransaction();

                if (transactionId != null)
                {
                    caseEntity["ts_validationrequesttransactionid"] = transactionId;
                    caseEntity["ts_validationrequestdate"] = DateTime.UtcNow;


                    DBAdminDataContext context = new DBAdminDataContext();
                    IEnumerable<usp_getValRequestResult> sprocQuery = null;
                    context.Connection.Open();

                    sprocQuery = from table in context.usp_getValRequest(transactionId)
                                 select table;
                    List<usp_getValRequestResult> sprocQueryResult = sprocQuery.ToList<usp_getValRequestResult>();

                    if (sprocQueryResult.Count == 0)
                        return;

                    usp_getValRequestResult item = sprocQueryResult.First();
                    dynamic validationRequestObj = JsonConvert.DeserializeObject(item.scoreMatrix);


                    string agentEmail = validationRequestObj.AgentEmail;
                    string agentName = validationRequestObj.AgentFirstName + " " + validationRequestObj.AgentLastName;
                    string activityCode = validationRequestObj.ActivityCode;


                    caseEntity["ts_validationagentemail"] = agentEmail;
                    caseEntity["ts_validationagentname"] = agentName;
                    caseEntity["ts_validationselfreportedactivitycode"] = activityCode;

                    caseEntity["ts_casestatus"] = new OptionSetValue(104695); //OQ - Automated Validation - Awaiting Disposition

                    DynamicsInterface.DataverseClient.Update(caseEntity);
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in addPreexistingValidationTransactionToCase(). Exception message: " + Environment.NewLine + e.Message);
            }
        }
        public static async Task processValidationRequest(Entity queueItem, string queueName, Guid caseId)
        {
            try
            {
              
                Entity caseEntity = await DynamicsInterface.DataverseClient.RetrieveAsync("incident", caseId, new ColumnSet(true));


                string tsType = caseEntity.Contains("ts_type") ? caseEntity.FormattedValues["ts_type"] : string.Empty;

                if (tsType != "Organization Qualification")
                {
                    await DynamicsInterface.DataverseClient.DeleteAsync(queueItem.LogicalName, queueItem.Id);
                    return;
                }

                EntityReference customerIdRef = caseEntity.GetAttributeValue<EntityReference>("customerid");

                if (customerIdRef == null || customerIdRef.LogicalName != "account")
                {
                    await DynamicsInterface.DataverseClient.DeleteAsync(queueItem.LogicalName, queueItem.Id);
                    return;
                }

                Entity account = await DynamicsInterface.DataverseClient.RetrieveAsync("account", customerIdRef.Id, new ColumnSet(true));



                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                EntityReference qualCodeRef = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid");

                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;
                Guid qualCodeId = qualCodeRef == null ? Guid.Empty : qualCodeRef.Id;

                if (orgDesigId != qualCodeId)
                {
                    await DynamicsInterface.DataverseClient.DeleteAsync(queueItem.LogicalName, queueItem.Id);
                    return;
                }




                bool pickExistingTransactionId = AutomatedValDefinition.config.pickExistingTransactionId == null ? false : AutomatedValDefinition.config.pickExistingTransactionId;

                string validationReqTransactionId = caseEntity.GetAttributeValue<string>("ts_validationrequesttransactionid");                  
                
                if (string.IsNullOrEmpty(validationReqTransactionId))
                {
                    bool isDuplicate = determineIfDuplicate(caseEntity, account, validationReqTransactionId, queueName);
                    if (isDuplicate)
                        return;

                    if (pickExistingTransactionId)
                    {
                        addPreexistingValidationTransactionToCase(caseEntity);
                    }
                    else
                    {
                        initiateDispositionRequest(caseEntity, account, queueName);
                    }
                }
                else
                {
                    determineProcessBehavior(caseEntity, account, validationReqTransactionId, queueName);
                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processAutoValidationCase(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "queueItemId: " + queueItem.Id.ToString()
                    );
            }

        }
        
        public static void determineProcessBehavior(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName)
        {
            try
            {
               
                bool caseHasDisposition = existsSystemNote(" --- Disposition Details --- ", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                if (!caseHasDisposition)
                {
                    bool okToProcess = getProcessingApproval(caseEntity, account, queueName);

                    if (okToProcess)
                    {
                        getValidationScoreMatrix(caseEntity, account, validationReqTransactionId, queueName);
                        caseHasDisposition = existsSystemNote(" --- Disposition Details --- ", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                    }
                }



                


                if (caseHasDisposition)
                    determineAction(caseEntity, account, validationReqTransactionId, queueName);
                
                
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in determineProcessBehavior(). Exception message: " + Environment.NewLine + e.Message);
            }
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
                processSystemNote("Initial Duplicate Check - Org Matches Found", dupesNoteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));


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

        public static void resolveActionAutoValidation(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName)
        {
            try
            {
                

                #region IRSRevoke
                bool isOrgIRSRevoked = caseEntity.GetAttributeValue<bool>("ts_validationdispositiononirsrevokelist");

                if (isOrgIRSRevoked)
                {
                    string irsDisqualificationQueue = AutomatedValDefinition.config.irsDisqualificationQueue;


                    caseEntity["ts_casestatus"] = new OptionSetValue(103982); //103982 - OQ - IRS Disqualified	
                    DynamicsInterface.DataverseClient.Update(caseEntity);
                    DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, irsDisqualificationQueue);
                    return;
                }
                #endregion


                #region FraudCheck
                bool isPotentialFraud = evaluateForFraudSimplified(caseEntity, account, validationReqTransactionId).Result;
                if (isPotentialFraud)
                    return;
                #endregion




                #region ParameterDefinitions
                bool isOrgValid = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesorgvalid");
                bool isAgentValid = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesagentvalid");
                bool isTrustWorthy = caseEntity.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                bool isActivityCodeValid = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesactivitycodevalid");               




                string postAutoValidationQueue = AutomatedValDefinition.config.postAutoValidationQueue;
                string postAutoValidationQueueHighPriority = AutomatedValDefinition.config.postAutoValidationQueueHighPriority;

                bool dispRulesAutoCloseQualify = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesautoclosequalify");
                bool closeOnAutoCloseDisp = AutomatedValDefinition.config.closeOnAutoCloseDisposition;

                dynamic customRule = null;

                string tsOrgId = account.GetAttributeValue<string>("accountnumber");
                bool hasOpenOrders = DynamicsProcessesHelper.orgHasOpenOrders(tsOrgId);
                #endregion


                

                #region AutoClose
                if (closeOnAutoCloseDisp)
                {
                    bool autoQualify = false;

                    if (isOrgValid && isAgentValid && isTrustWorthy && isActivityCodeValid)
                    {
                        autoQualify = true;
                    }
                    else
                    {

                        List<dynamic> autoCloseCustomeRules = ((JArray)AutomatedValDefinition.config.autoCloseCustomRules).ToList<dynamic>();

                        if (autoCloseCustomeRules.Exists(item => (string)item.rule == "ValidOrgAgentTrustACUpdate"))
                        {
                            

                            EntityReference validationDispositionActivityCodeRef = caseEntity.GetAttributeValue<EntityReference>("ts_validationdispositionactivitycode");

                            string activityCodeFinal = caseEntity.GetAttributeValue<string>("ts_validationdispositionactivitycodematch");

                            bool isEligWithDispActivityCode = DynamicsProcessesHelper.isOrgEligibleDiffActivityCode(tsOrgId, activityCodeFinal);

                            if (
                                isOrgValid && isAgentValid && isTrustWorthy && !isActivityCodeValid && validationDispositionActivityCodeRef != null 
                                && 
                                    (
                                    !hasOpenOrders
                                    || (hasOpenOrders && isEligWithDispActivityCode)
                                    )
                                )
                            {
                                autoQualify = true;
                                customRule = autoCloseCustomeRules.Find(item => (string)item.rule == "ValidOrgAgentTrustACUpdate");
                            }
                        }
                    }


                    if (autoQualify)
                    {
                        caseEntity["ts_casestatus"] = new OptionSetValue(102056); //102056 - 'OQ - Qualified'
                        DynamicsInterface.DataverseClient.Update(caseEntity);

                        string postAutoCloseQueue = AutomatedValDefinition.config.postAutoCloseQueue;
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, postAutoCloseQueue);

                        if (customRule != null)
                        {
                            string noteTitle = "Rule Used for AutoQualification";

                            string noteDesc = "Org was auto-qualified using the following rule:" + Environment.NewLine + Environment.NewLine;
                            noteDesc += customRule.description;

                            processSystemNote(noteTitle, noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                        }

                        DynamicsProcessesHelper.sendOrgQualifiedEmail(account.Id);
                        return;
                    }
                }
                #endregion




                #region ParameterChecks
                bool isValidationServicesSource = DynamicsProcessesHelper.isValidationServices(account); 

                bool isHighPriority = (isValidationServicesSource || hasOpenOrders) ? true : false;


                string orgName = caseEntity.GetAttributeValue<string>("ts_validationdispositionorgname");
                //account.GetAttributeValue<string>("name");

                if (orgName.ToLower().Contains("library"))
                {
                    routeToValidationRequired(caseEntity, account, validationReqTransactionId, queueName, isHighPriority);
                    caseEntity["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                    DynamicsInterface.DataverseClient.Update(caseEntity);
                    return;
                }
                #endregion



                #region EmailOutreach
                bool emailOutreachCriteria = evaluateCriteriaEmailOutreach(caseEntity, account, validationReqTransactionId);
                if (emailOutreachCriteria)
                {
                    processEmailOutreach(caseEntity, account, validationReqTransactionId, queueName, hasOpenOrders);
                    return;

                }
                #endregion


                #region Routing
                applyRoutingRules(caseEntity, account, validationReqTransactionId, queueName, isHighPriority);
                #endregion
               

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in resolveActionAutoValidation(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                    );
            }
        }


        public static void applyRoutingRules(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName, bool isHighPriority)
        {
            try
            {
                List<dynamic> autoCloseCustomeRules = ((JArray)AutomatedValDefinition.config.autoCloseCustomRules)?.ToList<dynamic>();
                List<dynamic> queueRoutingRules = ((JArray)AutomatedValDefinition.queueRoutingRules)?.ToList<dynamic>();


                List<dynamic> currentQueueRoutingRules = queueRoutingRules?.Where(rule => rule.routeFromQueue == queueName)?.ToList();

                foreach (dynamic rule in currentQueueRoutingRules)
                {
                    evaluateRoutingRule(rule, caseEntity, account, validationReqTransactionId, queueName, isHighPriority);
                }

                
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in applyRoutingRules(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
            }
        }



        public static void evaluateRoutingRule(dynamic rule, Entity caseEntity, Entity account, string validationReqTransactionId, string queueName, bool isHighPriority)
        {
            try
            {

                bool isOrgValiDispRules = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesorgvalid");
                bool isAgentValidDispRules = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesagentvalid");


                bool isOrgValidDisposition = caseEntity.GetAttributeValue<bool>("ts_validationorgdisposition");
                bool isAgentValidDisposition = caseEntity.GetAttributeValue<bool>("ts_validationagentdisposition");


                bool isTrustWorthy = caseEntity.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                bool isActivityCodeValid = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesactivitycodevalid");              



                string IRSOrgName = caseEntity.GetAttributeValue<string>("ts_validationdispositionirsorgname");


                
                string routeToQueue = rule.routeToQueue;
                string routingCriteria = rule.routingCriteria;
                switch (routingCriteria)
                {
                    case "ValDisposition-OrgAgentValid-Trustworthy-OrgNameFromIRSAvailable-ActivityCodeInvalid":

                        if (
                            isOrgValidDisposition && isAgentValidDisposition && isTrustWorthy && !isActivityCodeValid && !string.IsNullOrEmpty(IRSOrgName)
                            )
                        {
                            DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, routeToQueue);
                            caseEntity["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                            DynamicsInterface.DataverseClient.Update(caseEntity);
                            return;
                        }

                        break;

                    case "Not03IRSSubsection":

                        string IRSSubsection = caseEntity.GetAttributeValue<string>("ts_validationdispositionirssubsection");

                        if (!string.IsNullOrEmpty(IRSSubsection) && IRSSubsection != "03")
                        {
                            DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, routeToQueue);
                            caseEntity["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                            DynamicsInterface.DataverseClient.Update(caseEntity);
                            return;
                        }

                        break;


                }

                routeToValidationRequired(caseEntity, account, validationReqTransactionId, queueName, isHighPriority);
                caseEntity["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                DynamicsInterface.DataverseClient.Update(caseEntity);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in evaluateRoutingRule(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
            }
        }
        public static void routeToValidationRequired(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName, bool isHighPriority)
        {
            try
            {
                string libraryQueue = AutomatedValDefinition.config.libraryQueue;
                
                EntityReference qualCodeRef = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid");
                Entity qualCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", qualCodeRef.Id, new ColumnSet(true));

                string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");

                if (qualCode == "us-lib-fscs")
                {
                    DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, libraryQueue);
                    return;
                }

                string postAutoValidationQueue = AutomatedValDefinition.config.postAutoValidationQueue;
                string postAutoValidationQueueHighPriority = AutomatedValDefinition.config.postAutoValidationQueueHighPriority;

                string nextQueue = isHighPriority ? postAutoValidationQueueHighPriority : postAutoValidationQueue;

                DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, nextQueue);
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in routeToValidationRequired(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
            }
        }
        public static void determineAction(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName)
        {
            try
            {
                #region AutomatedValDefinition
                string postAutoValidationQueue = AutomatedValDefinition.config.postAutoValidationQueue == null ? "AutoValidation Inconclusive" : AutomatedValDefinition.config.postAutoValidationQueue;
                string postAutoValidationQueueHighPriority = AutomatedValDefinition.config.postAutoValidationQueueHighPriority;
                string outreachQueueName = AutomatedValDefinition.emailOutreachProcess.queueName;
                string outreachQueueHighPriority = AutomatedValDefinition.emailOutreachProcess.queueNameHighPriority;
                string pendingActivityCodeDecisionQueue = ((JArray)AutomatedValDefinition.queueRoutingRules)?
                                                                                                .ToList<dynamic>()?.Where(rule =>
                                                                                                rule.routingCriteria == "ValDisposition-OrgAgentValid-Trustworthy-OrgNameFromIRSAvailable-ActivityCodeInvalid")?
                                                                                               .Select(rule => (string)rule.routeToQueue)?.FirstOrDefault();

                string not03IRSSubsectionnQueue = ((JArray)AutomatedValDefinition.queueRoutingRules)?
                                                                                                .ToList<dynamic>()?.Where(rule =>
                                                                                                rule.routingCriteria == "Not03IRSSubsection")?
                                                                                               .Select(rule => (string)rule.routeToQueue)?.FirstOrDefault();


                int tsCaseStatusValue = caseEntity.GetAttributeValue<OptionSetValue>("ts_casestatus").Value;
                string tsCaseStatusText = caseEntity.FormattedValues["ts_casestatus"];

                List<string> directInterventionQueues = new List<string>();

                directInterventionQueues.Add(postAutoValidationQueue);
                directInterventionQueues.Add(postAutoValidationQueueHighPriority);
                directInterventionQueues.Add(pendingActivityCodeDecisionQueue);
                directInterventionQueues.Add(not03IRSSubsectionnQueue);
                #endregion


                #region IdentifyingQueues

                #region AutoValidation
                if (queueName == "AutoValidation")
                {
                    resolveActionAutoValidation(caseEntity, account, validationReqTransactionId, queueName);
                }
                #endregion

                #region Direct Involvement
                if (directInterventionQueues.Contains(queueName))
                {
                    if (tsCaseStatusText.ToLower().Contains("awaiting"))
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, outreachQueueName);

                }
                #endregion


                #region EmailOutreach
                if (queueName == outreachQueueName || queueName == outreachQueueHighPriority)
                {

                    bool isValidationServicesSource = DynamicsProcessesHelper.isValidationServices(account);

                    string tsOrgId = account.GetAttributeValue<string>("accountnumber");
                    bool hasOpenOrders = DynamicsProcessesHelper.orgHasOpenOrders(tsOrgId);

                    bool isHighPriority = (isValidationServicesSource || hasOpenOrders) ? true : false;


                    string nextQueue = ""; 

                    //if (queueName != nextQueue)
                    //    DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, nextQueue);


                   

                    if (tsCaseStatusValue == 104699) //OQ - AutoValidation - Customer Has Responded
                    {
                        nextQueue = isHighPriority ? postAutoValidationQueueHighPriority : postAutoValidationQueue;
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, nextQueue);
                        return;
                    }


                    if (tsCaseStatusValue != 104698 && tsCaseStatusValue != 104699) //104698 - OQ - AutoValidation - Awaiting Customer Response; 104699 - OQ - AutoValidation - Customer Has Responded
                    {
                        processEmailOutreach(caseEntity, account, validationReqTransactionId, queueName, hasOpenOrders);
                        return;
                    }

                    nextQueue = hasOpenOrders ? outreachQueueHighPriority : outreachQueueName;
                    //isHighPriority ? outreachQueueHighPriority : outreachQueueName;
                    //hasOpenOrders ? outreachQueueHighPriority : outreachQueueName;
                    if (queueName != nextQueue)
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, nextQueue);

                }
                #endregion

                #endregion
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in determineAction(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                    );
            }
        }

        public static void processEmailOutreach(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName, bool hasOpenOrders)
        {
            try
            {
                DateTime validationRequestDate = caseEntity.GetAttributeValue<DateTime>("ts_validationrequestdate");
                QueryExpression queryEmail = new QueryExpression("email");
                queryEmail.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, caseEntity.Id);
                //queryEmail.Criteria.AddCondition("createdon", ConditionOperator.GreaterThan, validationRequestDate);
                queryEmail.Criteria.AddCondition("subject", ConditionOperator.Equal, "TechSoup: Action Required for TechSoup Validation");
                EntityCollection emailCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEmail);

                if (emailCollection.Entities.Count > 0)
                    return;
                
                
                Entity email = new Entity("email");

                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, "Support");
                EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                if (entityCollection.Entities.Count == 0)
                {
                    DynamicsInterface.writeToLog("At processEmailOutreach(). No maibox queue found with name: " + "Support");
                    return;
                }

                Guid queueId = entityCollection.Entities.First().Id;

                EntityCollection fromParties = new EntityCollection();

                Entity fromQueue = new Entity("activityparty");
                fromQueue["partyid"] = new EntityReference("queue", queueId);
                fromParties.Entities.Add(fromQueue);

                EntityCollection toParties = new EntityCollection();

                Entity toparty = new Entity("activityparty");
                toparty["partyid"] = new EntityReference("account", account.Id);
                toParties.Entities.Add(toparty);



                email["from"] = fromParties;
                email["to"] = toParties;

                email["subject"] = "To be replaced";
                email["description"] = "To be replaced";
                email["directioncode"] = true;


                string templateName = AutomatedValDefinition.emailOutreachProcess.emailTemplate;
                Entity template = DynamicsProcessesHelper.getTemplateEntity(templateName);


                SendEmailFromTemplateRequest request = new SendEmailFromTemplateRequest()
                {
                    Target = email,

                    TemplateId = template.Id,

                    RegardingId = account.Id,
                    RegardingType = "account"
                };

                SendEmailFromTemplateResponse response = (SendEmailFromTemplateResponse)DynamicsInterface.DataverseClient.Execute(request);


                if (response.Id != Guid.Empty)
                {
                    email = DynamicsInterface.DataverseClient.Retrieve("email", response.Id, new ColumnSet(true));

                    email["regardingobjectid"] = new EntityReference("incident", caseEntity.Id);

                    DynamicsInterface.DataverseClient.Update(email);

                    caseEntity["ts_casestatus"] = new OptionSetValue(104698); //OQ - AutoValidation - Awaiting Customer Response

                    DynamicsInterface.DataverseClient.Update(caseEntity);

                    string outreachQueueName = AutomatedValDefinition.emailOutreachProcess.queueName;
                    string outreachQueueHighPriority = AutomatedValDefinition.emailOutreachProcess.queueNameHighPriority;

                    string nextQueue = hasOpenOrders ? outreachQueueHighPriority : outreachQueueName;

                    if(nextQueue != queueName)
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, nextQueue);
                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processEmailOutreach(...). Exception message: " + Environment.NewLine + e.Message
                                                                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                                                                );
            }

        }
        public static bool evaluateCriteriaEmailOutreach(Entity caseEntity, Entity account, string validationReqTransactionId)
        {
            bool emailOutreachCriteria = false;
            try
            {
                bool autoOrgEmailOutreachEnabled = AutomatedValDefinition.emailOutreachProcess.autoOrgOutreachEnabled == null ? false : AutomatedValDefinition.emailOutreachProcess.autoOrgOutreachEnabled;

                if (!autoOrgEmailOutreachEnabled)
                    return false;
                
               

                bool isOrgValidDispostion = caseEntity.GetAttributeValue<bool>("ts_validationorgdisposition");
                bool isAgentValidDisposition = caseEntity.GetAttributeValue<bool>("ts_validationagentdisposition");
                bool isDispositionTrustworthy =  caseEntity.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                bool isLegalEquivalencyDisp = caseEntity.GetAttributeValue<bool>("ts_validationlegalequivalencedisposition");

                if (
                    !(isOrgValidDispostion && isAgentValidDisposition && isDispositionTrustworthy && isLegalEquivalencyDisp)
                    )
                    emailOutreachCriteria = true;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in evaluateCriteriaEmailOutreach(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                    );
            }

            return emailOutreachCriteria;
        }

        public static bool getProcessingApproval(Entity caseEntity, Entity account, string queueName)
        {
            bool okToProcess = false;

            try
            {
                int validationRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");

                int topCount = AutomatedValDefinition.config.maximumCount;

                if (validationRequestChecksCount >= topCount && topCount != -1)
                {
                    string validationReqTransactionId = caseEntity.GetAttributeValue<string>("ts_validationrequesttransactionid");

                    removeCaseFromAutomatedValidation(caseEntity, account, validationReqTransactionId, queueName);

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


        public static void removeCaseFromAutomatedValidation(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName)
        {
            string noteDesc = "There was no validation resolution after the conclusion of the time allotted for this process";

            string subject = "ValidationDisposition:" + validationReqTransactionId + ":NotResolved";


            removeCaseFromAutomatedValidation(caseEntity, account, validationReqTransactionId, subject, noteDesc, queueName);
        }
        public static void removeCaseFromAutomatedValidation(Entity caseEntity, Entity account, string validationReqTransactionId, string noteTitle, string noteDesc, string queueName)
        {
            try
            {
                processSystemNote(noteTitle, noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));

                bool isValidationServicesSource = DynamicsProcessesHelper.isValidationServices(account);

                string tsOrgId = account.GetAttributeValue<string>("accountnumber");
                bool hasOpenOrders = DynamicsProcessesHelper.orgHasOpenOrders(tsOrgId);

                bool isHighPriority = (isValidationServicesSource || hasOpenOrders) ? true : false;

                routeToValidationRequired(caseEntity, account, validationReqTransactionId, queueName, isHighPriority);
                caseEntity["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                DynamicsInterface.DataverseClient.Update(caseEntity);
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in removeCaseFromAutomatedValidation(). Exception message: " + Environment.NewLine + e.Message );
            }
        }

        public static bool removeSystemNote(string noteTitle, EntityReference annotationParentRef)
        {
            bool existsNote = false;
            try
            {
                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryAnnotation);

                if (annotationCollection.Entities.Count() > 0)
                    DynamicsInterface.DataverseClient.Delete(annotationCollection.Entities.First().LogicalName, annotationCollection.Entities.First().Id);
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in existsSystemNote(). Exception message: " + Environment.NewLine + e.Message);
            }
            return existsNote;
        }

        public static bool existsSystemNote(string noteTitle, EntityReference annotationParentRef)
        {
            bool existsNote = false;
            try
            {
                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryAnnotation);
                
                if (annotationCollection.Entities.Count() > 0)
                    existsNote = true;   
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in existsSystemNote(). Exception message: " + Environment.NewLine + e.Message);
            }
            return existsNote;
        }
        public static void processSystemNote(string noteTitle, string noteDesc, EntityReference annotationParentRef)
        {
            try
            {               
                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryAnnotation);

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
                    annotation["objectid"] = annotationParentRef;
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
                    DynamicsInterface.DataverseClient.Update(annotation);
                }
                else
                {
                    Guid annotationId = DynamicsInterface.DataverseClient.Create(annotation);
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processSystemNote(). Exception message: " + Environment.NewLine + e.Message  );
            }
        }
        public static dynamic getCtpOrgData(string ctpOrgId, string validationReqTransactionId)
        {
            IDictionary<string, Object> ctpOrgEntity = new ExpandoObject() as IDictionary<string, Object>;

            try
            {

                HttpClient client = new HttpClient();
                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                string requestUrl = CTPUrl + "services/lookup/v_001/" + CTPSessionKey + "?org_id=" + ctpOrgId;


                HttpResponseMessage response = client.GetAsync(requestUrl).Result;

                string responseTxt = response.Content.ReadAsStringAsync().Result;


                IDictionary<string, Object> orgDataResponseExpando = JsonConvert.DeserializeObject<ExpandoObject>(responseTxt) as IDictionary<string, Object>;



                dynamic orgDataResponse = JsonConvert.DeserializeObject(responseTxt);

                List<dynamic> orgDataList = ((JArray)orgDataResponse.returnStatus.data).ToList<dynamic>();

                if (orgDataList.Count == 0)
                    return null;

                
                dynamic orgData = orgDataList.First();

                List<dynamic> financials = ((JArray)orgData.financials).ToList<dynamic>();
                dynamic operatingBudget = financials.Find(item => item.type == "operatingBudget");
                string budget = operatingBudget == null ? "" : operatingBudget.type_value;

                string orgCountry = orgData.country_code;
                string instanceId = orgData.instance_id;

                List<dynamic> purposes = ((JArray)orgData.purposes).ToList<dynamic>();
                dynamic subActivity = purposes.Find(item => item.type == "subActivity");
                string activityCode = subActivity == null ? "" : subActivity.type_value;

                dynamic name = orgData.name;
                string nameType = name.type;
                string orgName = name.type_value;


                List<dynamic> locations = ((JArray)orgData.locations).ToList<dynamic>();
                dynamic mainAddress = locations.Find(item => item.type == "main");

                string address1 = mainAddress == null ? "" : mainAddress.address;
                string address2 = mainAddress == null ? "" : (mainAddress.address_ext == "nil" ? null : mainAddress.address_ext);
                string city = mainAddress == null ? "" : mainAddress.city;
                string state = mainAddress == null ? "" : mainAddress.state_region;
                string postalCode = mainAddress == null ? "" : mainAddress.postal_code;
                string country = mainAddress == null ? "" : mainAddress.country_id;

                dynamic legalAddress = locations.Find(item => item.type == "legal");

                IDictionary<string, Object> legalAddressEntity = new ExpandoObject() as IDictionary<string, Object>;
                if (legalAddress != null)
                {
                    string legalAddress1 = (string)legalAddress.address;
                    string legalAddress2 = (string)legalAddress.address_ext == "nil" ? null : legalAddress.address_ext;
                    string legalAddressCity = (string)legalAddress.city;
                    string legalAddressState = (string)legalAddress.state_region;
                    string legalAddressPostalCode = (string)legalAddress.postal_code;
                    string legalAddressCountry = (string)legalAddress.country_id;

                    legalAddressEntity.Add("address1", legalAddress1);
                    legalAddressEntity.Add("address2", legalAddress2);
                    legalAddressEntity.Add("city", legalAddressCity);
                    legalAddressEntity.Add("state", legalAddressState);
                    legalAddressEntity.Add("postalCode", legalAddressPostalCode);
                    legalAddressEntity.Add("country", legalAddressCountry);


                }

                List<dynamic> websites = ((JArray)orgData.websites).ToList<dynamic>();
                string url = websites.Count == 0 ? "" : websites.First().type_value;


                List<dynamic> legalIdentifierList = ((JArray)orgData.legal_identifier).ToList<dynamic>();

                string legalIdentifier = "";
                string legalIdType = "";
                if (legalIdentifierList.Count > 0)
                {
                    legalIdType = legalIdentifierList.First().type;
                    legalIdentifier = legalIdentifierList.First().type_value;
                }

                List<dynamic> descriptiveTexts = ((JArray)orgData.descriptive_texts).ToList<dynamic>();

                dynamic missionObject = descriptiveTexts.Find(item => item.type == "missionStatement");

                string missionStatement = missionObject == null ? "" : (missionObject.type_value == "nil" ? "" : missionObject.type_value);



                dynamic status = orgData.status;

                status.typeValue = status.type_value;


                ctpOrgEntity.Add("orgName", orgName);
                ctpOrgEntity.Add("budget", budget);
                ctpOrgEntity.Add("activityCode", activityCode);

                ctpOrgEntity.Add("orgCountry", orgCountry);
                ctpOrgEntity.Add("instanceId", instanceId);



                ctpOrgEntity.Add("address1", address1);
                ctpOrgEntity.Add("address2", address2);
                ctpOrgEntity.Add("city", city);
                ctpOrgEntity.Add("state", state);
                ctpOrgEntity.Add("postalCode", postalCode);
                ctpOrgEntity.Add("country", country);
                ctpOrgEntity.Add("legalAddress", legalAddressEntity);
                ctpOrgEntity.Add("url", url);
                ctpOrgEntity.Add("legalIdentifier", legalIdentifier);
                ctpOrgEntity.Add("legalIdType", legalIdType);
                ctpOrgEntity.Add("missionStatement", missionStatement);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getCtpOrgData(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId  + "; ctpOrgId: " + ctpOrgId
                    );
            }

            return (dynamic)ctpOrgEntity;
        }


        public static void updateOrg(Entity account, string validationReqTransactionId, string ctpOrgId)
        {
            try
            {
                dynamic ctpOrgEntity = getCtpOrgData(ctpOrgId, validationReqTransactionId);

                account["name"] = ctpOrgEntity.orgName;

                account["ts_ctporgid"] = ctpOrgId;

                account["address1_line1"] = ctpOrgEntity.address1;
                account["address1_line2"] = ctpOrgEntity.address2;
                account["address1_city"] = ctpOrgEntity.city;
                account["address1_stateorprovince"] = ctpOrgEntity.state;
                account["address1_country"] = ctpOrgEntity.country;
                account["address1_postalcode"] = ctpOrgEntity.postalCode;

                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, ctpOrgEntity.country);
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
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, ctpOrgEntity.country);
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, ctpOrgEntity.state);
                fieldMapCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    account["ts_stateprovdesc"] = new OptionSetValue(countryOptionValue);
                }


                //account["emailaddress1"] =

                account["new_legalidentifier"] = ctpOrgEntity.legalIdentifier;
                account["websiteurl"] = ctpOrgEntity.url;
                account["new_budget"] = ctpOrgEntity.budget;

                QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, ctpOrgEntity.activityCode);
                EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                if (entityCollection.Entities.Count > 0)
                    account["new_activitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);


                DynamicsInterface.DataverseClient.Update(account);

                DynamicsProcessesHelper.addLegalAddress(account.Id, (dynamic)ctpOrgEntity.legalAddress);
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateOrg(). Exception message: " + Environment.NewLine + e.Message
                                                                     + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                                    );
            }
        }

        public static void processOrgQualified(Entity caseEntity, Entity account, string validationReqTransactionId)
        {
            try
            {
                string ctpOrgIdFinal = caseEntity.GetAttributeValue<string>("ts_validationrequestctporgidfinal");


                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(true);
                queryAccount.Criteria.AddCondition("ts_ctporgid", ConditionOperator.Equal, ctpOrgIdFinal);
                queryAccount.Criteria.AddCondition("accountid", ConditionOperator.NotEqual, account.Id);
                EntityCollection accountCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryAccount);


                if (accountCollection.Entities.Count == 0)
                {
                    caseEntity["ts_casestatus"] = new OptionSetValue(102056); //102056 - 'OQ - Qualified'
                    DynamicsInterface.DataverseClient.Update(caseEntity);
                    return;
                }

                Entity ctpOrgIdAccount = accountCollection.Entities.First();

                EntityReference orgDesigRef = ctpOrgIdAccount.GetAttributeValue<EntityReference>("new_orgdesignation");

                EntityReference qualCodeRef = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid");

                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;
                Guid qualCodeId = qualCodeRef == null ? Guid.Empty : qualCodeRef.Id;


                if (orgDesigId != qualCodeId)
                {
                    if (orgDesigId != Guid.Empty)
                    {
                        caseEntity["ts_qualificationcodeid"] = orgDesigRef;
                    }
                    else
                    {
                        DynamicsInterface.writeToLog("Error in processOrgQualified(). orgDesigId, " + orgDesigId + ", and qualCodeId, " + qualCodeId + ", are different");
                        return;
                    }
                }

                Entity qualCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", qualCodeRef.Id, new ColumnSet(true));

                string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                string qualTerm = qualCodeEntity.FormattedValues["new_qualterm"];
                string qualCategory = qualCodeEntity.GetAttributeValue<string>("new_qualcategory");
                string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");

                string orgQualStatus = DynamicsProcessesHelper.getOrgQualStatus(ctpOrgIdAccount.Id, qualCodeId);

                bool setNewOrgQualStatusDate = false;

                if (orgQualStatus == "Qualified")
                    setNewOrgQualStatusDate = true;

                caseEntity["customerid"] = new EntityReference("account", ctpOrgIdAccount.Id);
                DynamicsInterface.DataverseClient.Update(caseEntity);

                caseEntity["ts_casestatus"] = new OptionSetValue(102056); //102056 - 'OQ - Qualified'
                DynamicsInterface.DataverseClient.Update(caseEntity);

                if (setNewOrgQualStatusDate)
                    DynamicsProcessesHelper.setNewOrgQualStatusDate(orgDesigId, ctpOrgIdAccount.Id, DateTime.UtcNow);

                EntityReference dupeOrgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");

                //2 - Qualification Case; 101996 - Organization Qualification; 102074 -  OQ - Cancelled	  
                if (dupeOrgDesigRef == null)
                    return;
                
                Guid caseId = DynamicsProcessesHelper.createCase(qualCode + " - " + qualName, 2, 101996, 102074, account.Id, dupeOrgDesigRef.Id, null);

                string name = account.GetAttributeValue<string>("name");

                account["name"] = "[Duplicate] " + name;
                account["ts_duplicateofid"] = new EntityReference("account", ctpOrgIdAccount.Id);

                DynamicsInterface.DataverseClient.Update(account);


                updateOrg(ctpOrgIdAccount, validationReqTransactionId, ctpOrgIdFinal);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processOrgQualified(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId + "; caseEntity.Id: " + caseEntity.Id
                    );
            }
        }
        public static void getValidationStatus(Entity caseEntity, Entity account, string validationReqTransactionId)
        {
            try
            {
                HttpClient client = new HttpClient();
                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                string requestUrl = CTPUrl + "services/validation/v_001/" + CTPSessionKey + "/" + validationReqTransactionId;

                DynamicsInterface.writeToLog("getValidationStatus requestUrl: " + requestUrl);

                HttpResponseMessage response = client.GetAsync(requestUrl).Result;
                string responseTxt = response.Content.ReadAsStringAsync().Result;

                //DynamicsInterface.writeToLog("getValidationStatus responseTxt: " + responseTxt);

                dynamic validationResponse = JsonConvert.DeserializeObject(responseTxt);

                int statusCode = validationResponse.returnStatus.status_code;
                string validationRequestStatus = validationResponse.returnStatus.data.Transaction;
                string validationRequestStage = validationResponse.returnStatus.data["Transaction Phase"];
                string validationRequestQualStatus = validationResponse.returnStatus.data.Qualification;
                JArray orgIds = (JArray)validationResponse.returnStatus.data.OrgId;

                string validationRequestCtpOrgId = "";
                if (orgIds.Count() > 1)
                {
                    validationRequestCtpOrgId = string.Join(",", orgIds);

                    string dupesNoteDesc = "CTPOrgIds of potential duplicates: " + Environment.NewLine + Environment.NewLine;
                    dupesNoteDesc += validationRequestCtpOrgId;
                    processSystemNote("ValidationRequest:" + validationReqTransactionId + ":Potential Duplicates Found", dupesNoteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                }
                else if (orgIds.Count() == 1)
                {
                    validationRequestCtpOrgId = orgIds.First().ToString();
                }

                string noteTitle = "ValidationRequest:" + validationReqTransactionId + ":StatusCheck";
                string noteDesc = "validationStatusResponse on " + DateTime.Now.ToString("MM/dd/yyyy hh:mm:ss tt") + ": "
                    + Environment.NewLine + Environment.NewLine + responseTxt
                    ;
                processSystemNote(noteTitle, noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));



                if (statusCode == 200)
                {

                    caseEntity["ts_validationrequeststatus"] = validationRequestStatus;
                    caseEntity["ts_validationrequeststage"] = validationRequestStage;
                    caseEntity["ts_validationrequestqualstatus"] = validationRequestQualStatus;
                    caseEntity["ts_validationrequestlaststatuscheck"] = DateTime.UtcNow;

                    int validationRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");
                    validationRequestChecksCount++;
                    caseEntity["ts_validationstatuscheckscount"] = validationRequestChecksCount;

                    string tsCaseStatusDesc = caseEntity.Contains("ts_casestatus") ? caseEntity.FormattedValues["ts_casestatus"] : "";                    


                    if (validationRequestStatus.ToLower() == "closed")
                    {
                        if (validationRequestQualStatus.ToLower() == "qualified")
                        {                            
                            caseEntity["ts_validationrequestctporgidfinal"] = validationRequestCtpOrgId;
                            

                            noteTitle = "ValidationRequest:" + validationReqTransactionId + ":Qualified";
                            noteDesc = "The Validation Disposition for this organization is: 'Qualified'";
                            processSystemNote(noteTitle, noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));

                            processOrgQualified(caseEntity, account, validationReqTransactionId);        
                            
                            return;
                        }
                        else if (validationRequestQualStatus.ToLower() == "not qualified")
                        {
                            caseEntity["ts_casestatus"] = new OptionSetValue(102057);//102057 - OQ - Disqualified

                            noteTitle = "ValidationRequest:" + validationReqTransactionId + ":NotQualified";
                            noteDesc = "The Validation Disposition for this organization is: 'Not Qualified'";
                            processSystemNote(noteTitle, noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                        }
                        else
                        {
                            caseEntity["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                        }
                    }
                    else
                    {
                        caseEntity["ts_validationrequestctporgid"] = validationRequestCtpOrgId;

                        if (tsCaseStatusDesc == "OQ - Not Started")
                            caseEntity["ts_casestatus"] = new OptionSetValue(104695); //104695 - 'OQ - Automated Validation - Awaiting Disposition'
                    }


                    DynamicsInterface.DataverseClient.Update(caseEntity);


                    int caseStatus = caseEntity.GetAttributeValue<OptionSetValue>("ts_casestatus").Value;
                    int checkCountsForManagedQueue = AutomatedValDefinition.config.checkCountsForManagedQueue == null ? 0 : AutomatedValDefinition.config.checkCountsForManagedQueue;
                    string postAutoValidationQueue = AutomatedValDefinition.config.postAutoValidationQueue == null ? "AutoValidation Inconclusive" : AutomatedValDefinition.config.postAutoValidationQueue;

                    if (validationRequestStatus.ToLower() != "closed" && caseStatus == 104695 && validationRequestChecksCount >= checkCountsForManagedQueue)
                    {
                        caseEntity["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, postAutoValidationQueue);
                    }
                }
                else
                {
                    int validationRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");
                    validationRequestChecksCount++;
                    caseEntity["ts_validationstatuscheckscount"] = validationRequestChecksCount;

                    DynamicsInterface.DataverseClient.Update(caseEntity);
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getValidationStatus(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId + "; caseEntity.Id: " + caseEntity.Id
                    );
            }

        }

        public static void initiateDispositionRequest(Entity caseEntity, Entity account, string queueName)
        {
            try
            {
                string validationRequest = getValidationRequest(account);

                if (string.IsNullOrEmpty(validationRequest))
                    return;
                

                if (validationRequest == "noagent")
                {
                    removeCaseFromAutomatedValidation(caseEntity, account
                        , "NoTransactionId"
                        , "Org Has No Agent"
                        , "Removing Qualification Case from Automated Validation Queue, and reassigning it to normal workflow Queue"
                        , queueName);

                    return;
                }


                HttpClient client = new HttpClient();
                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                StringContent request = new StringContent(validationRequest, Encoding.UTF8, "application/json");

                string requestUrl = CTPUrl + "services/oidovalidation/v_001/" + CTPSessionKey;

                HttpResponseMessage response = client.PostAsync(requestUrl, request).Result;

                string responseTxt = response.Content.ReadAsStringAsync().Result;


                dynamic validationResponse = JsonConvert.DeserializeObject(responseTxt);

                int statusCode = validationResponse.returnStatus.status_code;
                string validationReqTransactionId = validationResponse.returnStatus.data.TransactionId == null ? "" : validationResponse.returnStatus.data.TransactionId;


                Entity annotation = new Entity("annotation");

                annotation["subject"] = "DispositionRequest:" + validationReqTransactionId;


                string noteDesc = "validationRequest: " + Environment.NewLine + validationRequest
                                    + Environment.NewLine + Environment.NewLine
                                    + "validationResponse: " + Environment.NewLine + Environment.NewLine + responseTxt
                                    ;

                var noteDirectives = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                noteDirectives.Add("sectionStart", "NoteSpecialDirectives");
                noteDirectives.Add("systemNote", true);
                noteDirectives.Add("sectionEnd", "NoteSpecialDirectives");
                string noteDirectivesJson = JsonConvert.SerializeObject(noteDirectives);

                noteDesc += string.Concat(Enumerable.Repeat(Environment.NewLine, 8).ToArray()) + noteDirectivesJson;

                annotation["notetext"] = noteDesc;
                annotation["objectid"] = new EntityReference("incident", caseEntity.Id);


                Guid annotationId = DynamicsInterface.DataverseClient.Create(annotation);

                dynamic validationRequestObj = JsonConvert.DeserializeObject(validationRequest);


                string agentEmail = validationRequestObj.AgentEmail;
                string agentName = validationRequestObj.AgentFirstName + " " + validationRequestObj.AgentLastName;
                string activityCode = validationRequestObj.ActivityCode;

                caseEntity["ts_validationagentemail"] = agentEmail;
                caseEntity["ts_validationagentname"] = agentName;
                caseEntity["ts_validationselfreportedactivitycode"] = activityCode;


                if (statusCode == 200)
                {

                    caseEntity["ts_validationrequesttransactionid"] = validationReqTransactionId;
                    int validationRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");

                    if (validationRequestChecksCount == 0)
                        caseEntity["ts_validationrequestdate"] = DateTime.UtcNow;

                    caseEntity["ts_validationstatuscheckscount"] = 0;

                    caseEntity["ts_casestatus"] = new OptionSetValue(104695); //OQ - Automated Validation - Awaiting Disposition

                    DynamicsInterface.DataverseClient.Update(caseEntity);
                }
                else
                {
                    caseEntity["ts_validationrequesttransactionid"] = validationReqTransactionId;

                    int validationRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");

                    if (validationRequestChecksCount == 0)
                        caseEntity["ts_validationrequestdate"] = DateTime.UtcNow;


                    validationRequestChecksCount++;
                    caseEntity["ts_validationstatuscheckscount"] = validationRequestChecksCount;

                    caseEntity["ts_casestatus"] = new OptionSetValue(104695); //OQ - Automated Validation - Awaiting Disposition

                    DynamicsInterface.DataverseClient.Update(caseEntity);



                    int maxUnsuccessfulInitialRequests = AutomatedValDefinition.config.maxUnsuccessfulInitialRequests;
                    if (validationRequestChecksCount >= maxUnsuccessfulInitialRequests && validationRequestChecksCount != -1)
                    {
                        string subject = "DispositionRequest:" + validationReqTransactionId + ":Error";
                        noteDesc = "There was a problem initiating the Disposition Request after " + validationRequestChecksCount.ToString() + " attempts"
                            + Environment.NewLine + Environment.NewLine + "Removing Qualification Case from Automated Validation Queue, and reassigning it to normal workflow Queue";
                        removeCaseFromAutomatedValidation(caseEntity, account, validationReqTransactionId, subject, noteDesc, queueName);
                    }
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in initiateDispositionRequest(...). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "caseEntity.Id: " + caseEntity.Id
                    );
            }
        }

        public static void initiateValidationRequest(Entity caseEntity, Entity account, string queueName)
        {
            string validationRequest = getValidationRequest(account);

            if (string.IsNullOrEmpty(validationRequest))
                return;


            HttpClient client = new HttpClient();
            HttpRequestHeaders headers = client.DefaultRequestHeaders;
            headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            StringContent request = new StringContent(validationRequest, Encoding.UTF8, "application/json");

            string requestUrl = CTPUrl + "services/validation/v_001/" + CTPSessionKey;

            HttpResponseMessage response = client.PostAsync(requestUrl, request).Result;

            string responseTxt = response.Content.ReadAsStringAsync().Result;


            dynamic validationResponse = JsonConvert.DeserializeObject(responseTxt);

            int statusCode = validationResponse.returnStatus.status_code;
            string validationRequestStatus = validationResponse.returnStatus.data.Transaction;
            string validationReqTransactionId = validationResponse.returnStatus.data.TransactionId;

            if (validationReqTransactionId == null)
                validationReqTransactionId = "";

            string validationRequestStage = validationResponse.returnStatus.data["Transaction Phase"];


            Entity annotation = new Entity("annotation");

            annotation["subject"] = "ValidationRequest:" + validationReqTransactionId;


            string noteDesc = "validationRequest: " + Environment.NewLine + validationRequest
                + Environment.NewLine + Environment.NewLine
                + "validationResponse: " + Environment.NewLine + Environment.NewLine + responseTxt
                ;

            var noteDirectives = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
            noteDirectives.Add("sectionStart", "NoteSpecialDirectives");
            noteDirectives.Add("systemNote", true);
            noteDirectives.Add("sectionEnd", "NoteSpecialDirectives");
            string noteDirectivesJson = JsonConvert.SerializeObject(noteDirectives);

            noteDesc += string.Concat(Enumerable.Repeat(Environment.NewLine, 8).ToArray()) + noteDirectivesJson;

            annotation["notetext"] = noteDesc;
            annotation["objectid"] = new EntityReference("incident", caseEntity.Id);


            Guid annotationId = DynamicsInterface.DataverseClient.Create(annotation);

            dynamic validationRequestObj = JsonConvert.DeserializeObject(validationRequest);


            string agentEmail = validationRequestObj.AgentEmail;
            string agentName = validationRequestObj.AgentFirstName + " " + validationRequestObj.AgentLastName;
            string activityCode = validationRequestObj.ActivityCode;

            caseEntity["ts_validationagentemail"] = agentEmail;
            caseEntity["ts_validationagentname"] = agentName;
            caseEntity["ts_validationselfreportedactivitycode"] = activityCode;


            if (statusCode == 200)
            {
                
                caseEntity["ts_validationrequesttransactionid"] = validationReqTransactionId;
                int validationRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");

                if (validationRequestChecksCount == 0)
                    caseEntity["ts_validationrequestdate"] = DateTime.UtcNow;

                caseEntity["ts_validationrequeststatus"] = validationRequestStatus;
                caseEntity["ts_validationrequeststage"] = validationRequestStage;                

                caseEntity["ts_validationstatuscheckscount"] = 0;

                caseEntity["ts_casestatus"] = new OptionSetValue(104695); //OQ - Automated Validation - Awaiting Disposition

                DynamicsInterface.DataverseClient.Update(caseEntity);
            }
            else
            {
                caseEntity["ts_validationrequesttransactionid"] = validationReqTransactionId;

                int validationRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");

                if (validationRequestChecksCount == 0)
                    caseEntity["ts_validationrequestdate"] = DateTime.UtcNow;

                caseEntity["ts_validationrequeststatus"] = validationRequestStatus;
                caseEntity["ts_validationrequeststage"] = validationRequestStage;

                validationRequestChecksCount++;
                caseEntity["ts_validationstatuscheckscount"] = validationRequestChecksCount;

                caseEntity["ts_casestatus"] = new OptionSetValue(104695); //OQ - Automated Validation - Awaiting Disposition

                DynamicsInterface.DataverseClient.Update(caseEntity);

                

                int maxUnsuccessfulInitialRequests = AutomatedValDefinition.config.maxUnsuccessfulInitialRequests;


                if(validationRequestChecksCount >= maxUnsuccessfulInitialRequests && validationRequestChecksCount != -1)
                {
                    string subject = "ValidationRequest:" + validationReqTransactionId + ":Error";
                    noteDesc = "There was a problem initiating the Validation Request after " + validationRequestChecksCount.ToString() + " attempts"
                        + Environment.NewLine + Environment.NewLine + "Removing Qualification Case from Automated Validation Queue, and reassigning it to normal workflow Queue";
                    removeCaseFromAutomatedValidation(caseEntity, account, validationReqTransactionId, subject, noteDesc, queueName);

                }
            }               

        }


        public static string getValidationRequest(Entity account)
        {
            string validationRequest = string.Empty;
            try
            {

                string tsOrgId = account.GetAttributeValue<string>("accountnumber");
                string orgName = account.GetAttributeValue<string>("name");

                string assignedId = account.GetAttributeValue<string>("new_platformid");
                string email = account.GetAttributeValue<string>("emailaddress1");
                string phone = account.GetAttributeValue<string>("telephone1");
                string legalIdentifier = account.GetAttributeValue<string>("new_legalidentifier");
                string url = account.GetAttributeValue<string>("websiteurl");
                string budget = account.GetAttributeValue<string>("new_budget");
                string isEmailValidCode = account.GetAttributeValue<bool>("new_isemailvalid") ? "0" : "1";
                string associationCode = account.GetAttributeValue<string>("new_associationcode");


                string mission = "nil";


                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                string orgDesignation = string.Empty;
                string orgDesignationDescription = string.Empty;
                Guid orgDesigId = Guid.Empty;
                if (orgDesigRef != null)
                {
                    Entity qualCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", orgDesigRef.Id, new ColumnSet(true));
                    orgDesigId = orgDesigRef.Id;
                    orgDesignation = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                    orgDesignationDescription = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                }

                string customerTypeCode = string.Empty;
                if (account.Contains("customertypecode"))
                    customerTypeCode = account.FormattedValues["customertypecode"];

                OptionSetValue orgSourceOption = account.GetAttributeValue<OptionSetValue>("new_source");
                int orgSource = orgSourceOption == null ? 0 : orgSourceOption.Value;



                EntityReference activityCodeRef = account.GetAttributeValue<EntityReference>("new_activitycode");
                string activityCode = string.Empty;
                string activityCodeDescription = string.Empty;
                string activityCodeCategory = string.Empty;
                if (activityCodeRef != null)
                {
                    Entity activityCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_activitycodes", activityCodeRef.Id, new ColumnSet(true));
                    activityCode = activityCodeEntity.GetAttributeValue<string>("new_activitycode");
                    activityCodeDescription = activityCodeEntity.GetAttributeValue<string>("new_activitycodedescription");
                    activityCodeCategory = activityCodeEntity.GetAttributeValue<string>("new_activitycodecategory");
                }


                string companyTypeCode = account.Contains("ts_countrydesc") ? account.FormattedValues["ts_countrydesc"] : account.GetAttributeValue<string>("address1_country");



                string countryCode = account.GetAttributeValue<string>("address1_country");
                string regionCode = account.GetAttributeValue<string>("address1_stateorprovince");
                string address1 = account.GetAttributeValue<string>("address1_line1");
                string address2 = account.GetAttributeValue<string>("address1_line2");
                if (address2 == null)
                    address2 = "nil";

                string address3 = account.GetAttributeValue<string>("address1_line3");
                string city = account.GetAttributeValue<string>("address1_city");
                string postalCode = account.GetAttributeValue<string>("address1_postalcode");



                EntityReference duplicateOfRef = account.GetAttributeValue<EntityReference>("ts_duplicateofid");
                string duplicateOf = string.Empty;
                if (duplicateOfRef != null)
                {
                    Entity accountDupeOf = DynamicsInterface.DataverseClient.Retrieve("account", duplicateOfRef.Id, new ColumnSet("accountnumber"));
                    duplicateOf = accountDupeOf.GetAttributeValue<string>("accountnumber");
                }


                int numberOfEmployees = account.GetAttributeValue<int>("numberofemployees");


                QueryExpression queryConnection = new QueryExpression("connection");
                queryConnection.ColumnSet = new ColumnSet(true);
                queryConnection.Criteria.AddCondition("record1id", ConditionOperator.Equal, account.Id);
                queryConnection.Criteria.AddCondition("record2objecttypecode", ConditionOperator.Equal, 2);
                queryConnection.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
                EntityCollection connectionCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryConnection);

                if (connectionCollection.Entities.Count() == 0)
                    return "noagent";

                Entity connection = connectionCollection.Entities.First();
                EntityReference contactRef = connection.GetAttributeValue<EntityReference>("record2id");

                Guid contactId = contactRef == null ? Guid.Empty : contactRef.Id;

                Entity contact = DynamicsInterface.DataverseClient.Retrieve("contact", contactId, new ColumnSet(true));

                string contactFirstName = contact.GetAttributeValue<string>("firstname");
                string contactLastName = contact.GetAttributeValue<string>("lastname");
                string contactEmail = contact.GetAttributeValue<string>("emailaddress1");
                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");


                string transIdDevSuffix = "";
                if (DynamicsEnvironments["DynamicsEnvironmentCurrent"] != "prod")
                    transIdDevSuffix += "-dev";

                string transactionPreffix = "tsorgid-";
                    //"batch-onyx-";
                string transactionId = transactionPreffix + tsOrgId + transIdDevSuffix;

                Guid caseId = DynamicsProcessesHelper.findCase(transactionId);

                int counter = 0;
                while (caseId != Guid.Empty && counter < 3)
                {
                    counter++;
                    switch (counter)
                    {
                        case 1:
                            transactionId = transactionPreffix + tsOrgId + transIdDevSuffix + "-" + DateTime.UtcNow.ToString("yyyyMMdd");
                            break;
                        case 2:
                            transactionId = transactionPreffix + tsOrgId + transIdDevSuffix + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
                            break;
                        case 3:
                            transactionId = transactionPreffix + tsOrgId + transIdDevSuffix + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                            break;
                    }
                    caseId = DynamicsProcessesHelper.findCase(transactionId);
                }
               
                
                
                
               



                IDictionary<string, Object> entity = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;

                entity.Add("LegalName", orgName);
                entity.Add("AddressLine1", address1);
                entity.Add("AddressOther", address2);
                entity.Add("AddressCity", city);
                entity.Add("AddressStateRegion", regionCode);
                entity.Add("AddressPostalCode", postalCode);
                entity.Add("AddressCountryId", countryCode);
                entity.Add("Email", email);
                entity.Add("Phone", phone);
                entity.Add("Website", url);
                entity.Add("MissionStatement", mission);
                entity.Add("OperatingBudget", budget);
                entity.Add("ActivityCode", activityCode);
                entity.Add("AgentFirstName", contactFirstName);
                entity.Add("AgentLastName", contactLastName);
                entity.Add("AgentEmail", contactEmail);
                entity.Add("EffectiveDatetime", timestamp);
                entity.Add("TransactionId", transactionId);



                List<dynamic> orgArray = new List<dynamic>();


                var registrationIdentifier = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;

                registrationIdentifier.Add("LegalIdentifier", legalIdentifier);
                registrationIdentifier.Add("RegulatoryBody", "EIN");
                registrationIdentifier.Add("ArtifactURI", false);
                orgArray.Add(registrationIdentifier);

                entity.Add("RegistrationIdentifiers", orgArray);

                validationRequest = Newtonsoft.Json.JsonConvert.SerializeObject(entity);

            }
            catch (Exception e)
            {
                string error = "Error in getValidationRequest(...). Exception message: " + Environment.NewLine + e.Message;
                DynamicsInterface.writeToLog("Error creating Contact record. Exception message: " + error  );
            }

            return validationRequest;
        }
        public static void getValidationScoreMatrix(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName)
        {
            try
            {
                #region Calling Score Matrix
                HttpClient client = new HttpClient();
                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));


                string requestUrl = CTPUrl + "services/vsscorematrix/v_001/" + CTPSessionKey + "?transaction_id=" + validationReqTransactionId;

                HttpResponseMessage response = client.GetAsync(requestUrl).Result;
                string responseTxt = response.Content.ReadAsStringAsync().Result;
                #endregion







                #region Deserializing Response
                dynamic validationResponse = JsonConvert.DeserializeObject(responseTxt);
                dynamic dispositionData = validationResponse.returnStatus.data;


                string dispositionDataText = JsonConvert.SerializeObject(dispositionData);
                caseEntity["ts_validationdispositiondata"] = dispositionDataText;

                string dispositionTransactionId = dispositionData.transaction_id;
                if (validationReqTransactionId != dispositionTransactionId)
                    return;
                #endregion


                #region  Disposition Details
                var scoreMatrixObj = JsonConvert.DeserializeObject<ExpandoObject>(responseTxt) as IDictionary<string, Object>;
                var dispositionScores = (IDictionary<string, Object>)((IDictionary<string, Object>)scoreMatrixObj["returnStatus"])["data"];


                #region Get Disposition Status
                string dispositionStatus = dispositionData.score_matrix_status == null ? "" : dispositionData.score_matrix_status;
                caseEntity["ts_validationdispositionstatus"] = dispositionStatus;

                string noteDesc = "";
                string noteTitle = "";
                if (dispositionStatus != "completed")
                {
                    int checkCountsForManagedQueue = AutomatedValDefinition.config.checkCountsForManagedQueue;

                    caseEntity["ts_validationrequestlaststatuscheck"] = DateTime.UtcNow;
                    int dispositionRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");
                    dispositionRequestChecksCount++;
                    caseEntity["ts_validationstatuscheckscount"] = dispositionRequestChecksCount;

                    if (dispositionRequestChecksCount >= checkCountsForManagedQueue)
                        removeCaseFromAutomatedValidation(caseEntity, account, validationReqTransactionId, queueName);


                    DynamicsInterface.DataverseClient.Update(caseEntity);
                    return;
                }
                #endregion


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





                #region AI Dispositions


                string orgDisposition = DynamicsProcessesHelper.regexMatchValue("\\w+(\\s\\w+)*", (string)dispositionData.org_disposition, 0);
                float orgDispScore = dispositionData.org_disposition_confidence;

                string agentDisposition = DynamicsProcessesHelper.regexMatchValue("\\w+(\\s\\w+)*", (string)dispositionData.agent_disposition, 0);
                float agentDispScore = dispositionData.agent_disposition_confidence;

                string trustworthyDisposition = DynamicsProcessesHelper.regexMatchValue("\\w+(\\s\\w+)*", (string)dispositionData.risk_disposition, 0);
                float trustworthyConfidence = dispositionData.risk_disposition_confidence;

                string activityCodeFinal = dispositionData.act_code_final;

                string legalEquivalenceDisposition = DynamicsProcessesHelper.regexMatchValue("\\w+(\\s\\w+)*", (string)dispositionData.led_disposition, 0);

                string legalEquivalenceDispositionLower = legalEquivalenceDisposition == null ? "" : legalEquivalenceDisposition.ToLower();



                string rulesEvaluatedAction = dispositionData.rules_evaluated_action;
                #endregion





                #region IRS
                string externalDBName = dispositionData.ext_db_name;

                bool isIRSPresent = false;
                if (externalDBName != null && externalDBName.ToLower().Contains("irs"))
                    isIRSPresent = true;

                string orgName = dispositionData.org_name;
                //account.GetAttributeValue<string>("name");
                caseEntity["ts_validationdispositionorgname"] = orgName;
                string IRSOrgName = dispositionData.ext_db_org_data.NAME;
                caseEntity["ts_validationdispositionirsorgname"] = IRSOrgName;

                IRSOrgName = IRSOrgName == null ? "" : IRSOrgName;
                float levenshteinDistance = Fastenshtein.Levenshtein.Distance(orgName.ToLower(), IRSOrgName.ToLower());
                float topLength = Math.Max(orgName.Length, IRSOrgName.Length);

                float levenshteinIndex = (topLength - levenshteinDistance) / topLength;
                //float levenshteinIndex = (orgName.Length - levenshteinDistance) / orgName.Length;

                bool orgNamesMatch = levenshteinIndex >= 0.60 ? true : false;

                string IRSSubSection = dispositionData.ext_db_org_data.SUBSECTION;

                caseEntity["ts_validationdispositionirssubsection"] = IRSSubSection;
                bool isSubSection03 = IRSSubSection == "03" ? true : false;

                string IRSOrgEIN = dispositionData.ext_db_org_data.EIN;
                string legalIdentifier = dispositionData.ctp_db_match_lid;
                //account.GetAttributeValue<string>("new_legalidentifier");

                legalIdentifier = legalIdentifier == null ? "" : legalIdentifier;
                bool legalIdentifiersMatch = IRSOrgEIN == legalIdentifier.Replace("-", "") ? true : false;


                string IRSRevocationCode = dispositionData.ext_db_org_data.REV_CD;

                //bool isOnIRSRevokeList = IRSRevocationCode == null || IRSRevocationCode.ToLower() == "na" ? false : true;


                string ein = account.GetAttributeValue<string>("new_legalidentifier");
                bool isOrgIRSRevoked = false;
                usp_getOrgIRSRevokeInfoResult orgIrsRevokeRecord = IRSRevocationProcess.getOrgIRSRevocationRecord(ein);

                if (orgIrsRevokeRecord != null)
                {
                    string reinstatedFetchExpression = @"
                                            <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""5000"" no-lock=""false"">
	                                                <entity name=""ts_reinstatedorgs"">
		                                                <attribute name=""ts_ein""/>
		                                                <attribute name=""ts_dateadded""/>
                                                            <filter type=""and"">
                                                                 <condition attribute=""ts_ein"" operator=""eq"" value=""" + ein + @"""/>
		                                                    </filter>
	                                                </entity>
                                            </fetch>
                                            ";


                    EntityCollection reinstatedOrgsCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(new FetchExpression(reinstatedFetchExpression));
                    noteTitle = "Org Found in the IRS Revocation Records";
                    if (reinstatedOrgsCollection.Entities.Count == 0)
                    {
                        isOrgIRSRevoked = true;
                        noteDesc = "The org's EIN, " + legalIdentifier + ", was not found in the list of reinstated orgs in our system. The Qual Case will be IRS Disqualified" + Environment.NewLine + Environment.NewLine;

                    }
                    else
                    {
                        noteDesc = "The org's EIN, " + legalIdentifier + ", is currently listed in the reinstated orgs records in our system. Org's qualification evaluation will proceed" + Environment.NewLine + Environment.NewLine;

                    }

                    noteDesc += "Information on the Revocation record:" + Environment.NewLine + Environment.NewLine;

                    noteDesc += "Revocation Date: " + orgIrsRevokeRecord.Revocation_Date.Value.ToString("MM/dd/yyyy") + Environment.NewLine;
                    noteDesc += "Revocation Posting Date: " + orgIrsRevokeRecord.Revocation_Posting_Date.Value.ToString("MM/dd/yyyy") + Environment.NewLine;

                    DateTime emptyDate = new DateTime(1900, 1, 1);
                    string reinstatementDateFormat = orgIrsRevokeRecord.Exemption_Reinstatement_Date.Value == emptyDate ? "" : orgIrsRevokeRecord.Exemption_Reinstatement_Date.Value.ToString("MM/dd/yyyy");
                    noteDesc += "Exemption Reinstatement Date: " + reinstatementDateFormat;


                    DynamicsProcessesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                }



                caseEntity["ts_validationdispositiononirsrevokelist"] = isOrgIRSRevoked;
                #endregion




                #region Org Rules

                bool isOrgValid = false;
                if (isIRSPresent && orgNamesMatch && isSubSection03 && legalIdentifiersMatch && !isOrgIRSRevoked //!isOnIRSRevokeList
                    )
                    isOrgValid = true;

                caseEntity["ts_validationdispositionrulesorgvalid"] = isOrgValid;


                caseEntity["ts_validationorgdisposition"] = orgDisposition.ToLower() == "is" ? true : false;
                caseEntity["ts_orgvalidationdispositionscore"] = orgDispScore.ToString();
                #endregion









                #region Agent Details

                string agentName = caseEntity.GetAttributeValue<string>("ts_validationagentname");

                float levenshteinDistOrgAgent = Fastenshtein.Levenshtein.Distance(orgName.ToLower(), agentName.ToLower());
                float topLengthOrgAgent = Math.Max(orgName.Length, agentName.Length);
                float levenshteinOrgAgentIndex = (topLengthOrgAgent - levenshteinDistOrgAgent) / topLengthOrgAgent;

                bool isAgentNameValid = levenshteinOrgAgentIndex < 0.60 ? true : false;


                List<dynamic> ctpOrgDataList = ((JArray)dispositionData.ctp_db_match_set).ToList<dynamic>();


                dynamic orgData = null;

                if (ctpOrgDataList.Count > 0)
                    orgData = ctpOrgDataList.First();


                string orgWebsite = orgData == null ? "" : (orgData.org_website == null ? "" : orgData.org_website);

                caseEntity["ts_validationdispositionorgwebsite"] = orgWebsite;

                string agentEmail = caseEntity.GetAttributeValue<string>("ts_validationagentemail") ?? "";

                string domain = DynamicsProcessesHelper.regexMatchValue("(?<=@)(.+)", agentEmail, 0);

                bool agentOrgCommonDomain = !string.IsNullOrEmpty(domain) && DynamicsProcessesHelper.regexMatch(domain, orgWebsite);


                #region Agent Rules
                caseEntity["ts_validationdispositionrulesagentvalid"] = agentDisposition?.ToLower() == "is" ? true : false;
                if (isAgentNameValid && agentOrgCommonDomain)
                    caseEntity["ts_validationdispositionrulesagentvalid"] = true;


                caseEntity["ts_validationagentdisposition"] = agentDisposition?.ToLower() == "is" ? true : false;
                caseEntity["ts_agentvalidationdispositionscore"] = agentDispScore.ToString();
                #endregion
                #endregion








                #region Trustworthy Disposition
                caseEntity["ts_validationdispositiontrustworthy"] = trustworthyDisposition.ToLower() == "is" ? true : false;
                caseEntity["ts_validationdispositiontrustworthyconfidence"] = trustworthyConfidence.ToString();
                #endregion






                #region Legal Equivalence Disposition
                caseEntity["ts_validationlegalequivalencedisposition"] = legalEquivalenceDispositionLower == "does" ? true : false;
                #endregion




                #region Activity Code Validation Rules
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





                caseEntity["ts_validationdispositionactivitycodematch"] = activityCodeFinal;


                if (!string.IsNullOrEmpty(activityCodeFinal) && activityCodeFinal != sensitiveListActivityCode)
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, activityCodeFinal);
                    EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                    if (entityCollection.Entities.Count > 0)
                        caseEntity["ts_validationdispositionactivitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);

                }


                #endregion






                #region Rules Evaluation
                List<dynamic> autoCloseCustomeRules = ((JArray)AutomatedValDefinition.config.autoCloseCustomRules).ToList<dynamic>();

                bool isAgentValid = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesagentvalid");
                bool isTrustWorthy = caseEntity.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                bool isActivityCodeValid = caseEntity.GetAttributeValue<bool>("ts_validationdispositionrulesactivitycodevalid");

                caseEntity["ts_validationdispositionrulesautoclosequalify"] = false;

                caseEntity["ts_validationactionfromdispositionrules"] = "Manual - Further Evaluation Needed";

                if (isOrgValid && isAgentValid && isTrustWorthy && isActivityCodeValid)
                {
                    caseEntity["ts_validationdispositionrulesautoclosequalify"] = true;

                    caseEntity["ts_validationactionfromdispositionrules"] = "AutoClose - Qualify";

                    /*Setting "Validation Disposition Activity Code Final" back to null*/
                    caseEntity["ts_validationdispositionactivitycode"] = null;
                }
                else if (autoCloseCustomeRules.Exists(item => (string)item.rule == "ValidOrgAgentTrustACUpdate"))
                {

                    string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                    if (isOrgValid && isAgentValid && isTrustWorthy && !isActivityCodeValid && caseEntity.GetAttributeValue<EntityReference>("ts_validationdispositionactivitycode") != null)
                    {
                        bool isEligWithDispActivityCode = DynamicsProcessesHelper.isOrgEligibleDiffActivityCode(tsOrgId, activityCodeFinal);
                        bool hasOpenOrders = DynamicsProcessesHelper.orgHasOpenOrders(tsOrgId);

                        if (hasOpenOrders && isEligWithDispActivityCode)
                            caseEntity["ts_validationactionfromdispositionrules"] = "Update Activity Code With " + activityCodeFinal + " - AutoQualify - Org still eligible for open order";

                        if (hasOpenOrders && !isEligWithDispActivityCode)
                            caseEntity["ts_validationactionfromdispositionrules"] = "Update Activity Code With " + activityCodeFinal + " -  Manual - Org is no longer eligible for open order";

                        if (!hasOpenOrders)
                            caseEntity["ts_validationactionfromdispositionrules"] = "Update Activity Code With " + activityCodeFinal + " -  AutoQualify - No Open Orders";
                    }

                }
                #endregion










                #region Disposition Action
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
                #endregion






                #region Update Case
                DynamicsInterface.DataverseClient.Update(caseEntity);
                #endregion

                #region Add System Note With Disposition Details
                string dispositionReference = dispositionData.reference_id == null ? "" : dispositionData.reference_id;
                string dispositionUrl = DynamicsProcessesHelper.regexMatchValue("https:.+?html", dispositionReference, 0);


                noteTitle = " --- Disposition Details --- ";

                noteDesc = "Full Disposition:" + Environment.NewLine + Environment.NewLine;
                noteDesc += dispositionUrl;
                noteDesc += Environment.NewLine + Environment.NewLine + "Score Matrix: ";
                noteDesc += Environment.NewLine + Environment.NewLine + scoreItems;

                processSystemNote(noteTitle, noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getValidationScoreMatrix(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                    );
            }
            #endregion
        }

       

        public static string getValidationRequest_b(Entity account)
        {
            string validationRequest = string.Empty;
            try
            {

                string tsOrgId = account.GetAttributeValue<string>("accountnumber");
                string orgName = account.GetAttributeValue<string>("name");

                string assignedId = account.GetAttributeValue<string>("new_platformid");
                string email = account.GetAttributeValue<string>("emailaddress1");
                string phone = account.GetAttributeValue<string>("telephone1");
                string legalIdentifier = account.GetAttributeValue<string>("new_legalidentifier");
                string url = account.GetAttributeValue<string>("websiteurl");
                string budget = account.GetAttributeValue<string>("new_budget");
                string isEmailValidCode = account.GetAttributeValue<bool>("new_isemailvalid") ? "0" : "1";
                string associationCode = account.GetAttributeValue<string>("new_associationcode");


                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                string orgDesignation = string.Empty;
                string orgDesignationDescription = string.Empty;
                Guid orgDesigId = Guid.Empty;
                if (orgDesigRef != null)
                {
                    Entity qualCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", orgDesigRef.Id, new ColumnSet(true));
                    orgDesigId = orgDesigRef.Id;
                    orgDesignation = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                    orgDesignationDescription = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                }

                string customerTypeCode = string.Empty;
                if (account.Contains("customertypecode"))
                    customerTypeCode = account.FormattedValues["customertypecode"];

                OptionSetValue orgSourceOption = account.GetAttributeValue<OptionSetValue>("new_source");
                int orgSource = orgSourceOption == null ? 0 : orgSourceOption.Value;



                EntityReference activityCodeRef = account.GetAttributeValue<EntityReference>("new_activitycode");
                string activityCode = string.Empty;
                string activityCodeDescription = string.Empty;
                string activityCodeCategory = string.Empty;
                if (activityCodeRef != null)
                {
                    Entity activityCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_activitycodes", activityCodeRef.Id, new ColumnSet(true));
                    activityCode = activityCodeEntity.GetAttributeValue<string>("new_activitycode");
                    activityCodeDescription = activityCodeEntity.GetAttributeValue<string>("new_activitycodedescription");
                    activityCodeCategory = activityCodeEntity.GetAttributeValue<string>("new_activitycodecategory");
                }


                string companyTypeCode = account.Contains("ts_countrydesc") ? account.FormattedValues["ts_countrydesc"] : account.GetAttributeValue<string>("address1_country");



                string countryCode = account.GetAttributeValue<string>("address1_country");
                string regionCode = account.GetAttributeValue<string>("address1_stateorprovince");
                string address1 = account.GetAttributeValue<string>("address1_line1");
                string address2 = account.GetAttributeValue<string>("address1_line2");
                string address3 = account.GetAttributeValue<string>("address1_line3");
                string city = account.GetAttributeValue<string>("address1_city");
                string postalCode = account.GetAttributeValue<string>("address1_postalcode");



                EntityReference duplicateOfRef = account.GetAttributeValue<EntityReference>("ts_duplicateofid");
                string duplicateOf = string.Empty;
                if (duplicateOfRef != null)
                {
                    Entity accountDupeOf = DynamicsInterface.DataverseClient.Retrieve("account", duplicateOfRef.Id, new ColumnSet("accountnumber"));
                    duplicateOf = accountDupeOf.GetAttributeValue<string>("accountnumber");
                }


                int numberOfEmployees = account.GetAttributeValue<int>("numberofemployees");


                QueryExpression queryConnection = new QueryExpression("connection");
                queryConnection.ColumnSet = new ColumnSet(true);
                queryConnection.Criteria.AddCondition("record1id", ConditionOperator.Equal, account.Id);
                queryConnection.Criteria.AddCondition("record2objecttypecode", ConditionOperator.Equal, 2);
                queryConnection.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
                EntityCollection connectionCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryConnection);

                if (connectionCollection.Entities.Count() == 0)
                    return null;

                Entity connection = connectionCollection.Entities.First();
                EntityReference contactRef = connection.GetAttributeValue<EntityReference>("record2id");

                Guid contactId = contactRef == null ? Guid.Empty : contactRef.Id;

                Entity contact = DynamicsInterface.DataverseClient.Retrieve("contact", contactId, new ColumnSet(true));

                string contactFirstName = contact.GetAttributeValue<string>("firstname");
                string contactLastName = contact.GetAttributeValue<string>("lastname");
                string contactEmail = contact.GetAttributeValue<string>("emailaddress1");

                string transactionId = "o" + tsOrgId.ToString() + "t" + DateTime.UtcNow.ToString("yyyyMMddHHmmssffff");
                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

                validationRequest = @"
                                        {
	                                        ""LegalName"": """ + orgName + @""",
	
	                                        ""OperatingBudget"": """ + budget + @""",
	                                        ""ActivityCode"": """ + activityCode + @""",
	                                        ""Email"": """ + email + @""",
	                                        ""Phone"": """ + phone + @""",
	                                        ""Website"": """ + url + @""",
	                                        ""AgentFirstName"":""" + contactFirstName + @""",
	                                        ""AgentLastName"": """ + contactLastName + @""",
	                                        ""AgentEmail"": """ + contactEmail + @""",
	                                        ""AddressLine1"": """ + address1 + @""",
	                                        ""AddressOther"": ""nil"",
	                                        ""AddressCity"": """ + city + @""",
	                                        ""AddressStateRegion"":""" + regionCode + @""",
	                                        ""AddressPostalCode"": """ + postalCode + @""",
	                                        ""AddressCountryID"": """ + countryCode + @""",
	                                        ""RegistrationIdentifiers"": [
		                                        {
			                                        ""LegalIdentifier"": """ + legalIdentifier + @""",
			                                        ""RegulatoryBody"": ""EIN"",
			                                        ""ArtifactURI"": ""file1.pdf""
		                                        }
	                                        ],
	                                        ""Tenant"": ""1234567890"",
	                                        ""Schema"": ""VS_TS_ORG_REG:001"",
	
	                                        ""EffectiveDatetime"": """ + timestamp + @""",
	                                        ""TransactionId"": """ + transactionId + @"""
                                        }
                                    ";

            }
            catch (Exception e)
            {
                string error = "Error in getValidationRequest(...). Exception message: "
                    + Environment.NewLine + e.Message;
                DynamicsInterface.writeToLog("Error creating Contact record. Exception message: " + error
                    );
                //ErrorStack.Add(error);
            }

            return validationRequest;
        }

        public static string getValidationDispositionArticles(string validationReqTransactionId)
        {
            string activityCodesCsv = null;
            try
            {


                HttpClient client = new HttpClient();
                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                //"https://dev.tsgctp.org:45056/services/validation/v_001/454c41a8-ad87-46df-a319-fef72560f75f/"

                HttpResponseMessage response = client.GetAsync("https://dev.tsgctp.org:45056/services/vsarticles/v_001/61695af7-1652-4b08-b786-192de1884f61?transaction_id=" + validationReqTransactionId).Result;


                string responseTxt = response.Content.ReadAsStringAsync().Result;

                dynamic validationResponse = JsonConvert.DeserializeObject(responseTxt);

                List<string> articles = ((JArray)validationResponse.returnStatus.data).Select(article => (string)article).ToList();


                string orgDispDesc = articles.Find(article => article.Contains("id=\"task-02\"") || article.ToLower().Contains("data-title=\"disposition\""));

                orgDispDesc = orgDispDesc.ToLower();




                string orgDetermination = DynamicsProcessesHelper.regexReplace(@"organization disposition(\n|.)+?\[(?<tagvalue>\w+(\s\w+)?)\]",
                                                                                DynamicsProcessesHelper.regexMatchValue(@"organization disposition(\n|.)+?\[\w+(\s\w+)?\]", orgDispDesc, 0)
                                                                                                , "${tagvalue}");

                string orgConfidence = DynamicsProcessesHelper.regexReplace(@"organization disposition(\n|.)+?\[(?<tagvalue>\d?\.\d{2,5})\]",
                                                                                DynamicsProcessesHelper.regexMatchValue(@"organization disposition(\n|.)+?\[\d?\.\d{2,5}\]", orgDispDesc, 0)
                                                                                               , "${tagvalue}");



                string agentDetermination = DynamicsProcessesHelper.regexReplace(@"agent disposition(\n|.)+?\[(?<tagvalue>\w+(\s\w+)?)\]",
                                                                                DynamicsProcessesHelper.regexMatchValue(@"agent disposition(\n|.)+?\[\w+(\s\w+)?\]", orgDispDesc, 0)
                                                                                                , "${tagvalue}");

                string agentConfidence = DynamicsProcessesHelper.regexReplace(@"agent disposition(\n|.)+?\[(?<tagvalue>\d?\.\d{2,5})\]",
                                                                                DynamicsProcessesHelper.regexMatchValue(@"agent disposition(\n|.)+?\[\d?\.\d{2,5}\]", orgDispDesc, 0)
                                                                                               , "${tagvalue}");



                string nteeRecommendations = articles.Find(article => article.Contains("id=\"task-90\"") || article.ToLower().Contains("data-title=\"pcs and ntee code recommendations\""));

                nteeRecommendations = nteeRecommendations.ToLower();

                string nteeRecommended = DynamicsProcessesHelper.regexReplace("acp:\\s{0,3}(?<tagvalue>(\\d|\\w){3})(\\s|\\n)(\\n|.)+?confidence",
                                                                      DynamicsProcessesHelper.regexMatchValue("acp:\\s{0,3}(\\d|\\w){3}(\\s|\\n)(\\n|.)+?confidence", nteeRecommendations, 0)
                                                                                   , "${tagvalue}");

                List<string> nteeSections = new List<string>();

                int pos = 0;
                Regex regex = new Regex("acp:(\\n|.)+?(\\d|\\w){3}(\\n|.)+?confidence:(\\n|.)+?\\d?\\.\\d{2,5}");
                Match match = regex.Match(nteeRecommendations, pos);
                int i = 0;
                while (match.Success)
                {
                    string matchedElement = match.Value;
                    nteeSections.Add(matchedElement);

                    pos = match.Index + 5;

                    match = regex.Match(nteeRecommendations, pos);
                }
                Dictionary<string, float> activityCodesRecommended = new Dictionary<string, float>();

                List<KeyValuePair<string, float>> actCodes = new List<KeyValuePair<string, float>>();
                foreach (string section in nteeSections)
                {
                    string acp = DynamicsProcessesHelper.regexReplace("acp:.+?(?<tagvalue>(\\d|\\w){2,3})",
                                                                      DynamicsProcessesHelper.regexMatchValue("acp:.+?(\\d|\\w){2,3}", section, 0)
                                                                                   , "${tagvalue}");

                    string acpConfidence = DynamicsProcessesHelper.regexReplace("confidence:.+?(?<tagvalue>\\d?\\.\\d{2,5})",
                                                                      DynamicsProcessesHelper.regexMatchValue("confidence:.+?\\d?\\.\\d{2,5}", section, 0)
                                                                                   , "${tagvalue}");


                    //Console.WriteLine(acp.ToUpper() + ": " + acpConfidence);
                    if (acp != "nil")
                        activityCodesRecommended[acp.ToUpper().PadLeft(3, '0')] = float.Parse(acpConfidence);

                    actCodes.Add(new KeyValuePair<string, float>(acp.ToUpper().PadLeft(3, '0'), float.Parse(acpConfidence)));
                }


                if (actCodes.Count > 0)
                {
                    var actCodesSorted = actCodes.Where(keyVal => keyVal.Key.ToLower() != "nil").OrderByDescending(keyVal => keyVal.Value);

                    foreach (KeyValuePair<string, float> activityCode in actCodesSorted)
                    {
                        Console.WriteLine(activityCode.Key + ": " + activityCode.Value);
                    }


                    string activityCodeHighestConf = actCodes.Where(keyVal => keyVal.Key.ToLower() != "nil").OrderByDescending(keyVal => keyVal.Value).First().Key;

                    if (activityCodesRecommended.Count > 0)
                    {
                        string[] activityCodes = activityCodesRecommended.OrderByDescending(keyVal => keyVal.Value).Select(keyVal => keyVal.Key).ToArray();

                        activityCodesCsv = String.Join(",", activityCodes);






                    }
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getValidationDispositionArticles(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                    );
            }
            return activityCodesCsv;
        }



        public static void initiateValidationRquestFoQualCases()
        {


            var automatedValSettings = getAutomatedValidationConfig();


            AutomatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);


            QueryExpression queryEntity = new QueryExpression("queue");
            queryEntity.ColumnSet = new ColumnSet(true);
            //queryEntity.Criteria.AddCondition("name", ConditionOperator.In, new object[] { "AutoValidation","AutoValidation Inconclusive" });
            queryEntity.Criteria.AddCondition("name", ConditionOperator.In, new object[] { "AutoValidation" });
            //queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
            EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

            foreach (Entity queue in entityCollection.Entities)
            {
                Guid queueId = queue.Id;
                string queueName = queue.GetAttributeValue<string>("name");


                QueryExpression queryQueueItem = new QueryExpression("queueitem");
                queryQueueItem.ColumnSet = new ColumnSet(true);
                queryQueueItem.Criteria.AddCondition("queueid", ConditionOperator.Equal, queueId);
                //queryQueueItem.Criteria.AddCondition("objectid", ConditionOperator.Equal, Guid.Parse("10E2091E-5E44-F011-8779-000D3A5CA4E4"));

                /*change this back to OrderType.Ascending*/
                queryQueueItem.AddOrder("enteredon", OrderType.Ascending);
                queryQueueItem.TopCount = 1;
                LinkEntity caseLink = queryQueueItem.AddLink("incident", "objectid", "incidentid", JoinOperator.Inner);

                caseLink.EntityAlias = "inc";

                caseLink.Columns = new ColumnSet(true);
                caseLink.LinkCriteria.AddCondition("ts_validationrequesttransactionid", ConditionOperator.Null);

                EntityCollection queueItemCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQueueItem);

                //DynamicsInterface.LogName += "_" + queueName.Replace(' ', '_');

                foreach (Entity queueItem in queueItemCollection.Entities)
                {
                    DynamicsInterface.errorStack = new List<string>();


                    string caseNum = (string)queueItem.GetAttributeValue<AliasedValue>("inc.ticketnumber").Value;
                    processValidationRequestsScoreMatrix(queueItem, queueName, false);
                }
            }


        }
        public static void scoreMatrixFoQualCases(bool uncondionalGetScoreMatrix)
        {


            var automatedValSettings = getAutomatedValidationConfig();


            AutomatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);


            QueryExpression queryEntity = new QueryExpression("queue");
            queryEntity.ColumnSet = new ColumnSet(true);
            //queryEntity.Criteria.AddCondition("name", ConditionOperator.In, new object[] { "AutoValidation","AutoValidation Inconclusive" });
            queryEntity.Criteria.AddCondition("name", ConditionOperator.In, new object[] { "AutoValidation" });
            //queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
            EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

            foreach (Entity queue in entityCollection.Entities)
            {
                Guid queueId = queue.Id;
                string queueName = queue.GetAttributeValue<string>("name");


                QueryExpression queryQueueItem = new QueryExpression("queueitem");
                queryQueueItem.ColumnSet = new ColumnSet(true);
                queryQueueItem.Criteria.AddCondition("queueid", ConditionOperator.Equal, queueId);
                //queryQueueItem.Criteria.AddCondition("objectid", ConditionOperator.Equal, Guid.Parse("10E2091E-5E44-F011-8779-000D3A5CA4E4"));

                /*change this back to OrderType.Ascending*/
                queryQueueItem.AddOrder("enteredon", OrderType.Ascending);
                //queryQueueItem.TopCount = 1;
                LinkEntity caseLink = queryQueueItem.AddLink("incident", "objectid", "incidentid", JoinOperator.Inner);

                caseLink.EntityAlias = "inc";

                caseLink.Columns = new ColumnSet(true);
                caseLink.LinkCriteria.AddCondition("ts_validationrequesttransactionid", ConditionOperator.NotNull);
                caseLink.LinkCriteria.AddCondition("ts_validationdispositionstatus", ConditionOperator.NotEqual, "completed");
                EntityCollection queueItemCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQueueItem);
                caseLink.LinkCriteria.AddCondition("ts_validationrequesttransactionid", ConditionOperator.Equal, "batch-onyx-2705876");

                //DynamicsInterface.LogName += "_" + queueName.Replace(' ', '_');

                foreach (Entity queueItem in queueItemCollection.Entities)
                {
                    DynamicsInterface.errorStack = new List<string>();


                    string caseNum = (string)queueItem.GetAttributeValue<AliasedValue>("inc.ticketnumber").Value;
                    processValidationRequestsScoreMatrix(queueItem, queueName, uncondionalGetScoreMatrix);
                }
            }


        }


        public static void processAutoValidationQueueManual()
        {
            var automatedValSettings = getAutomatedValidationConfig();


            AutomatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);


            QueryExpression queryEntity = new QueryExpression("queue");
            queryEntity.ColumnSet = new ColumnSet(true);
            //queryEntity.Criteria.AddCondition("name", ConditionOperator.In, new object[] { "AutoValidation","AutoValidation Inconclusive" });
            queryEntity.Criteria.AddCondition("name", ConditionOperator.In, new object[] { "AutoValidation" });
            //queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
            EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

            foreach (Entity queue in entityCollection.Entities)
            {
                Guid queueId = queue.Id;
                string queueName = queue.GetAttributeValue<string>("name");


                QueryExpression queryQueueItem = new QueryExpression("queueitem");
                queryQueueItem.ColumnSet = new ColumnSet(true);
                queryQueueItem.Criteria.AddCondition("queueid", ConditionOperator.Equal, queueId);
                //queryQueueItem.Criteria.AddCondition("objectid", ConditionOperator.Equal, Guid.Parse("10E2091E-5E44-F011-8779-000D3A5CA4E4"));

                /*change this back to OrderType.Ascending*/
                queryQueueItem.AddOrder("enteredon", OrderType.Ascending);
                
                LinkEntity caseLink = queryQueueItem.AddLink("incident", "objectid", "incidentid", JoinOperator.Inner);

                caseLink.EntityAlias = "inc";

                caseLink.Columns = new ColumnSet(true);
                caseLink.LinkCriteria.AddCondition("ts_validationrequesttransactionid", ConditionOperator.NotNull);
                caseLink.LinkCriteria.AddCondition("ts_validationdispositionstatus", ConditionOperator.Equal, "completed");
                caseLink.LinkCriteria.AddCondition("ts_validationactionfromdispositionrules", ConditionOperator.Like, "Manual%");
                //caseLink.LinkCriteria.AddCondition("ts_validationrequesttransactionid", ConditionOperator.Equal, "batch-onyx-2705986");
                caseLink.LinkCriteria.AddCondition("createdon", ConditionOperator.GreaterEqual, DateTime.Parse("2025-06-18 00:04:25"));


                queryQueueItem.TopCount = 100;



                EntityCollection queueItemCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQueueItem);



                
                foreach (Entity queueItem in queueItemCollection.Entities)
                {
                    DynamicsInterface.errorStack = new List<string>();


                    string caseNum = (string)queueItem.GetAttributeValue<AliasedValue>("inc.ticketnumber").Value;


                    Dictionary<string, Entity> entities = getData(queueItem, queueName);
                    if (entities["account"] != null)
                    {
                        Entity caseEntity = entities["caseEntity"];
                        Entity account = entities["account"];

                        string orgQualStatus = DynamicsProcessesHelper.getOrgQualStatus(account);

                        string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                        usp_getOrgQualTaskResult qualTask = DynamicsProcessesHelper.getOrgQualTask(int.Parse(tsOrgId));

                        string validationReqTransactionId = caseEntity.GetAttributeValue<string>("ts_validationrequesttransactionid");


                        if (orgQualStatus == "Qualification Pending")
                        {
                            resolveActionAutoValidation(caseEntity, account, validationReqTransactionId, queueName);
                        }
                        else
                        {
                            DynamicsProcessesHelper.removeCaseFromQueue(caseEntity.Id);
                        }

                    }
                }
            }


        }



        public static void processAutoCloseQualifyFoQualCases()
        {
            var automatedValSettings = getAutomatedValidationConfig();


            AutomatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);


            QueryExpression queryEntity = new QueryExpression("queue");
            queryEntity.ColumnSet = new ColumnSet(true);
            //queryEntity.Criteria.AddCondition("name", ConditionOperator.In, new object[] { "AutoValidation","AutoValidation Inconclusive" });
            queryEntity.Criteria.AddCondition("name", ConditionOperator.In, new object[] { "AutoValidation" });
            //queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
            EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

            foreach (Entity queue in entityCollection.Entities)
            {
                Guid queueId = queue.Id;
                string queueName = queue.GetAttributeValue<string>("name");


                QueryExpression queryQueueItem = new QueryExpression("queueitem");
                queryQueueItem.ColumnSet = new ColumnSet(true);
                queryQueueItem.Criteria.AddCondition("queueid", ConditionOperator.Equal, queueId);
                //queryQueueItem.Criteria.AddCondition("objectid", ConditionOperator.Equal, Guid.Parse("10E2091E-5E44-F011-8779-000D3A5CA4E4"));

                /*change this back to OrderType.Ascending*/
                queryQueueItem.AddOrder("enteredon", OrderType.Ascending);
                //queryQueueItem.TopCount = 1;
                LinkEntity caseLink = queryQueueItem.AddLink("incident", "objectid", "incidentid", JoinOperator.Inner);

                caseLink.EntityAlias = "inc";

                caseLink.Columns = new ColumnSet(true);
                caseLink.LinkCriteria.AddCondition("ts_validationrequesttransactionid", ConditionOperator.NotNull);
                caseLink.LinkCriteria.AddCondition("ts_validationdispositionstatus", ConditionOperator.Equal, "completed");
                caseLink.LinkCriteria.AddCondition("ts_validationactionfromdispositionrules", ConditionOperator.Equal, "AutoClose - Qualify");
                EntityCollection queueItemCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQueueItem);

                //DynamicsInterface.LogName += "_" + queueName.Replace(' ', '_');

                foreach (Entity queueItem in queueItemCollection.Entities)
                {
                    DynamicsInterface.errorStack = new List<string>();


                    string caseNum = (string)queueItem.GetAttributeValue<AliasedValue>("inc.ticketnumber").Value;
                    

                    Dictionary<string, Entity> entities = getData(queueItem, queueName);
                    if (entities["account"] != null)
                    {
                        Entity caseEntity = entities["caseEntity"];
                        Entity account = entities["account"];

                        string orgQualStatus = DynamicsProcessesHelper.getOrgQualStatus(account);

                        string validationReqTransactionId = caseEntity.GetAttributeValue<string>("ts_validationrequesttransactionid");
                        if (orgQualStatus == "Qualification Pending")
                            resolveActionAutoValidation(caseEntity, account, validationReqTransactionId, queueName);

                    }
                }
            }


        }

        public static Dictionary<string,Entity> getData(Entity queueItem, string queueName)
        {
            Dictionary<string, Entity> entities = new Dictionary<string, Entity> ();
            try
            {
                EntityReference queueItemObjRef = queueItem.GetAttributeValue<EntityReference>("objectid");

                if (queueItemObjRef == null || queueItemObjRef.LogicalName != "incident")
                {
                    DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                    return null;
                }

                Guid caseId = queueItemObjRef.Id;

                Entity caseEntity = DynamicsInterface.DataverseClient.Retrieve("incident", caseId, new ColumnSet(true));
               

                string tsType = caseEntity.Contains("ts_type") ? caseEntity.FormattedValues["ts_type"] : string.Empty;

                if (tsType != "Organization Qualification")
                {
                    DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                    return null;
                }

                EntityReference customerIdRef = caseEntity.GetAttributeValue<EntityReference>("customerid");

                if (customerIdRef == null || customerIdRef.LogicalName != "account")
                {
                    DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                    return null;
                }

                Entity account = DynamicsInterface.DataverseClient.Retrieve("account", customerIdRef.Id, new ColumnSet(true));



                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                EntityReference qualCodeRef = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid");

                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;
                Guid qualCodeId = qualCodeRef == null ? Guid.Empty : qualCodeRef.Id;

                if (orgDesigId != qualCodeId)
                {
                    DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                    return null;
                }



                entities.Add("caseEntity", caseEntity);
                entities.Add("account", account);
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processValidationRequestsScoreMatrix(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "queueItemId: " + queueItem.Id.ToString()
                    );
            }

            return entities;
        }
        public static void processValidationRequestsScoreMatrix(Entity queueItem, string queueName, bool forceGetScoreMatrix)
        {
            try
            {
                EntityReference queueItemObjRef = queueItem.GetAttributeValue<EntityReference>("objectid");

                if (queueItemObjRef == null || queueItemObjRef.LogicalName != "incident")
                {
                    DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                    return;
                }

                Guid caseId = queueItemObjRef.Id;

                Entity caseEntity = DynamicsInterface.DataverseClient.Retrieve("incident", caseId, new ColumnSet(true));


                string tsType = caseEntity.Contains("ts_type") ? caseEntity.FormattedValues["ts_type"] : string.Empty;

                if (tsType != "Organization Qualification")
                {
                    DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                    return;
                }

                EntityReference customerIdRef = caseEntity.GetAttributeValue<EntityReference>("customerid");

                if (customerIdRef == null || customerIdRef.LogicalName != "account")
                {
                    DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                    return;
                }

                Entity account = DynamicsInterface.DataverseClient.Retrieve("account", customerIdRef.Id, new ColumnSet(true));



                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                EntityReference qualCodeRef = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid");

                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;
                Guid qualCodeId = qualCodeRef == null ? Guid.Empty : qualCodeRef.Id;

                if (orgDesigId != qualCodeId)
                {
                    DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                    return;
                }




                bool pickExistingTransactionId = AutomatedValDefinition.config.pickExistingTransactionId == null ? false : AutomatedValDefinition.config.pickExistingTransactionId;

                string validationReqTransactionId = caseEntity.GetAttributeValue<string>("ts_validationrequesttransactionid");

                if (string.IsNullOrEmpty(validationReqTransactionId))
                {
                    bool isDuplicate = determineIfDuplicate(caseEntity, account, validationReqTransactionId, queueName);
                    if (isDuplicate)
                        return;

                    initiateDispositionRequest(caseEntity, account, queueName);                    
                }
                else
                {
                    bool caseHasDisposition = existsSystemNote(" --- Disposition Details --- ", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                    if (!caseHasDisposition || forceGetScoreMatrix)
                    {
                        bool okToProcess = getProcessingApproval(caseEntity, account, queueName);

                        if (okToProcess || forceGetScoreMatrix)
                            getValidationScoreMatrix(caseEntity, account, validationReqTransactionId, queueName);
                    }
                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processValidationRequestsScoreMatrix(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "queueItemId: " + queueItem.Id.ToString()
                    );
            }

        }

        public static async Task<bool> evaluateForFraudSimplified(Entity caseEntity, Entity organizationEntity, string validationReqTransactionId)
        {
            var fraudFlags = new List<string>();
            bool isFraudulent = false;


            //string orgDomainRegistrationCountry = string.Empty;


            try
            {
                string website = organizationEntity.GetAttributeValue<string>("websiteurl");
                string agentEmail = caseEntity.GetAttributeValue<string>("ts_validationagentemail");


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
                    //processSystemNote("-- Potential Fraud (Simplified Detection) --", noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));

                    string noteDesc = "Fraud analysis findings:\n\n" + string.Join("\n", fraudFlags.Select((flag, index) => $"{index + 1}. {flag}"));
                    ValidationServicesHelper.processSystemNote("-- Potential Fraud --", noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));


                    caseEntity["ts_casestatus"] = new OptionSetValue(104602); //104602 - OQ - Fraud Review
                    DynamicsInterface.DataverseClient.Update(caseEntity);


                    string fraudReviewQueue = AutomatedValDefinition.config.fraudReviewQueue;
                    DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, fraudReviewQueue);

                    DynamicsInterface.writeToLog($"Case {validationReqTransactionId} flagged as potential fraud with {fraudFlags.Count} simplified violations");
                }

                return isFraudulent;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error in evaluateForFraudSimplified for {validationReqTransactionId}: {ex.Message}");

                ValidationServicesHelper.processSystemNote("-- Potential Fraud --", $"Error during fraud evaluation: {ex.Message}\n\nCase requires manual review due to technical issues during automated validation."
                                    , new EntityReference(caseEntity.LogicalName, caseEntity.Id));

                caseEntity["ts_casestatus"] = new OptionSetValue(104602);//104602 - OQ - Fraud Review
                DynamicsInterface.DataverseClient.Update(caseEntity);

                string fraudReviewQueue = AutomatedValDefinition.config.fraudReviewQueue;
                DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, fraudReviewQueue);

                return true;
            }
        }
        public static async Task<bool> evaluateForFraudSimplified_backup(Entity caseEntity, Entity organizationEntity, string validationReqTransactionId)
        {
            var fraudFlags = new List<string>();
            bool isFraudulent = false;
            string WHOISJSON_API_KEY = System.Configuration.ConfigurationManager.AppSettings["WhoisJsonApiKey"];

            try
            {
                string website = organizationEntity.GetAttributeValue<string>("websiteurl");
                string agentEmail = caseEntity.GetAttributeValue<string>("ts_validationagentemail");
                string orgDomainRegistrationCountry = string.Empty;
                // 1. Enhanced Domain Registration Validation using WhoisJSON
                if (!string.IsNullOrEmpty(website))
                {
                    try
                    {
                        var enhancedDomainValidator = new EnhancedDomainValidator();
                        var enhancedResult = await enhancedDomainValidator.ValidateDomainAsync(website);

                        if (enhancedResult != null)
                        {
                          
                            if (enhancedResult.HasFraudIndicators)
                            {
                                fraudFlags.AddRange(enhancedResult.FraudFlags);
                                isFraudulent = true;
                            }

                            
                            if (enhancedResult.WhoisData != null)
                            {
                                var whoisData = enhancedResult.WhoisData;

                                orgDomainRegistrationCountry = whoisData.Registrar?.GetPhoneCountryCode() ?? string.Empty;

                                if (whoisData.Registrar?.GetPhoneCountryCode() != null &&
                                    whoisData.Registrar.GetPhoneCountryCode() != "US")
                                {
                                    fraudFlags.Add($"Registrar phone indicates non-US location: {whoisData.Registrar.Name} (Phone country: {whoisData.Registrar.GetPhoneCountryCode()})");
                                    isFraudulent = true;
                                }

                             
                                if (whoisData.IsRecentlyRegistered)
                                {
                                    fraudFlags.Add($"Domain was recently registered ({whoisData.DomainAgeInDays} days ago) - potential indicator of fraudulent activity");
                                    isFraudulent = true;
                                }

                              
                                if (whoisData.Contacts != null)
                                {
                                    var nonUSCountries = whoisData.Contacts.GetAllCountries()
                                        .Where(c => string.Equals(c, "US", StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(c, "UNITED STATES", StringComparison.OrdinalIgnoreCase))
                                        .ToList();

                                    if (nonUSCountries.Count == 0)
                                    {
                                        fraudFlags.Add($"No domain contact in the US");
                                        isFraudulent = true;
                                    }
                                }
                            }
                        }

                        enhancedDomainValidator.Dispose();
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog($"Error in enhanced domain validation for {validationReqTransactionId}: {ex.Message}");

                     
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

                    // 2. Website Validation
                    try
                    {
                        var websiteValidation = await NetworkValidationService.ValidateWebsiteAsync(website);
                        if (!websiteValidation.IsValid)
                        {
                            fraudFlags.Add("Website is not valid");
                            isFraudulent = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog($"Error in website validation for {validationReqTransactionId}: {ex.Message}");
                    }
                }

                // 3. Enhanced User Registration IP Address Validation with Reverse WHOIS correlation
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
                                fraudFlags.Add($"IP address used during registration, {userRegistrationIP}, is not in the US. Country of IP Address: {ipValidation.Country} (country code: {ipValidation.CountryCode})");
                                isFraudulent = true;

                                // Enhanced: Cross-reference with website domain location for consistency
                                //if (!string.IsNullOrEmpty(website) && orgDomainRegistrationCountry != "US")
                                //{
                                //    fraudFlags.Add($"Geographic inconsistency: User registration IP ({ipValidation.Country}) does not match expected US business location");
                                //}
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog($"Error in registration IP validation for {validationReqTransactionId}: {ex.Message}");
                    }
                }

                // 4. NEW: Enhanced Agent Email Domain Validation
                if (!string.IsNullOrEmpty(agentEmail))
                {
                    try
                    {
                       
                        var emailAnalysis = EmailDomainValidator.AnalyzeEmail(agentEmail);

                        if (emailAnalysis.HasFraudIndicators)
                        {
                            fraudFlags.AddRange(emailAnalysis.Issues.Select(issue => $"Agent email analysis: {issue}"));
                            isFraudulent = true;
                        }

                    
                        if (emailAnalysis.IsDisposable)
                        {
                            fraudFlags.Add($"Agent uses disposable email service: {emailAnalysis.Domain}");
                            isFraudulent = true;
                        }

                       
                        if (emailAnalysis.ShouldValidateDomain && emailAnalysis.IsValid)
                        {
                            
                            var enhancedDomainValidator = new EnhancedDomainValidator();
                            var emailDomainResult = await enhancedDomainValidator.ValidateDomainAsync(emailAnalysis.Domain);

                            if (emailDomainResult != null && emailDomainResult.HasFraudIndicators)
                            {
                                fraudFlags.Add($"Agent email domain shows fraud indicators: {emailAnalysis.Domain}");
                                fraudFlags.AddRange(emailDomainResult.FraudFlags.Select(f => $"Email domain: {f}"));
                                isFraudulent = true;
                            }

                           
                            if (emailDomainResult?.WhoisData != null)
                            {
                                var emailWhoisData = emailDomainResult.WhoisData;
                                if (emailWhoisData.Contacts != null && emailWhoisData.Contacts.HasNonUSContacts())
                                {
                                    var nonUSCountries = emailWhoisData.Contacts.GetAllCountries()
                                        .Where(c => string.Equals(c, "US", StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(c, "UNITED STATES", StringComparison.OrdinalIgnoreCase))
                                        .ToList();

                                    if (nonUSCountries.Count() == 0)
                                    {
                                        fraudFlags.Add($"Agent email domain - no contact in registration is from the US");
                                        isFraudulent = true;
                                    }
                                }

                               
                                if (emailWhoisData.Registrar?.GetPhoneCountryCode() != null &&
                                    emailWhoisData.Registrar.GetPhoneCountryCode() != "US")
                                {
                                    fraudFlags.Add($"Agent email domain registrar located outside US: {emailWhoisData.Registrar.Name} (Country: {emailWhoisData.Registrar.GetPhoneCountryCode()})");
                                    isFraudulent = true;
                                }
                            }

                            enhancedDomainValidator.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog($"Error in enhanced agent email validation for {validationReqTransactionId}: {ex.Message}");
                    }
                }

                // 5. Process results if fraud detected
                if (isFraudulent && fraudFlags.Count > 0)
                {
                   
                    string noteDesc = "Fraud analysis findings:\n\n" + string.Join("\n", fraudFlags.Select((flag, index) => $"{index + 1}. {flag}"));
                    processSystemNote("-- Potential Fraud --", noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));

                   
                    caseEntity["ts_casestatus"] = new OptionSetValue(104602);
                    DynamicsInterface.DataverseClient.Update(caseEntity);

                  
                    string fraudReviewQueue = AutomatedValDefinition.config.fraudReviewQueue;
                    DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, fraudReviewQueue);

                    DynamicsInterface.writeToLog($"Case {validationReqTransactionId} flagged as potential fraud with {fraudFlags.Count} enhanced violations");
                }

                return isFraudulent;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error in evaluateForFraudSimplified for {validationReqTransactionId}: {ex.Message}");

                // On error, flag as potential fraud for manual review
                processSystemNote("-- Potential Fraud --", $"Error during fraud evaluation: {ex.Message}\n\nCase requires manual review due to technical issues during automated validation."
                                    , new EntityReference(caseEntity.LogicalName, caseEntity.Id));

                caseEntity["ts_casestatus"] = new OptionSetValue(104602);
                DynamicsInterface.DataverseClient.Update(caseEntity);

                string fraudReviewQueue = AutomatedValDefinition.config.fraudReviewQueue;
                DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, fraudReviewQueue);

                return true;
            }
        }


    }
}
