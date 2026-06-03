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
using Microsoft.Xrm.Sdk.Metadata;

namespace DynamicsProcesses
{
    internal class ValidationServicesHelper
    {
        public static DispositionRequest DispositionClass = new DispositionRequest();
        public static RegistrationIdentifier RegIdentifierClass = new RegistrationIdentifier();

        public static Dictionary<string, string> QualStatus = new Dictionary<string, string>
                {
                    {"1", "qualified"},
                    {"3", "requalification pending"},
                    {"4", "qualification pending"},
                    {"5", "disqualified"},
                    {"13", "canceled"},
                    {"14", "abandoned"},
                    {"15", "expired"},
                    {"23", "irs disqualified"},
                    {"26", "unresponsive"},
                    {"qualified", "1"},
                    {"requalification pending", "3"},
                    {"qualification pending", "4"},
                    {"disqualified", "5"},
                    {"canceled", "13"},
                    {"abandoned", "14"},
                    {"expired", "15"},
                    {"irs disqualified", "23"},
                    {"unresponsive", "26"}
                };

        
        public static string CTPSessionKey = ConfigurationManager.AppSettings["CTPSessionKey"];

        public static string AzureStorage7C = ConfigurationManager.AppSettings["AzureStorage7C"];
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
                DynamicsInterface.writeToLog($"Error in createCase(). Exception message: {Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}{customerRef?.LogicalName ?? "null customer reference"} Id: {customerRef?.Id.ToString() ?? ""}"
                                                );

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
                DynamicsInterface.writeToLog($"Error in makeHttpGetCall(). Exception message: {Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}Endpoint: {baseUrl + endPointPath}"
                                                );
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
                DynamicsInterface.writeToLog($"Error in makeHttpPostCall(). Exception message: {Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}Endpoint: {baseUrl + endPointPath}{Environment.NewLine}requestJson:{Environment.NewLine}{requestJson}"
                                                );
            }

