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
using Newtonsoft.Json;
using System.Runtime.Remoting.Services;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Web;
using System.Web.UI;
using System.Xml.XPath;

namespace DynamicsProcesses
{
    internal class DynamicsInterface
    {
        private static string ExceptionsFilePath = ConfigurationManager.AppSettings["ExceptionsFilePath"];
        private static string MaxLogSize = ConfigurationManager.AppSettings["MaxLogSize"];



        public static string DynamicsEnvironment = ConfigurationManager.AppSettings["DynamicsEnvironment"];
        private static string ClientId = ConfigurationManager.AppSettings["ClientId"];
        private static string ClientSecret = ConfigurationManager.AppSettings["ClientSecret"];

        public static string Sql2kServer = ConfigurationManager.AppSettings["Sql2kServer"];

        public static DataverseClientLib.ServiceClient DataverseClient;

        public static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        public static string LogName = "log";

        public static List<string> errorStack;

        public static string[] Args = null;
        static async Task Main(string[] args)
        {


            try
            {
                bool success = false;

                Args = args;

                string process = args[0];

                //foreach (string arg in args)
                //{
                //    string first = arg;
                //}

                DataverseClient = getDynamicsClient();

                if (DataverseClient == null)
                    return;


                string azureStorage7C = ConfigurationManager.AppSettings["AzureStorage7C"];

                ParallelProcessesHelper.SemaphoreClient = ParallelProcessesHelper.getTableClientAsync(azureStorage7C);


                switch (process)
                {
                    case "orgqualifiedemail":
                        writeToLog("Start: orgqualifiedemail. draft: " + args.Contains("draft").ToString());
                        int fromTime = 240;
                        int toTime = 60;
                        if (DynamicsInterface.Args.Length > 2)
                        {
                            fromTime = int.Parse(DynamicsInterface.Args[1]);
                            toTime = int.Parse(DynamicsInterface.Args[2]);
                        }
                        batchMailOrgQualified(args.Contains("draft"), fromTime, toTime);
                        break;
                    case "retryonyxupdate":
                        LogName = "log_DynamicsToOnyxRetry";
                        DynamicsProcessesOnyxUpdate.dynamicsToOnyxRetry();
                        break;
                    case "automatedvalidation":
                        LogName = "log_AutomatedValidation";
                        await DynamicsProcessesAutomatedValidation.processAutomatedValidation();
                        break;
                    case "validationservices":
                        LogName = "log_ValidationServices";
                        //await ParallelProcessesHelper.cleanUpExpiredSemaphores(0);

                        await DynamicsProcessesValidationServices.processValidationServices();
                        break;
                    case "fraudreview":
                        LogName = "log_FraudReview";
                        //processFraudReview();
                        break;
                    case "irsrevocation":
                        LogName = "log_IRSRevocation";
                        IRSRevocationProcess.processIRSRevocation();

                        await ParallelProcessesHelper.cleanUpExpiredSemaphores();

                        break;
                    case "getctporgids":
                        LogName = "log_GetCtpOrgIds";
                        DynamicsIntegrationProcesses.getCtpOrgIdsForAccounts();

                        await ParallelProcessesHelper.cleanUpExpiredSemaphores();
                        break;

                }

            }
            catch (Exception e)
            {
                writeToLog("DynamicsProcesses Error in Main(string[] args). Exception message: " + Environment.NewLine + e.Message
                            );
            }
            //writeToLog("Execution Ended");
        }

        public static void processFraudReview()
        {
            try
            {
                DateTime fromDate = DateTime.Now.AddMinutes(-240);

                ServiceAdminDataContext context = new ServiceAdminDataContext();

                IEnumerable<usp_getOrgsFlaggedFraudReviewResult> fraudQuery = null;

                fraudQuery = from table in context.usp_getOrgsFlaggedFraudReview(fromDate)
                             select table;

                List<usp_getOrgsFlaggedFraudReviewResult> fraudReviewResult = fraudQuery.ToList<usp_getOrgsFlaggedFraudReviewResult>();


                foreach (usp_getOrgsFlaggedFraudReviewResult fraudReviewItem in fraudReviewResult)
                {
                    DynamicsInterface.errorStack = new List<string>();

                    setOrgOnFraudReview(fraudReviewItem);
                }
            }
            catch (Exception e)
            {
                writeToLog("Error in processFraudReview(). Exception message: " + Environment.NewLine + e.Message
                    );
            }
        }


