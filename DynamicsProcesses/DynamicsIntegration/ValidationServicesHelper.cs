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
using System.Text.RegularExpressions;
using DynamicsProcesses.DataAccessService;
using System.Xml;
using System.Runtime.Remoting.Services;
using System.Net.Mail;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using System.Dynamic;
using System.Web.Configuration;
using Microsoft.Extensions.Logging;
using System.Web.Util;
using System.Security.Policy;

namespace DynamicsProcesses
{
    internal class ValidationServicesHelper
    {
        public static DispositionRequest DispositionClass = new DispositionRequest();
        public static RegistrationIdentifier RegIdentifierClass = new RegistrationIdentifier();


        public static Guid createCase(string title
                                        , int caseTypeCode
                                        , int? type
                                        , EntityReference customerRef
                                        , int caseStatus
                                        , Guid? qualCodeId
                                        , Dictionary<string, string> extraCaseFields
                                        )
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

                caseId = DynamicsInterface.DataverseClient.Create(caseEntity);

            }
            catch (Exception e)
            {


            }

            return caseId;
        }

        public static dynamic makeHttpGetCall(
                                                string baseUrl, string endPointPath
                                                , string key, Dictionary<string, string> queryParams
                                                )
        {
            dynamic respDynObject = null;

            try
            {
                HttpClient client = new HttpClient();
                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));



                string queryString = "";
                if (queryParams != null && queryParams.Count > 0)
                {
                    queryString = string.Join("&", queryParams.Select(kvp => $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));
                    queryString = "?" + queryString;
                }
                string requestUrl = baseUrl + endPointPath + key + queryString;





                HttpResponseMessage response = client.GetAsync(
                                                                requestUrl
                                                                )
                                                                    .Result;



                string responseTxt = response.Content.ReadAsStringAsync().Result;
                respDynObject = JsonConvert.DeserializeObject(responseTxt);

                client.Dispose();

            }
            catch (Exception e)
            {
            }

            return respDynObject;
        }




        public static dynamic makeHttpPostCall(string requestJson
                                                            , string baseUrl, string endPointPath
                                                            , string key, Dictionary<string, string> queryParams
            //, ILogger<ValidationRequest> logger
            )
        {

            dynamic respDynObject = null;

            try
            {
                HttpClient client = new HttpClient();
                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));




                //baseUrl ??= DynamicsEnvironment;
                //endPointPath ??= "";
                //key ??= "";

                string queryString = "";
                if (queryParams != null && queryParams.Count > 0)
                {
                    queryString = string.Join("&", queryParams.Select(kvp => $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));
                    queryString = "?" + queryString;
                }
                string requestUrl = baseUrl + endPointPath + key + queryString;






                StringContent contentRequest = new StringContent(requestJson, Encoding.UTF8, "application/json");

                HttpResponseMessage response = client.PostAsync(
                                                                requestUrl
                                                                , contentRequest
                                                                 )
                                                                    .Result;






                string responseTxt = response.Content.ReadAsStringAsync().Result;

                respDynObject = JsonConvert.DeserializeObject(responseTxt);


            }
            catch (Exception e)
            {

            }

            return respDynObject;
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
                    annotation["objectid"] = new EntityReference("incident", annotationParentRef.Id);
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
                DynamicsInterface.writeToLog("Error in processSystemNote(). Exception message: " + Environment.NewLine + e.Message);
            }
        }


        public static async Task getValidationScoreMatrix(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            try
            {

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
                dynamic automatedValSettings = getAutomatedValidationConfig();

                dynamic automatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);











                /***/


                dynamic dispositionData = dispositionResponse.returnStatus.data;


                string dispositionDataText = JsonConvert.SerializeObject(dispositionData);
                caseEntity["ts_validationdispositiondata"] = dispositionDataText;



                /***/
                string dispositionTransactionId = dispositionData.transaction_id;
                if (validationReqTransactionId != dispositionTransactionId)
                    return;











                /**Disposition Details**/

                var scoreMatrixObj = JsonConvert.DeserializeObject<System.Dynamic.ExpandoObject>(dispositionResponseText) as IDictionary<string, Object>;
                var dispositionScores = (IDictionary<string, Object>)((IDictionary<string, Object>)scoreMatrixObj["returnStatus"])["data"];









                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;

                

                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                   Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                   caseEntity.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
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

                        processSystemNote("No Disposition Received",  "There was no validation resolution after the conclusion of the time allotted for this process", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
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














                /**AI Dispositions**/


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











                /**IRS**/

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

                bool isOnIRSRevokeList = IRSRevocationCode == null || IRSRevocationCode.ToLower() == "na" ? false : true;

                caseEntity["ts_validationdispositiononirsrevokelist"] = isOnIRSRevokeList;










                /**Org Rules**/


                bool isOrgValid = false;
                if (isIRSPresent && orgNamesMatch && isSubSection03 && legalIdentifiersMatch && !isOnIRSRevokeList)
                    isOrgValid = true;

                caseEntity["ts_validationdispositionrulesorgvalid"] = isOrgValid;


                caseEntity["ts_validationorgdisposition"] = orgDisposition.ToLower() == "is" ? true : false;
                caseEntity["ts_orgvalidationdispositionscore"] = orgDispScore.ToString();











                /**Agent**/

                string agentName = caseEntity.GetAttributeValue<string>("ts_validationagentname") ?? "";

                float levenshteinDistOrgAgent = Fastenshtein.Levenshtein.Distance(orgName.ToLower(), agentName.ToLower());
                float topLengthOrgAgent = Math.Max(orgName.Length, agentName.Length);
                float levenshteinOrgAgentIndex = (topLengthOrgAgent - levenshteinDistOrgAgent) / topLengthOrgAgent;

                bool isAgentNameValid = string.IsNullOrWhiteSpace(agentName) ? false : (levenshteinOrgAgentIndex < 0.60 ? true : false);


                List<dynamic> ctpOrgDataList = ((JArray)dispositionData.ctp_db_match_set).ToList<dynamic>();


                dynamic orgData = null;

                if (ctpOrgDataList.Count > 0)
                    orgData = ctpOrgDataList.First();


                string orgWebsite = orgData == null ? "" : (orgData.org_website == null ? "" : orgData.org_website);

                caseEntity["ts_validationdispositionorgwebsite"] = orgWebsite;





                string validationRequestWebsite = caseEntity.GetAttributeValue<string>("ts_validationrequestwebsite") ?? "";

                string validationRequestAgentEmail = caseEntity.GetAttributeValue<string>("ts_validationrequestagentemail") ?? "";

                string valReqAgentEmailDomain = ValidationServicesHelper.regexMatchValue("(?<=@)(.+)", validationRequestAgentEmail, 0);

                bool agentOrgCommonDomain = string.IsNullOrWhiteSpace(valReqAgentEmailDomain) ? false : ValidationServicesHelper.regexMatch(valReqAgentEmailDomain, validationRequestWebsite);



                #region Agent Diposition & Validation Rules
                //validationRequestCase["ts_validationdispositionrulesagentvalid"] = agentDisposition.ToLower() == "is" ? true : false;
                bool dispositionAgentValid = agentDisposition.ToLower() == "is" ? true : false;

                caseEntity["ts_validationdispositionrulesagentvalid"] = false;
                caseEntity["ts_validationrequestagentverification"] = new OptionSetValue(0);

                if (dispositionAgentValid && isAgentNameValid && agentOrgCommonDomain)
                {
                    caseEntity["ts_validationdispositionrulesagentvalid"] = true;
                    caseEntity["ts_validationrequestagentverification"] = new OptionSetValue(1);
                }

                caseEntity["ts_validationagentdisposition"] = agentDisposition.ToLower() == "is" ? true : false;
                caseEntity["ts_agentvalidationdispositionscore"] = agentDispScore.ToString();
                #endregion






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

                processSystemNote(noteTitle, noteDesc, new EntityReference(caseEntity.LogicalName, caseEntity.Id));

                Entity validationRequestCase =  getCaseForTransactionId(validationReqTransactionId);

                processSystemNote(noteTitle, noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getValidationScoreMatrix(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                    );
            }

        }


        public static string regexReplace(string pattern, string expresion, string replaceWith)
        {
            string convExpresion = expresion;

            Regex regexObj = new Regex(pattern);
            convExpresion = regexObj.Replace(convExpresion, replaceWith);

            return convExpresion;
        }


        public static string regexMatchValue(string pattern, string input, int startAt)
        {
            Regex regexObj = new Regex(@pattern);

            Match match = regexObj.Match(input, startAt);

            return match.Value;
        }

        public static bool regexMatch(string pattern, string input)
        {
            Regex regex = new Regex(@pattern);
            return regex.IsMatch(input);
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
                    DynamicsInterface.writeToLog("Error in getAutomatedValidationConfig(...). ts_fieldname = 'AutomatedValidation' was not found in ts_fieldhierarchyandmapping");
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


        public static async Task<IDictionary<string, Object>> getValidationRequestOrgAccountInfo(string validationReqTransactionId)
        {
            try
            {


                string fetchExpressionQuery2 = @"
                        <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""5000"" no-lock=""false"">

	                        <entity name=""incident"">
		                        <attribute name=""ts_validationrequesttransactionid""/>
		                        <attribute name=""ts_casestatus""/>
                        <filter type=""and"">
			                        <condition attribute=""ts_validationrequesttransactionid"" operator=""eq"" value=""" + validationReqTransactionId + @""" />
		                        </filter>
		                        <link-entity alias=""qualcase"" name=""incident"" to=""parentcaseid"" from=""incidentid"" link-type=""outer"">
			                        <attribute name=""ts_validationrequesttransactionid""/>
			                        
			                        <link-entity name=""account"" alias=""aa"" to=""customerid""  from=""accountid"" link-type=""outer"" >
				                        <attribute name=""accountnumber""/>
                                    <attribute name=""ts_ctporgid""/>
                                        <attribute name=""accountid""/>
			                        </link-entity>
		                        </link-entity>
		                        
	                        </entity>
                        </fetch>
                        ";
                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery2);

                string newfetchxml = fetchXmlDoc.ToString();


                EntityCollection accountCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(new FetchExpression(newfetchxml));
                if (accountCollection.Entities.Count == 0)
                    return null;
                Entity validReq = accountCollection.Entities.First();


                


                Guid? accountId = (Guid?)validReq.GetAttributeValue<AliasedValue>("aa.accountid")?.Value;
                string ctpOrgId = (string)validReq.GetAttributeValue<AliasedValue>("aa.ts_ctporgid")?.Value;
                string tsOrgId = (string)validReq.GetAttributeValue<AliasedValue>("aa.accountnumber")?.Value;

                IDictionary<string, Object> valReqOrgAccountObj = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;


                valReqOrgAccountObj["valReqTranId"] = validReq.GetAttributeValue<string>("ts_validationrequesttransactionid");
                valReqOrgAccountObj["ctpOrgId"] = ctpOrgId;
                valReqOrgAccountObj["tsOrgId"] = tsOrgId;

                return valReqOrgAccountObj;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getValidationRequestOrgAccountInfo(). Exception message: " + Environment.NewLine + e.Message);
                return null;
            }
        }



        public static string getParentCaseAccountCtpOrgId(Guid caseId)
        {
            try
            {
                string caseIdText = caseId.ToString().Replace("{", "").Replace("}", "");

                string fetchExpressionQuery2 = @"
                        <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""5000"" no-lock=""false"">

	                        <entity name=""incident"">
		                        <attribute name=""ts_validationrequesttransactionid""/>
		                        <attribute name=""ts_casestatus""/>
                        <filter type=""and"">
			                        <condition attribute=""incidentid"" operator=""eq"" value=""" + caseIdText + @""" />
		                        </filter>
		                        <link-entity alias=""qualcase"" name=""incident"" to=""parentcaseid"" from=""incidentid"" link-type=""inner"">
			                        <attribute name=""ts_validationrequesttransactionid""/>
			                        
			                        <link-entity name=""account"" alias=""aa"" to=""customerid""  from=""accountid"" link-type=""inner"" >
				                        <attribute name=""accountnumber""/>
                                    <attribute name=""ts_ctporgid""/>
                                        <attribute name=""accountid""/>
			                        </link-entity>
		                        </link-entity>
		                        
	                        </entity>
                        </fetch>
                        ";


                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery2);

                string newfetchxml = fetchXmlDoc.ToString();


                EntityCollection accountCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(new FetchExpression(newfetchxml));

                if (accountCollection.Entities.Count == 0)
                    return null;

                Entity validReq = accountCollection.Entities.First();

                Guid? accountId = (Guid)validReq.GetAttributeValue<AliasedValue>("aa.accountid")?.Value;

                string ctpOrgId = (string)validReq.GetAttributeValue<AliasedValue>("aa.ts_ctporgid")?.Value;

                return ctpOrgId;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getParentCaseAccountCtpOrgId(). Exception message: " + Environment.NewLine + e.Message);
                return null;
            }
        }

        public static Entity processQualCaseFromValidationRequest(Entity account, Entity validationRequestCase)
        {
            //Do update for qual case; mupdate qual code
            Entity qualCase = null;
            try
            {
                #region Check If Qual Case Already Exists
                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;

                if (orgDesigId == Guid.Empty)
                    return null;

                bool caseExists = false;
                Guid caseId = Guid.Empty;

                EntityReference vallReqParentCaseRef =   validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid");
                caseId = vallReqParentCaseRef == null ? Guid.Empty : vallReqParentCaseRef.Id;

                if (caseId != Guid.Empty)
                {
                    caseExists = true;
                }
                {
                    QueryExpression queryQualCase = new QueryExpression("incident");
                    queryQualCase.ColumnSet = new ColumnSet(true);
                    queryQualCase.Criteria.AddCondition("casetypecode", ConditionOperator.Equal, 2);
                    queryQualCase.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, orgDesigId);
                    queryQualCase.Criteria.AddCondition("customerid", ConditionOperator.Equal, account.Id);
                    queryQualCase.AddOrder("createdon", OrderType.Descending);
                    queryQualCase.TopCount = 1;
                    EntityCollection qualCaseCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQualCase);


                    if (qualCaseCollection.Entities.Count > 0)
                    {
                        caseExists = true;
                        qualCase = qualCaseCollection.Entities.First();
                        caseId = qualCase.Id;
                    }
                    else
                    {
                        qualCase = new Entity("incident");
                    }
                }
                #endregion

                #region Transfer Values From Validation Request To Qual Case
                EntityReference validationRequest = validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid");

                Entity activityCodeEntity = account.GetAttributeValue<EntityReference>("new_activitycode") == null ? null
                                                                                                    : DynamicsInterface.DataverseClient.Retrieve("new_activitycodes", account.GetAttributeValue<EntityReference>("new_activitycode").Id, new ColumnSet(true));

                Entity qualCodeEntity = account.GetAttributeValue<EntityReference>("new_orgdesignation") == null ? null
                                                                                                    : DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", account.GetAttributeValue<EntityReference>("new_orgdesignation").Id, new ColumnSet(true));

                string orgActivityCode = activityCodeEntity?.GetAttributeValue<string>("new_activitycode");
                string qualCode = qualCodeEntity?.GetAttributeValue<string>("new_qualcode");

                string qualTerm = qualCodeEntity.FormattedValues["new_qualterm"];
                string qualCategory = qualCodeEntity.GetAttributeValue<string>("new_qualcategory");
                string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");


                EntityReference qualCodeRef = new EntityReference(qualCodeEntity.LogicalName, qualCodeEntity.Id);


                CaseDefinition caseLogicalName = CaseDefinition.CreateInstance();

                string title = qualCode + " - " + qualName;
                qualCase[caseLogicalName.CaseTitle] = title;
                qualCase[caseLogicalName.QualificationCodeId] = qualCodeRef;

                if (!caseExists)
                {
                    int caseTypeCode = 2;
                    int type = 101996;
                    EntityReference customerRef = new EntityReference(account.LogicalName, account.Id);

                    qualCase[caseLogicalName.CaseCategory] = new OptionSetValue(caseTypeCode);
                    qualCase[caseLogicalName.Type] = new OptionSetValue(type);
                    qualCase[caseLogicalName.Customer] = customerRef;
                }

                qualCase[caseLogicalName.CaseStatus] = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_casestatus") ?? new OptionSetValue(102050); //102050 - 'OQ - Not Started'

                qualCase[caseLogicalName.ValidationRequestTransactionId] = "qualcase:" + validationRequestCase.GetAttributeValue<string>("ts_validationrequesttransactionid");


                var caseNameLogicalMap = CaseDefinition.CreateDictionary();

                List<string> attributeNames = caseNameLogicalMap.Where(caseItem => caseItem.Value.Contains("validation")
                                                                                    && caseItem.Value != "ts_validationrequesttransactionid"
                                                                        )
                                                                      .Select(caseItem => caseItem.Value)
                                                                      .ToList();

                foreach (string fieldName in attributeNames)
                {
                    object fieldValue = validationRequestCase.GetAttributeValue<object>(fieldName);


                    switch (fieldValue)
                    {
                        case EntityReference entityRef:
                            qualCase[fieldName] = entityRef;
                            break;

                        case OptionSetValue optionSet:
                            qualCase[fieldName] = optionSet;
                            break;

                        case Money money:
                            qualCase[fieldName] = money;
                            break;

                        case DateTime dateTime:
                            qualCase[fieldName] = dateTime;
                            break;

                        case int intValue:
                            qualCase[fieldName] = intValue;
                            break;

                        case decimal decimalValue:
                            qualCase[fieldName] = decimalValue;
                            break;

                        case double doubleValue:
                            qualCase[fieldName] = doubleValue;
                            break;

                        case bool boolValue:
                            qualCase[fieldName] = boolValue;
                            break;

                        case Guid guidValue:
                            qualCase[fieldName] = guidValue;
                            break;

                        case string stringValue:
                            qualCase[fieldName] = stringValue;
                            break;

                        default:
                            //caseEntity[fieldName] = TryConvertValue(fieldName, fieldValue);
                            break;
                    }

                }
                #endregion

                #region Create/Update
                if (caseExists)
                {
                    DynamicsInterface.DataverseClient.Update(qualCase);
                }
                else
                {
                    caseId = DynamicsInterface.DataverseClient.Create(qualCase);
                }

                qualCase = DynamicsInterface.DataverseClient.Retrieve(qualCase.LogicalName, caseId, new ColumnSet(true));

                return qualCase;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processQualCaseFromValidationRequest(...). Exception message: " + Environment.NewLine + e.Message
                                                   + Environment.NewLine + "Validation Request Transaction Id: " + validationRequestCase.GetAttributeValue<string>("ts_validationrequesttransactionid")
                                                   );
                return null;
            }
            #endregion

        }



        public static Entity connectAgentToAccount(Guid accountId, Guid contactId, OptionSetValue agentVerificationStatusOption, string validationReqTransactionId)
        {
            try
            {
                string connectionRoleToName = "Agent";
                string connectionRoleFromName = "Organization";

                Entity connectionEntity = null;

                #region Check If Connection Already Exists
                QueryExpression queryConnection = new QueryExpression("connection");
                queryConnection.ColumnSet = new ColumnSet(true);
                queryConnection.Criteria.AddCondition("record1id", ConditionOperator.Equal, accountId);
                queryConnection.Criteria.AddCondition("record2id", ConditionOperator.Equal, contactId);
                EntityCollection connectionCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryConnection);

                if (connectionCollection.Entities.Count > 0)
                {
                    connectionEntity = connectionCollection.Entities.First();
                    int? agentVerificationStatusCurrent = connectionEntity.GetAttributeValue<OptionSetValue>("ts_agentverificationstatus")?.Value;

                    if (agentVerificationStatusCurrent == null || agentVerificationStatusCurrent != agentVerificationStatusOption.Value)
                    {
                        connectionEntity["ts_agentverificationstatus"] = agentVerificationStatusOption;
                        DynamicsInterface.DataverseClient.Update(connectionEntity);                        
                    }
                    /*Todo: handle the other scenarios where the connection already exists but the agent verification status is not the same as the one provided*/
                    return connectionEntity;
                }
                #endregion


                connectionEntity = new Entity("connection");


                #region Connection Roles
                QueryExpression queryConnectionRole = new QueryExpression("connectionrole");
                queryConnectionRole.Criteria.AddCondition("name", ConditionOperator.Equal, connectionRoleToName);
                EntityCollection connectionRoleCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryConnectionRole);

                if (connectionRoleCollection.Entities.Count == 0)
                    return null;

                Guid connectionRoleToId = connectionRoleCollection.Entities.First().Id;


                QueryExpression queryConnectionFromRole = new QueryExpression("connectionrole");
                queryConnectionFromRole.Criteria.AddCondition("name", ConditionOperator.Equal, connectionRoleFromName);
                EntityCollection connectionRoleFromCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryConnectionFromRole);

                if (connectionRoleFromCollection.Entities.Count == 0)
                    return null;

                Guid connectionRoleFromId = connectionRoleFromCollection.Entities.First().Id;
                #endregion

                #region Connection Attributes & Create
                connectionEntity["record1id"] = new EntityReference("account", accountId);
                connectionEntity["record1objecttypecode"] = new OptionSetValue(1);

                connectionEntity["record2id"] = new EntityReference("contact", contactId);
                connectionEntity["record2objecttypecode"] = new OptionSetValue(2);

                connectionEntity["record1roleid"] = new EntityReference("connectionrole", connectionRoleFromId);
                connectionEntity["record2roleid"] = new EntityReference("connectionrole", connectionRoleToId);


                connectionEntity["ts_agentverificationstatus"] = agentVerificationStatusOption;

                Guid connectionId = DynamicsInterface.DataverseClient.Create(connectionEntity);

                connectionEntity = DynamicsInterface.DataverseClient.Retrieve("connection", connectionId, new ColumnSet(true));
                return connectionEntity;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in connectAgentToAccount(...). Exception message: " + Environment.NewLine + e.Message
                                                   + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                   );
                return null;
            }
            #endregion
        }








        public static IDictionary<string, Object> getAgentContact(Entity validationRequestCase, Entity orgAccount, string validationReqTransactionId, IDictionary<string, Object> dispositionRequest)
        {
            try
            {
                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationagentemail");
                IDictionary<string, Object> agentContact = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                
                #region Find If Exists
                QueryExpression queryContact = new QueryExpression("contact");
                queryContact.ColumnSet = new ColumnSet(true);
                queryContact.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, agentEmail);
                EntityCollection contactCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryContact);

                if (contactCollection.Entities.Count == 0)
                    return agentContact;


                agentContact.Add("contactEntity", contactCollection.Entities.First());
                Guid contactId = ((Entity)agentContact["contactEntity"]).Id;
                #endregion

                #region Find If It's An AgentContact For Org
                QueryExpression queryConnection = new QueryExpression("connection");
                queryConnection.ColumnSet = new ColumnSet(true);
                queryConnection.Criteria.AddCondition("record1id", ConditionOperator.Equal, orgAccount.Id);
                queryConnection.Criteria.AddCondition("record2id", ConditionOperator.Equal, contactId);
                EntityCollection connectionCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryConnection);

                if (connectionCollection.Entities.Count == 0)
                    return agentContact;

                agentContact.Add("connectionEntity", connectionCollection.Entities.First());

                int? agentVerifcationStatus = ((Entity)agentContact["connectionEntity"]).GetAttributeValue<OptionSetValue>("ts_agentverificationstatus")?.Value;
                string agentVerifcationStatusText = agentVerifcationStatus == null ? "" : ((Entity)agentContact["connectionEntity"]).FormattedValues["ts_agentverificationstatus"];

                agentContact["agentVerifcationStatus"] = agentVerifcationStatus;
                agentContact["agentVerifcationStatusText"] = agentVerifcationStatusText;

                return agentContact;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getAgentContact(...). Exception message: " + Environment.NewLine + e.Message
                                                   + Environment.NewLine + "Validation Request Transaction Id: " + validationRequestCase.GetAttributeValue<string>("ts_validationrequesttransactionid")
                                                   );
                return null;
            }
            #endregion
        }


        public static Entity createAgentContact(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName)
        {
            try
            {
                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationagentemail");

                #region FindIfExists
                QueryExpression queryContact = new QueryExpression("contact");
                queryContact.ColumnSet = new ColumnSet(true);
                queryContact.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, agentEmail);
                EntityCollection contactCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryContact);

                if (contactCollection.Entities.Count > 0)
                    return contactCollection.Entities.First();
                #endregion

                #region CreateContact
                Entity contact = new Entity("contact");
                contact["firstname"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentfirstname");
                contact["lastname"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentlastname");

                contact["emailaddress1"] = agentEmail;
                contact["ts_emailvalidationstatus"] = new OptionSetValue(4);

                string tsContactId = DynamicsProcessesHelper.getNewTSOrgContactId();
                contact["new_contactaccountnumber"] = tsContactId;
                //contact["adx_identity_username"] = "agent." + tsContactId;

                contact["new_source"] = new OptionSetValue(105000); //105000 - ValidationRequest

                Guid contactId = DynamicsInterface.DataverseClient.Create(contact);

                contact = DynamicsInterface.DataverseClient.Retrieve(contact.LogicalName, contactId, new ColumnSet(true));
                

                return contact;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in createAgentContact(...). Exception message: " + Environment.NewLine + e.Message
                                                   + Environment.NewLine + "Validation Request Transaction Id: " + validationRequestCase.GetAttributeValue<string>("ts_validationrequesttransactionid")
                                                   );
                return null;
            }
            #endregion
        }
        public static Entity createOrgFromCase(Entity requestingAccount, Entity validationRequestCase, string validationReqTransactionId)
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
                string orgRefId = DynamicsProcessesHelper.regexMatchValue(@"(?<=\w+_)(\d+)(?=_\w+$)", validationReqTransactionId, 0);


                string tsPngoCode = requestingAccount?.GetAttributeValue<string>("ts_tspngocode");

                string countryCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid") == null ? ""
                                                                                    : validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid").ToLower();

                if (countryCode != "us" && !string.IsNullOrEmpty(tsPngoCode) && !string.IsNullOrEmpty(orgRefId))
                {
                    account["ts_pporgid"] = orgRefId;
                    account["ts_orgppid"] = new EntityReference("account", requestingAccount.Id);
                }
                #endregion


                #region Name, Org Designation, Mission Statement...
                account["name"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalname");
                account["customertypecode"] = new OptionSetValue(3); //Customer
                account["new_source"] = new OptionSetValue(101892); //TSS Web Site 101892

                EntityReference qualCodeRef = validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid");
                account["new_orgdesignation"] = qualCodeRef;

                account["ts_missionstatement"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestmissionstatement");

                account["telephone1"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestphone");
                #endregion


                #region Address
                account["address1_country"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid");

                account["address1_stateorprovince"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressstateregion");

                account["address1_line1"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressline1");


                account["address1_line2"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressother") == "nil" ? null : validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressother");

                account["address1_city"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscity");
                account["address1_postalcode"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresspostalcode");


                #region Country And State Hierarchy Mapping
                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid"));
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
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid"));
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressstateregion"));
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
                account["emailaddress1"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestemail");

                account["websiteurl"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite");

                account["new_budget"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestoperatingbudget");




                account["new_legalidentifier"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalidentifier");

                EntityReference validationDispositionActivityCodeRef = validationRequestCase.GetAttributeValue<EntityReference>("ts_validationdispositionactivitycode");

                if (validationDispositionActivityCodeRef != null)
                {
                    account["new_activitycode"] = validationDispositionActivityCodeRef;
                }
                else
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, validationRequestCase.GetAttributeValue<string>("ts_validationrequestactivitycode"));
                    EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                    if (entityCollection.Entities.Count > 0)
                    {
                        Entity activityCodeEntity = entityCollection.Entities.First();
                        account["new_activitycode"] = new EntityReference(activityCodeEntity.LogicalName, activityCodeEntity.Id);

                    }
                }
                Guid activityCodeId = account.GetAttributeValue<EntityReference>("new_activitycode") == null ? Guid.Empty
                                                                                                    : account.GetAttributeValue<EntityReference>("new_activitycode").Id;
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



            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in createOrgFromCase(string validationReqTransactionId). Exception message: " + Environment.NewLine + e.Message
                                                  + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                  );


                DynamicsProcessesHelper.processSystemNote(" --- Error Creating Org --- ", "Error in createOrgFromCase(string validationReqTransactionId). Exception message: " + Environment.NewLine + e.Message
                                                                , new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


            }
            #endregion

            return newAccount;
        }
        public static Entity createOrgFromCase_backup(Entity requestingAccount, Entity caseEntity, string validationReqTransactionId, IDictionary<string, Object> dispositionRequest)
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
                string orgRefId = DynamicsProcessesHelper.regexMatchValue(@"(?<=\w+_)(\d+)(?=_\w+$)", validationReqTransactionId, 0);


                string tsPngoCode = requestingAccount?.GetAttributeValue<string>("ts_tspngocode");

                string countryCode = caseEntity.GetAttributeValue<string>("ts_validationrequestaddresscountryid") == null ? ""
                                                                                    : caseEntity.GetAttributeValue<string>("ts_validationrequestaddresscountryid").ToLower();

                if (countryCode != "us" && !string.IsNullOrEmpty(tsPngoCode) && !string.IsNullOrEmpty(orgRefId))
                {
                    account["ts_pporgid"] = orgRefId;
                    account["ts_orgppid"] = new EntityReference("account", requestingAccount.Id);
                }
                #endregion


                #region Name, Org Designation, Mission Statement...
                account["name"] = dispositionRequest[nameof(DispositionClass.LegalName)];
                account["customertypecode"] = new OptionSetValue(3); //Customer
                account["new_source"] = new OptionSetValue(101892); //TSS Web Site 101892

                EntityReference qualCodeRef = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid");
                account["new_orgdesignation"] = qualCodeRef;

                if (
                   !hasNullEmptyValues(dispositionRequest[nameof(DispositionClass.MissionStatement)].ToString())
                   )
                    account["ts_missionstatement"] = dispositionRequest[nameof(DispositionClass.MissionStatement)].ToString();

                if (
                     !hasNullEmptyValues(dispositionRequest[nameof(DispositionClass.Phone)].ToString())
                     )
                    account["telephone1"] = dispositionRequest[nameof(DispositionClass.Phone)];
                #endregion


                #region Address
                account["address1_country"] = dispositionRequest[nameof(DispositionClass.AddressCountryId)];

                account["address1_stateorprovince"] = dispositionRequest[nameof(DispositionClass.AddressStateRegion)];

                account["address1_line1"] = dispositionRequest[nameof(DispositionClass.AddressLine1)];

                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(DispositionClass.AddressOther)].ToString()
                                        )
                       )
                    account["address1_line2"] = dispositionRequest[nameof(DispositionClass.AddressOther)];

                account["address1_city"] = dispositionRequest[nameof(DispositionClass.AddressCity)];
                account["address1_postalcode"] = dispositionRequest[nameof(DispositionClass.AddressPostalCode)];


                #region Country And State Hierarchy Mapping
                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, dispositionRequest[nameof(DispositionClass.AddressCountryId)]);
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
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, dispositionRequest[nameof(DispositionClass.AddressCountryId)]);
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, dispositionRequest[nameof(DispositionClass.AddressStateRegion)]);
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
                     !hasNullEmptyValues(dispositionRequest[nameof(DispositionClass.Email)].ToString())
                     )
                    account["emailaddress1"] = dispositionRequest[nameof(DispositionClass.Email)];                

                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(DispositionClass.Website)].ToString()
                                        )
                    )
                    account["websiteurl"] = dispositionRequest[nameof(DispositionClass.Website)].ToString();

                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(DispositionClass.OperatingBudget)].ToString()
                                        )
                    )
                    account["new_budget"] = dispositionRequest[nameof(DispositionClass.OperatingBudget)].ToString();

                

                
                List<IDictionary<string, Object>> registrationIdentifiers = JsonConvert.DeserializeObject<List<ExpandoObject>>(
                                                                                                                                 JsonConvert.SerializeObject(dispositionRequest["RegistrationIdentifiers"])
                                                                                                                                 ).ToList<IDictionary<string, Object>>();



                if (registrationIdentifiers != null && registrationIdentifiers.Count() > 0
                    && !hasNullEmptyValues(registrationIdentifiers.First()[nameof(RegIdentifierClass.LegalIdentifier)].ToString())
                    )
                {

                    account["new_legalidentifier"] = registrationIdentifiers.First()[nameof(RegIdentifierClass.LegalIdentifier)].ToString();
                }
                else
                {
                    //"error"
                }    


                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(DispositionClass.ActivityCode)].ToString()
                                        )
                    )
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, dispositionRequest[nameof(DispositionClass.ActivityCode)].ToString());
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

        public static Entity updateOrgFromValidationRequestCase(Entity requestingAccount, Entity caseEntity, string validationReqTransactionId, IDictionary<string, Object> dispositionRequest)
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
                string orgRefId = DynamicsProcessesHelper.regexMatchValue(@"(?<=\w+_)(\w+)(?=_\w+)", validationReqTransactionId, 0);

                account["ts_pporgid"] = orgRefId;
                account["ts_orgppid"] = new EntityReference("account", requestingAccount.Id);
                #endregion


                #region Name, Org Designation, Mission Statement...
                account["name"] = dispositionRequest[nameof(DispositionClass.LegalName)];
                account["customertypecode"] = new OptionSetValue(3); //Customer
                account["new_source"] = new OptionSetValue(101892); //TSS Web Site 101892

                EntityReference qualCodeRef = caseEntity.GetAttributeValue<EntityReference>("ts_qualificationcodeid");
                account["new_orgdesignation"] = qualCodeRef;

                if (
                   !hasNullEmptyValues(dispositionRequest[nameof(DispositionClass.MissionStatement)].ToString())
                   )
                    account["ts_missionstatement"] = dispositionRequest[nameof(DispositionClass.MissionStatement)].ToString();

                if (
                     !hasNullEmptyValues(dispositionRequest[nameof(DispositionClass.Phone)].ToString())
                     )
                    account["telephone1"] = dispositionRequest[nameof(DispositionClass.Phone)];
                #endregion


                #region Address
                account["address1_country"] = dispositionRequest[nameof(DispositionClass.AddressCountryId)];

                account["address1_stateorprovince"] = dispositionRequest[nameof(DispositionClass.AddressStateRegion)];

                account["address1_line1"] = dispositionRequest[nameof(DispositionClass.AddressLine1)];

                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(DispositionClass.AddressOther)].ToString()
                                        )
                       )
                    account["address1_line2"] = dispositionRequest[nameof(DispositionClass.AddressOther)];

                account["address1_city"] = dispositionRequest[nameof(DispositionClass.AddressCity)];
                account["address1_postalcode"] = dispositionRequest[nameof(DispositionClass.AddressPostalCode)];


                #region Country And State Hierarchy Mapping
                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, dispositionRequest[nameof(DispositionClass.AddressCountryId)]);
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
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, dispositionRequest[nameof(DispositionClass.AddressCountryId)]);
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, dispositionRequest[nameof(DispositionClass.AddressStateRegion)]);
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
                     !hasNullEmptyValues(dispositionRequest[nameof(DispositionClass.Email)].ToString())
                     )
                    account["emailaddress1"] = dispositionRequest[nameof(DispositionClass.Email)];

                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(DispositionClass.Website)].ToString()
                                        )
                    )
                    account["websiteurl"] = dispositionRequest[nameof(DispositionClass.Website)].ToString();

                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(DispositionClass.OperatingBudget)].ToString()
                                        )
                    )
                    account["new_budget"] = dispositionRequest[nameof(DispositionClass.OperatingBudget)].ToString();




                List<IDictionary<string, Object>> registrationIdentifiers = JsonConvert.DeserializeObject<List<ExpandoObject>>(
                                                                                                                                 JsonConvert.SerializeObject(dispositionRequest["RegistrationIdentifiers"])
                                                                                                                                 ).ToList<IDictionary<string, Object>>();



                if (registrationIdentifiers != null && registrationIdentifiers.Count() > 0
                    && !hasNullEmptyValues(registrationIdentifiers.First()[nameof(RegIdentifierClass.LegalIdentifier)].ToString())
                    )
                {

                    account["new_legalidentifier"] = registrationIdentifiers.First()[nameof(RegIdentifierClass.LegalIdentifier)].ToString();
                }
                else
                {
                    //"error"
                }


                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(DispositionClass.ActivityCode)].ToString()
                                        )
                    )
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, dispositionRequest[nameof(DispositionClass.ActivityCode)].ToString());
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
        public static Entity getAccountEntityFromValidationRequestCase(Entity requestingAccount, Entity validationRequestCase, string validationReqTransactionId, IDictionary<string, Object> dispositionRequest)
        {
            DispositionRequest dispositionClass = new DispositionRequest();
            RegistrationIdentifier regIdentifierClass = new RegistrationIdentifier();


            try
            {

                Entity account = new Entity("account");


                #region PngoId & OrgRefId
                string orgRefId = validationReqTransactionId;

                if (
                      dispositionRequest.ContainsKey(nameof(DispositionClass.OrgRefId)) && !hasNullEmptyValues(dispositionRequest[nameof(DispositionClass.OrgRefId)].ToString())
                      )
                    orgRefId = dispositionRequest[nameof(DispositionClass.OrgRefId)].ToString();


                account["ts_pporgid"] = orgRefId;
                account["ts_orgppid"] = new EntityReference("account", requestingAccount.Id);
                #endregion

                #region Name, Org Designation, Mission Statement...

                account["name"] = dispositionRequest[nameof(dispositionClass.LegalName)];
                account["customertypecode"] = new OptionSetValue(3); //Customer
                account["new_source"] = new OptionSetValue(101892); //TSS Web Site 101892

                EntityReference qualCodeRef = validationRequestCase.GetAttributeValue<EntityReference>("ts_qualificationcodeid");
                account["new_orgdesignation"] = qualCodeRef;



                if (
                     !hasNullEmptyValues(dispositionRequest[nameof(dispositionClass.Phone)].ToString())
                     )
                    account["telephone1"] = dispositionRequest[nameof(dispositionClass.Phone)];

                if (
                  !hasNullEmptyValues(dispositionRequest[nameof(dispositionClass.MissionStatement)].ToString())
                  )
                    account["ts_missionstatement"] = dispositionRequest[nameof(dispositionClass.MissionStatement)].ToString();
                #endregion


                #region Address
                account["address1_country"] = dispositionRequest[nameof(dispositionClass.AddressCountryId)];
                account["address1_stateorprovince"] = dispositionRequest[nameof(dispositionClass.AddressStateRegion)];
                account["address1_line1"] = dispositionRequest[nameof(dispositionClass.AddressLine1)];

                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(dispositionClass.AddressOther)].ToString()
                                        )
                       )
                    account["address1_line2"] = dispositionRequest[nameof(dispositionClass.AddressOther)];

                account["address1_city"] = dispositionRequest[nameof(dispositionClass.AddressCity)];
                account["address1_postalcode"] = dispositionRequest[nameof(dispositionClass.AddressPostalCode)];



                /************************/


                #region Country And State Hierarchy Mapping
                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, dispositionRequest[nameof(dispositionClass.AddressCountryId)]);
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
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, dispositionRequest[nameof(dispositionClass.AddressCountryId)]);
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, dispositionRequest[nameof(dispositionClass.AddressStateRegion)]);
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
                   !hasNullEmptyValues(dispositionRequest[nameof(dispositionClass.Email)].ToString())
                   )
                    account["emailaddress1"] = dispositionRequest[nameof(dispositionClass.Email)];


                if (!hasNullEmptyValues(
                                       dispositionRequest[nameof(dispositionClass.Website)].ToString()
                                       )
                   )
                    account["websiteurl"] = dispositionRequest[nameof(dispositionClass.Website)].ToString();



                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(dispositionClass.OperatingBudget)].ToString()
                                        )
                    )
                    account["new_budget"] = dispositionRequest[nameof(dispositionClass.OperatingBudget)].ToString();



                List<IDictionary<string, Object>> registrationIdentifiers = JsonConvert.DeserializeObject<List<ExpandoObject>>(
                                                                                                                                 JsonConvert.SerializeObject(dispositionRequest["RegistrationIdentifiers"])
                                                                                                                                 ).ToList<IDictionary<string, Object>>();



                if (registrationIdentifiers != null && registrationIdentifiers.Count() > 0
                    && !hasNullEmptyValues(registrationIdentifiers.First()[nameof(regIdentifierClass.LegalIdentifier)].ToString())
                    )
                {

                    account["new_legalidentifier"] = registrationIdentifiers.First()[nameof(regIdentifierClass.LegalIdentifier)].ToString();
                }
                else
                {
                    //"error"
                }


                if (!hasNullEmptyValues(
                                        dispositionRequest[nameof(dispositionClass.ActivityCode)].ToString()
                                        )
                    )
                {
                    QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                    queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, dispositionRequest[nameof(dispositionClass.ActivityCode)].ToString());
                    EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                    if (entityCollection.Entities.Count > 0)
                        account["new_activitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);
                }

                

                return account;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getAccountEntityFromValidationRequestCase(...). Exception message: " + Environment.NewLine + e.Message
                                                  + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                  );
                return null;

            }
            #endregion

        }

        public static bool? evaluateForFraud(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            bool potentialFraud = false;
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

                string fraudReviewQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.fraudReviewQueue;
                fraudReviewQueue = countryAutomatedValDefinition?.fraudReviewQueue ?? fraudReviewQueue;
                fraudReviewQueue = customerAutomatedValDefinition == null ? fraudReviewQueue : (customerAutomatedValDefinition?.fraudReviewQueue ?? validationServicesCustomers?.fraudReviewQueue) ?? fraudReviewQueue;
                #endregion

                #region Parameters
                //string postalCode = valReqOrgEntity.GetAttributeValue<string>("address1_postalcode");
                //string name = valReqOrgEntity.GetAttributeValue<string>("name");
                //string legalIdentifier = valReqOrgEntity.GetAttributeValue<string>("new_legalidentifier");
                string url = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite") ?? "";

                //string phone = valReqOrgEntity.GetAttributeValue<string>("telephone1");
                //string countryCode = valReqOrgEntity.GetAttributeValue<string>("address1_country");
                #endregion

                #region Criteria For Fraud
                if (url != null && url.ToLower().Contains("fraud"))
                {
                    DynamicsInterface.writeToLog("Fraud detected based on URL: " + url);



                    potentialFraud = true;

                    string dupesNoteDesc = "Initial check found strong indications of fraud";
                    processSystemNote("Potential Fraud Identified", dupesNoteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104602); //OQ - Fraud Review	104602
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);





                    if (!string.IsNullOrEmpty(fraudReviewQueue))
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, fraudReviewQueue);
                }
                
                return potentialFraud;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in evaluateForFraud(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
                return null;
            }
            #endregion

        }

        public static bool processValidationRequestAccountFound(AccountMatch accountMatch, Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            bool success = true;
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

                string duplicateReviewQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.duplicateReviewQueue;
                duplicateReviewQueue = countryAutomatedValDefinition?.duplicateReviewQueue ?? duplicateReviewQueue;
                duplicateReviewQueue = customerAutomatedValDefinition == null ? duplicateReviewQueue : (customerAutomatedValDefinition?.duplicateReviewQueue ?? validationServicesCustomers?.duplicateReviewQueue) ?? duplicateReviewQueue;

                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postNoAutoCloseQueue;
                postNoAutoCloseQueue = countryAutomatedValDefinition?.postNoAutoCloseQueue ?? postNoAutoCloseQueue;
                postNoAutoCloseQueue = customerAutomatedValDefinition == null ? postNoAutoCloseQueue : (customerAutomatedValDefinition?.postNoAutoCloseQueue ?? validationServicesCustomers?.postNoAutoCloseQueue) ?? postNoAutoCloseQueue;

                string postAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postAutoCloseQueue;
                postAutoCloseQueue = countryAutomatedValDefinition?.postAutoCloseQueue ?? postAutoCloseQueue;
                postAutoCloseQueue = customerAutomatedValDefinition == null ? postAutoCloseQueue : (customerAutomatedValDefinition?.postAutoCloseQueue ?? validationServicesCustomers?.postAutoCloseQueue) ?? postAutoCloseQueue;

                bool agentValidation = customerAutomatedValDefinition?.targetedValidations == null
                    || ((JArray)customerAutomatedValDefinition.targetedValidations).ToList<dynamic>().Where(item => ((string)item)?.ToLower() == "agent")?.FirstOrDefault() != null;
                #endregion

                Entity matchingAccount = DynamicsInterface.DataverseClient.Retrieve("account", accountMatch.AccountId, new ColumnSet(true));

                #region HandlingAccountFound

                Entity qualCase = DynamicsProcessesHelper.retrieveOrgQualCase(matchingAccount);

                if (qualCase == null)
                    return false;


                validationRequestCase["parentcaseid"] = new EntityReference(qualCase.LogicalName, qualCase.Id);
                DynamicsInterface.DataverseClient.Update(validationRequestCase);


                IDictionary<string, Object> AgentContact = getAgentContact(validationRequestCase, matchingAccount, validationReqTransactionId, dispositionRequest);

                string agentContactVerificationStatusText = AgentContact.ContainsKey("agentVerifcationStatusText") ? (string)AgentContact["agentVerifcationStatusText"] : "";

                string valReqAgentValidationStatusText = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_validationrequestagentverification") == null ? ""
                                                                                                                                : validationRequestCase.FormattedValues["ts_validationrequestagentverification"];

                #endregion

                #region Org & Agent Valid
                int? tsQualCaseStatus = qualCase.GetAttributeValue<OptionSetValue>("ts_casestatus")?.Value;

                if (
                        (
                        tsQualCaseStatus != null && tsQualCaseStatus == 102056 //102056 - OQ - Qualified
                        && (agentContactVerificationStatusText == "Verified" || !agentValidation)
                        )
                    )
                {
                    validationRequestCase["ts_casestatus"] = qualCase.GetAttributeValue<OptionSetValue>("ts_casestatus");
                    if (agentContactVerificationStatusText == "Verified")
                        validationRequestCase["ts_validationrequestagentverification"] = new OptionSetValue(1);
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);


                    #region PngoId & OrgRefId
                    string orgRefId = DynamicsProcessesHelper.regexMatchValue(@"(?<=\w+_)(\d+)(?=_\w+$)", validationReqTransactionId, 0);

                    string tsPngoCode = account.GetAttributeValue<string>("ts_tspngocode");

                    string countryCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid") == null ? ""
                                                                                        : validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid").ToLower();

                    if (countryCode != "us" && !string.IsNullOrEmpty(tsPngoCode) && !string.IsNullOrEmpty(orgRefId))
                    {
                        matchingAccount["ts_pporgid"] = orgRefId;
                        matchingAccount["ts_orgppid"] = new EntityReference("account", matchingAccount.Id);
                        DynamicsInterface.DataverseClient.Update(matchingAccount);
                    }
                    #endregion


                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseQueue);

                    return success;
                }
                #endregion

                #region Handle Agent Not Found - Found But Not With Org
                //"contactEntity"
                //AgentContact.ContainsKey("connectionEntity")
                string reason = "";

                if (tsQualCaseStatus == 102056 && agentContactVerificationStatusText != "Verified")
                    reason = "Agent needs to be verified";
                else if (tsQualCaseStatus != 102056 && agentContactVerificationStatusText != "Verified")
                    reason = "Org needs to be qualified and Agent needs to be verified";

                string dupesNoteDesc = "An existing org has been found for this Validation Request: " + Environment.NewLine + Environment.NewLine;
                dupesNoteDesc += "TSOrgId: " + matchingAccount.GetAttributeValue<string>("accountnumber") + Environment.NewLine + Environment.NewLine;
                dupesNoteDesc += reason;
                processSystemNote("An Org Match Has Been Found", dupesNoteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                DynamicsInterface.DataverseClient.Update(validationRequestCase);
                DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);
                #endregion

                return success;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processValidationRequestAccountFound(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
                return false;
            }

        }

        public static IDictionary<string, System.Object> findValidationRequestAccountMatches(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            IDictionary<string, System.Object> response = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;
            try
            {
                #region AutomatedValDefinition
                string duplicateReviewQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.duplicateReviewQueue;
                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb", "uk")
                                                                                                        )?.FirstOrDefault();

                string requestingClientName = account.GetAttributeValue<string>("name");
                dynamic customerAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.customers)?.ToList<dynamic>()?.
                                                                                                                                                                                Where(customer => ((string)customer.name)?.ToLower() == requestingClientName.ToLower()
                                                                                                                                                                                        )?.FirstOrDefault();
                dynamic validationServicesCustomers = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.validationServicesCustomers;

                duplicateReviewQueue = countryAutomatedValDefinition?.duplicateReviewQueue ?? duplicateReviewQueue;
                duplicateReviewQueue = customerAutomatedValDefinition == null ? duplicateReviewQueue : (customerAutomatedValDefinition?.duplicateReviewQueue ?? validationServicesCustomers?.duplicateReviewQueue) ?? duplicateReviewQueue;
                #endregion

                #region Call Account Match Service
                string legalId = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalidentifier");
                string name = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalname");
                string address1 = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressline1");
                string stateProvince =  validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressstateregion");
                string postalCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresspostalcode");
                string countryCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid");
                string website = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite");
                string phone = validationRequestCase.GetAttributeValue<string>("ts_validationrequestphone");

                IDictionary<string, System.Object> matchRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;
                matchRequest["legalId"] = legalId;
                matchRequest["name"] = name;
                matchRequest["address1"] = address1;
                matchRequest["stateProvince"] = stateProvince;
                matchRequest["postalCode"] = postalCode;
                matchRequest["countryCode"] = countryCode;
                matchRequest["website"] = website;
                matchRequest["phone"] = phone;

                string matchRequestText = JsonConvert.SerializeObject(matchRequest);

                AccountMatchService accountMatchService = new AccountMatchService(DynamicsInterface.DataverseClient
                                                                                    , DynamicsProcessesValidationServices.DynamicsEnvironments
                                                                                    , DynamicsProcessesValidationServices.EnvVariables
                                                                                    );

                AccountMatchResponse accountMatchResponse = accountMatchService.FindMatches(matchRequestText);

                List<AccountMatch> accountMatches = accountMatchResponse.Matches.Where(Match => Match.OverallScore >= 0.70 
                                                                                                    && DynamicsProcessesHelper.getOrgQualStatus(Match.AccountId) != "Canceled"
                                                                                                    && !Match.AccountName.StartsWith("[")
                                                                                        )?.ToList();

                if (accountMatches == null || accountMatches.Count == 0)
                    return response;
                #endregion

                #region Process Matches
                response["existsAccount"] = true;
                if (accountMatches.Count > 1)
                {

                    string[] matchingTsOrgIds = accountMatches.Select(match => match.TSOrgId).ToArray();

                    string matchingTsOrgIdsCsv = string.Join(", ", matchingTsOrgIds);


                    string dupesNoteDesc = "TSOrgIds of matching orgs: " + Environment.NewLine + Environment.NewLine;
                    dupesNoteDesc += matchingTsOrgIdsCsv + Environment.NewLine + Environment.NewLine;
                    dupesNoteDesc += "Routing case to Duplicate Review queue";
                    processSystemNote("Initial Duplicate Check - Org Matches Found", dupesNoteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104696); //OQ - AutoValidation - Duplicate Review
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);

                    if (!string.IsNullOrEmpty(duplicateReviewQueue))
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, duplicateReviewQueue);

                    response["validationProcessAction"] = "terminate";
                }
                else if (accountMatches.Count == 1)
                {
                    if (validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid") == null)
                    {
                        AccountMatch accountMatch = accountMatches.First();
                        bool success = processValidationRequestAccountFound(accountMatch, validationRequestCase, account, validationReqTransactionId, queueName, dispositionRequest);

                        if (!success)
                            response["validationProcessAction"] = "ValidationRequestStatusUpdate-RouteToQueue";
                        else
                            response["validationProcessAction"] = "terminate";
                    }
                }
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in findValidationRequestAccountMatches(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
                response["validationProcessAction"] = "ValidationRequestStatusUpdate-RouteToQueue";

            }
            #endregion
            return response;
        }
       
        public static Guid? getMailBoxQueueId(string queueName)
        {
            try
            {
                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
                EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                if (entityCollection.Entities.Count > 0)
                    return entityCollection.Entities.First().Id;

                dynamic defaultMailboxQueue = getDefaultMailboxQueueId();
                return defaultMailboxQueue.queueId;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getMailBoxQueueId(). Exception message: " + Environment.NewLine + e.Message
                                                    );
            }

            return Guid.Empty;
        }
        public static dynamic getDefaultMailboxQueueId()
        {
            dynamic defaultMailboxQueue = new JObject();
            try
            {
                string caseQueueName = string.Empty;

                QueryExpression queryDefaultMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryDefaultMapping.ColumnSet = new ColumnSet(true);
                queryDefaultMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "mailboxqueue");
                queryDefaultMapping.Criteria.AddCondition("ts_setting1", ConditionOperator.Equal, 2);
                EntityCollection defaultMappingCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryDefaultMapping);

                if (defaultMappingCollection.Entities.Count == 0)
                    return defaultMailboxQueue;

                Entity defaultMailboxMapping = defaultMappingCollection.Entities.First();
                caseQueueName = defaultMailboxMapping.GetAttributeValue<string>("ts_value");
                defaultMailboxQueue.queueName = caseQueueName;


                QueryExpression queryQueue = new QueryExpression("queue");
                queryQueue.Criteria.AddCondition("name", ConditionOperator.Equal, caseQueueName);
                EntityCollection queueCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQueue);

                if (queueCollection.Entities.Count == 0)
                    return defaultMailboxQueue;

                defaultMailboxQueue.queueId = queueCollection.Entities.First().Id;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getDefaultMailboxQueueId(). Exception message: " + Environment.NewLine + e.Message
                                                    );
            }
            return defaultMailboxQueue;
        }

        public static bool? findExistingAccount(Entity validationRequestCase, Entity account, string validationReqTransactionId, string queueName, Entity valReqOrgEntity, IDictionary<string, Object> dispositionRequest)
        {
            bool existsAccount = false;
            try
            {
                #region AutomatedValDefinition
                string duplicateReviewQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.duplicateReviewQueue;
                dynamic countryAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.countries)?.ToList<dynamic>()?.
                                                                                                        Where(country => ((string)country.country)?.ToLower() ==
                                                                                                                                        validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid")?.ToLower()?.Replace("gb","uk")
                                                                                                        )?.FirstOrDefault();


                duplicateReviewQueue = countryAutomatedValDefinition == null ? duplicateReviewQueue : countryAutomatedValDefinition.duplicateReviewQueue;
                #endregion

                #region Search Matches
                //Entity accountEntity = getAccountEntityFromValidationRequestCase(validationRequestCase, validationReqTransactionId, dispositionRequest);

                string name = valReqOrgEntity.GetAttributeValue<string>("name");
                string legalIdentifier = valReqOrgEntity.GetAttributeValue<string>("new_legalidentifier");
                string addressLine1 = valReqOrgEntity.GetAttributeValue<string>("address1_line1");
                string addressPostalCode = valReqOrgEntity.GetAttributeValue<string>("address1_postalcode");
                addressPostalCode = addressPostalCode.Length < 5 ? addressPostalCode.Substring(0, addressPostalCode.Length) : addressPostalCode.Substring(0, 5);
                string addressCountryCode = valReqOrgEntity.GetAttributeValue<string>("address1_country");

                Entity matchingAccount = DynamicsProcessesHelper.findMatchAccount(name, legalIdentifier, addressLine1, addressPostalCode, addressCountryCode, accountId: null);
                #endregion


                if (matchingAccount != null)
                {
                    #region HandlingAccountFound
                    existsAccount = true;
                    Entity qualCase = DynamicsProcessesHelper.retrieveOrgQualCase(matchingAccount);

                    if (qualCase == null)
                        return null;

                   
                    validationRequestCase["parentcaseid"] = new EntityReference(qualCase.LogicalName, qualCase.Id);
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);


                    IDictionary<string, Object> AgentContact = getAgentContact(validationRequestCase, matchingAccount, validationReqTransactionId, dispositionRequest);

                    string agentContactVerificationStatusText = AgentContact.ContainsKey("agentVerifcationStatusText") ? (string)AgentContact["agentVerifcationStatusText"] : "";
                                                                    
                    string valReqAgentValidationStatusText = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_validationrequestagentverification") == null ? "" 
                                                                                                                                    : validationRequestCase.FormattedValues["ts_validationrequestagentverification"];

                    #endregion

                    #region Org & Agent Valid
                    int? tsQualCaseStatus = qualCase.GetAttributeValue<OptionSetValue>("ts_casestatus")?.Value;

                    if (
                            (
                            tsQualCaseStatus != null && tsQualCaseStatus == 102056 //102056 - OQ - Qualified
                            && agentContactVerificationStatusText == "Verified"
                            )
                        )
                    {
                        validationRequestCase["ts_casestatus"] = qualCase.GetAttributeValue<OptionSetValue>("ts_casestatus");
                        DynamicsInterface.DataverseClient.Update(validationRequestCase);
                        string postAutoCloseQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices.postAutoCloseQueue;
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseQueue);

                        return existsAccount;
                    }
                    #endregion

                    #region Handle Agent Not Found - Found But Not With Org
                    

                    string dupesNoteDesc = "An existing org has been found for this Validation Request: " + Environment.NewLine + Environment.NewLine;
                    dupesNoteDesc += "TSOrgId: " + matchingAccount.GetAttributeValue<string>("accountnumber") + Environment.NewLine + Environment.NewLine;
                    dupesNoteDesc += "Routing case to Duplicate Review queue";
                    processSystemNote("An Org Match Has Been Found", dupesNoteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104696); //OQ - AutoValidation - Duplicate Review
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);

                    if (!string.IsNullOrEmpty(duplicateReviewQueue))
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, duplicateReviewQueue);

                    #endregion


                }


                return existsAccount;



               
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in findExistingAccount(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                );
                return null;
            }

        }

        public static bool updateValidationRequestCaseStatus(Entity validationRequestCase, int validationRequestCaseStatus, string validationReqTransactionId)
        {

            try
            {
                EntityReference parentCaseRef = validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid");

                if (parentCaseRef == null)
                {
                    DynamicsInterface.writeToLog("Validation Request Transaction Id: " + validationReqTransactionId + " does not have a parent case associated. Cannot update status");
                    return false;
                }

                validationRequestCase["ts_casestatus"] = new OptionSetValue(validationRequestCaseStatus); //102056 - 'OQ - Qualified'
                DynamicsInterface.DataverseClient.Update(validationRequestCase);


                Entity qualCase = DynamicsInterface.DataverseClient.Retrieve(parentCaseRef.LogicalName, parentCaseRef.Id, new ColumnSet("ts_casestatus"));

                qualCase["ts_casestatus"] = new OptionSetValue(validationRequestCaseStatus); //102056 - 'OQ - Qualified'
                DynamicsInterface.DataverseClient.Update(qualCase);

                //Entity validationRequestCase = ValidationServicesHelper.getCaseForTransactionId(validationReqTransactionId);
                //validationRequestCase["ts_statusopenclosed"] = new OptionSetValue(1);
                //DynamicsInterface.DataverseClient.Update(validationRequestCase);
                return true;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateValidationRequestCaseStatus(...). Exception message: " + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                        );
                return false;
            }
        }




        public static dynamic getDispositionRequestFromCaseNote(string noteTitle, EntityReference annotationParentRef)
        {
            string dispositionRequestText = "";
            try
            {

                Entity annotation = getSystemNote(noteTitle, annotationParentRef);

                string noteDesc = annotation.GetAttributeValue<string>("notetext");

                noteDesc = ValidationServicesHelper.regexReplace(@"\r\n\t*", noteDesc, "");


                dispositionRequestText = ValidationServicesHelper.regexMatchValue("(?<=validationRequest:)(.+)", noteDesc, 0);
                dispositionRequestText = ValidationServicesHelper.regexMatchValue("(.+)(?=validationResponse:)", dispositionRequestText, 0);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getDispositionRequestFromCaseNote(...). Exception message: " + Environment.NewLine + e.Message
                                                   + Environment.NewLine + "caseId: " + annotationParentRef.Id.ToString()
                                                   );
            }

            return dispositionRequestText;
        }









        public static Dictionary<string, string> getKeyValueForVariable(IDictionary<string, Object> expandoObject)
        {
            Dictionary<string, string> expandoList = new Dictionary<string, string>();

            foreach (KeyValuePair<string, Object> item in expandoObject)
            {
                string elementType = item.Value.GetType().Name;
                List<string> element = null;

                if (elementType.ToLower().Contains("object"))
                {
                    IDictionary<string, Object> itemExpando = (IDictionary<string, Object>)item.Value;
                    element = getListOfBasicNames(itemExpando);

                    foreach (string subElement in element)
                    {
                        expandoList.Add(subElement, subElement);
                    }

                }

                if (!item.Key.Contains("@odata"))
                {
                    if (
                        !(elementType.StartsWith("List") || elementType.ToLower().Contains("object"))
                        )
                        expandoList.Add(item.Key, item.Key);
                }

                if (elementType.StartsWith("List"))
                {
                    string listName = item.Key;


                    List<IDictionary<string, Object>> listExpando = JsonConvert.DeserializeObject<List<ExpandoObject>>(
                                                                                                                        JsonConvert.SerializeObject(item.Value)
                                                                                                                        ).ToList<IDictionary<string, Object>>();


                    foreach (IDictionary<string, Object> listElement in listExpando)
                    {
                        element = getListOfBasicNames(listElement);
                        foreach (string subElement in element)
                        {
                            expandoList.Add(subElement, subElement);
                        }

                    }

                }

                if (element != null)
                    expandoList.Add(item.Value.ToString(), item.Value.ToString());
            }
            return expandoList;
        }



        public static List<string> getListOfBasicNames(IDictionary<string, Object> expandoObject)
        {
            List<string> expandoList = new List<string>();

            foreach (KeyValuePair<string, Object> item in expandoObject)
            {
                string elementType = item.Value.GetType().Name;
                List<string> element = null;

                if (elementType.ToLower().Contains("object"))
                {
                    IDictionary<string, Object> itemExpando = (IDictionary<string, Object>)item.Value;
                    element = getListOfBasicNames(itemExpando);

                    foreach (string subElement in element)
                    {
                        expandoList.Add(subElement);
                    }

                }

                if (!item.Key.Contains("@odata"))
                {
                    if (
                        !(elementType.StartsWith("List") || elementType.ToLower().Contains("object"))
                        )
                        expandoList.Add(item.Key);
                }

                if (elementType.StartsWith("List"))
                {
                    string listName = item.Key;

                    List<IDictionary<string, Object>> listExpando = (List<IDictionary<string, Object>>)item.Value; 


                    foreach (IDictionary<string, Object> listElement in listExpando)
                    {
                        element = getListOfBasicNames(listElement);
                        foreach (string subElement in element)
                        {
                            expandoList.Add(subElement);
                        }

                    }

                }

                if (element != null)
                    expandoList.Add((string)item.Value);
            }


            return expandoList;
        }






        public static Entity getSystemNote(string noteTitle, EntityReference annotationParentRef)
        {
            Entity annotation = null;
            try
            {
                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryAnnotation);

                if (annotationCollection.Entities.Count() > 0)
                    annotation = annotationCollection.Entities.First();
            }
            catch (Exception e)
            {

            }
            return annotation;
        }





        public static bool hasNullEmptyValues(string filedValue)
        {
            bool isFieldNullEmpty = true;

            if (filedValue != null && filedValue.ToString().Trim() != "nil" && filedValue.ToString().Trim() != string.Empty)
                isFieldNullEmpty = false;


            return isFieldNullEmpty;
        }



        public static Entity getCaseForTransactionId(string validationReqTransactionId)
        {
            Entity caseEntity = null;
            try
            {
                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet(true);
                queryQualCase.Criteria.AddCondition("ts_validationrequesttransactionid", ConditionOperator.Equal, validationReqTransactionId);
                EntityCollection qualCaseCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQualCase);

                if (qualCaseCollection.Entities.Count > 0)
                    caseEntity = qualCaseCollection.Entities.First();
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getCaseForTransactionId(string validationReqTransactionId). Exception message: " + Environment.NewLine + e.Message
                                                    + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                                                    );
            }

            return caseEntity;
        }


        public static async Task<Entity> findCaseGenericFilterInAndOut(Dictionary<string, object> filterFieldsIn, Dictionary<string, object> filterFieldsOut)
        {
            Entity caseEntity = null;
            try
            {
                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet(true);


                if (filterFieldsIn != null)
                {
                    foreach (KeyValuePair<string, object> criteriaField in filterFieldsIn)
                    {
                        assignValueToQueryExpressionCondition(queryQualCase, criteriaField, "equal");
                    }
                }


                if (filterFieldsOut != null)
                {
                    foreach (KeyValuePair<string, object> criteriaField in filterFieldsOut)
                    {
                        assignValueToQueryExpressionCondition(queryQualCase, criteriaField, "notequal");
                    }
                }

                EntityCollection qualCaseCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryQualCase);

                if (qualCaseCollection.Entities.Count == 0)
                    return null;
                

                caseEntity = qualCaseCollection.Entities.First();
                Guid caseId = caseEntity.Id;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in findCaseGenericFilterInAndOut(...). Exception message: " + Environment.NewLine + e.Message);
            }

            return caseEntity;
        }


        public static async Task<EntityCollection> findEntityeGenericFilterInAndOut(string entityLogicalName, Dictionary<string, object> filterFieldsIn, Dictionary<string, object> filterFieldsOut)
        {
            Entity caseEntity = null;
            try
            {
                QueryExpression queryEntity = new QueryExpression(entityLogicalName);
                queryEntity.ColumnSet = new ColumnSet(true);


                if (filterFieldsIn != null)
                {
                    foreach (KeyValuePair<string, object> criteriaField in filterFieldsIn)
                    {
                        assignValueToQueryExpressionCondition(queryEntity, criteriaField, "equal");
                    }
                }


                if (filterFieldsOut != null)
                {
                    foreach (KeyValuePair<string, object> criteriaField in filterFieldsOut)
                    {
                        assignValueToQueryExpressionCondition(queryEntity, criteriaField, "notequal");
                    }
                }

                EntityCollection entityCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryEntity);

                return entityCollection;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in findCaseGenericFilterInAndOut(...). Exception message: " + Environment.NewLine + e.Message);
                return null;
            }

            
        }


        public static void assignValueToQueryExpressionCondition(QueryExpression queryQualCase, KeyValuePair<string, object> criteriaField, string conditionOperator = "equal")
        {
            try
            {

                string fieldName = criteriaField.Key;
                object fieldValue = criteriaField.Value;



                switch (fieldValue)
                {
                    case EntityReference entityRef:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, entityRef);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, entityRef);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, entityRef);
                                break;
                        }
                        break;

                    case OptionSetValue optionSet:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, optionSet);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, optionSet);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, optionSet);
                                break;
                        }
                        break;

                    case Money money:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, money);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, money);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, money);
                                break;
                        }
                        break;

                    case DateTime dateTime:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, dateTime);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, dateTime);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, dateTime);
                                break;
                        }
                        break;

                    case int intValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, intValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, intValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, intValue);
                                break;
                        }
                        break;

                    case decimal decimalValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, decimalValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, decimalValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, decimalValue);
                                break;
                        }
                        break;

                    case double doubleValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, doubleValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, doubleValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, doubleValue);
                                break;
                        }
                        break;

                    case bool boolValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, boolValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, boolValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, boolValue);
                                break;
                        }
                        break;

                    case Guid guidValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, guidValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, guidValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, guidValue);
                                break;
                        }
                        break;

                    case string stringValue:
                        switch (conditionOperator.ToLower())
                        {
                            case "eq":
                            case "equal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, stringValue);
                                break;
                            case "ne":
                            case "notequal":
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.NotEqual, stringValue);
                                break;
                            default:
                                queryQualCase.Criteria.AddCondition(fieldName, ConditionOperator.Equal, stringValue);
                                break;
                        }
                        break;

                    default:
                        //caseEntity[fieldName] = TryConvertValue(fieldName, fieldValue);
                        break;
                }

            }
            catch (Exception e)
            {
                string error = "Error in assignValueToQueryExpressionCondition(QueryExpression queryQualCase, KeyValuePair<string, object> criteriaField, string conditionOperator = \"equal\"). Exception message: "
                                + Environment.NewLine + e.Message
                                + Environment.NewLine + "fieldName: " + criteriaField.Key
                                + Environment.NewLine + "fieldValue: " + criteriaField.Value.ToString()
                                ;
                DynamicsInterface.writeToLog(error);

            }

        }
        public static void removeAccountConnection(Guid accountId, string agentEmailAddress)
        {
            try
            {
                QueryExpression queryConnection = new QueryExpression("connection");
                queryConnection.ColumnSet = new ColumnSet(true);
                queryConnection.Criteria.AddCondition("record1id", ConditionOperator.Equal, accountId);
                queryConnection.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
                queryConnection.Criteria.AddCondition("record2objecttypecode", ConditionOperator.Equal, 2);
                EntityCollection connectionCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryConnection);


                foreach (Entity connection in connectionCollection.Entities)
                {

                    Guid connectionToId = connection.GetAttributeValue<EntityReference>("record2id") == null ? Guid.Empty : connection.GetAttributeValue<EntityReference>("record2id").Id;
                    Entity contact = DynamicsInterface.DataverseClient.Retrieve("contact", connectionToId, new ColumnSet("emailaddress1"));
                    string contactEmail = contact.GetAttributeValue<string>("emailaddress1");
                    if (contactEmail != agentEmailAddress)
                        DynamicsInterface.DataverseClient.Delete("connection", connection.Id);
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in removeAccountConnection. Exception message: " + Environment.NewLine + e.Message
                                            + Environment.NewLine + "accountId: " + accountId
                                            + Environment.NewLine + "agentEmailAddress: " + agentEmailAddress
                                            );
            }
        }

        public static async Task<bool> validationServicesEvaluateForFraudSimplified(Entity validationRequestCase, Entity requestingAccount, string validationReqTransactionId)
        {
            var fraudFlags = new List<string>();
            bool isFraudulent = false;

            try
            {
                string website = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite");
                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationagentemail");
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


                // 4. Agent Email Domain Validation
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





                if (isFraudulent && fraudFlags.Count > 0)
                {

                    string noteDesc = "Fraud analysis findings:\n\n" + string.Join("\n", fraudFlags.Select((flag, index) => $"{index + 1}. {flag}"));
                    processSystemNote("-- Potential Fraud --", noteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                    validationRequestCase["ts_casestatus"] = new OptionSetValue(104602); //104602 - OQ - Fraud Review
                    DynamicsInterface.DataverseClient.Update(validationRequestCase);


                    string fraudReviewQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.config.fraudReviewQueue;
                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, fraudReviewQueue);

                    DynamicsInterface.writeToLog($"Case {validationReqTransactionId} flagged as potential fraud with {fraudFlags.Count} enhanced violations");
                }

                return isFraudulent;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error in evaluateForFraudSimplified for {validationReqTransactionId}: {ex.Message}");

                processSystemNote("-- Potential Fraud --", $"Error during fraud evaluation: {ex.Message}\n\nCase requires manual review due to technical issues during automated validation."
                                    , new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                validationRequestCase["ts_casestatus"] = new OptionSetValue(104602);//104602 - OQ - Fraud Review
                DynamicsInterface.DataverseClient.Update(validationRequestCase);

                string fraudReviewQueue = DynamicsProcessesValidationServices.AutomatedValDefinition.config.fraudReviewQueue;
                DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, fraudReviewQueue);

                return true;
            }
        }

        [JsonObject]
        public class DispositionRequest
        {
            [JsonProperty]
            public string LegalName { get; set; }

            [JsonProperty]
            public string AddressLine1 { get; set; }

            [JsonProperty]
            public string AddressOther { get; set; }

            [JsonProperty]
            public string AddressCity { get; set; }

            [JsonProperty]
            public string AddressStateRegion { get; set; }

            [JsonProperty]
            public string AddressPostalCode { get; set; }

            [JsonProperty]
            public string AddressCountryId { get; set; }
            [JsonProperty]
            public string Email { get; set; }
            [JsonProperty]
            public string Phone { get; set; }

            [JsonProperty]
            public string Website { get; set; }

            [JsonProperty]
            public string MissionStatement { get; set; }

            [JsonProperty]
            public string OperatingBudget { get; set; }

            [JsonProperty]
            public string ActivityCode { get; set; }

            [JsonProperty]
            public string AgentFirstName { get; set; }

            [JsonProperty]
            public string AgentLastName { get; set; }

            [JsonProperty]
            public string AgentEmail { get; set; }

            [JsonProperty]
            public string EffectiveDatetime { get; set; }

            [JsonProperty]
            public string TransactionId { get; set; }

            [JsonProperty]
            public string OrgType { get; set; }

            [JsonProperty]
            public string OrgRefId { get; set; }

            [JsonProperty]
            public RegistrationIdentifier[] RegistrationIdentifiers { get; set; }
        }


        [JsonObject]
        public class RegistrationIdentifier
        {
            [JsonProperty]
            public string LegalIdentifier { get; set; }

            [JsonProperty]
            public string RegulatoryBody { get; set; }

            [JsonProperty]
            public string ArtifactURI { get; set; }
        }



    }

}