            return respDynObject;
        }

        public static async Task<dynamic> makeHttpGetCall(
                                               string baseUrl, string endPointPath
                                               , Dictionary<string, string> queryParams
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
                string requestUrl = baseUrl + endPointPath + queryString;





                HttpResponseMessage response = await client.GetAsync(
                                                                requestUrl
                                                                );



                string responseTxt = await response.Content.ReadAsStringAsync();
                respDynObject = JsonConvert.DeserializeObject(responseTxt);

                client.Dispose();

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in async makeHttpGetCall(). Exception message: {Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}Endpoint: {baseUrl + endPointPath}"
                                                );
            }

            return respDynObject;
        }

        public static async Task<dynamic> makeHttpPostCall(string requestJson
                                                           , string baseUrl, string endPointPath
                                                            , Dictionary<string, string> queryParams
                                                            , Dictionary<string, string> extraHeaders = null
                                                            )
        {
            try
            {
                #region Initialize HttpClient and Headers
                dynamic respDynObject = null;

                HttpClient client = new HttpClient();
                HttpRequestHeaders headers = client.DefaultRequestHeaders;
                headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                if (extraHeaders != null)
                {
                    foreach (KeyValuePair<string, string> header in extraHeaders)
                    {
                        headers.Add(header.Key, header.Value);
                    }
                }
                #endregion


                #region Set Base URL, Endpoint Path, Key, and Query Parameters
                string queryString = "";
                if (queryParams != null && queryParams.Count > 0)
                {
                    queryString = string.Join("&", queryParams.Select(kvp => $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));
                    queryString = "?" + queryString;
                }
                string requestUrl = baseUrl + endPointPath + queryString;
                #endregion


                #region Create Request Content & Send POST Request
                StringContent contentRequest = new StringContent(requestJson, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await client.PostAsync(
                                                                requestUrl
                                                                , contentRequest
                                                                 );
                #endregion


                #region Read Response & Return
                string responseTxt = await response.Content.ReadAsStringAsync();

                respDynObject = JsonConvert.DeserializeObject(responseTxt);
                client.Dispose();
                return respDynObject;
                #endregion

            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in async makeHttpPostCall(). Exception message: {Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}Endpoint: {baseUrl + endPointPath}{Environment.NewLine}requestJson:{Environment.NewLine}{requestJson}"
                                                );
                return null;
            }
            #endregion

        }



        public static async Task initiateNonprofitVerificationAgent(string caseIdText)
        {
            try
            {

                if (DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] != "prod")
                    return;


                DynamicsInterface.writeToLog($"Entering initiateNonprofitVerificationAgent with caseId: {caseIdText}"
                                                );



                dynamic request = new JObject();
                request.caseId = caseIdText;
                string requestJson = JsonConvert.SerializeObject(request);



                string baseUrl = "https://techsoupservices.org";
                string endPointPath = "/agent/nonprofit/verify-case-async/noauth";



                dynamic response = await makeHttpPostCall(
                                                            requestJson: requestJson
                                                            , baseUrl: baseUrl
                                                            , endPointPath: endPointPath
                                                            , queryParams: null
                                                            );


                string responseText = JsonConvert.SerializeObject(response, Newtonsoft.Json.Formatting.Indented);


                DynamicsInterface.writeToLog($"initiateNonprofitVerificationAgent(). Response from agent call:{Environment.NewLine}{responseText}"
                                                                                                                                );
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in initiateNonprofitVerificationAgent(). Exception message:{Environment.NewLine}{e.Message}"
                                                                                                                            );

            }


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
                DynamicsInterface.writeToLog("Error in processSystemNote(). Exception message: " + Environment.NewLine + e.Message);
            }
        }


        public static async Task getValidationScoreMatrix(Entity caseEntity, Entity account, string validationReqTransactionId, string queueName, IDictionary<string, Object> dispositionRequest)
        {
            try
            {

                int maxDispositionRetrievalAttempts = DynamicsProcessesValidationServices.ConfigParams.maxDispositionRetrievalAttempts;
                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.ConfigParams.postNoAutoCloseQueue;
                bool autoCloseEnabled = DynamicsProcessesValidationServices.ConfigParams.autoCloseEnabled;


                string ctpUrl = "https://tsvc.tsgctp.org/";
                string ctpSessionKey = CTPSessionKey;
                    //"61695af7-1652-4b08-b786-192de1884f61";
                string endPointPath = "services/vsscorematrix/v_001/";

                Dictionary<string, string> queryParams = new Dictionary<string, string>();
                queryParams.Add("transaction_id", validationReqTransactionId);


                dynamic dispositionResponse = ValidationServicesHelper.makeHttpGetCall(
                                                                                        ctpUrl, endPointPath
                                                                                        , ctpSessionKey, queryParams
                                                                                        );



                string dispositionResponseText = JsonConvert.SerializeObject(dispositionResponse);




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








                



                string dispositionStatus = dispositionData.score_matrix_status == null ? "" : dispositionData.score_matrix_status;
                caseEntity["ts_validationdispositionstatus"] = dispositionStatus;

                string noteDesc = "";
                string noteTitle = "";
                if (dispositionStatus != "completed")
                {
                    caseEntity["ts_validationrequestlaststatuscheck"] = DateTime.UtcNow;
                    int dispositionRequestChecksCount = caseEntity.GetAttributeValue<int>("ts_validationstatuscheckscount");
                    dispositionRequestChecksCount++;
                    caseEntity["ts_validationstatuscheckscount"] = dispositionRequestChecksCount;

                    if (dispositionRequestChecksCount >= maxDispositionRetrievalAttempts)
                    {
                        caseEntity["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                        //DynamicsInterface.DataverseClient.Update(caseEntity);
                        DynamicsProcessesHelper.addCaseToQueue(caseEntity.Id, postNoAutoCloseQueue);

                        processSystemNote("No Disposition Received",  "No Disposition was received after the conclusion of the time allotted for this process", new EntityReference(caseEntity.LogicalName, caseEntity.Id));
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

                if (dispositionAgentValid && isAgentNameValid && agentOrgCommonDomain && autoCloseEnabled)
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
                List<dynamic> autoCloseCustomeRules = ((JArray)DynamicsProcessesValidationServices.ConfigJson.config.autoCloseCustomRules).ToList<dynamic>();

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


        public static string regexReplace(string pattern, string expresion, string replaceWith, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            string convExpresion = expresion;

            Regex regexObj = new Regex(pattern, regexOptions);
            convExpresion = regexObj.Replace(expresion, replaceWith);

            return convExpresion;
        }

        public static bool regexMatch(string pattern, string input, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            Regex regex = new Regex(pattern, regexOptions);
            return regex.IsMatch(input);
        }

        public static int regexMatchPos(string pattern, string input, int startAt, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            Regex regexObj = new Regex(pattern, regexOptions);

            Match match = regexObj.Match(input, startAt);

            return match.Index;
        }

        public static string regexMatchValue(string pattern, string input, int startAt, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            Regex regexObj = new Regex(pattern, regexOptions);

            Match match = regexObj.Match(input, startAt);

            return match.Value;
        }

        public static bool regexMatchSuccess(string pattern, string input, int startAt, RegexOptions regexOptions = RegexOptions.IgnoreCase)
        {
            Regex regexObj = new Regex(pattern, regexOptions);

            Match match = regexObj.Match(input, startAt);

            return match.Success;
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

        public static Entity processQualCaseFromValidationRequest(Entity orgAccount, Entity validationRequestCase)
        {
            
            Entity qualCase = null;
            try
            {
                #region Check If Qual Case Already Exists
                EntityReference orgDesigRef = orgAccount.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;

                if (orgDesigId == Guid.Empty)
                    return null;

                bool caseExists = false;
                Guid caseId = Guid.Empty;

                EntityReference vallReqParentCaseRef =  validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid");
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
                    queryQualCase.Criteria.AddCondition("customerid", ConditionOperator.Equal, orgAccount.Id);
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

                Entity activityCodeEntity = orgAccount.GetAttributeValue<EntityReference>("new_activitycode") == null ? null
                                                                                                    : DynamicsInterface.DataverseClient.Retrieve("new_activitycodes", orgAccount.GetAttributeValue<EntityReference>("new_activitycode").Id, new ColumnSet(true));

                Entity qualCodeEntity = orgAccount.GetAttributeValue<EntityReference>("new_orgdesignation") == null ? null
                                                                                                    : DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", orgAccount.GetAttributeValue<EntityReference>("new_orgdesignation").Id, new ColumnSet(true));

                string orgActivityCode = activityCodeEntity?.GetAttributeValue<string>("new_activitycode");
                string qualCode = qualCodeEntity?.GetAttributeValue<string>("new_qualcode");

                string qualTerm = qualCodeEntity.FormattedValues["new_qualterm"];
                string qualCategory = qualCodeEntity.GetAttributeValue<string>("new_qualcategory");
                string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");


                EntityReference qualCodeRef = new EntityReference(qualCodeEntity.LogicalName, qualCodeEntity.Id);


                CaseDefinition caseLogicalName = CaseDefinition.CreateInstance();

                string tsOrgId = orgAccount.GetAttributeValue<string>("accountnumber");
                string title = $"{qualCode} - {qualName} - TSOrgId: {tsOrgId}";
                qualCase[caseLogicalName.CaseTitle] = title;
                qualCase[caseLogicalName.QualificationCodeId] = qualCodeRef;

                if (!caseExists)
                {
                    int caseTypeCode = 2;
                    int type = 101996;
                    EntityReference customerRef = orgAccount.ToEntityReference();

                    qualCase[caseLogicalName.CaseCategory] = new OptionSetValue(caseTypeCode);
                    qualCase[caseLogicalName.Type] = new OptionSetValue(type);
                    qualCase[caseLogicalName.Customer] = customerRef;
                }

                qualCase[caseLogicalName.CaseStatus] = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_casestatus") ?? new OptionSetValue(102050); //102050 - 'OQ - Not Started'

                int caseResolution = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_caseresolution")?.Value ?? -1;

                if (caseResolution == 3) //Fraud
                    qualCase["ts_caseresolution"] = new OptionSetValue(3);

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
                DynamicsInterface.writeToLog($"Error in connectAgentToAccount(Guid accountId, Guid contactId). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}accountId: {accountId.ToString()}; contactId: {contactId.ToString()}"
                                                   );
                return null;
            }
            #endregion
        }








        public static IDictionary<string, Object> getAgentContact(Entity validationRequestCase, Entity orgAccount, string validationReqTransactionId)
        {
            try
            {
                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationagentemail");
                IDictionary<string, Object> agentContact = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                
                if (string.IsNullOrWhiteSpace(agentEmail))
                    return agentContact;

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
                DynamicsInterface.writeToLog($"Error in getAgentContact(). Exception message:{Environment.NewLine}{e.Message}"
                                                   + $"{Environment.NewLine}validationReqTransactionId: {validationReqTransactionId}"
                                                   );
                return null;

                
            }
            #endregion
        }


        public static Entity createAgentContact(Entity validationRequestCase, Entity validationRequestor, string validationReqTransactionId, string queueName)
        {
            try
            {
                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationagentemail");

                if (string.IsNullOrWhiteSpace(agentEmail))
                    return null;

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
                DynamicsInterface.writeToLog($"Error in createAgentContact(Entity validationRequestCase). Exception message:{Environment.NewLine}{e.Message}"
                                                   + $"{Environment.NewLine}validationReqTransactionId: {validationReqTransactionId}"
                                                   );
                return null;

                
            }
            #endregion
        }

        public static async Task<Entity> createAgentContact(dynamic orgAgent, string validationReqTransactionId)
        {
            try
            {
                string agentEmail = orgAgent.email;

                if (agentEmail == null)
                    return null;

                string firstName = orgAgent.firstName;
                string lastname = orgAgent.lastName;

                #region FindIfExists
                QueryExpression queryContact = new QueryExpression("contact");
                queryContact.ColumnSet = new ColumnSet(true);
                queryContact.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, agentEmail);
                EntityCollection contactCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryContact);

                if (contactCollection.Entities.Count > 0)
                    return contactCollection.Entities.First();
                #endregion

                #region CreateContact
                Entity contact = new Entity("contact");
                contact["firstname"] = firstName;
                contact["lastname"] = lastname;

                contact["emailaddress1"] = agentEmail;
                contact["ts_emailvalidationstatus"] = new OptionSetValue(4);

                string tsContactId = DynamicsProcessesHelper.getNewTSOrgContactId();
                contact["new_contactaccountnumber"] = tsContactId;
                //contact["adx_identity_username"] = "agent." + tsContactId;

                contact["new_source"] = new OptionSetValue(105000); //105000 - ValidationRequest

                Guid contactId = await DynamicsInterface.DataverseClient.CreateAsync(contact);

                contact = await DynamicsInterface.DataverseClient.RetrieveAsync(contact.LogicalName, contactId, new ColumnSet(true));


                return contact;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in createAgentContact(orgAgent). Exception message:{Environment.NewLine}{e.Message}"
                    + $"{Environment.NewLine}transactionId: {validationReqTransactionId}; agentEmail: {orgAgent.email ?? ""}"                                                   
                                                   );
                return null;
            }
            #endregion
        }
        public static Entity createOrgFromCase(Entity validationRequestor, Entity validationRequestCase, string validationReqTransactionId)
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


                string tsPngoCode = validationRequestor.GetAttributeValue<string>("ts_tspngocode");
                string requestorName = validationRequestor.GetAttributeValue<string>("name");

                string countryCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid") == null ? ""
                                                                                    : validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid").ToLower();

                if (countryCode != "us" && !string.IsNullOrEmpty(tsPngoCode) && !string.IsNullOrEmpty(orgRefId))
                {
                    account["ts_pporgid"] = orgRefId;
                    account["ts_orgppid"] = validationRequestor.ToEntityReference();
                }
                #endregion


                #region Name, Org Designation, Mission Statement...
                account["name"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalname");
                account["customertypecode"] = new OptionSetValue(3); //Customer



                

                dynamic optionValueResponse = null;
                if (!string.IsNullOrEmpty(tsPngoCode))
                    optionValueResponse = getOptionSetValue("new_tsgsource", "Partner Platform", false);
                else
                    optionValueResponse = getOptionSetValue("new_tsgsource", requestorName, false);

                OptionSetValue orgSource = optionValueResponse?.optionValue != null ? new OptionSetValue((int)optionValueResponse.optionValue) : new OptionSetValue(101892);


                string referralId = validationRequestCase.GetAttributeValue<string>("ts_validationrequestreferralid");

                if (!string.IsNullOrEmpty(referralId) && regexMatch(@"^\d+$", referralId) && existsOptionSetValue("new_tsgsource", int.Parse(referralId)).Result)
                    orgSource = new OptionSetValue(int.Parse(referralId));



                account["new_source"] = orgSource;



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
                    string error = $"Error in createOrgFromCase(). Account was not created. validationReqTransactionId: {validationReqTransactionId}";
                    DynamicsInterface.writeToLog(error);
                    return null;
                }

                newAccount = DynamicsInterface.DataverseClient.Retrieve(account.LogicalName, accountId, new ColumnSet(true));
                tsOrgId = newAccount.GetAttributeValue<string>("accountnumber");
                #endregion



            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in createOrgFromCase(). Exception message:{Environment.NewLine}{e.Message}"
                                                        + $"{Environment.NewLine}validationReqTransactionId: {validationReqTransactionId}"
                                                  );


                DynamicsProcessesHelper.processSystemNote(" --- Error Creating Org --- ", $"Error in createOrgFromCase(). Exception message:{Environment.NewLine}{e.Message}"
                                                                , validationRequestCase.ToEntityReference());


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

        

        public static async Task<bool> processValidationRequestAccountFound(Entity matchingAccount, Entity validationRequestCase, Entity validationRequestor, string validationReqTransactionId
                                                                                                                                                            , IDictionary<string, System.Object> response
                                                                                                                                                            , IDictionary<string, Object> ctpOrgEntity = null)
        {
            bool success = true;
            try
            {
                #region AutomatedValDefinition

                string duplicateReviewQueue = DynamicsProcessesValidationServices.ConfigParams.duplicateReviewQueue;
                string postNoAutoCloseQueue = DynamicsProcessesValidationServices.ConfigParams.postNoAutoCloseQueue;
                string postAutoCloseQueue = DynamicsProcessesValidationServices.ConfigParams.postAutoCloseQueue;

                //dynamic customerAutomatedValDefinition = ((JArray)DynamicsProcessesValidationServices.ConfigParams.customerDefinitionQueryResult)?.ToList<dynamic>()?.FirstOrDefault();
                List<dynamic> targetedValidations = ((JArray)DynamicsProcessesValidationServices.ConfigParams.targetedValidations)?.ToList<dynamic>();
                //((dynamic)((JObject)ConfigParams.customerAutomatedValDefinition))?.targetedValidations

                bool agentValidation = targetedValidations == null
                                                                || targetedValidations.Where(item => ((string)item)?.ToLower() == "agent")?.FirstOrDefault() != null;
                #endregion

                //Entity matchingAccount = DynamicsInterface.DataverseClient.Retrieve("account", accountMatch.AccountId, new ColumnSet(true));

                #region HandlingAccountFound

                Entity qualCase = await DynamicsProcessesHelper.retrieveOrgQualCase(matchingAccount);

                if (qualCase == null)
                    return false;


                string ctpOrgId = matchingAccount.GetAttributeValue<string>("ts_ctporgid"); // to make sure accoun
                

                if (ctpOrgEntity == null)
                    ctpOrgEntity = await ValidationServicesHelper.getCTPOrgObjects(ctpOrgId);

                if (ctpOrgEntity != null)
                {
                    bool existsTransactionIdExternalReference = ((List<dynamic>)ctpOrgEntity["transactionIdExternalReferences"]).Any(tranRef => (string)tranRef.typeValue == validationReqTransactionId);

                    if (!existsTransactionIdExternalReference)
                        await ProcessHelper.addObject_001ToCtpOrg("ExternalReferenceObject_001", "transaction", validationReqTransactionId, ctpOrgEntity, "nil");

                }


                validationRequestCase["parentcaseid"] = new EntityReference(qualCase.LogicalName, qualCase.Id);
                await DynamicsInterface.DataverseClient.UpdateAsync(validationRequestCase);


                IDictionary<string, Object> AgentContact = getAgentContact(validationRequestCase, matchingAccount, validationReqTransactionId);

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
                    await DynamicsInterface.DataverseClient.UpdateAsync(validationRequestCase);


                    #region PngoId & OrgRefId
                    string orgRefId = DynamicsProcessesHelper.regexMatchValue(@"(?<=\w+_)(\d+)(?=_\w+$)", validationReqTransactionId, 0);

                    string tsPngoCode = validationRequestor.GetAttributeValue<string>("ts_tspngocode");

                    string countryCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid") == null ? ""
                                                                                        : validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid").ToLower();

                    if (countryCode != "us" && !string.IsNullOrEmpty(tsPngoCode) && !string.IsNullOrEmpty(orgRefId))
                    {
                        matchingAccount["ts_pporgid"] = orgRefId;
                        matchingAccount["ts_orgppid"] = new EntityReference("account", validationRequestor.Id);
                        await DynamicsInterface.DataverseClient.UpdateAsync(matchingAccount);
                    }
                    #endregion


                    DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postAutoCloseQueue);

                    string orgMatchNoteDesc = $"An existing org has been found for this Validation Request:"
                                                + $"{Environment.NewLine}{Environment.NewLine}TSOrgId: {matchingAccount.GetAttributeValue<string>("accountnumber")}"
                                                    + $"{Environment.NewLine}{Environment.NewLine}The Org is in qualiified status, which will be applied to the Validation Request";

                    processSystemNote("An Org Match Has Been Found", orgMatchNoteDesc, new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));

                    await ValidationServicesHelper.sendValidationRequestStatusToRequestor(validationReqTransactionId, validationRequestCase, validationRequestor);

                    return success;
                }
                #endregion
                string orgFoundTitle = "";
                string orgFoundNote = "";

                if (
                        tsQualCaseStatus != null && tsQualCaseStatus == 102154 //102154 - OQ - Expired
                    )
                {
                    Entity orgDesignationCodeEntity = matchingAccount.GetAttributeValue<EntityReference>("new_orgdesignation") == null ? null :
                                                           await DynamicsInterface.DataverseClient.RetrieveAsync("new_qualificationcode", matchingAccount.GetAttributeValue<EntityReference>("new_orgdesignation").Id, new ColumnSet(true));

                    Guid orgDesigId = orgDesignationCodeEntity == null ? Guid.Empty : orgDesignationCodeEntity.Id;
                    string qualCode = orgDesignationCodeEntity?.GetAttributeValue<string>("new_qualcode");
                    string qualName = orgDesignationCodeEntity.GetAttributeValue<string>("new_qualname");

                    string tsOrgId = matchingAccount.GetAttributeValue<string>("accountnumber");

                    Guid caseId = await createCaseGeneric(title: $"{qualCode} - {qualName} - TSOrgId: {tsOrgId}"
                                                                                , caseTypeCode: 2
                                                                                , type: 101996
                                                                                , customerRef: matchingAccount.ToEntityReference()
                                                                                , caseStatus: 102050
                                                                                , qualCodeId: orgDesignationCodeEntity.Id
                                                                                , extraCaseFields: null
                                                                                );
                    if (caseId != Guid.Empty)
                    {
                        validationRequestCase["parentcaseid"] = new EntityReference("incident", caseId);
                        await DynamicsInterface.DataverseClient.UpdateAsync(validationRequestCase);

                        orgFoundTitle = "Org Qualification Expired";
                        orgFoundNote = $"TSOrgId: {matchingAccount.GetAttributeValue<string>("accountnumber")}"
                                        + $"{Environment.NewLine}{Environment.NewLine}Org was previously qualified, but its qualified status expired on {qualCase.GetAttributeValue<DateTime>("ts_expirationdate").ToString("MM/dd/yyyy")}";

                        processSystemNote(orgFoundTitle, orgFoundNote, validationRequestCase.ToEntityReference());
                        response["validationProcessAction"] = "continue";
                        return true;
                    }
                    else
                    {
                        orgFoundTitle = "Org Qualification Expired";
                        orgFoundNote = $"TSOrgId: {matchingAccount.GetAttributeValue<string>("accountnumber")}"
                                        + $"{Environment.NewLine}{Environment.NewLine}Org qualification has expired, but there was a problem creating the new qual case";
                        processSystemNote(orgFoundTitle, orgFoundNote, validationRequestCase.ToEntityReference());

                    }
                }
                #region Handle Agent Not Found - Found But Not With Org

                string reason = "";

                if (tsQualCaseStatus == 102056 && agentContactVerificationStatusText != "Verified")
                    reason = "Agent needs to be verified";
                else if (tsQualCaseStatus != 102056 && agentContactVerificationStatusText != "Verified")
                    reason = "Org needs to be qualified and Agent needs to be verified";


                orgFoundTitle = "An Org Match Has Been Found";
                orgFoundNote = $"An existing org has been found for this Validation Request:"
                                        + $"{Environment.NewLine}{Environment.NewLine}TSOrgId: {matchingAccount.GetAttributeValue<string>("accountnumber")}"
                                            + $"{Environment.NewLine}{Environment.NewLine}{reason}";                




                string ctpOrgStatus = ctpOrgEntity != null ? ((string)ctpOrgEntity["statusOrgWithExpirationCheck"])?.ToLower() : "";

                if (ctpOrgStatus == "expired")
                {
                    orgFoundTitle = "Org Qualification Expired";
                    orgFoundNote = $"TSOrgId: {matchingAccount.GetAttributeValue<string>("accountnumber")}"
                                    + $"{Environment.NewLine}{Environment.NewLine}Org was previously qualified, but its qualified status expired on {((DateTime?)ctpOrgEntity["expirationDate"])?.ToString("MM/dd/yyyy") ?? ""}";
                }




                processSystemNote(orgFoundTitle, orgFoundNote, validationRequestCase.ToEntityReference());


                validationRequestCase["ts_casestatus"] = new OptionSetValue(104697);//OQ - AutoValidation - Requires Further Evaluation
                await DynamicsInterface.DataverseClient.UpdateAsync(validationRequestCase);
                DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, postNoAutoCloseQueue);
                #endregion

                return success;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in processValidationRequestAccountFound(...). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}validationReqTransactionId: {validationReqTransactionId}"
                                                );
                return false;
            }

        }

        public static async Task<Entity> getAccountForTsOrgId(string tsOrgId)
        {
            try
            {
                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(true);
                queryAccount.Criteria.AddCondition("accountnumber", ConditionOperator.Equal, tsOrgId);
                EntityCollection accountCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryAccount);

                if (accountCollection.Entities.Count == 0)
                    return null;                

                Entity account = accountCollection.Entities.First();
                return account;
            }          
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in getAccountForTsOrgId(). Exception message:{Environment.NewLine}{e.Message}{Environment.NewLine}tsOrgId: {tsOrgId}");
                return null;
            }     
        }

        public static async Task<Entity> findCtpOrgMatch(Entity validationRequestCase, Entity validationRequestor, string validationReqTransactionId, IDictionary<string, System.Object> response)
        {
            Entity orgAccount = null;
            try
            {    
                string dispositionDataText = validationRequestCase.GetAttributeValue<string>("ts_validationdispositiondata");
                dynamic dispositionData = JsonConvert.DeserializeObject(dispositionDataText);

                //bool ctpOrgMatch = dispositionData?.ctp_db_match_status ?? false;

                string matchCtpOrgId = ((JArray)dispositionData?.ctp_db_match_id_set)?.ToList<dynamic>()?.Select(item => (string)item)?.FirstOrDefault();

                if (string.IsNullOrEmpty(matchCtpOrgId))
                    return null;

                IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(matchCtpOrgId);


                if (ctpOrgEntity == null)
                {
                    string error = $"At findValidationRequestAccountMatches - ctpOrgEntity is null for ctpOrgId: {matchCtpOrgId}";
                    DynamicsInterface.writeToLog(error);
                    return null;
                }

                string tsOrgId = "";

                if (
                        !string.IsNullOrEmpty((string)ctpOrgEntity["externalReferenceOnyx"]) && DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] == "prod"
                    )
                {
                    tsOrgId = (string)ctpOrgEntity["externalReferenceOnyx"];

                    orgAccount = await getAccountForTsOrgId(tsOrgId);

                    //if (orgAccount == null)
                    //{
                    //    string noteTitle = $"CTP Org Contains TSOrgId Reference - But Was Not Found In Dynamics";
                    //    string noteDesc = $"CTP Org object contains a reference to TSOrgId"
                    //                        + $"{Environment.NewLine}Reference value (signature: ExternalReferenceObject_001, type: orgonyx): {tsOrgId}";
                    //    ;
                    //    await ProcessHelper.processSystemNote(noteTitle, noteDesc, validationRequestCase.ToEntityReference());
                    //    return null;
                    //}

                }


                if (string.IsNullOrEmpty(tsOrgId))
                {
                    tsOrgId = DynamicsProcessesHelper.getNextTsCustomerId();

                    if (string.IsNullOrEmpty(tsOrgId))
                        return null;

                    if (DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] == "prod")
                    {
                        if (ctpOrgEntity["externalReferenceObjectOnyx"] != null)
                            await ProcessHelper.updateCtpOrgOnyxExternalReference(tsOrgId, ctpOrgEntity);
                        else
                            await ProcessHelper.addOnyxExternalReferenceToCtpOrg(tsOrgId, ctpOrgEntity);                        
                        
                        
                        ctpOrgEntity = await ProcessHelper.getCTPOrgObjects(matchCtpOrgId);
                        if (ctpOrgEntity == null)
                            return null;

                        string externalReferenceOnyx = (string)ctpOrgEntity["externalReferenceOnyx"];

                        if (string.IsNullOrEmpty(externalReferenceOnyx))
                        {
                            string error = $"findCtpOrgMatch(). externalReferenceOnyx, for {tsOrgId}, not captured in ctpOrgObject, for ctpOrgId {matchCtpOrgId}, after update";
                            DynamicsInterface.writeToLog(error);
                            return null;
                        }
                    }

                    orgAccount = await createOrgForCtp(ctpOrgEntity, tsOrgId, validationReqTransactionId);

                    if (orgAccount == null)
                        return null;
                }
               

                bool success = await processValidationRequestAccountFound(matchingAccount: orgAccount
                                                                            , validationRequestCase: validationRequestCase
                                                                            , validationRequestor: validationRequestor
                                                                            , validationReqTransactionId: validationReqTransactionId
                                                                            , response: response
                                                                            , ctpOrgEntity: ctpOrgEntity
                                                                            );

                if (!success)
                {
                    response["validationProcessAction"] = "ValidationRequestStatusUpdate-RouteToQueue";
                }
                else
                {
                    int validationRequestCaseStatus = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_casestatus")?.Value ?? -1;
                    if (validationRequestCaseStatus == 102056) //102056 - OQ - Qualified
                        response["validationProcessAction"] = "terminate";
                }

                return orgAccount;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in findCtpOrgMatch(). Exception message:{Environment.NewLine}{e.Message}{Environment.NewLine}transactionId: {validationReqTransactionId}");
                return null;
            }
        }
        public static async Task<IDictionary<string, System.Object>> findValidationRequestAccountMatches(Entity validationRequestCase, Entity validationRequestor, string validationReqTransactionId)
        {
            IDictionary<string, System.Object> response = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;
            try
            {
                #region AutomatedValDefinition
                string duplicateReviewQueue = DynamicsProcessesValidationServices.ConfigParams.duplicateReviewQueue;
                bool includeCtpOrgMatch = DynamicsProcessesValidationServices.ConfigParams.includeCtpOrgMatch;
                #endregion

                #region Call Account Match Service
                string legalId = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalidentifier") ?? "";
                string name = validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalname") ?? "";
                string address1 = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressline1") ?? "";
                string stateProvince = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddressstateregion") ?? "";
                string postalCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresspostalcode") ?? "";
                string countryCode = validationRequestCase.GetAttributeValue<string>("ts_validationrequestaddresscountryid") ?? "";
                string website = validationRequestCase.GetAttributeValue<string>("ts_validationrequestwebsite") ?? "";
                string phone = validationRequestCase.GetAttributeValue<string>("ts_validationrequestphone") ?? "";

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

                #endregion

                #region Process Matches                
                if (accountMatches == null || accountMatches.Count == 0)
                {
                    Entity orgAccount = null;
                    if (includeCtpOrgMatch)
                        orgAccount = await findCtpOrgMatch(validationRequestCase, validationRequestor, validationReqTransactionId, response);

                    return response;
                }


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
                    await DynamicsInterface.DataverseClient.UpdateAsync(validationRequestCase);

                    if (!string.IsNullOrEmpty(duplicateReviewQueue))
                        DynamicsProcessesHelper.addCaseToQueue(validationRequestCase.Id, duplicateReviewQueue);

                    response["validationProcessAction"] = "terminate";
                }
                else if (accountMatches.Count == 1)
                {
                    if (validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid") == null)
                    {
                        AccountMatch accountMatch = accountMatches.First();
                        Entity matchingAccount = await DynamicsInterface.DataverseClient.RetrieveAsync("account", accountMatch.AccountId, new ColumnSet(true));

                        bool success = await processValidationRequestAccountFound(matchingAccount: matchingAccount
                                                                                    , validationRequestCase: validationRequestCase
                                                                                    , validationRequestor: validationRequestor
                                                                                    , validationReqTransactionId: validationReqTransactionId
                                                                                    , response: response
                                                                                    );

                        if (!response.ContainsKey("validationProcessAction"))
                        {
                            if (!success)
                                response["validationProcessAction"] = "ValidationRequestStatusUpdate-RouteToQueue";
                            else
                                response["validationProcessAction"] = "terminate";
                        }
                    }
                }
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in findValidationRequestAccountMatches(...). Exception message: {Environment.NewLine}{e.Message}"
                                            + $"{Environment.NewLine}validationReqTransactionId: {validationReqTransactionId}"
                                                );
                response["validationProcessAction"] = "ValidationRequestStatusUpdate-RouteToQueue";

            }
            #endregion
            return response;
        }
        public static async Task<string> getCtpOrgOnyxExternalReference(string ctpOrgId)
        {
            try
            {
                IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);

                string externalReferenceOnyx = (string)ctpOrgEntity["externalReferenceOnyx"];

                return externalReferenceOnyx;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in getCtpOrgOnyxExternalReference(...). Exception message: {Environment.NewLine}{e.Message}");
                return null;
            }
        }
        public static async Task<bool> addOnyxExternalReferenceToCtpOrg(string tsOrgId, IDictionary<string, Object> ctpOrgEntity)
        {
            try
            {
                string ctpOrgId = (string)ctpOrgEntity["ctpOrgId"];

                Dictionary<string, string> ctpObjectIds = new Dictionary<string, string>();
                ctpObjectIds["rootId"] = (string)ctpOrgEntity["rootId"];
                ctpObjectIds["associateId"] = (string)ctpOrgEntity["associateId"];
                ctpObjectIds["tsOrgId"] = tsOrgId;

                Dictionary<string, string> ctpNewObjectIds = await getCTPNewRootInstance();
                ctpObjectIds["instanceId"] = ctpNewObjectIds["instanceId"];
                ctpObjectIds["crc"] = ctpNewObjectIds["crc"];

                List<dynamic> io = new List<dynamic>();
                dynamic newOnyxExternalReference = await getCTPOnyxExternalReferenceObject(ctpObjectIds);
                io.Add(newOnyxExternalReference);

                IDictionary<string, Object> ctpOrgObjectExpando = JsonConvert.DeserializeObject<ExpandoObject>((string)ctpOrgEntity["orgObjectText"]) as IDictionary<string, Object>;
                ctpOrgObjectExpando.Remove("io");
                ctpOrgObjectExpando.Add("io", io);

                string ctpNewOrgObjectText = JsonConvert.SerializeObject(ctpOrgObjectExpando, Newtonsoft.Json.Formatting.None);


                string ctpUrl = "https://objects-sysrw.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                //"61695af7-1652-4b08-b786-192de1884f61";
                //"0948d6b4-9276-45e7-a345-7de662c3fad5";
                string endPointPath = $"/services/object/v_001/{ctpSessionKey}/{ctpOrgId}/any/all";
                dynamic ctpOrgUpdateResponse = await makeHttpPostCall(ctpNewOrgObjectText
                                                                , ctpUrl, endPointPath
                                                                , queryParams: null
                                                                );


                return true;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in addOnyxExternalReferenceToCtpOrg(). Exception message: {Environment.NewLine}{e.Message}");
                return false;
            }

        }
        public static async Task<dynamic> getCTPOnyxExternalReferenceObject(Dictionary<string, string> ctpObjectIds)
        {
            try
            {
                Int64 externalObjectTimeStamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                string touchedDate = DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'");

                string onyxExternalReferenceTemplate = @"
                {
			        ""instance"": {
				        ""active"": false,
				        ""touched_date"": """",
				        ""state"": true,
				        ""typeSource"": ""onyx""
			        },
			        ""version"": 1,
			        ""timestamp"": 1,
			        ""public"": true,
			        ""protected"": false,
			        ""type"": ""orgonyx"",
			        ""signature"": ""ExternalReferenceObject_001"",
			        ""state"": true,
			        ""crc"": """",
			        ""instanceId"": """",
			        ""rootId"": """",
			        ""associateId"": """",
			        ""typeValue"": """",
			        ""statustimestamp"": 0
		                        }
                ";

                dynamic onyxExternalReferenceObject = JsonConvert.DeserializeObject(onyxExternalReferenceTemplate);

                onyxExternalReferenceObject.instance.touched_date = touchedDate;
                onyxExternalReferenceObject.timestamp = externalObjectTimeStamp;
                onyxExternalReferenceObject.crc = ctpObjectIds["crc"];
                onyxExternalReferenceObject.instanceId = ctpObjectIds["instanceId"];
                onyxExternalReferenceObject.rootId = ctpObjectIds["rootId"];
                onyxExternalReferenceObject.associateId = ctpObjectIds["associateId"];
                onyxExternalReferenceObject.typeValue = ctpObjectIds["tsOrgId"];

                return onyxExternalReferenceObject;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in getCTPOnyxExternalReferenceObject(). Exception message: {Environment.NewLine}{e.Message}");
                return null;
            }

        }

        public static async Task<Dictionary<string, string>> getCTPNewRootInstance()
        {
            Dictionary<string, string> ctpObjectIds = new Dictionary<string, string>();
            try
            {
                string ctpUrl = "https://resource.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                //ffa07369-c658-41c5-8322-4cf76f2142ea
                string endPointPath = $"/services/resource/v_001/{ctpSessionKey}/models/generate/processobject";

                dynamic responseJson = await makeHttpGetCall(
                                                                ctpUrl, endPointPath
                                                                , queryParams: null
                                                                );


                dynamic ctpProcessObject = responseJson.returnStatus?.data;


                dynamic revisionObject = ((JArray)ctpProcessObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "RevisionObject_001"
                                                                                                    )?.FirstOrDefault();

                string crc = revisionObject?.crc;
                string instanceId = revisionObject?.instanceId;
                string rootId = revisionObject?.rootId;
                string associateId = revisionObject?.associateId;

                ctpObjectIds["crc"] = crc;
                ctpObjectIds["instanceId"] = instanceId;
                ctpObjectIds["rootId"] = rootId;
                ctpObjectIds["associateId"] = associateId;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in getCTPNewRootInstance(). Exception message: {Environment.NewLine}{e.Message}");
            }
            return ctpObjectIds;
        }

        public static async Task<IDictionary<string, Object>> getCTPOrgObjects(string ctpOrgId)
        {
            try
            {
                string ctpUrl = "https://objects-sysrw.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                //"61695af7-1652-4b08-b786-192de1884f61";
                //"0948d6b4-9276-45e7-a345-7de662c3fad5";
                //"61695af7-1652-4b08-b786-192de1884f61";
                string endPointPath = $"/services/object/v_001/{ctpSessionKey}/{ctpOrgId}/any/all";

                dynamic responseJson = await makeHttpGetCall(
                                                            ctpUrl, endPointPath
                                                            , queryParams: null
                                                            );


                if (responseJson == null)
                {
                    string error = "At getCTPOrgObjects(). Call to get ctpOrgObject returned null";
                    DynamicsInterface.writeToLog(error);
                    return null;
                }

                dynamic ctpOrgObject = responseJson.returnStatus?.data;


                IDictionary<string, Object> ctpOrgEntity = new ExpandoObject() as IDictionary<string, Object>;

                string ctpOrgObjectText = JsonConvert.SerializeObject(ctpOrgObject, Newtonsoft.Json.Formatting.Indented);

                int ctpOrgObjectLength = ctpOrgObjectText.Length;
                if (DynamicsInterface.VerboseLog)
                    DynamicsInterface.writeToLog($"getCTPOrgObjects()"
                                                    + $"{Environment.NewLine}ctpOrgObject length: {ctpOrgObjectLength}"
                                                    );

                ctpOrgEntity["orgObjectText"] = ctpOrgObjectText;
                ctpOrgEntity["ctpOrgId"] = ctpOrgId;
                ctpOrgEntity["sourceTytpe"] = cleanCtpString((string)ctpOrgObject?.type) ?? "";
                ctpOrgEntity["validationRequestTransactionId"] = cleanCtpString((string)ctpOrgObject?.externalTransactionId) ?? "";
                ctpOrgEntity["ctpOrgObjectDate"] = convertTimestampToDatetime((long)(ctpOrgObject?.timestamp ?? 0));

                dynamic operatingBudget = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "FinancialObject_001" && instance.type == "operatingBudget")?.FirstOrDefault();
                ctpOrgEntity["operatingBudget"] = cleanCtpString((string)operatingBudget?.typeValue);

                dynamic phoneMain = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "PhoneObject_001" && instance.type == "main")?.FirstOrDefault();
                ctpOrgEntity["phoneMain"] = cleanCtpString((string)phoneMain?.typeValue);

                dynamic locationMain = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "LocationObject_001" && instance.type == "main")?.FirstOrDefault();
                IDictionary<string, Object> addressMain = new ExpandoObject() as IDictionary<string, Object>;
                addressMain["address"] = cleanCtpString((string)locationMain?.instance?.address);
                addressMain["addressExt"] = cleanCtpString((string)locationMain?.instance?.addressExt);
                addressMain["city"] = cleanCtpString((string)locationMain?.instance?.city);
                addressMain["countryId"] = cleanCtpString((string)locationMain?.instance?.countryId);
                addressMain["postalCode"] = cleanCtpString((string)locationMain?.instance?.postalCode);

                string countryId = cleanCtpString((string)locationMain?.instance?.countryId);
                string stateRegion = cleanCtpString((string)locationMain?.instance?.stateRegion ?? "");
                stateRegion = regexReplace(@"^" + countryId + "-", stateRegion, "");
                addressMain["stateRegion"] = stateRegion;

                ctpOrgEntity.Add("addressMain", addressMain);

                dynamic locationLegal = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "LocationObject_001" && instance.type == "legal")?.FirstOrDefault();
                IDictionary<string, Object> addressLegal = new ExpandoObject() as IDictionary<string, Object>;
                addressLegal["address"] = cleanCtpString((string)locationLegal?.instance?.address);
                addressLegal["addressExt"] = cleanCtpString((string)locationLegal?.instance?.addressExt);
                addressLegal["city"] = cleanCtpString((string)locationLegal?.instance?.city);
                addressLegal["countryId"] = cleanCtpString((string)locationLegal?.instance?.countryId);
                addressLegal["postalCode"] = cleanCtpString((string)locationLegal?.instance?.postalCode);

                countryId = cleanCtpString((string)locationLegal?.instance?.countryId);
                stateRegion = cleanCtpString((string)locationLegal?.instance?.stateRegion ?? "");
                stateRegion = regexReplace(@"^" + countryId + "-", stateRegion, "");
                addressLegal["stateRegion"] = stateRegion;

                ctpOrgEntity.Add("addressLegal", addressLegal);

                dynamic locationOperations = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "LocationObject_001" && instance.type == "operations")?.FirstOrDefault();
                IDictionary<string, Object> addressOperations = new ExpandoObject() as IDictionary<string, Object>;
                addressOperations["address"] = cleanCtpString((string)locationOperations?.instance?.address);
                addressOperations["addressExt"] = cleanCtpString((string)locationOperations?.instance?.addressExt);
                addressOperations["city"] = cleanCtpString((string)locationOperations?.instance?.city);
                addressOperations["countryId"] = cleanCtpString((string)locationOperations?.instance?.countryId);
                addressOperations["postalCode"] = cleanCtpString((string)locationOperations?.instance?.postalCode);

                countryId = cleanCtpString((string)locationOperations?.instance?.countryId);
                stateRegion = cleanCtpString((string)locationOperations?.instance?.stateRegion ?? "");
                stateRegion = regexReplace(@"^" + countryId + "-", stateRegion, "");
                addressOperations["stateRegion"] = stateRegion;

                ctpOrgEntity.Add("addressOperations", addressOperations);



                dynamic purposeActivityCode = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "PurposeObject_001" && instance.type == "subActivity")?.FirstOrDefault();
                ctpOrgEntity["purposeActivityCode"] = cleanCtpString((string)purposeActivityCode?.typeValue);

                dynamic languageEnglish = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "LanguageObject_001")?.FirstOrDefault();// && instance.type == "english"
                ctpOrgEntity["language"] = cleanCtpString((string)languageEnglish?.type);
                ctpOrgEntity["languageCode"] = cleanCtpString((string)languageEnglish?.typeValue);

                dynamic descriptiveObjectMission = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "DescriptiveTextObject_001" && instance.type == "missionStatement"
                                                                                                                      && !string.IsNullOrWhiteSpace((string)instance.instance?.rawText) && (string)instance.instance?.rawText != "nil"
                                                                                                                      )?.FirstOrDefault();
                ctpOrgEntity["descriptiveObjectMission"] = cleanCtpString((string)descriptiveObjectMission?.instance?.rawText);

                dynamic webSiteMain = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "WebsiteObject_001" && instance.type == "main")?.FirstOrDefault();
                ctpOrgEntity["webSiteMain"] = cleanCtpString((string)webSiteMain?.typeValue);


                /******/


                dynamic externalReferenceOnyx = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "ExternalReferenceObject_001" && instance.type == "orgonyx")?.FirstOrDefault();
                ctpOrgEntity["externalReferenceObjectOnyx"] = externalReferenceOnyx;
                ctpOrgEntity["externalReferenceOnyx"] = cleanCtpString((string)externalReferenceOnyx?.typeValue);


                List<dynamic> onyxExternalReferences = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "ExternalReferenceObject_001" && instance.type == "orgonyx")?.ToList() ?? new List<dynamic>();
                ctpOrgEntity["onyxExternalReferences"] = onyxExternalReferences;


                /******/



                dynamic externalReferenceTransaction = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "ExternalReferenceObject_001" && instance.type == "transaction")?.FirstOrDefault();
                ctpOrgEntity["externalReferenceTransactionId"] = cleanCtpString((string)externalReferenceTransaction?.typeValue);

                dynamic transactionIdExternalReferences = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "ExternalReferenceObject_001" && instance.type == "transaction")?.ToList() ?? new List<dynamic>();
                ctpOrgEntity["transactionIdExternalReferences"] = transactionIdExternalReferences;





                dynamic tracker = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "TrackerObject_001" && instance.type == "tracker")?.FirstOrDefault();
                ctpOrgEntity["trackerObjectValue"] = cleanCtpString((string)tracker?.typeValue);
                ctpOrgEntity["trackerObjectDate"] = convertTimestampToDatetime((long)(tracker?.timestamp ?? 0));

                dynamic entityOrganization = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "EntityObject_001" && instance.type == "Organization")?.FirstOrDefault();
                ctpOrgEntity["entityObjectOrganization"] = entityOrganization;
                ctpOrgEntity["entityOrganization"] = cleanCtpString((string)entityOrganization?.typeValue) ?? "";
                ctpOrgEntity["rootId"] = (string)entityOrganization?.rootId;
                ctpOrgEntity["entityOrganizationInstanceId"] = (string)entityOrganization?.instanceId;
                ctpOrgEntity["associateId"] = (string)entityOrganization?.associateId;
                ctpOrgEntity["entityOrganizationTimestamp"] = (string)entityOrganization?.timestamp;
                ctpOrgEntity["entityOrganizationDate"] = convertTimestampToDatetime((long)(entityOrganization?.timestamp ?? 0));

                dynamic emailMain = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "EmailObject_001" && instance.type == "main" && instance.associateId == ctpOrgEntity["entityOrganizationInstanceId"])?.FirstOrDefault();
                ctpOrgEntity["emailMain"] = cleanCtpString((string)emailMain?.typeValue);

                dynamic domainObject = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "DomainObject_001" && instance.type == "domain")?.FirstOrDefault();
                ctpOrgEntity["orgDomain"] = cleanCtpString((string)domainObject?.typeValue);

                dynamic legalIdentifiers = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "LegalIdentifierObject_001").FirstOrDefault();// "type": "EIN" // && instance.type == "");
                ctpOrgEntity["legalIdentifier"] = cleanCtpString((string)legalIdentifiers?.typeValue);
                ctpOrgEntity["legalIdentifierType"] = cleanCtpString((string)legalIdentifiers?.type);

                dynamic statusOrg = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "StatusObject_001" && instance.type == "main")?.FirstOrDefault();
                string qualStatusOrg = cleanCtpString((string)statusOrg?.typeValue ?? "");
                DateTime statusDate = (long)(statusOrg?.statustimestamp ?? 0) == 0 ? convertTimestampToDatetime((long)(statusOrg?.timestamp ?? 0)) : convertTimestampToDatetime((long)(statusOrg?.statustimestamp ?? 0));
                string yearsQualifiedStatusIsValid = cleanCtpString((string)statusOrg?.instance?.vinfo?.years);


                ctpOrgEntity["statusOrg"] = qualStatusOrg;
                ctpOrgEntity["statusDate"] = statusDate;
                ctpOrgEntity["yearsQualifiedStatusIsValid"] = yearsQualifiedStatusIsValid;

                int validityPeriodYears = 0;
                if (!string.IsNullOrEmpty(yearsQualifiedStatusIsValid))
                    int.TryParse(yearsQualifiedStatusIsValid, out validityPeriodYears);

                DateTime expirationDate = qualStatusOrg.ToLower() != "qualified" ? DateTime.MinValue : statusDate.AddYears(validityPeriodYears).Date.AddMonths(-2);
                string statusOrgWithExpirationCheck = qualStatusOrg.ToLower() != "qualified" ? "Disqualified" : //Todo: need to check transaction status to see if disqualified
                                                                                            (expirationDate > DateTime.UtcNow.Date ? "Qualified" : "Expired");
                //(expirationDate > DateTime.UtcNow.Date.AddMonths(3) ? "Qualified" : "Expired");


                ctpOrgEntity["expirationDate"] = expirationDate;
                ctpOrgEntity["statusOrgWithExpirationCheck"] = statusOrgWithExpirationCheck;


                dynamic orgLegalName = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "NameObject_001" && instance.type == "legalName")?.FirstOrDefault();
                ctpOrgEntity["orgLegalName"] = cleanCtpString((string)orgLegalName?.typeValue);


                List<dynamic> entityAgents = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "EntityObject_001" && instance.type == "Person" && instance.typeValue == "Agent")?.ToList() ?? new List<dynamic>();

                List<dynamic> ctpOrgAgents = new List<dynamic>();


                foreach (dynamic entityAgent in entityAgents)
                {
                    IDictionary<string, Object> orgAgent = new ExpandoObject() as IDictionary<string, Object>;

                    orgAgent["agentInstanceId"] = cleanCtpString((string)entityAgent?.instanceId);

                    dynamic agentFirstName = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "NameObject_001" && instance.type == "firstName" && instance.associateId == orgAgent["agentInstanceId"])?.FirstOrDefault();
                    orgAgent["firstName"] = cleanCtpString((string)agentFirstName?.typeValue);

                    dynamic agentLastName = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "NameObject_001" && instance.type == "lastName" && instance.associateId == orgAgent["agentInstanceId"])?.FirstOrDefault();
                    orgAgent["lastName"] = cleanCtpString((string)agentLastName?.typeValue);

                    dynamic agentEmail = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "EmailObject_001" && instance.type == "main" && instance.associateId == orgAgent["agentInstanceId"])?.FirstOrDefault();
                    orgAgent["email"] = cleanCtpString((string)agentEmail?.typeValue);

                    dynamic agentStatus = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "StatusObject_001" && instance.type == "agent" && instance.associateId == orgAgent["agentInstanceId"])?.FirstOrDefault();
                    orgAgent["status"] = cleanCtpString((string)agentStatus?.typeValue);
                    orgAgent["agentStatusObject"] = agentStatus;

                    ctpOrgAgents.Add(orgAgent);
                }

                ctpOrgEntity.Add("orgAgents", ctpOrgAgents);


                string ctpOrgEntityText = JsonConvert.SerializeObject(ctpOrgEntity, Newtonsoft.Json.Formatting.Indented);

                dynamic ctpOrgEntityJson = JsonConvert.DeserializeObject(ctpOrgEntityText);

                return ctpOrgEntity;

            }
            catch (Exception e)
            {
                string error = $"Error in getCTPOrgObjects(). Exception message: {Environment.NewLine}{e.Message}{Environment.NewLine}ctpOrgId: {ctpOrgId}";
                DynamicsInterface.writeToLog(error);
            }

            return null;
        }

        public static async Task<int> getOptionSetValue(string optionSetName, string optionLabel
                                                        , DataverseClientLib.ServiceClient dataverseClient = null)
        {
            int optionValue = 0;
            try
            {
                dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;

                RetrieveOptionSetRequest retrieveOptionSetRequest = new RetrieveOptionSetRequest
                {
                    Name = optionSetName
                };

                RetrieveOptionSetResponse retrieveOptionSetResponse = (RetrieveOptionSetResponse)await dataverseClient.ExecuteAsync(retrieveOptionSetRequest);
                OptionSetMetadata retrievedOptionSetMetadata = (OptionSetMetadata)retrieveOptionSetResponse.OptionSetMetadata;
                OptionMetadataCollection options = retrievedOptionSetMetadata.Options;
                OptionMetadata option = options.ToList().Find(item => item.Label.UserLocalizedLabel.Label.ToLower() == optionLabel.ToLower());

                if (option != null)
                    optionValue = option.Value.Value;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in getOptionSetValue(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}optionSetName: {optionSetName}; optionLabel: {optionLabel}"
                                                );
            }

            return optionValue;
        }

        public static dynamic getOptionSetValue(string optionSetName, string optionLabel, bool addIfNotFound = true)
        {
            dynamic optionValueResponse = new JObject();
            try
            {
                RetrieveOptionSetRequest retrieveOptionSetRequest = new RetrieveOptionSetRequest
                {
                    Name = optionSetName
                };

                RetrieveOptionSetResponse retrieveOptionSetResponse = (RetrieveOptionSetResponse)DynamicsInterface.DataverseClient.Execute(retrieveOptionSetRequest);
                OptionSetMetadata retrievedOptionSetMetadata = (OptionSetMetadata)retrieveOptionSetResponse.OptionSetMetadata;
                OptionMetadataCollection options = retrievedOptionSetMetadata.Options;
                OptionMetadata option = options.ToList().Find(item => item.Label.UserLocalizedLabel.Label.ToLower() == optionLabel.ToLower());

                if (option != null)
                {
                    optionValueResponse.optionValue = (int)option.Value.Value;
                    optionValueResponse.newValue = false;
                    return optionValueResponse;
                }

                if (addIfNotFound)
                {
                    InsertOptionValueRequest request = new InsertOptionValueRequest();
                    request.OptionSetName = optionSetName;
                    request.Label = new Label(optionLabel, 1033);

                    InsertOptionValueResponse response = (InsertOptionValueResponse)DynamicsInterface.DataverseClient.Execute(request);

                    optionValueResponse.optionValue = response.NewOptionValue;
                    optionValueResponse.newValue = true;
                    return optionValueResponse;
                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in getOptionSetValue(addIfNotFound). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}optionSetName: {optionSetName}; optionLabel: {optionLabel}"
                                                );
            }
            return optionValueResponse;
        }


        public static async Task<bool> existsOptionSetValue(string optionSetName, int value)
        {
            try
            {
                RetrieveOptionSetRequest retrieveOptionSetRequest = new RetrieveOptionSetRequest
                {
                    Name = optionSetName
                };

                RetrieveOptionSetResponse retrieveOptionSetResponse = (RetrieveOptionSetResponse)await DynamicsInterface.DataverseClient.ExecuteAsync(retrieveOptionSetRequest);
                OptionSetMetadata retrievedOptionSetMetadata = (OptionSetMetadata)retrieveOptionSetResponse.OptionSetMetadata;
                OptionMetadataCollection options = retrievedOptionSetMetadata.Options;
                OptionMetadata option = options.ToList().Find(item => item.Value == value);

                return option != null;
            }
            catch (Exception e)
            {
                string error = $"Error in existsOptionSetValue(). Exception message:{Environment.NewLine}{e.Message}{Environment.NewLine}optionSetName: {optionSetName}; Option Value: {value}"
                                ;
                DynamicsInterface.writeToLog(error
                                                );
                return false;
            }
        }
        public static async Task<Guid> createOrgQualification(Guid accountId, Guid qualCodeId, int qualStatusCode, DateTime qualStatusDateUTC
                                                                            , int tsOrgId, string qualCode
                                                                            , DataverseClientLib.ServiceClient dataverseClient = null)
        {
            Guid orgQualId = Guid.Empty;
            try
            {
                dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;

                Entity orgQualification = new Entity("ts_organizationqualification");



                orgQualification["ts_qualificationstatus"] = new OptionSetValue(qualStatusCode); //choice ts_qualstatus 
                orgQualification["ts_qualificationstatusdate"] = qualStatusDateUTC;
                orgQualification["ts_accountid"] = new EntityReference("account", accountId);
                orgQualification["ts_qualificationcodeid"] = new EntityReference("new_qualificationcode", qualCodeId);
                orgQualification["ts_name"] = tsOrgId.ToString() + " - " + qualCode;



                orgQualId = await dataverseClient.CreateAsync(orgQualification);


                Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");

                orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQualId);
                orgQualHistory["ts_qualificationstatusaction"] = new OptionSetValue(qualStatusCode);
                orgQualHistory["ts_qualificationactiondate"] = qualStatusDateUTC;
                orgQualHistory["ts_name"] = orgQualId.ToString() + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                Guid orgQualHistoryId = await dataverseClient.CreateAsync(orgQualHistory);
            }
            catch (Exception e)
            {
                string error = $"Error in createOrgQualification(...). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}accountId: {accountId.ToString()}; qualCodeId: {qualCodeId.ToString()}{Environment.NewLine}tsOrgId: {tsOrgId.ToString()}; qualCode: {qualCode}"
                                + $"; qualStatusCode: {qualStatusCode.ToString()}"
                                ;
                DynamicsInterface.writeToLog(error);

                return Guid.Empty;
            }

            return orgQualId;
        }
        public static async Task<Entity> createOrgForCtp(IDictionary<string, Object> ctpOrgEntity
                                                            , string tsOrgId
                                                            , string validationReqTransactionId
                                                            , DataverseClientLib.ServiceClient dataverseClient = null
                                                            )
        {
            try
            {
                dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;

                #region New Account Entity
                Entity account = new Entity("account");
                #endregion

                string sourceTytpe = (string)ctpOrgEntity["sourceTytpe"] ?? "";

                Dictionary<string, string> sourceTypeMappings = new Dictionary<string, string>()
                {
                    { "msft", "Microsoft" }                    
                };

                sourceTytpe = sourceTypeMappings.ContainsKey(sourceTytpe.ToLower()) ? sourceTypeMappings[sourceTytpe.ToLower()] : sourceTytpe;

                int orgSource = await getOptionSetValue("new_tsgsource", sourceTytpe
                                                                , dataverseClient
                                                                 );

                #region Name, Org Designation, Mission Statement...
                if (tsOrgId != null)
                    account["accountnumber"] = tsOrgId;

                account["name"] = ctpOrgEntity["orgLegalName"];
                account["customertypecode"] = new OptionSetValue(3); //3 - Customer
                account["new_source"] = orgSource == 0 ? new OptionSetValue(101892) : new OptionSetValue(orgSource); //TSS Web Site 101892

                account["ts_ctporgid"] = ctpOrgEntity["ctpOrgId"];

                IDictionary<string, Object> addressMain = (IDictionary<string, Object>)ctpOrgEntity["addressMain"];


                string countryCode = (string)addressMain["countryId"] ?? "";
                string entityOrganization = (string)ctpOrgEntity["entityOrganization"] ?? "";
                string qualCode = (countryCode.ToLower() == "gb" ? "uk" : countryCode.ToLower())
                                                                                + (entityOrganization.ToLower() == "lib" ? "-lib" : "-npo");

                if (countryCode.ToLower() == "ca" && entityOrganization.ToLower() == "npo")
                    qualCode = "ca-cha";

                List<string> usAndTerritoriesCodes = new string[] { "AS", "FM", "GU", "MP", "PR", "UM", "US", "VI" }.ToList();

                if (
                    usAndTerritoriesCodes.Exists(code => code.ToLower() == countryCode.ToLower()) && entityOrganization.ToLower() == "lib"
                    )
                    qualCode += "-501c3";


                QueryExpression queryQualCode = new QueryExpression("new_qualificationcode");
                queryQualCode.ColumnSet = new ColumnSet(true);
                queryQualCode.Criteria.AddCondition("new_qualcode", ConditionOperator.Equal, qualCode);

                EntityCollection qualCodeCollection = await dataverseClient.RetrieveMultipleAsync(queryQualCode);

                if (qualCodeCollection.Entities.Count == 0)
                {
                    string error = "Error in createOrgFromCase(). No Qualification Code found for qualCode: " + qualCode;
                    DynamicsInterface.writeToLog(error);
                    return null;
                }
                Entity qualCodeEntity = qualCodeCollection.Entities.First();
                string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");

                account["new_orgdesignation"] = qualCodeEntity.ToEntityReference();

                account["ts_missionstatement"] = (string)ctpOrgEntity["descriptiveObjectMission"];

                account["telephone1"] = (string)ctpOrgEntity["phoneMain"];
                #endregion


                #region Address
                account["address1_country"] = (string)addressMain["countryId"];
                account["address1_stateorprovince"] = (string)addressMain["stateRegion"];
                account["address1_line1"] = (string)addressMain["address"];
                account["address1_line2"] = (string)addressMain["addressExt"];
                account["address1_city"] = (string)addressMain["city"];
                account["address1_postalcode"] = (string)addressMain["postalCode"];


                #region Country And State Hierarchy Mapping
                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, (string)addressMain["countryId"]);
                EntityCollection fieldMapCollection = await dataverseClient.RetrieveMultipleAsync(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    account["ts_countrydesc"] = new OptionSetValue(countryOptionValue);
                }


                queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, (string)addressMain["countryId"]);
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, (string)addressMain["stateRegion"]);
                fieldMapCollection = await dataverseClient.RetrieveMultipleAsync(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    account["ts_stateprovdesc"] = new OptionSetValue(countryOptionValue);
                }
                #endregion
                #endregion


                #region Email, Url, Budget, Legal Identifier & Activity Code

                account["emailaddress1"] = (string)ctpOrgEntity["emailMain"];
                account["websiteurl"] = (string)ctpOrgEntity["webSiteMain"];
                string budget = (string)ctpOrgEntity["operatingBudget"];
                account["new_budget"] = budget;


                account["new_legalidentifier"] = ctpOrgEntity["legalIdentifier"];


                QueryExpression queryEntity = new QueryExpression("new_activitycodes");
                queryEntity.Criteria.AddCondition("new_activitycode", ConditionOperator.Equal, (string)ctpOrgEntity["purposeActivityCode"]);
                EntityCollection entityCollection = await dataverseClient.RetrieveMultipleAsync(queryEntity);

                if (entityCollection.Entities.Count > 0)
                    account["new_activitycode"] = new EntityReference("new_activitycodes", entityCollection.Entities.First().Id);

                #endregion

                OptionSetValueCollection accountDirectivesCollection = new OptionSetValueCollection();
                //accountDirectivesCollection.Add(new OptionSetValue(1)); //1 - ExcludeDataIntegration
                accountDirectivesCollection.Add(new OptionSetValue(2)); //2 - ExcludePostAccountCreateAutoLogic
                accountDirectivesCollection.Add(new OptionSetValue(3)); //3 - BypassPreAccountValidation
                account["ts_accountdirectives"] = accountDirectivesCollection;

                OptionSetValueCollection accountTypeCollection = new OptionSetValueCollection();
                accountTypeCollection.Add(new OptionSetValue(18)); // 18 - Validation Services
                account["ts_accounttype"] = accountTypeCollection;

                account["overriddencreatedon"] = (DateTime)ctpOrgEntity["entityOrganizationDate"];

                #region Account Create & Retrieve
                Guid accountId = await dataverseClient.CreateAsync(account);

                if (accountId == Guid.Empty)
                {
                    string error = "Error in createOrgFromCase(). Account was not created";

                    DynamicsInterface.writeToLog(error);
                    return null;
                }

                account = await dataverseClient.RetrieveAsync(account.LogicalName, accountId, new ColumnSet(true));
                tsOrgId = account.GetAttributeValue<string>("accountnumber");
                #endregion

                int qualStatusCode = int.Parse(QualStatus[((string)ctpOrgEntity["statusOrgWithExpirationCheck"]).ToLower()]);
                DateTime statusDate = QualStatus[qualStatusCode.ToString()] == "expired" ? (DateTime)ctpOrgEntity["expirationDate"] : (DateTime)ctpOrgEntity["statusDate"];
                await createOrgQualification(accountId, qualCodeEntity.Id, qualStatusCode, statusDate, int.Parse(tsOrgId)
                                                                , qualCode
                                                                , dataverseClient
                                                                 );

                int tsCaseStatusCode = QualStatus[qualStatusCode.ToString()] == "qualified" ? 102056 : 102057;//102056 - OQ - Qualified; 102057 - OQ - Disqualified; 102050 - OQ - Not Started


                QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryMapping.ColumnSet = new ColumnSet("ts_value", "ts_valuecode");
                queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "ts_casestatus");
                queryMapping.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, "Organization Qualification");
                queryMapping.Criteria.AddCondition("ts_mappedfieldvalue", ConditionOperator.Equal, (string)ctpOrgEntity["statusOrgWithExpirationCheck"]);
                EntityCollection mappingCollection = await dataverseClient.RetrieveMultipleAsync(queryMapping);

                if (mappingCollection.Entities.Count > 0)
                {
                    Entity fieldMapping = mappingCollection.Entities.First();
                    string tsCaseStatus = fieldMapping.GetAttributeValue<string>("ts_value"); //Case status
                    tsCaseStatusCode = fieldMapping.GetAttributeValue<int>("ts_valuecode"); //Case status option value
                }

                DateTime accountCreatedOn = account.GetAttributeValue<DateTime>("createdon");

                Dictionary<string, object> extraCaseFields = new Dictionary<string, object>();
                extraCaseFields.Add("overriddencreatedon", accountCreatedOn);
                if (QualStatus[qualStatusCode.ToString()] == "qualified" || QualStatus[qualStatusCode.ToString()] == "expired")
                    extraCaseFields.Add("ts_expirationdate", (DateTime)ctpOrgEntity["expirationDate"]);


                Guid caseId = await createCaseGeneric(title: $"{qualCode} - {qualName} - TSOrgId: {tsOrgId}"
                                                                            , caseTypeCode: 2
                                                                            , type: 101996
                                                                            , customerRef: account.ToEntityReference()
                                                                            , caseStatus: tsCaseStatusCode
                                                                            , qualCodeId: qualCodeEntity.Id
                                                                            , extraCaseFields: extraCaseFields
                                                                            , dataverseClient
                                                                            );


                if (QualStatus[qualStatusCode.ToString()] != "qualified") //restart the validation process, even for disqualified orgs
                    caseId = await createCaseGeneric(title: $"{qualCode} - {qualName} - TSOrgId: {tsOrgId}"
                                                                            , caseTypeCode: 2
                                                                            , type: 101996
                                                                            , customerRef: account.ToEntityReference()
                                                                            , caseStatus: 102050
                                                                            , qualCodeId: qualCodeEntity.Id
                                                                            , extraCaseFields: null
                                                                            , dataverseClient
                                                                            );


                IDictionary<string, Object> addressLegal = (IDictionary<string, Object>)(ctpOrgEntity["addressLegal"] ?? ctpOrgEntity["addressMain"]);
                await addLegalAddress(accountId, addressLegal
                                                            , dataverseClient
                                        );

                processSystemNote(" --- ctpOrgObject --- ", (string)ctpOrgEntity["orgObjectText"], account.ToEntityReference());




                dynamic ctpOrgEntityJson = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(ctpOrgEntity));
                List<dynamic> orgAgents = ((JArray)ctpOrgEntityJson.orgAgents)?.ToList<dynamic>() ?? new List<dynamic>();

                foreach (dynamic orgAgent in orgAgents)
                {
                    OptionSetValue agentVerificationStatusOption = orgAgent.status == "Verified" ? new OptionSetValue(1) : new OptionSetValue(0); //0 - not verified; 1 - verified

                    Entity agentContact = await createAgentContact(orgAgent, validationReqTransactionId);

                    if (agentContact != null)
                        connectAgentToAccount(accountId, agentContact.Id, agentVerificationStatusOption, validationReqTransactionId);
                }


                //dynamic orgAgent = ((JArray)ctpOrgEntityJson.orgAgents)?.ToList<dynamic>()?.Where(agent => (string)agent.status == "Verified")?.FirstOrDefault();               

                //orgAgent = orgAgent ?? ((JArray)ctpOrgEntityJson.orgAgents)?.ToList<dynamic>()?.FirstOrDefault();
                //OptionSetValue agentVerificationStatusOption = orgAgent == null ? new OptionSetValue(0) : new OptionSetValue(1); //0 - not verified; 1 - verified
                //if (orgAgent != null)
                //{
                //    Entity agentContact = createAgentContact(orgAgent, validationReqTransactionId);

                //    if (agentContact != null)
                //        connectAgentToAccount(accountId, agentContact.Id, agentVerificationStatusOption, validationReqTransactionId);
                //}



                return account;
            }
            #region Catch
            catch (Exception e)
            {
                string error = $"Error in createOrgForCtp(...). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}transactionId: {validationReqTransactionId}; ctpOrgId: {ctpOrgEntity["ctpOrgId"]}; tsOrgId: {tsOrgId ?? ""}";
                DynamicsInterface.writeToLog(error);
                return null;
            }
            #endregion

        }

        public static async Task<Guid> createCaseGeneric(
                                        string title
                                        , int caseTypeCode
                                        , int? type
                                        , EntityReference customerRef
                                        , int? caseStatus
                                        , Guid? qualCodeId
                                        , Dictionary<string, object> extraCaseFields
                                        , DataverseClientLib.ServiceClient dataverseClient = null
                                     )
        {
            dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;

            Guid caseId = Guid.Empty;

            try
            {
                Entity caseEntity = new Entity("incident");


                caseEntity["title"] = title;
                caseEntity["casetypecode"] = new OptionSetValue(caseTypeCode);

                if (type != null)
                    caseEntity["ts_type"] = new OptionSetValue(type.Value);

                caseEntity["customerid"] = customerRef;

                if (caseStatus != null)
                    caseEntity["ts_casestatus"] = new OptionSetValue(caseStatus.Value);

                if (qualCodeId != null)
                    caseEntity["ts_qualificationcodeid"] = new EntityReference("new_qualificationcode", qualCodeId.Value);


                if (extraCaseFields != null)
                {
                    foreach (KeyValuePair<string, object> caseField in extraCaseFields)
                    {
                        assignValueToAttribute(caseEntity, caseField);
                    }
                }

                caseId = await dataverseClient.CreateAsync(caseEntity);

            }
            catch (Exception e)
            {
                string error = "Error in createCaseGeneric(...). Exception message: " + Environment.NewLine + e.Message
                                    + "accountId: " + customerRef.Id.ToString()
                                ;
                DynamicsInterface.writeToLog(error);

            }

            return caseId;
        }
        public static void assignValueToAttribute(Entity caseEntity, KeyValuePair<string, object> caseField)
        {
            try
            {

                string fieldName = caseField.Key;
                object fieldValue = caseField.Value;



                switch (fieldValue)
                {
                    case EntityReference entityRef:
                        caseEntity[fieldName] = entityRef;
                        break;

                    case OptionSetValue optionSet:
                        caseEntity[fieldName] = optionSet;
                        break;

                    case OptionSetValueCollection optionSetCollection:
                        caseEntity[fieldName] = optionSetCollection;
                        break;

                    case Money money:
                        caseEntity[fieldName] = money;
                        break;

                    case DateTime dateTime:
                        caseEntity[fieldName] = dateTime;
                        break;

                    case int intValue:
                        caseEntity[fieldName] = intValue;
                        break;

                    case decimal decimalValue:
                        caseEntity[fieldName] = decimalValue;
                        break;

                    case double doubleValue:
                        caseEntity[fieldName] = doubleValue;
                        break;

                    case bool boolValue:
                        caseEntity[fieldName] = boolValue;
                        break;

                    case Guid guidValue:
                        caseEntity[fieldName] = guidValue;
                        break;

                    case string stringValue:
                        caseEntity[fieldName] = stringValue;
                        break;

                    default:
                        //caseEntity[fieldName] = TryConvertValue(fieldName, fieldValue);
                        break;
                }




            }
            catch (Exception e)
            {
                string error = "Error in assignValueToAttribute(...). Exception message: " + Environment.NewLine + e.Message
                    ;
                DynamicsInterface.writeToLog(error);

            }

        }

        public static async Task addLegalAddress(Guid accountId, IDictionary<string, Object> addressLegal                                                                            
                                                                            , DataverseClientLib.ServiceClient dataverseClient = null)
        {
            try
            {
                dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;

                QueryExpression queryEntity = new QueryExpression("customeraddress");
                queryEntity.Criteria.AddCondition("parentid", ConditionOperator.Equal, accountId);
                queryEntity.Criteria.AddCondition("addresstypecode", ConditionOperator.Equal, 5);
                EntityCollection entityCollection = await dataverseClient.RetrieveMultipleAsync(queryEntity);

                Entity address = null;
                bool addressExists = false;
                if (entityCollection.Entities.Count > 0)
                {
                    address = entityCollection.Entities.First();
                    addressExists = true;
                }
                else
                {
                    address = new Entity("customeraddress");
                    address["addresstypecode"] = new OptionSetValue(5);
                    address["parentid"] = new EntityReference("account", accountId);
                    address["objecttypecode"] = 1;
                }


                address["line1"] = (string)addressLegal["address"];
                address["line2"] = (string)addressLegal["addressExt"];
                address["city"] = (string)addressLegal["city"];
                address["stateorprovince"] = (string)addressLegal["stateRegion"];
                address["country"] = (string)addressLegal["countryId"];

                string postalCode = (string)addressLegal["postalCode"];
                address["postalcode"] = postalCode.Length > 20 ? postalCode.Substring(0, 19) : postalCode;


                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, (string)addressLegal["countryId"]);
                EntityCollection fieldMapCollection = await dataverseClient.RetrieveMultipleAsync(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    address["ts_countrydesc"] = new OptionSetValue(countryOptionValue);
                }


                queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, (string)addressLegal["countryId"]);
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, (string)addressLegal["stateRegion"]);
                fieldMapCollection = await dataverseClient.RetrieveMultipleAsync(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    address["ts_stateprovdesc"] = new OptionSetValue(countryOptionValue);
                }


                if (addressExists)
                {
                    await dataverseClient.UpdateAsync(address);
                }
                else
                {
                    Guid addressId = await dataverseClient.CreateAsync(address);
                }
            }
            catch (Exception e)
            {
                string error = "Error in addLegalAddress(...). Exception message: " + Environment.NewLine + e.Message
               ;
                DynamicsInterface.writeToLog(error);
            }

        }

        public static dynamic getGenericValidationRequestStatusObject(string validationReqTransactionId)
        {

            try
            {
                DateTime ValidationRequestEnd = DateTime.Now;
                TimeSpan elapsedTime = ValidationRequestEnd - ValidationRequestEnd.AddMilliseconds(-1);


                IDictionary<string, System.Object> validationResponse = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;
                validationResponse.Add("elapsed", elapsedTime.TotalMilliseconds.ToString("#.00") + " milliseconds");
                validationResponse.Add("node", "na");
                validationResponse.Add("status_code", 200);

                IDictionary<string, System.Object> data = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;


                List<string> orgList = new List<string>();
                data.Add("OrgId", orgList);
                data.Add("Qualification", "Unknown");
                data.Add("TSOrgId", "");
                data.Add("ActivityCode", "");
                data.Add("AgentVerification", "");
                data.Add("Transaction Phase", "Open");
                data.Add("Transaction", "Open");
                data.Add("Timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
                data.Add("OrgTransactionId", validationReqTransactionId);
                data.Add("TransactionId", validationReqTransactionId);

                validationResponse.Add("data", data);

                string responseBodyId = $"{"ValidationRequest".ToLower()}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                string receiptToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(responseBodyId));

                validationResponse.Add("receipt", receiptToken);
                validationResponse.Add("id", receiptToken);
                validationResponse.Add("status", "request_processed");

                return (ExpandoObject)validationResponse;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in getGenericValidationRequestStatusObject(...). Exception message:{Environment.NewLine}{e.Message}"
                                              );
                return null;
            }


        }


        public static async Task sendValidationRequestStatusToRequestor(string validationReqTransactionId, Entity validationRequestCase, Entity validationRequestor)
        {
            try
            {

                string callBackUrl = validationRequestCase.GetAttributeValue<string>("ts_validationrequestcallbackurl");

                if (string.IsNullOrEmpty(callBackUrl))
                {
                    DynamicsInterface.writeToLog($"No callback URL provided for transaction id: {validationReqTransactionId}. Cannot send status update to requestor.");
                    return;
                }

                dynamic genericValidationResponse = getGenericValidationRequestStatusObject(validationReqTransactionId);

                IDictionary<string, System.Object> transactionIdRelatedValues = await getTransactionIdRelatedValues(validationReqTransactionId);

                bool isPngoPP = !string.IsNullOrEmpty(validationRequestor.GetAttributeValue<string>("ts_tspngocode"));


                if (
                    ((string)transactionIdRelatedValues["qualificationStatus"] == "Qualified" || (string)transactionIdRelatedValues["tsCaseStatusText"] == "OQ - Disqualified")
                    && transactionIdRelatedValues["ctpOrgId"] != null
                    )
                    ((List<string>)genericValidationResponse.data.OrgId).Add((string)transactionIdRelatedValues["ctpOrgId"]);


                genericValidationResponse.data.Qualification = transactionIdRelatedValues["qualificationStatus"];
                if (
                    ((string)transactionIdRelatedValues["qualificationStatus"] == "Qualified" || (string)transactionIdRelatedValues["tsCaseStatusText"] == "OQ - Disqualified")
                            && isPngoPP
                    )
                {
                    genericValidationResponse.data.TSOrgId = transactionIdRelatedValues["tsOrgId"];
                    genericValidationResponse.data.ActivityCode = transactionIdRelatedValues["activityCode"];
                    genericValidationResponse.data.AgentVerification = transactionIdRelatedValues["agentVerification"];
                }
                else
                {
                    ((IDictionary<string, System.Object>)genericValidationResponse.data).Remove("TSOrgId");
                    ((IDictionary<string, System.Object>)genericValidationResponse.data).Remove("ActivityCode");
                    ((IDictionary<string, System.Object>)genericValidationResponse.data).Remove("AgentVerification");
                }


                string transaction = (string)transactionIdRelatedValues["transactionStatus"] ?? "";
                string transactionPhase = (string)transactionIdRelatedValues["transactionPhase"] ?? "";

                genericValidationResponse.data.Transaction = transaction;


                if (!isPngoPP && transaction.ToLower() == "open")
                {
                    if (transactionPhase.ToLower().Contains("disposition"))
                        transactionPhase = "Started";
                    else if (transactionPhase.ToLower().Contains("response"))
                        transactionPhase = "In Process - Awaiting Response";
                    else
                        transactionPhase = "In Process";
                }
                else if (!isPngoPP && transaction.ToLower() == "closed")
                {
                    transactionPhase = "Completed";

                    if ((string)transactionIdRelatedValues["caseResolution"] == "Fraud")
                        transactionPhase += " - Fraud Detected";
                }

                ((IDictionary<string, System.Object>)genericValidationResponse.data)["Transaction Phase"] = transactionPhase;

                IDictionary<string, System.Object> returnStatus = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;

                returnStatus.Add("returnStatus", genericValidationResponse);
                string returnStatusText = JsonConvert.SerializeObject(returnStatus, Newtonsoft.Json.Formatting.Indented);


                dynamic response =  await makeHttpPostCall(requestJson: returnStatusText
                                                           , baseUrl: callBackUrl, endPointPath: ""
                                                            , queryParams: null
                                                            );

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in sendValidationRequestStatusToRequestor(...). Exception message:{Environment.NewLine}{e.Message}"
                                            );
            }


        }

        public static async Task<Entity> getValidationRequestAccount(Entity validationRequestCase)
        {
            try
            {

                Guid? qualCaseId = validationRequestCase.GetAttributeValue<EntityReference>("parentcaseid")?.Id;

                Entity qualCase = qualCaseId == null ? null : await DynamicsInterface.DataverseClient.RetrieveAsync("incident", qualCaseId.Value, new ColumnSet(true));

                Guid? orgAccountId = qualCase?.GetAttributeValue<EntityReference>("customerid")?.Id;
                Entity orgAccount = orgAccountId == null ? null : await DynamicsInterface.DataverseClient.RetrieveAsync("account", orgAccountId.Value, new ColumnSet(true));

                return orgAccount;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getValidationRequestAccount(Entity validationRequestCase). Exception message: " + Environment.NewLine + e.Message
                                                    + Environment.NewLine + "validationReqTransactionId: " + validationRequestCase.GetAttributeValue<string>("ts_validationrequesttransactionid")
                                                    );
                return null;
            }
        }
        public static async Task<IDictionary<string, System.Object>> getTransactionIdRelatedValues(string validationReqTransactionId)
        {

            IDictionary<string, System.Object> transactionIdRelatedValues = new System.Dynamic.ExpandoObject() as IDictionary<string, System.Object>;

            try
            {
                Entity validationRequestCase = getCaseForTransactionId(validationReqTransactionId);

                bool caseHasDisposition = DynamicsProcessesHelper.existsSystemNote(" --- Disposition Details --- ", new EntityReference(validationRequestCase.LogicalName, validationRequestCase.Id));


                Entity orgAccount = await getValidationRequestAccount(validationRequestCase);

                string logMsg = "getTransactionIdRelatedValues is orgAccount null: " + orgAccount == null ? "yes" : "no";



                if (orgAccount != null)
                {
                    transactionIdRelatedValues["accountId"] = orgAccount.Id.ToString();
                    transactionIdRelatedValues["tsOrgId"] = orgAccount.GetAttributeValue<string>("accountnumber");
                    transactionIdRelatedValues["accountOrgName"] = orgAccount.GetAttributeValue<string>("name");
                    transactionIdRelatedValues["ctpOrgId"] = orgAccount.GetAttributeValue<string>("ts_ctporgid");

                    Entity activityCodeEntity = orgAccount.GetAttributeValue<EntityReference>("new_activitycode") == null ? null
                                                                                                        : await DynamicsInterface.DataverseClient.RetrieveAsync("new_activitycodes", orgAccount.GetAttributeValue<EntityReference>("new_activitycode").Id, new ColumnSet(true));

                    Entity qualCodeEntity = orgAccount.GetAttributeValue<EntityReference>("new_orgdesignation") == null ? null
                                                                                                        : await DynamicsInterface.DataverseClient.RetrieveAsync("new_qualificationcode", orgAccount.GetAttributeValue<EntityReference>("new_orgdesignation").Id, new ColumnSet(true));

                    transactionIdRelatedValues["orgActivityCode"] = activityCodeEntity?.GetAttributeValue<string>("new_activitycode");
                    transactionIdRelatedValues["orgDesignation"] = qualCodeEntity?.GetAttributeValue<string>("new_qualcode");
                }

                transactionIdRelatedValues["isActivityCodeValid"] = validationRequestCase.GetAttributeValue<bool>("ts_validationdispositionrulesactivitycodevalid");
                transactionIdRelatedValues["tsCseStatus"] = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_casestatus").Value;
                transactionIdRelatedValues["dispositionStatus"] = validationRequestCase.GetAttributeValue<string>("ts_validationdispositionstatus");
                string dispositionProcessingStatus = validationRequestCase.GetAttributeValue<string>("ts_validationdispositionstatus");
                dispositionProcessingStatus = dispositionProcessingStatus == null ? "" : dispositionProcessingStatus.ToLower().Trim();
                transactionIdRelatedValues["selfReportedActivityCode"] = validationRequestCase.GetAttributeValue<string>("ts_validationselfreportedactivitycode");
                transactionIdRelatedValues["agentEmail"] = validationRequestCase.GetAttributeValue<string>("ts_validationagentemail");
                transactionIdRelatedValues["agentName"] = validationRequestCase.GetAttributeValue<string>("ts_validationagentname");
                transactionIdRelatedValues["tsCaseStatusText"] = validationRequestCase.FormattedValues["ts_casestatus"];


                dynamic transactionIdAttributes = (ExpandoObject)transactionIdRelatedValues;


                string tsCaseStatusTextShort = transactionIdAttributes.tsCaseStatusText.Replace("OQ - ", "").Replace("AutoValidation - ", "");

                transactionIdAttributes.activityCode = transactionIdAttributes.tsCaseStatusText == "OQ - Qualified" && transactionIdAttributes.orgActivityCode != null ? transactionIdAttributes.orgActivityCode : null;
                transactionIdAttributes.orgName = transactionIdAttributes.tsCaseStatusText == "OQ - Qualified" && transactionIdAttributes.accountOrgName != null ? transactionIdAttributes.accountOrgName : validationRequestCase.GetAttributeValue<string>("ts_validationrequestlegalname");
                transactionIdAttributes.agentVerification = validationRequestCase.Contains("ts_validationrequestagentverification") ? validationRequestCase.FormattedValues["ts_validationrequestagentverification"] : "Not Verified";



                transactionIdAttributes.qualificationStatus = "Not Qualified";
                transactionIdAttributes.transactionStatus = "Open";
                transactionIdAttributes.transactionPhase = tsCaseStatusTextShort;

                int caseResolution = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_caseresolution")?.Value ?? -1;
                transactionIdAttributes.caseResolution = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_caseresolution") == null ? "" : validationRequestCase.FormattedValues["ts_caseresolution"];

                if (transactionIdAttributes.tsCaseStatusText == "OQ - AutoValidation - Awaiting Disposition" && transactionIdAttributes.dispositionStatus == "completed")
                    transactionIdAttributes.transactionPhase = "Disposition received";


                string[] closingCaseStatuses = { "OQ - Qualified", "OQ - Disqualified", "OQ - Cancelled", "OQ - Closed", "OQ - Abandoned", "OQ - Expired", "OQ - Rejected" };
                if (closingCaseStatuses.Contains((string)transactionIdAttributes.tsCaseStatusText))
                {
                    transactionIdAttributes.transactionPhase = "Closed";
                    transactionIdAttributes.transactionStatus = "Closed";

                    if (transactionIdAttributes.tsCaseStatusText == "OQ - Qualified")
                        transactionIdAttributes.qualificationStatus = "Qualified";

                }

                transactionIdRelatedValues = (ExpandoObject)transactionIdAttributes as IDictionary<string, System.Object>;

                return transactionIdRelatedValues;
            }
            catch (Exception e)
            {
                string error = $"Error in getTransactionIdRelatedValues(...). Exception message:{Environment.NewLine}{e.Message}";
                DynamicsInterface.writeToLog(error);
                return null;
            }




        }
        public static DateTime convertTimestampToDatetime(long unixTimestampMilliseconds)
        {
            DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return unixEpoch.AddMilliseconds(unixTimestampMilliseconds);
        }

        public static string cleanCtpString(string fieldValue)
        {
            fieldValue = fieldValue == null ? null :
                                            (fieldValue.Trim() == "nil" ? "" : fieldValue.Trim());

            return fieldValue;
        }
        public static Guid? getMailBoxQueueId(string queueName)
        {
            try
            {
                dynamic defaultMailboxQueue = null;
                if (string.IsNullOrEmpty(queueName))
                {
                    defaultMailboxQueue = getDefaultMailboxQueueId();
                    return defaultMailboxQueue.queueId;
                }
                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
                EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                if (entityCollection.Entities.Count > 0)
                    return entityCollection.Entities.First().Id;

                defaultMailboxQueue = getDefaultMailboxQueueId();
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

                int qualCaseStatus = qualCase.GetAttributeValue<OptionSetValue>("ts_casestatus")?.Value ?? -1;
                if (qualCaseStatus != validationRequestCaseStatus)
                {
                    qualCase["ts_casestatus"] = new OptionSetValue(validationRequestCaseStatus); //102056 - 'OQ - Qualified'
                    DynamicsInterface.DataverseClient.Update(qualCase);
                }
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
                DynamicsInterface.writeToLog("Error in findEntityeGenericFilterInAndOut(...). Exception message: " + Environment.NewLine + e.Message);
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