        public static void setOrgOnFraudReview(usp_getOrgsFlaggedFraudReviewResult fraudReviewItem)
        {
            try
            {


                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(true);
                queryAccount.Criteria.AddCondition("accountnumber", ConditionOperator.Equal, fraudReviewItem.owner_id.ToString());
                EntityCollection accountCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryAccount);

                if (accountCollection.Entities.Count == 0)
                {
                    string error = "No organization found for TSOrgId: " + fraudReviewItem.owner_id.ToString();
                    DynamicsInterface.writeToLog("Error in setOrgOnFraudReview(...). " + error
                    );
                    return;
                }

                Entity account = accountCollection.Entities.First();
                Guid accountId = accountCollection.Entities.First().Id;

                DateTime commentCreatedUTC = TimeZoneInfo.ConvertTimeToUtc(fraudReviewItem.insert_date, DynamicsInterface.pstZone);


                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, accountId);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, fraudReviewItem.note_desc);
                queryAnnotation.Criteria.AddCondition("createdon", ConditionOperator.GreaterEqual, commentCreatedUTC);
                EntityCollection annotationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryAnnotation);

                if (annotationCollection.Entities.Count > 0)
                    return;

                EntityReference accountRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid qualCodeId = accountRef == null ? Guid.Empty : accountRef.Id;

                if (accountRef == null)
                {
                    string error = "The organization doesn't have Org Designation";
                    DynamicsInterface.writeToLog("Error in setOrgOnFraudReview(...). " + error
                    );
                    return;
                }



                //104602  OQ - Fraud Review	
                Guid caseId = DynamicsProcessesHelper.setCaseStatus(2, 101996, 104602
                                , accountId, qualCodeId, null
                                );


                if (caseId != Guid.Empty)
                    DynamicsProcessesHelper.addCaseToQueue(caseId, "TS US - Fraud Review");




                Entity annotation = new Entity("annotation");

                annotation["objectid"] = new EntityReference("account", accountId);
                annotation["subject"] = fraudReviewItem.note_desc;

                annotation["notetext"] = fraudReviewItem.text;

                Guid annotationId = DynamicsInterface.DataverseClient.Create(annotation);

            }
            catch (Exception e)
            {
                writeToLog("Error in setOrgOnFraudReview(). Exception message: " + Environment.NewLine + e.Message
                    );
            }

        }


        public static string getHashBase64UrlEncoded(string ein, string orgEmail, string tsOrgId)
        {
            try
            {
                writeToLog("getHashBase64. ein: " + ein + "; orgEmail: " + orgEmail + "; tsOrgId: " + tsOrgId);

                string concatenatedValue = ein + orgEmail + tsOrgId;
                
                using (SHA256 sha256Hash = SHA256.Create())
                {
                    byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(concatenatedValue));

                    string orgHashBase64 = Convert.ToBase64String(bytes);

                    string urlEncodedOrgHash = HttpUtility.UrlEncode(orgHashBase64);

                    return urlEncodedOrgHash;
                }
            }
            catch (Exception e)
            {
                string error = "Error in getHashBase64(...). Exception message: "
                                     + Environment.NewLine + e.Message;
                writeToLog(error);
            }
            return "";
        }


        public static void processNonC3EligibilityVerifiedEmail_SendEmailFromTemplateRequest(Entity account)
        {
            try
            {


                string ein = account.GetAttributeValue<string>("new_legalidentifier");
                string orgEmail = account.GetAttributeValue<string>("emailaddress1");
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");
                string orgName = account.GetAttributeValue<string>("name");

                string orgHashBase64UrlEncoded = getHashBase64UrlEncoded(ein, orgEmail, tsOrgId);





                string nonC3VerifiedEmailTemplate =
                                            (
                                                ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                        Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                            )?.emailOutreachProcess?.orgDesignationVerifiedEmailTemplate;




                Entity templateEntity = DynamicsProcessesHelper.getTemplateEntity(nonC3VerifiedEmailTemplate);

                if (DynamicsProcessesValidationServices.DynamicsEnvironments.ContainsKey(DynamicsInterface.DynamicsEnvironment))
                {
                    string DynamicsEnvironmentCurrentName = DynamicsProcessesValidationServices.DynamicsEnvironments[DynamicsInterface.DynamicsEnvironment];
                    DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] = DynamicsEnvironmentCurrentName;
                }





                Entity email = new Entity("email");

                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, "Support");
                EntityCollection entityCollection = DataverseClient.RetrieveMultiple(queryEntity);

                if (entityCollection.Entities.Count == 0)
                {
                    writeToLog("At processNonC3EligibilityVerifiedEmail(). No maibox Queue found with name: " + "Support");
                    return;
                }

                Guid queueId = entityCollection.Entities.First().Id;

                EntityCollection fromParties = new EntityCollection();
                Entity fromQueue = new Entity("activityparty");
                fromQueue["partyid"] = new EntityReference("queue", queueId);
                fromParties.Entities.Add(fromQueue);



                EntityCollection toParties = new EntityCollection();
                Entity toparty = new Entity("activityparty");
                if (DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] == "prod")
                {                    
                    toparty["partyid"] = new EntityReference("account", account.Id);
                    toParties.Entities.Add(toparty);
                }
                else
                {
                    toparty["addressused"] = "franciscocastellanos@yahoo.com";
                    toParties.Entities.Add(toparty);
                }


                email["from"] = fromParties;
                email["to"] = toParties;


                email["subject"] = "To be replaced";
                email["description"] = "To be replaced";
                email["directioncode"] = true;

                SendEmailFromTemplateRequest request = new SendEmailFromTemplateRequest()
                {
                    Target = email,
                    TemplateId = templateEntity.Id,
                    RegardingId = account.Id,
                    RegardingType = "account"

                };

                SendEmailFromTemplateResponse response = (SendEmailFromTemplateResponse)DynamicsInterface.DataverseClient.Execute(request);


                //email["regardingobjectid"] = new EntityReference("account", account.Id);

                //Guid emailId = DataverseClient.Create(email);


               
            }
            catch (Exception e)
            {
                writeToLog("Error in processNonC3EligibilityVerifiedEmail(...). Exception message: " + Environment.NewLine + e.Message
                    );
            }
        }


        public static void processNonC3EligibilityVerifiedEmail(Entity account)
        {
            try
            {


                string ein = account.GetAttributeValue<string>("new_legalidentifier");
                string orgEmail = account.GetAttributeValue<string>("emailaddress1");
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");
                string orgName = account.GetAttributeValue<string>("name");

                string orgHashBase64UrlEncoded = getHashBase64UrlEncoded(ein, orgEmail, tsOrgId);





                string nonC3VerifiedEmailTemplate =
                                            (
                                                ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                        Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                            )?.emailOutreachProcess?.orgDesignationVerifiedEmailTemplate;




                Entity templateEntity = DynamicsProcessesHelper.getTemplateEntity(nonC3VerifiedEmailTemplate);






                Entity email = new Entity("email");

                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, "Support");
                EntityCollection entityCollection = DataverseClient.RetrieveMultiple(queryEntity);

                if (entityCollection.Entities.Count == 0)
                {
                    writeToLog("At processNonC3EligibilityVerifiedEmail(). No maibox Queue found with name: " + "Support");
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




                string emailSubject = templateEntity.GetAttributeValue<string>("subject");

                string templValue = string.Empty;

                try
                {
                    var doc = XDocument.Parse(emailSubject);

                    XElement templ = doc.Elements().First().Elements().Where(element => element.Name.LocalName == "template").First();

                    templValue = templ.Value;

                    emailSubject = string.IsNullOrEmpty(templValue) ? emailSubject : templValue;


                    var docTempl = XDocument.Parse(templ.Value);
                    XElement pElement = docTempl.XPathSelectElement("//p");
                    string subjectValue = pElement == null ? "" : pElement.Value;

                    emailSubject = string.IsNullOrEmpty(subjectValue) ? emailSubject : subjectValue;

                }
                catch
                {
                    emailSubject = string.IsNullOrEmpty(templValue) ? emailSubject : templValue;
                }


                try
                {
                    XElement divWrapper = XElement.Parse(emailSubject).Descendants("div").FirstOrDefault();

                    XElement spanEelement = divWrapper.Descendants().ToList().Find(element => element.Name == "span");

                    if (spanEelement != null)
                        emailSubject = spanEelement.Value;

                }
                catch { }




                email["subject"] = emailSubject; // "TechSoup: Your Eligibility for AWS Credit Grant is Confirmed";// templateEntity.GetAttributeValue<string>("subject");







                string emailBody = templateEntity.GetAttributeValue<string>("body");

                emailBody = emailBody.Replace("{$hash}", orgHashBase64UrlEncoded).Replace("{$Organization_Name}", orgName);
                email["description"] = emailBody;


                email["directioncode"] = true;

                email["regardingobjectid"] = new EntityReference("account", account.Id);

                Guid emailId = DataverseClient.Create(email);


                if (DynamicsProcessesValidationServices.DynamicsEnvironments.ContainsKey(DynamicsInterface.DynamicsEnvironment))
                {
                    string DynamicsEnvironmentCurrentName = DynamicsProcessesValidationServices.DynamicsEnvironments[DynamicsInterface.DynamicsEnvironment];
                    DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] = DynamicsEnvironmentCurrentName;
                }

                bool isDraft = DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] == "prod" ? false : true;


                if (!isDraft)
                {
                    SendEmailRequest emailRequest = new SendEmailRequest
                    {
                        EmailId = emailId,
                        TrackingToken = "",
                        IssueSend = true
                    };

                    SendEmailResponse emailResponse = (SendEmailResponse)DataverseClient.Execute(emailRequest);
                }
            }
            catch (Exception e)
            {
                writeToLog("Error in processNonC3EligibilityVerifiedEmail(...). Exception message: " + Environment.NewLine + e.Message
                    );
            }
        }


        public static void batchMailOrgQualified(bool isDraft, int fromTime = 240, int toTime = 60)
        {
            try
            {

                dynamic automatedValSettings = DynamicsProcessesAutomatedValidation.getAutomatedValidationConfig();
                DynamicsProcessesValidationServices.AutomatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);

                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification")
                {
                    ColumnSet = new ColumnSet(true),
                    LinkEntities =
                        {
                            new LinkEntity
                            {
                                JoinOperator = JoinOperator.Inner,
                                LinkFromEntityName = "ts_organizationqualification",
                                LinkFromAttributeName = "ts_accountid",
                                LinkToEntityName = "account",
                                LinkToAttributeName = "accountid",
                                Columns = new ColumnSet(true),
                                EntityAlias = "acc"
                            }
                        }
                };

                
                queryOrgQualification.Criteria.AddCondition("ts_qualificationstatus", ConditionOperator.Equal, 1);
                queryOrgQualification.Criteria.AddCondition("ts_qualificationstatusdate", ConditionOperator.Between, DateTime.UtcNow.AddMinutes(-1 * fromTime), DateTime.UtcNow.AddMinutes(-1 * toTime));

                queryOrgQualification.Criteria.AddCondition("acc", "address1_country", ConditionOperator.In, new object[] { "AS", "FM", "GU", "MP", "PR", "UM", "US", "VI" });

                //queryOrgQualification.Criteria.AddCondition("acc", "accountnumber", ConditionOperator.Equal, "2650826");

                EntityCollection orgQualificationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryOrgQualification);

                foreach (Entity orgQualification in orgQualificationCollection.Entities)
                {
                    Guid qualCodeId = orgQualification.GetAttributeValue<EntityReference>("ts_qualificationcodeid").Id;
                    EntityReference orgDesigRef = (EntityReference)orgQualification.GetAttributeValue<AliasedValue>("acc.new_orgdesignation").Value;

                    Guid orgDesigId = Guid.Empty;
                    if (orgDesigRef != null)
                        orgDesigId = orgDesigRef.Id;

                    if (qualCodeId == orgDesigId)
                    {
                        string tsOrgId = orgQualification.GetAttributeValue<AliasedValue>("acc.accountnumber").Value.ToString();
                        string countryCode = orgQualification.GetAttributeValue<AliasedValue>("acc.address1_country").Value.ToString();
                        Guid accountId = (Guid)orgQualification.GetAttributeValue<AliasedValue>("acc.accountid").Value;

                        QueryExpression queryQualHistory = new QueryExpression("ts_organizationqualificationhistory");
                        queryQualHistory.ColumnSet = new ColumnSet(true);
                        queryQualHistory.Criteria.AddCondition("ts_organizationqualificationid", ConditionOperator.Equal, orgQualification.Id);
                        queryQualHistory.AddOrder("ts_qualificationactiondate", OrderType.Descending);
                        queryQualHistory.TopCount = 2;
                        EntityCollection qualHistoryCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQualHistory);

                        int i = 0;
                        string currentQualActiont = string.Empty;
                        string previousQualAction = string.Empty;
                        foreach (Entity orgQualHistory in qualHistoryCollection.Entities)
                        {
                            i++;
                            string qualAction = orgQualHistory.FormattedValues["ts_qualificationstatusaction"];

                            if (i == 1)
                                currentQualActiont = qualAction;

                            if (i == 2)
                                previousQualAction = qualAction;

                        }

                        Entity account = DynamicsInterface.DataverseClient.Retrieve("account", accountId, new ColumnSet(true));

                        List<string> nonC3Designations = ((JArray)(
                                                                   ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                                                 Where(country => ((string)country.country)?.ToLower() == "us"
                                                                                                                                                                                                        )?.FirstOrDefault()?.orgDesignations
                                                                  ))?.ToList<dynamic>()?.Select(item => (string)item)?.ToList();

                        Entity orgDesigQualCodeEntity = account.GetAttributeValue<EntityReference>("new_orgdesignation") == null ? null :
                                                                                        DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", account.GetAttributeValue<EntityReference>("new_orgdesignation").Id, new ColumnSet(true));



                        string orgDesignation = orgDesigQualCodeEntity?.GetAttributeValue<string>("new_qualcode");


                        bool isNonC3 = (
                                        nonC3Designations.Count > 0 && !string.IsNullOrEmpty(orgDesignation)
                                        && nonC3Designations.Exists(designation => designation.ToLower() == orgDesignation.ToLower())
                                       ) ? true : false;





                        string nonC3VerifiedEmailTemplate =
                                                    (
                                                        ((JArray)DynamicsProcessesValidationServices.AutomatedValDefinition.validationServices?.orgDesignations)?.ToList<dynamic>()?.
                                                                                                                                                                                Where(country => ((string)country.country)?.ToLower() == "us")?.FirstOrDefault()
                                                    )?.emailOutreachProcess?.orgDesignationVerifiedEmailTemplate;




                        Entity templateEntity = null;

                        if (isNonC3)
                        {
                            templateEntity = DynamicsProcessesHelper.getTemplateEntity(nonC3VerifiedEmailTemplate);
                        }
                        else
                        {
                            templateEntity = DynamicsProcessesHelper.getTemplateEntity("Your Organization Has Been Qualified");
                        }


                        string emailSubject = templateEntity.GetAttributeValue<string>("subject");

                        string templValue = string.Empty;

                        try
                        {
                            var doc = XDocument.Parse(emailSubject);

                            XElement templ = doc.Elements().First().Elements().Where(element => element.Name.LocalName == "template").First();

                            templValue = templ.Value;

                            emailSubject = string.IsNullOrEmpty(templValue) ? emailSubject : templValue;


                            var docTempl = XDocument.Parse(templ.Value);
                            XElement pElement = docTempl.XPathSelectElement("//p");
                            string subjectValue = pElement == null ? "" : pElement.Value;

                            emailSubject = string.IsNullOrEmpty(subjectValue) ? emailSubject : subjectValue;

                        }
                        catch
                        {
                            emailSubject = string.IsNullOrEmpty(templValue) ? emailSubject : templValue;
                        }


                        try
                        {
                            XElement divWrapper = XElement.Parse(emailSubject).Descendants("div").FirstOrDefault();

                            XElement spanEelement = divWrapper.Descendants().ToList().Find(element => element.Name == "span");

                            if (spanEelement != null)
                                emailSubject = spanEelement.Value;

                        }
                        catch { }




                        //templateEntity.GetAttributeValue<string>("title")

                        if (currentQualActiont == "Qualified" && previousQualAction != "Qualified")
                        {
                            DateTime qualStatusDate = orgQualification.GetAttributeValue<DateTime>("ts_qualificationstatusdate");
                            QueryExpression queryEmail = new QueryExpression("email");
                            queryEmail.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, accountId);
                            queryEmail.Criteria.AddCondition("createdon", ConditionOperator.GreaterThan, qualStatusDate);

                            if (isNonC3)
                            {
                                queryEmail.Criteria.AddCondition("subject", ConditionOperator.Equal, emailSubject);
                            }
                            else
                            {
                                queryEmail.Criteria.AddCondition("subject", ConditionOperator.Equal, "Your Organization Has Been Qualified");
                            }
                            
                            EntityCollection emailCollection = DataverseClient.RetrieveMultiple(queryEmail);

                            if (emailCollection.Entities.Count == 0)
                            {

                                if (
                                    //nonC3Designations.Count > 0 && !string.IsNullOrEmpty(orgDesignation)
                                    //&& nonC3Designations.Exists(designation => designation.ToLower() == orgDesignation.ToLower())
                                    isNonC3
                                   )
                                {
                                    processNonC3EligibilityVerifiedEmail(account);
                                    continue;
                                }

                                 

                                Entity email = new Entity("email");

                                QueryExpression queryEntity = new QueryExpression("queue");
                                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, "Support");
                                EntityCollection entityCollection = DataverseClient.RetrieveMultiple(queryEntity);

                                if (entityCollection.Entities.Count == 0)
                                {
                                    writeToLog("At batchMailOrgQualified(). No maibox Queue found with name: " + "Support");
                                    continue;
                                }

                                Guid queueId = entityCollection.Entities.First().Id;

                                EntityCollection fromParties = new EntityCollection();

                                Entity fromQueue = new Entity("activityparty");
                                fromQueue["partyid"] = new EntityReference("queue", queueId);
                                fromParties.Entities.Add(fromQueue);

                                EntityCollection toParties = new EntityCollection();

                                Entity toparty = new Entity("activityparty");
                                toparty["partyid"] = new EntityReference("account", accountId);
                                toParties.Entities.Add(toparty);



                                email["from"] = fromParties;
                                email["to"] = toParties;

                                email["subject"] = templateEntity.GetAttributeValue<string>("title");
                                email["description"] = templateEntity.GetAttributeValue<string>("body");
                                email["directioncode"] = true;

                                email["regardingobjectid"] = new EntityReference("account", accountId);

                                Guid emailId = DataverseClient.Create(email);


                                if (DynamicsProcessesValidationServices.DynamicsEnvironments.ContainsKey(DynamicsInterface.DynamicsEnvironment))
                                {
                                    string DynamicsEnvironmentCurrentName = DynamicsProcessesValidationServices.DynamicsEnvironments[DynamicsInterface.DynamicsEnvironment];
                                    DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] = DynamicsEnvironmentCurrentName;
                                }

                                isDraft = DynamicsProcessesValidationServices.DynamicsEnvironments["DynamicsEnvironmentCurrent"] == "prod" ? false : true;

                                if (!isDraft)
                                {
                                    SendEmailRequest emailRequest = new SendEmailRequest
                                    {
                                        EmailId = emailId,
                                        TrackingToken = "",
                                        IssueSend = true
                                    };

                                    SendEmailResponse emailResponse = (SendEmailResponse)DataverseClient.Execute(emailRequest);
                                }

                            }
                        }

                    }

                }

            }
            catch (Exception e)
            {
                writeToLog("Error in batchMailOrgQualified(). Exception message: " + Environment.NewLine + e.Message
                    );
            }
        }











        static DataverseClientLib.ServiceClient getDynamicsClient()
        {
            DataverseClientLib.ServiceClient dataverseClient = null;
            string dynamicsEnv = DynamicsEnvironment;
            string clientId = ClientId;
            string clientSecret = ClientSecret;
            try
            {
                string connectionString = $"AuthType=ClientSecret;Url={dynamicsEnv};ClientId={clientId};ClientSecret={clientSecret};";
                dataverseClient = new DataverseClientLib.ServiceClient(connectionString);
            }
            catch (Exception e)
            {
                writeToLog("Error connecting to Dynamics Environment: " + dynamicsEnv + ". Exception message: " + Environment.NewLine + e.Message);
            }
            return dataverseClient;
        }



        public static void updateReferenceValue(string value, string ReferenceName, string ReferenceType)
        {
            try
            {
                ServiceAdminDataContext context = new ServiceAdminDataContext();
                IQueryable<ReferenceValue> query = null;

                query = from refVal in context.ReferenceValues
                        where
                            refVal.ReferenceGroup == "DynamicsIntegration"
                            && refVal.ReferenceType == ReferenceType
                            && refVal.ReferenceName == ReferenceName
                        select refVal;


                query.First().ReferenceValue1 = value;

                context.SubmitChanges();

                context.Dispose();
            }
            catch (Exception e)
            {
                writeToLog("Errr in updateReferenceValue(string value, string ReferenceName, string ReferenceType). Exception message: " 
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "ReferenceType: " + ReferenceType + "; ReferenceName: " + ReferenceName + "; value: " + value
                    );
            }

        }


        public static string getReferenceValue(string ReferenceName, string ReferenceType)
        {
            string value = string.Empty;
            try
            {
                ServiceAdminDataContext context = new ServiceAdminDataContext();
                IQueryable<ReferenceValue> query = null;

                query = from refVal in context.ReferenceValues
                        where
                            refVal.ReferenceGroup == "DynamicsIntegration"
                            && refVal.ReferenceType == ReferenceType
                            && refVal.ReferenceName == ReferenceName
                        select refVal;


                value = query.First().ReferenceValue1;

                context.SubmitChanges();

                context.Dispose();
            }
            catch (Exception e)
            {
                writeToLog("Error in  getReferenceValue(string ReferenceName, string ReferenceType). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "ReferenceType: " + ReferenceType + "; ReferenceName: " + ReferenceName
                    );
            }

            return value;
        }


        public static void writeToLog(string message)
        {
            writeToLog(LogName, message);
        }
        public static void writeToLog(string logName, string message)
        {
            int maxSize = int.Parse(MaxLogSize) * 1000;

            FileInfo file = new FileInfo(ExceptionsFilePath + logName + ".txt");

            if (file.Exists)
            {
                if (file.Length > maxSize)
                {
                    file.MoveTo(ExceptionsFilePath + logName + " - " + DateTime.Now.ToString("yyyyMMdd HHmm") + ".txt");
                }
            }

            using (StreamWriter w = File.AppendText(ExceptionsFilePath + logName + ".txt"))
            {
                w.WriteLine(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
                w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ":" + Environment.NewLine + message);
                w.Flush();
                w.Close();
            }

            if (message.StartsWith("Error"))
                addToErrorStack(message);
        }


        public static void addToErrorStack(string message)
        {
            if (errorStack == null)
                errorStack = new List<string>();

            errorStack.Add(message);

        }
    }
}
