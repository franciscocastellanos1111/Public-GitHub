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
using Azure;

namespace DynamicsProcesses
{
    internal class ValidationServicesUSNonC3
    {


        public static async Task determineProcessBehavior(Entity caseEntity, Entity validationRequestor, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region Disposition
                bool caseHasDisposition = DynamicsProcessesHelper.existsSystemNote(" --- Disposition Details --- ", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                if (!caseHasDisposition)
                {
                    
                    bool okToProcess = await DynamicsProcessesValidationServices.getProcessingApproval(caseEntity, validationRequestor, queueName);

                    if (okToProcess)
                    {
                        await ValidationServicesUSNonC3.getValidationScoreMatrix(caseEntity, validationRequestor, validationReqTransactionId, queueName, dispositionRequest);
                        caseHasDisposition = DynamicsProcessesHelper.existsSystemNote(" --- Disposition Details --- ", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
                    }
                }
                #endregion



                #region Determine Action
                if (caseHasDisposition)
                    await ValidationServicesUSNonC3.determineAction(caseEntity, validationRequestor, validationReqTransactionId, queueName, dispositionRequest);
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in determineProcessBehavior(). Exception message: " + Environment.NewLine + e.Message);
            }
            #endregion
        }
        public static async Task determineAction(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName,  IDictionary<string, Object> dispositionRequest)
        {
            try
            {

                #region AutomatedValDefinition
                bool autoCloseEnabled = DynamicsProcessesValidationServices.ConfigParams.autoCloseEnabled;
                string initialQueue = DynamicsProcessesValidationServices.ConfigParams.initialQueue;
                string postAutoCloseQueue = DynamicsProcessesValidationServices.ConfigParams.postAutoCloseQueue;
                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.ConfigParams.postNoAutoCloseQueue;
                string outreachQueueName = DynamicsProcessesValidationServices.ConfigParams.outreachQueueName;

                bool closoeAbandonedRequests = DynamicsProcessesValidationServices.ConfigParams.closeAbandonedRequests;
                int numberOfDaysInQueueToCloseAbandoned = DynamicsProcessesValidationServices.ConfigParams.numberOfDaysInQueueToCloseAbandoned;
                #endregion



                #region Parameters
                IDictionary<string, Object> valReqOrgAccountObj = await ValidationServicesHelper.getValidationRequestOrgAccountInfo(validationReqTransactionId);
                #endregion



                #region InitialQueue
                if (queueName == initialQueue)
                {
                    #region Catch Validation Requests That Already Have Org Account
                    if (valReqOrgAccountObj["tsOrgId"] != null)
                        hasOrgAccount_RouteOut(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest, valReqOrgAccountObj);
                    #endregion

                 

                    


                    #region nonC3 Subsection Mismatch Checks
                    bool subSectionMismatchNotEligible = DynamicsProcessesHelper.existsSystemNote(" --- Validation Request & IRS SubSections Don't Match --- ", new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                    bool subSectionMismatch_IRSSubsectionElig = DynamicsProcessesHelper.existsSystemNote(" --- SubSection Mismatch - Org Eligible Under IRS SubSection --- ", new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                    bool isIrsSubSectionEmpty = DynamicsProcessesHelper.existsSystemNote(" --- IRS SubSection Is Empty --- ", new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                    if (subSectionMismatchNotEligible)
                    {
                        bool isSubSection03 = validationRequestCase.GetAttributeValue<string>("ts_validationdispositionirssubsection") == "03" ? true : false;

                        if (!isSubSection03)
                        {
                            string orgNotEligibleEmailTemplate =
                                                                       (
                                                                           ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                                                   Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                                                       )?.emailOutreachProcess?.orgNotEligibleEmailTemplate;

                            await sendEmailFromTemplate(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest, orgNotEligibleEmailTemplate);

                            validationRequestCase["ts_casestatus"] = new OptionSetValue(102057);//OQ - Disqualified
                            DynamicsInterface.DataverseClient.Update(validationRequestCase);
                            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseQueue);

                        }
                        else
                        {
                            string orgIs501c3PerDispositionTemplate =
                                                                       (
                                                                           ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                                                   Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                                                       )?.emailOutreachProcess?.orgIs501c3PerDispositionTemplate;

                            processValidationRequestEmailOutreach(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest, updateCaseAndRoute: false);

                            validationRequestCase["ts_casestatus"] = new OptionSetValue(102057);//OQ - Disqualified
                            DynamicsInterface.DataverseClient.Update(validationRequestCase);
                            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseQueue);
                        }

                        return;

                    }
                    else if (subSectionMismatch_IRSSubsectionElig || isIrsSubSectionEmpty)
                    {
                        if (subSectionMismatch_IRSSubsectionElig)
                        {
                            validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                            DynamicsInterface.DataverseClient.Update(validationRequestCase);
                            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);
                            return;
                        }
                    }
                    #endregion



                    #region Fraud Check
                    bool? potentialFraud = await ValidationServicesUSNonC3.validationServicesEvaluateForFraudSimplified(validationRequestCase, account, validationReqTransactionId);

                    if (potentialFraud == null || potentialFraud.Value)
                        return;
                    #endregion



                    #region nonC3 EIN Matches
                    IDictionary<string, Object> einMatchResponse = await processNonC3EinMatches(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);

                    if (einMatchResponse != null && einMatchResponse.ContainsKey("validationProcessAction")
                        && (string)einMatchResponse["validationProcessAction"] == "terminate"
                        )
                        return;                    
                    #endregion



                    #region AutoClose
                    if (autoCloseEnabled)
                    {

                        bool isOrgValid = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesorgvalid");
                        bool isAgentValid = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesagentvalid");
                        bool isTrustWorthy = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                        bool isActivityCodeValid = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesactivitycodevalid");


                        bool isAgentValidDisposition = validationRequestCase.GetAttributeValue<bool>("ts_validationagentdisposition");
                        bool isOrgValidDisposition = validationRequestCase.GetAttributeValue<bool>("ts_validationorgdisposition");

                        EntityReference validationDispositionActivityCodeRef = validationRequestCase.GetAttributeValue<EntityReference>("ts_validationdispositionactivitycode");

                        if (
                            (isOrgValid && isAgentValid && isActivityCodeValid)

                            || (isOrgValid && isAgentValid && !isActivityCodeValid && validationDispositionActivityCodeRef != null)

                            )
                        {
                            await initiateOrgIncorporation(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
                            return;
                        }
                    }
                    #endregion



                    #region Routing & EmailOutreach
                    ValidationServicesUSNonC3.applyRoutingRules(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);
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





        public static void applyRoutingRules(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region AutomatedValDefinition & Parameters
                List<dynamic> autoCloseCustomeRules = ((JArray)DynamicsProcessesValidationServices.ConfigJson.config.autoCloseCustomRules)?.ToList<dynamic>();
                List<dynamic> queueRoutingRules = ((JArray)DynamicsProcessesValidationServices.ConfigJson.queueRoutingRules)?.ToList<dynamic>();

                List<dynamic> currentQueueRoutingRules = queueRoutingRules?.Where(rule => rule.routeFromQueue == queueName)?.ToList();


                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.ConfigParams.postNoAutoCloseQueue;
                #endregion



                #region EmailOutreach
                bool emailOutreachCriteria = evaluateValidationRequestCriteriaEmailOutreach(validationRequestCase, account, validationReqTransactionId, queueName,  dispositionRequest);
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
                bool autoOrgEmailOutreachEnabled = DynamicsProcessesValidationServices.ConfigParams.autoOrgOutreachEnabled;
                bool skipEmailOutreachIfArtifactPresent = DynamicsProcessesValidationServices.ConfigParams.skipEmailOutreachIfArtifactPresent;
                int artifactWaitTimeMinutes = DynamicsProcessesValidationServices.ConfigParams.artifactWaitTimeMinutes;


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


        public static void evaluateRoutingRule(dynamic rule, Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName)
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
        public static void hasOrgAccount_RouteOut(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest
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

        public static async Task sendEmailFromTemplate(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest, string templateName = null)
        {    
            try
            {
                #region Parameters
                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        ((string)dispositionRequest["AddressCountryId"])?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();


                if (templateName == null)
                {
                    templateName = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.emailTemplate;

                    string nonC3TemplateNanme = (
                                                    ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                            Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                                )?.emailOutreachProcess.emailTemplate;


                    templateName = nonC3TemplateNanme ?? templateName;
                }


                Entity template = DynamicsProcessesHelper.getTemplateEntity(templateName);

                string outreachQueueName = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.queueName;
                string outreachQueueHighPriority = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.queueNameHighPriority;

                string nonC3OutreachQueueName =
                                            (
                                                ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                        Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                            )?.emailOutreachProcess?.queueName;

                string nonC3OutreachQueueHighPriority =
                                            (
                                                ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                        Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                            )?.emailOutreachProcess?.queueNameHighPriority;

                #endregion



                #region Check If Email Already Sent
                DateTime validationRequestDate = validationRequestCase.GetAttributeValue<DateTime>("ts_validationrequestdate");
                QueryExpression queryEmail = new QueryExpression("email");
                queryEmail.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, validationRequestCase.Id);
                //queryEmail.Criteria.AddCondition("createdon", ConditionOperator.GreaterThan, validationRequestDate);
                queryEmail.Criteria.AddCondition("subject", ConditionOperator.Equal, template.GetAttributeValue<string>("subject"));
                EntityCollection emailCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryEmail);

                if (emailCollection.Entities.Count > 0)
                    return;
                #endregion



                #region ProcessingEmail
                Entity email = new Entity("email");

                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, "Support");
                EntityCollection entityCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryEntity);

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

                };


                SendEmailFromTemplateResponse response = (SendEmailFromTemplateResponse)(await DynamicsInterface.DataverseClient.ExecuteAsync(request));
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in sendEmailFromTemplate(...). Exception message: " + Environment.NewLine + e.Message
                                                                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                                                                );
            }
            #endregion
        }


        public static void getCustomerHasRespondedEmailOutreach()
        {
            try
            {
                #region AutomatedValDefinition
                string nonC3OutreachQueueName =
                                            (
                                                ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                        Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                            )?.emailOutreachProcess?.queueName;

                string nonC3OutreachQueueHighPriority =
                                            (
                                                ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                        Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                            )?.emailOutreachProcess?.queueNameHighPriority;




                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;


                string postNoAutoCloseNonC3Queue =
                               (
                                   ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                           Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                               )?.postNoAutoCloseQueue;


                postNoAutoCloseNonC3Queue = postNoAutoCloseNonC3Queue ?? postNoAutoCloseQueue;
                #endregion



                //ts_casestatus = 104699 -> OQ - AutoValidation - Customer Has Responded
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
                                    <filter type=""and"">
                                        <condition attribute=""ts_casestatus"" operator=""eq"" value=""104699""/>
                                    </filter>
			                        <link-entity name=""account"" alias=""aa"" link-type=""inner"" from=""accountid"" to=""customerid"">
				                        <attribute name=""accountnumber""/>
			                        </link-entity>
		                        </link-entity>
		                        <link-entity name=""queue"" alias=""qu"" link-type=""inner"" from=""queueid"" to=""queueid"">
			                        <filter type=""and"">
				                        <condition attribute=""name"" operator=""in"">
                                            <value>" + nonC3OutreachQueueName + @"</value>
                                            <value>" + nonC3OutreachQueueHighPriority + @"</value>
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


                    DynamicsProcessesHelper.addCaseToQueue(caseId, postNoAutoCloseNonC3Queue);
                }

                return;






                //    Entity caseEntity = DynamicsInterface.DataverseClient.Retrieve("incident", caseId, new ColumnSet(true));


                //    EntityReference customerIdRef = caseEntity.GetAttributeValue<EntityReference>("customerid");
                //    if (customerIdRef == null || customerIdRef.LogicalName != "account")
                //    {
                //        DynamicsInterface.DataverseClient.Delete(queueItem.LogicalName, queueItem.Id);
                //        continue;
                //    }

                //    Entity account = DynamicsInterface.DataverseClient.Retrieve("account", customerIdRef.Id, new ColumnSet(true));


                //    string validationReqTransactionId = caseEntity.GetAttributeValue<string>("ts_validationrequesttransactionid");


                //    Guid queueId = queueItem.GetAttributeValue<EntityReference>("queueid").Id;
                //    Entity queueEntity = DynamicsInterface.DataverseClient.Retrieve("queue", queueId, new ColumnSet("name"));
                //    string queueName = queueEntity.GetAttributeValue<string>("name");


                //    //determineAction(caseEntity, account, validationReqTransactionId, queueName);
                //}
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getCustomerHasRespondedEmailOutreach(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static void processValidationRequestEmailOutreach(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest, string templateName = null, bool updateCaseAndRoute = true)
        {
            try
            {
                #region Parameters
                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        ((string)dispositionRequest["AddressCountryId"])?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();


                if (templateName == null)
                {
                    templateName = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.emailOutreachProcess.emailTemplate;

                    string nonC3TemplateNanme = (
                                                    ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                            Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                                )?.emailOutreachProcess.emailTemplate;


                    templateName = nonC3TemplateNanme ?? templateName;
                }


                Entity template = DynamicsProcessesHelper.getTemplateEntity(templateName);

                string outreachQueueName = DynamicsProcessesValidationServices.ConfigParams.outreachQueueName;
                string outreachQueueHighPriority = DynamicsProcessesValidationServices.ConfigParams.outreachQueueHighPriority;

                #endregion



                #region Check If Email Already Sent
                DateTime validationRequestDate = validationRequestCase.GetAttributeValue<DateTime>("ts_validationrequestdate");
                QueryExpression queryEmail = new QueryExpression("email");
                queryEmail.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, validationRequestCase.Id);
                //queryEmail.Criteria.AddCondition("createdon", ConditionOperator.GreaterThan, validationRequestDate);
                queryEmail.Criteria.AddCondition("subject", ConditionOperator.Equal, template.GetAttributeValue<string>("subject"));
                EntityCollection emailCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEmail);

                if (emailCollection.Entities.Count > 0)
                    return;
                #endregion



                #region ProcessingEmail
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
                    
                };

                
                SendEmailFromTemplateResponse response = (SendEmailFromTemplateResponse)DynamicsInterface.DataverseClient.Execute(request);
                #endregion



                #region Updating Case & Routing
                if (response.Id != Guid.Empty)
                {
                    email = DynamicsInterface.DataverseClient.Retrieve("email", response.Id, new ColumnSet(true));
                    email["regardingobjectid"] = new EntityReference("incident", validationRequestCase.Id);
                    DynamicsInterface.DataverseClient.Update(email);

                    if (updateCaseAndRoute)
                    {
                        validationRequestCase["ts_casestatus"] = new OptionSetValue(104698); //OQ - AutoValidation - Awaiting Customer Response
                        DynamicsInterface.DataverseClient.Update(validationRequestCase);

                        string nextQueue = outreachQueueName;
                        if (nextQueue != queueName)
                            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, nextQueue);
                    }
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
        public static async Task initiateOrgIncorporation(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region AutomatedValDefinition & Parameters
                string postAutoCloseQueue = DynamicsProcessesValidationServices.ConfigParams.postAutoCloseQueue;
                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.ConfigParams.postNoAutoCloseQueue;
                #endregion



                #region Create Org Account
                Guid? qualCaseId = validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid")?.Id;
                Entity qualCase = qualCaseId == null ? null : DynamicsInterface.DataverseClient.Retrieve("incident", qualCaseId.Value, new ColumnSet(true));
                Guid? orgAccountId = qualCase?.GetAttributeValue<EntityReference>("customerid")?.Id;
                Entity orgAccount = orgAccountId == null ? null : DynamicsInterface.DataverseClient.Retrieve("account", orgAccountId.Value, new ColumnSet(true));

                orgAccount = await ValidationServicesUSNonC3.createUpdateOrgFromValReq(account, validationRequestCase, validationReqTransactionId, dispositionRequest, orgAccount);

                if (orgAccount == null)
                {
                    DynamicsInterface.writeToLog("orgAccount is null after createUpdateOrgFromValReq(...)");

                    
                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);

                    return;
                }
                string validationRequestAgentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentemail");
                ValidationServicesHelper.removeAccountConnection(orgAccount.Id, validationRequestAgentEmail);
                #endregion



                #region CreateQualCase & Contact & Connection


                Entity orgDesignationCodeEntity = orgAccount.GetAttributeValue<EntityReference>("new_orgdesignation") == null ? null :
                                                           DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", orgAccount.GetAttributeValue<EntityReference>("new_orgdesignation").Id, new ColumnSet(true));



                Guid orgDesigId = orgDesignationCodeEntity == null ? Guid.Empty : orgDesignationCodeEntity.Id;
                string orgDesigCode = orgDesignationCodeEntity?.GetAttributeValue<string>("new_qualcode");


                DynamicsInterface.writeToLog("New orgDesigCode: " + orgDesigCode);


                //Get the qualCase again, in case orgAccount changed designation
                Entity qualCaseAfterOrgUpdate = DynamicsProcessesHelper.getCaseEntity(
                                                            caseTypeCode: 2 // Qualification Case
                                                            , type: 101996 // Organization Qualification
                                                            , accountId: orgAccount.Id
                                                            , qualCodeId: orgDesigId
                                                            , tsOrderId: null
                                                            );

                Guid qualCaseIdeAfterOrgUpdate = qualCaseAfterOrgUpdate == null ? Guid.Empty : qualCaseAfterOrgUpdate.Id;


                DynamicsInterface.writeToLog("Initial qualCaseId: " + qualCaseId + "; qualCaseIdeAfterOrgUpdate: " + qualCaseIdeAfterOrgUpdate);

                if (qualCase != null && qualCaseId != qualCaseIdeAfterOrgUpdate)
                {
                    qualCase["ts_validationrequesttransactionid"] = null;
                    DynamicsInterface.DataverseClient.Update(qualCase);

                    DynamicsInterface.writeToLog("ts_validationrequesttransactionid for Initial qualCaseId: " + qualCaseId + ", set to null");

                    validationRequestCase["parentcaseid"] = qualCaseAfterOrgUpdate.ToEntityReference();
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    DynamicsInterface.writeToLog("Validation Request re-assigned to qual case with the same qual code");

                }
                else if (qualCase == null && qualCaseAfterOrgUpdate != null)
                {
                    validationRequestCase["parentcaseid"] = qualCaseAfterOrgUpdate.ToEntityReference();
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);

                    DynamicsInterface.writeToLog ("Validation Request assigned to the qual case that was created as a result of the new org account being created");
                }

                qualCase = ValidationServicesHelper.processQualCaseFromValidationRequest(orgAccount, validationRequestCase);


                EntityReference vallReqParentCaseRef = validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid");

                if (vallReqParentCaseRef == null)
                {
                    validationRequestCase["parentcaseid"] = new EntityReference(qualCase.LogicalName, qualCase.Id);
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                }



                OptionSetValue agentVerificationStatusOption = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_validationrequestagentverification");

                Entity agentContact = ValidationServicesHelper.createAgentContact(validationRequestCase, account, validationReqTransactionId, queueName);

                if (agentContact != null)
                    ValidationServicesHelper.connectAgentToAccount(orgAccount.Id, agentContact.Id, agentVerificationStatusOption, validationReqTransactionId);


                if (orgAccount == null || agentContact == null)
                {
                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);

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


        public static async Task<Entity> createUpdateOrgFromValReq(Entity requestingAccount, Entity validationRequestCase, string validationReqTransactionId, IDictionary<string, Object> dispositionRequest, Entity orgAccount)
        {
            #region Parameters
            Guid accountId = Guid.Empty;
            string tsOrgId = string.Empty;
            Entity newAccount = null;
            #endregion

            try
            {
                #region New Account Entity
                bool isNewAccount = false;
                if (orgAccount == null)
                {
                    isNewAccount = true;
                    orgAccount = new Entity("account");
                }
                else
                {
                    accountId = orgAccount.Id;
                }
                #endregion

                #region Name, Org Designation, Mission Statement...
                orgAccount["name"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalname");
                orgAccount["customertypecode"] = new OptionSetValue(3); //Customer
                orgAccount["new_source"] = new OptionSetValue(101892); //TSS Web Site 101892

                EntityReference qualCodeRef = validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid");
                orgAccount["new_orgdesignation"] = qualCodeRef;

                orgAccount["ts_missionstatement"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestmissionstatement");

                orgAccount["telephone1"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestphone");
                #endregion


                #region Address
                orgAccount["address1_country"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid");

                orgAccount["address1_stateorprovince"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressstateregion");

                orgAccount["address1_line1"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressline1");


                orgAccount["address1_line2"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressother") == "nil" ? null : validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressother");

                orgAccount["address1_city"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscity");
                orgAccount["address1_postalcode"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresspostalcode");


                #region Country And State Hierarchy Mapping
                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid"));
                EntityCollection fieldMapCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    orgAccount["ts_countrydesc"] = new OptionSetValue(countryOptionValue);
                }


                queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid"));
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressstateregion"));
                fieldMapCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int stateRegionOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    orgAccount["ts_stateprovdesc"] = new OptionSetValue(stateRegionOptionValue);
                }
                #endregion
                #endregion


                #region Email, Url, Budget, Legal Identifier & Activity Code

                orgAccount["emailaddress1"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestemail");


                orgAccount["websiteurl"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite");

                orgAccount["new_budget"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestoperatingbudget");




                List<IDictionary<string, Object>> registrationIdentifiers = JsonConvert.DeserializeObject<List<ExpandoObject>>(
                                                                                                                                 JsonConvert.SerializeObject(dispositionRequest["RegistrationIdentifiers"])
                                                                                                                                 ).ToList<IDictionary<string, Object>>();




                orgAccount["new_legalidentifier"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalidentifier");

                EntityReference validationDispositionActivityCodeRef = validationRequestCase.GetAttributeValue<EntityReference>("ts_validationdispositionactivitycode");

                if (validationDispositionActivityCodeRef != null)
                {
                    orgAccount["new_activitycode"] = validationDispositionActivityCodeRef;
                }
                else
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, validationRequestCase.GetAttributeValue<string>("ts_validationrequestactivitycode"));
                    EntityCollection entityCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryEntity);

                    if (entityCollection.Entities.Count > 0)
                        orgAccount["new_activitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);
                }

                #endregion


                #region Account Update
                if (isNewAccount)
                {
                    accountId = await DynamicsInterface.DataverseClient.CreateAsync(orgAccount);

                    //orgAccount = await DynamicsInterface.DataverseClient.RetrieveAsync(orgAccount.LogicalName, accountId, new ColumnSet(true));
                }
                else
                {
                    await DynamicsInterface.DataverseClient.UpdateAsync(orgAccount);
                }

                orgAccount = await DynamicsInterface.DataverseClient.RetrieveAsync("account", accountId, new ColumnSet(true));
                #endregion

            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in createUpdateOrgFromValReq(...). Exception message: " + Environment.NewLine + e.Message
                                                  + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                  );


                DynamicsProcessesHelper.processSystemNote(" --- Error Creating/Updating Org --- ", "Error in createOrgFromCase(string validationReqTransactionId). Exception message: " + Environment.NewLine + e.Message
                                                                , new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


            }
            #endregion

            return orgAccount;
        }
        public static async Task getValidationScoreMatrix(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                #region Calling Score Matrix API
                string ctpUrl = "https://tsvc.tsgctp.org/";
                string ctpSessionKey = ValidationServicesHelper.CTPSessionKey;
                //"61695af7-1652-4b08-b786-192de1884f61";
                string endPointPath = "services/vsscorematrix/v_001/";

                Dictionary<string, string> queryParams = new Dictionary<string, string>();
                queryParams.Add("transaction_id", validationReqTransactionId);


                dynamic dispositionResponse = ValidationServicesHelper.makeHttpGetCall(
                                                                                        ctpUrl, endPointPath
                                                                                        , ctpSessionKey, queryParams
                                                                                        );



                string dispositionResponseText = JsonConvert.SerializeObject(dispositionResponse);
                dynamic automatedValSettings = ValidationServicesHelper.getAutomatedValidationConfig();

                dynamic automatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);
                #endregion





                #region Get Disposition Data & Disposition Transaction Id
                dynamic dispositionData = dispositionResponse.returnStatus.data;


                string dispositionDataText = JsonConvert.SerializeObject(dispositionData);
                validationRequestCase["ts_validationdispositiondata"] = dispositionDataText;


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
                                                                                                                                       validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToString() ?? ""
                                                                                                                                       )?
                                                                                                                                       .ToLower()?.Replace("gb", "uk")
                                                                                                                       )?.FirstOrDefault();

                postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;





                string dispositionStatus = dispositionData.score_matrix_status == null ? "" : dispositionData.score_matrix_status;
                validationRequestCase["ts_validationdispositionstatus"] = dispositionStatus;

                string noteDesc = "";
                string noteTitle = "";
                if (dispositionStatus != "completed")
                {
                    int checkCountsForManagedQueue = automatedValDefinition.config.checkCountsForManagedQueue;

                    validationRequestCase["ts_validationrequestlaststatuscheck"] = DateTime.UtcNow;
                    int dispositionRequestChecksCount = validationRequestCase.GetAttributeValue<int>("ts_validationstatuscheckscount");
                    dispositionRequestChecksCount++;
                    validationRequestCase["ts_validationstatuscheckscount"] = dispositionRequestChecksCount;

                    if (dispositionRequestChecksCount >= checkCountsForManagedQueue)
                    {
                        validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                                                                                            //DynamicsInterface.DataverseClient.Update(validationRequestCase);
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);

                        ValidationServicesHelper.processSystemNote("No Disposition Received", "There was no validation resolution after the conclusion of the time allotted for this process", new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));
                    }
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
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
                validationRequestCase["ts_validationdispositionorgname"] = dispositionOrgName;

                string validationRequestLegalName = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalname");

                string IRSOrgName = dispositionData.ext_db_org_data.NAME;
                validationRequestCase["ts_validationdispositionirsorgname"] = IRSOrgName;

                IRSOrgName = IRSOrgName == null ? "" : IRSOrgName;


                float levenshteinDistance = Fastenshtein.Levenshtein.Distance(validationRequestLegalName.ToLower(), IRSOrgName.ToLower());
                float topLength = Math.Max(validationRequestLegalName.Length, IRSOrgName.Length);

                float levenshteinIndex = (topLength - levenshteinDistance) / topLength;

                bool orgNamesMatch = levenshteinIndex >= 0.60 ? true : false;
                #endregion


                #region SubSection Matching
                string IRSSubSection = dispositionData.ext_db_org_data.SUBSECTION;
                IRSSubSection = IRSSubSection == null ? "" : IRSSubSection.PadLeft(2, '0');

                IRSSubSection = IRSSubSection == "00" ? "" : IRSSubSection;

                Entity qualCodeEntity = validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid") == null ? null :
                                                            DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid").Id, new ColumnSet(true));

                string validationRequestNonC3OrgDesig = qualCodeEntity?.GetAttributeValue<string>("new_qualcode");

                string valReqNonC3SubSection = validationRequestNonC3OrgDesig.Substring(validationRequestNonC3OrgDesig.Length - 2, 2);
                valReqNonC3SubSection = DynamicsProcessesHelper.regexMatch(@"^\d+$", valReqNonC3SubSection) ? valReqNonC3SubSection :
                                                                                                    validationRequestNonC3OrgDesig.Substring(validationRequestNonC3OrgDesig.Length - 1, 1).PadLeft(2, '0');


                validationRequestCase["ts_validationdispositionirssubsection"] = IRSSubSection;
                bool valReqIrsSubSectionMatch = IRSSubSection == valReqNonC3SubSection ? true : false;







                if ((!valReqIrsSubSectionMatch))
                {
                    if (IRSSubSection == "")
                    {
                        noteTitle = " --- IRS SubSection Is Empty --- ";

                        noteDesc = "IRS SubSection provided by the Disposition is empty"
                                    + Environment.NewLine + Environment.NewLine + "Validation Request will be routed for manual validation";

                    }
                    else
                    {
                        string valReqOrgDesigExtractedCSection = validationRequestNonC3OrgDesig.Substring(validationRequestNonC3OrgDesig.Length - 2, 2);
                        valReqOrgDesigExtractedCSection = DynamicsProcessesHelper.regexMatch(@"^\d+$", valReqOrgDesigExtractedCSection) ? "c" + valReqOrgDesigExtractedCSection : valReqOrgDesigExtractedCSection;


                        string irsCSectionValue = "c" + (IRSSubSection.Substring(0, 1) == "0" ? IRSSubSection.Substring(1, 1) : IRSSubSection);
                        string irsValidatedOrgDesignation = validationRequestNonC3OrgDesig.Replace(valReqOrgDesigExtractedCSection, irsCSectionValue);


                        List<string> nonC3Designations = ((JArray)(
                                        ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                      Where(country => ((string)country.country)?.ToLower() == "us"
                                                                                                      )?.FirstOrDefault()?.orgDesignations
                                       ))?.ToList<dynamic>()?.Select(item => (string)item)?.ToList();



                        if (
                            nonC3Designations.Exists(designation => designation.ToLower() == irsValidatedOrgDesignation.ToLower())
                            )
                        {
                            noteTitle = " --- SubSection Mismatch - Org Eligible Under IRS SubSection --- ";

                            noteDesc = "Validation Request Subsection, " + valReqNonC3SubSection + ", and IRS SubSection, " + IRSSubSection + ", don't match"
                                        + Environment.NewLine + "Org eligible if Validation Request is updated to match IRS subsection"
                                        + Environment.NewLine + Environment.NewLine + "Validation Request will be routed for manual validation";

                        }
                        else
                        {
                            noteTitle = " --- Validation Request & IRS SubSections Don't Match --- ";

                            noteDesc = "Validation Request Subsection, " + valReqNonC3SubSection + ", and IRS SubSection, " + IRSSubSection + ", don't match"
                                        + Environment.NewLine + "And Org IS NOT eligible if Validation Request is updated to match IRS subsection"
                                        + Environment.NewLine + Environment.NewLine + "Validation Request will be disqualified";


                        }
                    }

                    ValidationServicesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));
                }
                #endregion


                #region Legal Identifier Matching
                string IRSOrgEIN = dispositionData.ext_db_org_data.EIN;
                string legalIdentifier = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalidentifier");

                legalIdentifier = legalIdentifier == null ? "" : legalIdentifier;
                bool legalIdentifiersMatch = IRSOrgEIN == legalIdentifier.Replace("-", "") ? true : false;
                #endregion

                #region IRS Revocation Check
                string IRSRevocationCode = dispositionData.ext_db_org_data.REV_CD;

                bool isOnIRSRevokeList = IRSRevocationCode == null || IRSRevocationCode.ToLower() == "na" ? false : true;

                validationRequestCase["ts_validationdispositiononirsrevokelist"] = isOnIRSRevokeList;
                #endregion

                #endregion





                #region Org Rules
                bool isOrgValid = false;
                if (isIRSPresent && orgNamesMatch &&
                    valReqIrsSubSectionMatch &&
                    legalIdentifiersMatch && !isOnIRSRevokeList)
                    isOrgValid = true;

                validationRequestCase["ts_validationdispositionrulesorgvalid"] = isOrgValid;


                validationRequestCase["ts_validationorgdisposition"] = orgDisposition.ToLower() == "is" ? true : false;
                validationRequestCase["ts_orgvalidationdispositionscore"] = orgDispScore.ToString();
                #endregion






                #region Agent Details
                string agentName = validationRequestCase.GetAttributeValue<string>("ts_validationagentname") ?? "";

                float levenshteinDistOrgAgent = Fastenshtein.Levenshtein.Distance(validationRequestLegalName.ToLower(), agentName.ToLower());
                float topLengthOrgAgent = Math.Max(validationRequestLegalName.Length, agentName.Length);
                float levenshteinOrgAgentIndex = (topLengthOrgAgent - levenshteinDistOrgAgent) / topLengthOrgAgent;

                bool isAgentNameValid = string.IsNullOrWhiteSpace(agentName) ? false : (levenshteinOrgAgentIndex < 0.60 ? true : false);


                List<dynamic> ctpOrgDataList = ((JArray)dispositionData.ctp_db_match_set).ToList<dynamic>();


                dynamic orgData = null;

                if (ctpOrgDataList.Count > 0)
                    orgData = ctpOrgDataList.First();


                string dispositionOrgWebsite = orgData == null ? "" : (orgData.org_website == null ? "" : orgData.org_website);

                validationRequestCase["ts_validationdispositionorgwebsite"] = dispositionOrgWebsite;


                string validationRequestWebsite = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite") == null ? "" 
                                                                                                                        : validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite");

                string validationRequestAgentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentemail") ?? "";

                string valReqAgentEmailDomain = ValidationServicesHelper.regexMatchValue("(?<=@)(.+)", validationRequestAgentEmail, 0);

                bool agentOrgCommonDomain = string.IsNullOrWhiteSpace(valReqAgentEmailDomain) ? false : ValidationServicesHelper.regexMatch(valReqAgentEmailDomain, validationRequestWebsite);
                #endregion







                #region Agent Diposition & Validation Rules
                //validationRequestCase["ts_validationdispositionrulesagentvalid"] = agentDisposition.ToLower() == "is" ? true : false;
                bool dispositionAgentValid = agentDisposition.ToLower() == "is" ? true : false;

                validationRequestCase["ts_validationdispositionrulesagentvalid"] = false;
                validationRequestCase["ts_validationrequestagentverification"] = new OptionSetValue(0);

                if (dispositionAgentValid && isAgentNameValid && agentOrgCommonDomain)
                {
                    validationRequestCase["ts_validationdispositionrulesagentvalid"] = true;
                    validationRequestCase["ts_validationrequestagentverification"] = new OptionSetValue(1);
                }

                validationRequestCase["ts_validationagentdisposition"] = agentDisposition.ToLower() == "is" ? true : false;
                validationRequestCase["ts_agentvalidationdispositionscore"] = agentDispScore.ToString();


                //initial value for Agent Verification
                

                #endregion




                #region Trustworthy Disposition
                validationRequestCase["ts_validationdispositiontrustworthy"] = trustworthyDisposition.ToLower() == "is" ? true : false;
                validationRequestCase["ts_validationdispositiontrustworthyconfidence"] = trustworthyConfidence.ToString();
                #endregion



                #region Legal Equivalence Disposition
                validationRequestCase["ts_validationlegalequivalencedisposition"] = legalEquivalenceDispositionLower == "does" ? true : false;
                #endregion



                #region Activity Code Validation Rules
                string selfReportedActivityCode = validationRequestCase.GetAttributeValue<string>("ts_validationselfreportedactivitycode");

                string IRSNteeCode = dispositionData.ext_db_org_data.NTEE_CD == null ? "" : dispositionData.ext_db_org_data.NTEE_CD;

                validationRequestCase["ts_validationdispositionirsnteecode"] = IRSNteeCode;

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

                validationRequestCase["ts_validationdispositionsystemreportednteecodes"] = dispositionSystemNteeCodesCsv;
                validationRequestCase["ts_validationdispositionsystemactivitycodes"] = dispositionSystemActivityCodesCsv;


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

                validationRequestCase["ts_validationsensitivitylistactivitycode"] = sensitiveListActivityCode;


                string[] whiteListedActivityCodes = { "030", "046", "205", "317", "318" };


                bool isActivityCodeWhiteListed = whiteListedActivityCodes.Contains(selfReportedActivityCode) ? true : false;

                bool isActivityCodeInInternalList = activityCodes.Exists(item => item.act_sub == IRSNteeCode); //IRS NTEE found in internal reference

                bool nteeSensitiveListFound = dispositionData.ntee_code_sensitive_list_found;

                if (
                    (isActivityCodeWhiteListed || isActivityCodeInInternalList || selfReportedActivityCode == activityCodeFinal)
                    && !nteeSensitiveListFound
                    )
                    validationRequestCase["ts_validationdispositionrulesactivitycodevalid"] = true;
                




                validationRequestCase["ts_validationdispositionactivitycodematch"] = activityCodeFinal;


                if (!string.IsNullOrEmpty(activityCodeFinal) && activityCodeFinal != sensitiveListActivityCode)
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, activityCodeFinal);
                    EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                    if (entityCollection.Entities.Count > 0)
                        validationRequestCase["ts_validationdispositionactivitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);

                }
                #endregion








                #region AutoClose Rules
                List<dynamic> autoCloseCustomeRules = ((JArray)automatedValDefinition.config.autoCloseCustomRules).ToList<dynamic>();

                bool isAgentValid = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesagentvalid");
                bool isTrustWorthy = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositiontrustworthy");
                bool isActivityCodeValid = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesactivitycodevalid");


                bool isAgentValidDisposition = validationRequestCase.GetAttributeValue<bool>("ts_validationagentdisposition");
                bool isOrgValidDisposition = validationRequestCase.GetAttributeValue<bool>("ts_validationorgdisposition");



                validationRequestCase["ts_validationdispositionrulesautoclosequalify"] = false;
                validationRequestCase["ts_validationactionfromdispositionrules"] = "Manual - Further Evaluation Needed";

                if (isOrgValid && isAgentValid && isActivityCodeValid)
                {
                    validationRequestCase["ts_validationdispositionrulesautoclosequalify"] = true;

                    validationRequestCase["ts_validationactionfromdispositionrules"] = "Qualify - AutoClose";

                    /*Setting "Validation Disposition Activity Code Final" back to null*/
                    validationRequestCase["ts_validationdispositionactivitycode"] = null;
                }
                else if (isOrgValid && isAgentValid && !isActivityCodeValid && validationRequestCase.GetAttributeValue<EntityReference>("ts_validationdispositionactivitycode") != null)
                {
                    validationRequestCase["ts_validationactionfromdispositionrules"] = "Update Activity Code With " + activityCodeFinal + " -  AutoQualify";
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
                        if (validationRequestCase.GetAttributeValue<bool>("ts_validationorgdisposition"))
                            rulesEvaluatedAction = "AutoClose - Qualify";
                        break;

                    default:
                        rulesEvaluatedAction = string.IsNullOrEmpty(rulesEvaluatedAction) ? "" : Char.ToUpper(rulesEvaluatedAction[0]) + rulesEvaluatedAction.Substring(1);
                        break;
                }

                validationRequestCase["ts_validationdispositionaction"] = rulesEvaluatedAction;
                #endregion







                #region Update validationRequestCase
                DynamicsInterface.DataverseClient.Update(validationRequestCase);
                #endregion


                #region Add System Note With Disposition Details
                string dispositionReference = dispositionData.reference_id == null ? "" : dispositionData.reference_id;
                string dispositionUrl = ValidationServicesHelper.regexMatchValue("https:.+?html", dispositionReference, 0);


                noteTitle = " --- Disposition Details --- ";

                noteDesc = "Full Disposition:" + Environment.NewLine + Environment.NewLine;
                noteDesc += dispositionUrl;
                noteDesc += Environment.NewLine + Environment.NewLine + "Score Matrix: ";
                noteDesc += Environment.NewLine + Environment.NewLine + scoreItems;

                ValidationServicesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                //Entity validationRequestCase = ValidationServicesHelper.getCaseForTransactionId(validationReqTransactionId);

                //ValidationServicesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));
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
        public static async Task<IDictionary<string, Object>> processNonC3EinMatches(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            IDictionary<string, Object> response = new ExpandoObject() as IDictionary<string, Object>;
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

                string postAutoCloseNonC3Queue =
                               (
                                   ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                           Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                               )?.postAutoCloseQueue;


                postAutoCloseNonC3Queue = postAutoCloseNonC3Queue ?? postAutoCloseQueue;


                string postNoAutoCloseNonC3Queue =
                               (
                                   ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                           Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                               )?.postNoAutoCloseQueue;


                postNoAutoCloseNonC3Queue = postNoAutoCloseNonC3Queue ?? postNoAutoCloseQueue;
                #endregion


                string validationRequestLegalIdentifier = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalidentifier");
                //ts_validationrequestlegalidentifier

                Dictionary<string, object> filterFieldsIn = new Dictionary<string, object>();
                filterFieldsIn.Add("casetypecode", 5); //Validation Request
                filterFieldsIn.Add("ts_validationrequestlegalidentifier", validationRequestLegalIdentifier);

                Dictionary<string, object> filterFieldsOut = new Dictionary<string, object>();
                filterFieldsOut.Add("ts_validationrequesttransactionid", validationReqTransactionId);


                Entity caseEntityExcludingPresentTranId = await ValidationServicesHelper.findCaseGenericFilterInAndOut(filterFieldsIn, filterFieldsOut);

                bool exitsCaseExcludingPresentTranId = caseEntityExcludingPresentTranId == null ? false : true;

                if ((exitsCaseExcludingPresentTranId))
                {
                    {

                        string noteTitle = " --- A Validation Request Already Exists For EIN --- ";

                        string noteDesc = "There's already a Validation Request case created with the same Legal Identifier (EIN): " + validationRequestLegalIdentifier
                                            + Environment.NewLine + Environment.NewLine + "Current Validation Request will be cancelled";

                        ValidationServicesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                        validationRequestCase["ts_casestatus"] = new OptionSetValue(102074);//OQ - Cancelled
                        DynamicsInterface.DataverseClient.Update(validationRequestCase);
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseNonC3Queue);

                        response["einMatchFound"] = true;
                        response["validationProcessAction"] = "terminate";
                        return response;

                    }
                }

                

                filterFieldsIn = new Dictionary<string, object>();
                filterFieldsIn.Add("new_legalidentifier", validationRequestLegalIdentifier);

                EntityCollection entityCollection = await ValidationServicesHelper.findEntityeGenericFilterInAndOut("account", filterFieldsIn, null);

                if (entityCollection == null)
                {
                    response["einMatchFound"] = false;
                    response["error"] = "null was returned from findEntityeGenericFilterInAndOut for account entity";
                    response["validationProcessAction"] = "terminate";
                    return response;
                }
                else if (entityCollection.Entities.Count == 0)
                {
                    response["einMatchFound"] = false;
                    response["validationProcessAction"] = "continue";
                    return response;
                }
                else if (entityCollection.Entities.Count > 1)
                {
                    List<string> tsOrgIdsMatchingEin = entityCollection.Entities.Select(acc => acc.GetAttributeValue<string>("accountnumber")).ToList();

                    string tsOrgIdsMatchingEinCsv = string.Join(", ", tsOrgIdsMatchingEin);


                    string noteTitle = " --- Multiple Accounts Found For EIN --- ";

                    string noteDesc = "Multiple account records found with the same Legal Identifier (EIN): " + validationRequestLegalIdentifier
                                        + Environment.NewLine + "TSOrgIds: " + tsOrgIdsMatchingEinCsv
                                        + Environment.NewLine + Environment.NewLine + "Current Validation Request will be routed for manual validation";

                    ValidationServicesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104697); //OQ - AutoValidation - Requires Further Evaluation	
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseNonC3Queue);


                    response["einMatchFound"] = true;
                    response["validationProcessAction"] = "terminate";
                    return response;

                }
                else if (entityCollection.Entities.Count == 1)
                {
                    Entity matchAccount = entityCollection.Entities.First();

                    string matchAccTsOrgId = matchAccount.GetAttributeValue<string>("accountnumber");
                    Entity matchAccountQualCodeEntity = matchAccount.GetAttributeValue<EntityReference>("new_orgdesignation") == null ? null :
                                                           DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", matchAccount.GetAttributeValue<EntityReference>("new_orgdesignation").Id, new ColumnSet(true));



                    Guid matchAccountOrgDesigId = matchAccountQualCodeEntity == null ? Guid.Empty : matchAccountQualCodeEntity.Id;
                    string matchAccountOrgDesigCode = matchAccountQualCodeEntity?.GetAttributeValue<string>("new_qualcode");



                    Entity qualCase = DynamicsProcessesHelper.getCaseEntity(
                                                        caseTypeCode: 2 // Qualification Case
                                                        , type: 101996 // Organization Qualification
                                                        , accountId: matchAccount.Id
                                                        , qualCodeId: matchAccountOrgDesigId
                                                        , tsOrderId: null
                                                        );

                    if (qualCase != null)
                    {
                        validationRequestCase["parentcaseid"] = qualCase.ToEntityReference();
                        DynamicsInterface.DataverseClient.Update(validationRequestCase);
                    }


                    Entity qualCodeEntity = validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid") == null ? null :
                                                            DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid").Id, new ColumnSet(true));

                    string nonC3ValReqQualCode = qualCodeEntity?.GetAttributeValue<string>("new_qualcode");


                    if (matchAccountOrgDesigCode != nonC3ValReqQualCode)
                    {
                        string[] disqualifiedOrgQualStatuses = { "Disqualified", "Canceled" };


                        string matchAccountOrgQualStatus = DynamicsProcessesHelper.getOrgQualStatus(matchAccount.Id, matchAccountOrgDesigId);


                        if (disqualifiedOrgQualStatuses.Contains(matchAccountOrgQualStatus))
                        {
                            string noteTitle = " --- Acccount Found For EIN --- ";

                            string noteDesc = "Account record found for Legal Identifier (EIN): " + validationRequestLegalIdentifier
                                                + Environment.NewLine + "TSOrgId: " + matchAccTsOrgId
                                                + Environment.NewLine + Environment.NewLine + "Since Qual Status is " + matchAccountOrgQualStatus + ", Validation Request for " + nonC3ValReqQualCode + " can proceed";

                            ValidationServicesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));



                            response["einMatchFound"] = true;
                            response["validationProcessAction"] = "continue";

                            return response;
                        }
                        else
                        {
                            string noteTitle = " --- Acccount Found For EIN --- ";

                            string noteDesc = "Account record found for Legal Identifier (EIN): " + validationRequestLegalIdentifier
                                                + Environment.NewLine + "TSOrgId: " + matchAccTsOrgId
                                                + Environment.NewLine + Environment.NewLine + "Since Qual Status, " + matchAccountOrgQualStatus + ", is not Disqualified or Canceled, there seems to be a real discrepancy between "
                                                + "Validation Request, " + nonC3ValReqQualCode + ", and account Org Designation, " + matchAccountOrgDesigCode
                                                + Environment.NewLine + Environment.NewLine + "Current Validation Request will be routed for manual validation";


                            ValidationServicesHelper.processSystemNote(noteTitle, noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                            string orgDesigDiscrepancyEmailTemplate =
                                                                       (
                                                                           ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                                                   Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                                                       )?.emailOutreachProcess?.orgDesigDiscrepancyEmailTemplate;

                            processValidationRequestEmailOutreach(validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest, orgDesigDiscrepancyEmailTemplate);



                            validationRequestCase["ts_casestatus"] = new OptionSetValue(104697); //OQ - AutoValidation - Requires Further Evaluation	
                            DynamicsInterface.DataverseClient.Update(validationRequestCase);
                            DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseNonC3Queue);


                            response["einMatchFound"] = true;
                            response["validationProcessAction"] = "terminate";
                            return response;


                        }
                    }
                }
                return response;
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processNonC3EinMatches(...). Exception message: " + Environment.NewLine + e.Message
                                                 + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
                response["einMatchFound"] = false;
                response["error"] = e.Message;
                response["validationProcessAction"] = "terminate";
                return response;
            }
            #endregion

        }


        public static async Task<bool> validationServicesEvaluateForFraudSimplified(Entity validationRequestCase, Entity requestingAccount, string validationReqTransactionId)
        {
            var fraudFlags = new List<string>();
            bool isFraudulent = false;

            
            //string orgDomainRegistrationCountry = string.Empty;


            try
            {
                string website = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite");
                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationagentemail");

                
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


                    string fraudReviewNonC3Queue =
                               (
                                   ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                           Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                               )?.fraudReviewQueue;


                    
                    string fraudReviewQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.config.fraudReviewQueue;

                    fraudReviewNonC3Queue = fraudReviewNonC3Queue ?? fraudReviewQueue;

                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, fraudReviewNonC3Queue);

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
