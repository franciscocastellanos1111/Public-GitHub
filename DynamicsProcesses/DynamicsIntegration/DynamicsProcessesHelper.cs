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

namespace DynamicsProcesses
{
    internal class DynamicsProcessesHelper
    {
        public static Entity getTemplateEntity(string templateName)
        {
            Entity emailTemplate = null;

            QueryExpression queryTemplate = new QueryExpression("template");
            queryTemplate.ColumnSet = new ColumnSet(true);
            queryTemplate.Criteria.AddCondition("title", ConditionOperator.Equal, templateName);
            EntityCollection templateCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryTemplate);

            if (templateCollection.Entities.Count > 0)
                emailTemplate = templateCollection.Entities.First();

            return emailTemplate;
        }






        public static void updateDynamicsoOnyxIntegrationLog(Entity dynOnyxIntLog)
        {

            try
            {
                int i = 0;
                string errorStackText = Environment.NewLine + Environment.NewLine + "Integration retry - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") 
                    + ":" + Environment.NewLine;

                foreach (string errorEntry in DynamicsInterface.errorStack)
                {
                    i++;

                    string errorEntryFormatted = errorEntry;

                    if (i > 1)
                    {
                        errorEntryFormatted = regexReplace(@"\r\n", errorEntryFormatted, "\r\n\t\t");
                        errorStackText += Environment.NewLine + "\t";
                    }

                    errorStackText += errorEntryFormatted;
                }

                string log = dynOnyxIntLog.GetAttributeValue<string>("ts_log");
                //log += errorStackText;

                Regex regexAddFromTemplToken = new Regex(@"###tsSendEmailRequest###.+###tsSendEmailRequest###");
                Match match = regexAddFromTemplToken.Match(log, 0);

                errorStackText += Environment.NewLine + Environment.NewLine + match.Value;

                dynOnyxIntLog["ts_log"] = errorStackText;

                int attemptCount = dynOnyxIntLog.GetAttributeValue<int>("ts_currentattemptcount");
                attemptCount++;

                dynOnyxIntLog["ts_currentattemptcount"] = attemptCount;
                DynamicsInterface.DataverseClient.Update(dynOnyxIntLog);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateDynamicsoOnyxIntegrationLog(...). Exception message: " + Environment.NewLine + e.Message                   
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


        public static Dictionary<string, string> GetEnvironmentVariables()
        {

            Dictionary<string, string> envVariables = new Dictionary<string, string>();

            QueryExpression query = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("statecode", "defaultvalue", "valueschema",
                  "schemaname", "environmentvariabledefinitionid", "type"),
                LinkEntities =
                        {
                            new LinkEntity
                            {
                                JoinOperator = JoinOperator.LeftOuter,
                                LinkFromEntityName = "environmentvariabledefinition",
                                LinkFromAttributeName = "environmentvariabledefinitionid",
                                LinkToEntityName = "environmentvariablevalue",
                                LinkToAttributeName = "environmentvariabledefinitionid",
                                Columns = new ColumnSet("statecode", "value", "environmentvariablevalueid"),
                                EntityAlias = "v"
                            }
                        }
            };

            EntityCollection results = DynamicsInterface.DataverseClient.RetrieveMultiple(query);


            if (results?.Entities.Count > 0)
            {
                foreach (Entity entity in results.Entities)
                {
                    string schemaName = entity.GetAttributeValue<string>("schemaname");
                    string value = entity.GetAttributeValue<AliasedValue>("v.value")?.Value?.ToString();
                    string defaultValue = entity.GetAttributeValue<string>("defaultvalue");

                    if (schemaName != null && !envVariables.ContainsKey(schemaName))
                        envVariables.Add(schemaName, string.IsNullOrEmpty(value) ? defaultValue : value);


                }
            }


            return envVariables;


        }


        public static void addCaseToQueue(Guid caseId, string queueName)
        {
            try
            {
                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.ColumnSet = new ColumnSet(true);
                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
                EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                Guid queueId = Guid.Empty;
                if (entityCollection.Entities.Count == 0)
                {
                    queueId = createQueue(queueName);

                    if (queueId == Guid.Empty)
                        return;
                }
                else
                {
                    queueId = entityCollection.Entities.First().Id;
                }

                AddToQueueRequest request = new AddToQueueRequest()
                {
                    Target = new EntityReference("incident", caseId),
                    DestinationQueueId = queueId
                };

                AddToQueueResponse response = (AddToQueueResponse)DynamicsInterface.DataverseClient.Execute(request);
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in addCaseToQueue(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}caseId: {caseId.ToString()}; queueName: {queueName}"
                                                );
            }
        }

        public static Guid createQueue(string queueName)
        {
            Guid queueId = Guid.Empty;
            try
            {
                QueryExpression queryQueue = new QueryExpression("queue");
                queryQueue.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
                EntityCollection queueCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQueue);

                if (queueCollection.Entities.Count == 0)
                {
                    Entity queue = new Entity("queue");

                    queue["name"] = queueName; // "Membership";//Automated Validation";//NGOkSlackOQ";// efulfillment";
                    queue["incomingemaildeliverymethod"] = new OptionSetValue(0);//0 none; 2 - Server-Side Synchronization or Email Router
                    queue["incomingemailfilteringmethod"] = new OptionSetValue(0); //0	All email messages
                    queue["outgoingemaildeliverymethod"] = new OptionSetValue(0);//0 none; 2 - Server-Side Synchronization or Email Router
                    queue["queueviewtype"] = new OptionSetValue(0);//0 - Public

                    queueId = DynamicsInterface.DataverseClient.Create(queue);
                }
                else
                {
                    queueId = queueCollection.Entities.First().Id;
                }

                return queueId;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in createQueue(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}queueName: {queueName}"
                                                );
                return Guid.Empty;
            }
        }

        public static void addCaseToQueue_backup(Guid caseId, string queueName)
        {
            try
            {
                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.ColumnSet = new ColumnSet(true);
                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
                EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                if (entityCollection.Entities.Count == 0)
                    return;

                Guid queueId = entityCollection.Entities.First().Id;

                AddToQueueRequest request = new AddToQueueRequest()
                {
                    Target = new EntityReference("incident", caseId),
                    DestinationQueueId = queueId
                };

                AddToQueueResponse response = (AddToQueueResponse)DynamicsInterface.DataverseClient.Execute(request);
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in addCaseToQueue(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}caseId: {caseId.ToString()}; queueName: {queueName}"
                                                );
            }
        }

        public static Guid setCaseStatus(int caseTypeCode, int type, int caseStatus
                                           , Guid accountId, Guid qualCodeId, string tsOrderId
                                            )
        {
            Guid caseId = Guid.Empty;

            try
            {
                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet("ts_casestatus");
                FilterExpression filterQualCase = new FilterExpression(LogicalOperator.And);
                filterQualCase.AddCondition("casetypecode", ConditionOperator.Equal, caseTypeCode);
                filterQualCase.AddCondition("ts_type", ConditionOperator.Equal, type);
                filterQualCase.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                filterQualCase.AddCondition("accountid", ConditionOperator.Equal, accountId);
                if (tsOrderId != null)
                    filterQualCase.AddCondition("ts_tsorderid", ConditionOperator.Equal, tsOrderId);
                queryQualCase.Criteria.AddFilter(filterQualCase);
                queryQualCase.AddOrder("createdon", OrderType.Descending);
                queryQualCase.TopCount = 1;
                EntityCollection qualCaseCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQualCase);

                
                if (qualCaseCollection.Entities.Count > 0)
                {
                    Entity caseEntity = qualCaseCollection.Entities.First();
                    caseId = caseEntity.Id;
                    caseEntity["ts_casestatus"] = new OptionSetValue(caseStatus);			
                    DynamicsInterface.DataverseClient.Update(caseEntity);
                }
                else
                {
                    Entity qualCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", qualCodeId, new ColumnSet("new_qualname", "new_qualcode"));

                    string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                    string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");

                    caseId = DynamicsProcessesHelper.createCase(qualCode + " - " + qualName, caseTypeCode, type, caseStatus, accountId, qualCodeId, tsOrderId);
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in setCaseStatus(...). Exception message: "
                                                + Environment.NewLine + e.Message
                                                + Environment.NewLine + "caseTypeCode: " + caseTypeCode.ToString() + "; type: " + type.ToString() + "; accountId: " + accountId.ToString() + "; qualCodeId: " + qualCodeId.ToString()
                                                + "; tsOrderId: " + tsOrderId.ToString()
                                                );

            }

            return caseId;
        }


        public static Entity getCaseEntity(int caseTypeCode, int type
                                            , Guid accountId, Guid? qualCodeId, string tsOrderId
                                            )
        {
            Entity caseEntity = null;

            try
            {
                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet(true);
                FilterExpression filterQualCase = new FilterExpression(LogicalOperator.And);
                filterQualCase.AddCondition("casetypecode", ConditionOperator.Equal, caseTypeCode);
                filterQualCase.AddCondition("ts_type", ConditionOperator.Equal, type);
                filterQualCase.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                filterQualCase.AddCondition("accountid", ConditionOperator.Equal, accountId);
                if (tsOrderId != null)
                    filterQualCase.AddCondition("ts_tsorderid", ConditionOperator.Equal, tsOrderId);
                queryQualCase.Criteria.AddFilter(filterQualCase);
                queryQualCase.AddOrder("createdon", OrderType.Descending);
                queryQualCase.TopCount = 1;
                EntityCollection qualCaseCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQualCase);

                if (qualCaseCollection.Entities.Count > 0)
                {
                    caseEntity = qualCaseCollection.Entities.First();
                    return caseEntity;
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getCaseEntity(...). Exception message: "
                                                + Environment.NewLine + e.Message
                                                + Environment.NewLine + "caseTypeCode: " + caseTypeCode.ToString() + "; type: " + type.ToString() + "; accountId: " + accountId.ToString() + "; qualCodeId: " + qualCodeId.ToString()
                                                + "; tsOrderId: " + tsOrderId.ToString()
                                                );

            }

            return caseEntity;
        }

        public static Guid findCase(string validationReqTransactionId)
        {
            Guid caseId = Guid.Empty;
            try
            {
                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.Criteria.AddCondition("ts_validationrequesttransactionid", ConditionOperator.Equal, validationReqTransactionId);
                EntityCollection qualCaseCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQualCase);

                if (qualCaseCollection.Entities.Count > 0)
                    caseId = qualCaseCollection.Entities.First().Id;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in findCase(string validationReqTransactionId). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "validationReqTransactionId: " + validationReqTransactionId
                    );
            }

            return caseId;
        }

        public static Guid createCase(string title, int caseTypeCode, int type, int caseStatus
                                        , Guid accountId, Guid? qualCodeId, string tsOrderId
                                        )
        {
            Guid caseId = Guid.Empty;
            try
            {
                Entity caseEntity = new Entity("incident");

                caseEntity["title"] = title;
                caseEntity["casetypecode"] = new OptionSetValue(caseTypeCode);
                caseEntity["ts_type"] = new OptionSetValue(type);
                caseEntity["ts_casestatus"] = new OptionSetValue(caseStatus);
                //accountid
                caseEntity["customerid"] = new EntityReference("account", accountId);

                if (qualCodeId != null)
                    caseEntity["ts_qualificationcodeid"] = new EntityReference("new_qualificationcode", qualCodeId.Value);

                if (!string.IsNullOrEmpty(tsOrderId))
                    caseEntity["ts_tsorderid"] = tsOrderId;

                caseId = DynamicsInterface.DataverseClient.Create(caseEntity);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error creating Case in createCase(...). Exception message: "
                                                + Environment.NewLine + e.Message
                                                + Environment.NewLine + "accountId: " + accountId.ToString() + "; title: " + title + "; caseTypeCode: " + caseTypeCode.ToString() + "; type: " + type.ToString()
                                                );

            }

            return caseId;
        }

      
        public static string resolveSourceQueueAssignmentBasic(Entity qualCodeEntity, string qualCategory, Entity account, string tsOrgId, string defaultQueue)
        {
            string queueName = defaultQueue;
            try
            {
                if (qualCategory != "QualOrg" || !account.Contains("new_source"))
                    return queueName;



                string orgSource = account.FormattedValues["new_source"];

                QueryExpression queryOrgSourceMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryOrgSourceMapping.ColumnSet = new ColumnSet(true);
                queryOrgSourceMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "queuemap");
                queryOrgSourceMapping.Criteria.AddCondition("ts_valuedescription", ConditionOperator.Equal, "orgSource");
                queryOrgSourceMapping.Criteria.AddCondition("ts_value", ConditionOperator.Equal, orgSource);

                EntityCollection orgSourceCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryOrgSourceMapping);

                if (orgSourceCollection.Entities.Count > 0)
                {
                    Entity orgSourceMap = orgSourceCollection.Entities.First();

                    queueName = orgSourceMap.GetAttributeValue<string>("ts_mappedfieldvalue");

                    return queueName;
                }

                QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                //queryMapping.ColumnSet = new ColumnSet("ts_value", "ts_valuecode");
                queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "nonValidationServicesSource");
                queryMapping.Criteria.AddCondition("ts_value", ConditionOperator.Equal, orgSource);

                EntityCollection mappingCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryMapping);

                if (mappingCollection.Entities.Count == 0)
                {
                    QueryExpression queryMailboxMap = new QueryExpression("ts_fieldhierarchyandmapping");
                    queryMailboxMap.ColumnSet = new ColumnSet(true);
                    queryMailboxMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "queuemap");
                    queryMailboxMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, "ValidationServices");
                    EntityCollection mailboxMapCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryMailboxMap);

                    if (mailboxMapCollection.Entities.Count > 0)
                    {
                        Entity mailboxMap = mailboxMapCollection.Entities.First();

                        queueName = mailboxMap.GetAttributeValue<string>("ts_mappedfieldvalue");
                    }

                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in resolveSourceQueueAssignmentBasic(...). Exception message: "
                                                + Environment.NewLine + e.Message
                                                + Environment.NewLine + "tsOrgId: " + tsOrgId
                                                );

            }

            return queueName;
        }

        public static void addLegalAddress(Guid accountId, dynamic legalAddress)
        {
            try
            {
                QueryExpression queryEntity = new QueryExpression("customeraddress");
                queryEntity.Criteria.AddCondition("parentid", ConditionOperator.Equal, accountId);
                queryEntity.Criteria.AddCondition("addresstypecode", ConditionOperator.Equal, 5);
                EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

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
                }

                //address["addresstypecode"] = new OptionSetValue(5);
                //address["parentid"] = new EntityReference("account", accountId);
                //address["objecttypecode"] = 1;

                address["line1"] = legalAddress.address1;
                address["line2"] = legalAddress.address2;
                //address["line3"] = adddressRec.address3;
                address["city"] = legalAddress.city;
                address["stateorprovince"] = legalAddress.state;
                address["country"] = legalAddress.country;
                address["postalcode"] = legalAddress.postalCode;


                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "country");
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, legalAddress.country);
                EntityCollection fieldMapCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    address["ts_countrydesc"] = new OptionSetValue(countryOptionValue);
                }


                queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, legalAddress.country);
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, legalAddress.state);
                fieldMapCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    address["ts_stateprovdesc"] = new OptionSetValue(countryOptionValue);
                }


                if (addressExists)
                {
                    DynamicsInterface.DataverseClient.Update(address);
                }
                else
                {
                    Guid addressId = DynamicsInterface.DataverseClient.Create(address);
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in addLegalAddress(...). Exception message: " + Environment.NewLine + e.Message
                                                    + Environment.NewLine + "accountId: " + accountId.ToString());
            }

        }

        public static void sendOrgQualifiedEmail(Guid accountId)
        {
            try
            {

                string queueName = "Noreply Mailbox Queue"; //"Support"
                Entity templateEntity = DynamicsProcessesHelper.getTemplateEntity("Your Organization Has Been Qualified");

                Entity email = new Entity("email");

                QueryExpression queryEntity = new QueryExpression("queue");
                queryEntity.Criteria.AddCondition("name", ConditionOperator.Equal, queueName);
                EntityCollection entityCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryEntity);

                if (entityCollection.Entities.Count == 0)
                {
                    DynamicsInterface.writeToLog("Error in sendOrgQualifiedEmail(). No maibox Queue found with name: " + queueName
                        );
                    return;
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

                Guid emailId = DynamicsInterface.DataverseClient.Create(email);


                //bool issueSend = false;
                //if (DynamicsProcessesAutomatedValidation.DynamicsEnvironments["DynamicsEnvironmentCurrent"] == "prod")
                bool issueSend = true;



                SendEmailRequest emailRequest = new SendEmailRequest
                {
                    EmailId = emailId,
                    TrackingToken = "",
                    IssueSend = issueSend
                };

                SendEmailResponse emailResponse = (SendEmailResponse)DynamicsInterface.DataverseClient.Execute(emailRequest);


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in sendOrgQualifiedEmail(). Exception message: " + Environment.NewLine + e.Message);
            }
        }


        public static void updateOrgQualification(Guid accountId, Guid qualCodeId, int qualStatusCode, string qualStatus
                                                    , string tsOrgId, string qualCode)
        {
            try
            {
                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                EntityCollection orgQualificationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                {
                    Entity orgQualification = orgQualificationCollection.Entities.First();

                    orgQualification["ts_qualificationstatus"] = new OptionSetValue(qualStatusCode);
                    orgQualification["ts_qualificationstatusdate"] = DateTime.UtcNow;
                    DynamicsInterface.DataverseClient.Update(orgQualification);

                    Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");

                    orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQualification.Id);
                    orgQualHistory["ts_qualificationstatusaction"] = new OptionSetValue(qualStatusCode);
                    orgQualHistory["ts_qualificationactiondate"] = DateTime.UtcNow;
                    orgQualHistory["ts_name"] = orgQualification.Id.ToString() + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                    DynamicsInterface.DataverseClient.Create(orgQualHistory);
                }
                else
                {
                    DynamicsProcessesHelper.createOrgQualification(accountId, qualCodeId, qualStatusCode, DateTime.UtcNow
                                                                    , tsOrgId, qualCode);
                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateOrgQualification(). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "accountId: " + accountId + "; qualCodeId: " + qualCodeId
                                                );
            }
        }

        public static void setNewOrgQualStatusDate(Guid orgDesigId, Guid accountId, DateTime newQualStatusDateUtc)
        {

            try
            {
                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet = new ColumnSet("ts_qualificationstatusdate", "ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, orgDesigId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                EntityCollection orgQualificationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count == 0)
                    return;

                Entity orgQualification = orgQualificationCollection.Entities.First();

                DateTime currentQualStatusDateUtc = orgQualification.GetAttributeValue<DateTime>("ts_qualificationstatusdate");


                OptionSetValue qualStatusOptionValue = orgQualification.GetAttributeValue<OptionSetValue>("ts_qualificationstatus");

                orgQualification["ts_qualificationstatusdate"] = newQualStatusDateUtc;
                DynamicsInterface.DataverseClient.Update(orgQualification);

                Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");

                orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQualification.Id);
                orgQualHistory["ts_qualificationstatusaction"] = qualStatusOptionValue;
                orgQualHistory["ts_qualificationactiondate"] = newQualStatusDateUtc;
                orgQualHistory["ts_name"] = orgQualification.Id.ToString() + " - " + newQualStatusDateUtc.ToString("yyyy-MM-dd HH:mm:ss");

                DynamicsInterface.DataverseClient.Create(orgQualHistory);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processOrgQualified(). Exception message: " + Environment.NewLine + e.Message
                     + Environment.NewLine + "accountId: " + accountId 
                    );
            }
        }
        public static Guid createOrgQualification(Guid accountId, Guid qualCodeId, int qualStatusCode, DateTime qualStatusDateUTC
                                                    , string tsOrgId, string qualCode)
        {
            Guid orgQualId = Guid.Empty;
            try
            {
                Entity orgQualification = new Entity("ts_organizationqualification");

                orgQualification["ts_qualificationstatus"] = new OptionSetValue(qualStatusCode);
                orgQualification["ts_qualificationstatusdate"] = qualStatusDateUTC;
                orgQualification["ts_accountid"] = new EntityReference("account", accountId);
                orgQualification["ts_qualificationcodeid"] = new EntityReference("new_qualificationcode", qualCodeId);
                orgQualification["ts_name"] = tsOrgId.ToString() + " - " + qualCode;

                orgQualId = DynamicsInterface.DataverseClient.Create(orgQualification);


                Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");

                orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQualId);
                orgQualHistory["ts_qualificationstatusaction"] = new OptionSetValue(qualStatusCode);
                orgQualHistory["ts_qualificationactiondate"] = DateTime.UtcNow;
                orgQualHistory["ts_name"] = orgQualId.ToString() + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                DynamicsInterface.DataverseClient.Create(orgQualHistory);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in createOrgQualification(). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "accountId: " + accountId + "; qualCodeId: " + qualCodeId
                    );
            }

            return orgQualId;
        }


        public static string getOrgQualStatus(Guid accountId, Guid qualCodeId)
        {
            string orgQualStatus = string.Empty;
            try
            {
                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                EntityCollection orgQualificationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                {
                    Entity orgQualification = orgQualificationCollection.Entities.First();

                    orgQualStatus = orgQualification.FormattedValues["ts_qualificationstatus"];
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in etOrgQualStatus(Guid accountId, Guid qualCodeId). Exception message: " + Environment.NewLine + e.Message
                   + Environment.NewLine + "accountId: " + accountId + "; qualCodeId: " + qualCodeId
                   );
            }

            return orgQualStatus;
        }
        public static string getOrgQualStatus(Guid accountId)
        {
            string orgQualStatus = string.Empty;
            try
            {
                Entity account = DynamicsInterface.DataverseClient.Retrieve("account", accountId, new ColumnSet("new_orgdesignation"));

                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;

                if (orgDesigId == Guid.Empty)
                    return "";

                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, orgDesigId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                EntityCollection orgQualificationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                {
                    Entity orgQualification = orgQualificationCollection.Entities.First();
                    orgQualStatus = orgQualification.FormattedValues["ts_qualificationstatus"];
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getOrgQualStatus(Guid accountId). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}accountId: {accountId}"
                   );
            }

            return orgQualStatus;
        }

        public static string getOrgQualStatus(Entity account)
        {
            string orgQualStatus = string.Empty;
            try
            {
               
                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;

                if (orgDesigId == Guid.Empty)
                    return "";

                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, orgDesigId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, account.Id);
                EntityCollection orgQualificationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                {
                    Entity orgQualification = orgQualificationCollection.Entities.First();
                    orgQualStatus = orgQualification.FormattedValues["ts_qualificationstatus"];
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getOrgQualStatus(Entity account). Exception message: " + Environment.NewLine + e.Message
                   + Environment.NewLine + "accountId: " + account.Id.ToString()
                   );
            }

            return orgQualStatus;
        }


        public static Entity getOrgQualCase(Entity account)
        {
            Entity caseEntity = null;
            try
            {

                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;

                if (orgDesigId == Guid.Empty)
                    return null;

                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet(true);
                queryQualCase.Criteria.AddCondition("casetypecode", ConditionOperator.Equal, 2);
                queryQualCase.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, orgDesigId);
                queryQualCase.Criteria.AddCondition("customerid", ConditionOperator.Equal, account.Id);
                queryQualCase.AddOrder("createdon", OrderType.Descending);
                queryQualCase.TopCount = 1;
                EntityCollection qualCaseCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQualCase);

                if (qualCaseCollection.Entities.Count > 0)
                    caseEntity = qualCaseCollection.Entities.First();

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getOrgQualCase(Entity account). Exception message: " + Environment.NewLine + e.Message
                                                   + Environment.NewLine + "accountId: " + account.Id.ToString()
                                                   );
            }

            return caseEntity;
        }

        public static async Task<Entity> retrieveOrgQualCase(Entity account)
        {            
            try
            {
                Entity caseEntity = null;
                EntityReference orgDesigRef = account.GetAttributeValue<EntityReference>("new_orgdesignation");
                Guid orgDesigId = orgDesigRef == null ? Guid.Empty : orgDesigRef.Id;

                if (orgDesigId == Guid.Empty)
                    return null;

                string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet(true);
                queryQualCase.Criteria.AddCondition("casetypecode", ConditionOperator.Equal, 2);
                queryQualCase.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, orgDesigId);
                queryQualCase.Criteria.AddCondition("customerid", ConditionOperator.Equal, account.Id);
                queryQualCase.AddOrder("createdon", OrderType.Descending);
                queryQualCase.TopCount = 1;
                EntityCollection qualCaseCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryQualCase);

                if (qualCaseCollection.Entities.Count > 0)
                {
                    caseEntity = qualCaseCollection.Entities.First();
                }
                else
                {
                    string qualStatus = getOrgQualStatus(account.Id, orgDesigId);

                    QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                    queryMapping.ColumnSet = new ColumnSet("ts_value", "ts_valuecode");
                    queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "ts_casestatus");
                    queryMapping.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, "Organization Qualification");
                    queryMapping.Criteria.AddCondition("ts_mappedfieldvalue", ConditionOperator.Equal, qualStatus);
                    EntityCollection mappingCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryMapping);

                    if (mappingCollection.Entities.Count == 0)
                    {
                        DynamicsInterface.writeToLog("At retrieveOrgQualCase(Entity account). No ts_fieldhierarchyandmapping found for ts_casestatus with ts_parentfieldvalue = 'Organization Qualification' and ts_mappedfieldvalue = " + qualStatus                                                   );
                        return null;
                    }

                    Entity fieldMapping = mappingCollection.Entities.First();
                    string tsCaseStatus = fieldMapping.GetAttributeValue<string>("ts_value");//Case status
                    int tsCaseStatusCode = fieldMapping.GetAttributeValue<int>("ts_valuecode");//Case status option value


                    Entity qualCodeEntity = await DynamicsInterface.DataverseClient.RetrieveAsync("new_qualificationcode", orgDesigId, new ColumnSet(true));
                    string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                    string qualTerm = qualCodeEntity.FormattedValues["new_qualterm"];
                    string qualCategory = qualCodeEntity.GetAttributeValue<string>("new_qualcategory");
                    string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");

                    //Guid caseId = ValidationServicesHelper.createCase(title: qualCode + " - " + qualName
                    //                                                        , caseTypeCode: 2
                    //                                                        , type: 101996
                    //                                                        , customerRef: new EntityReference(account.LogicalName, account.Id)
                    //                                                        , caseStatus: tsCaseStatusCode
                    //                                                        , qualCodeId: orgDesigId
                    //                                                        , extraCaseFields: null
                    //                                                        );

                    Guid caseId = await ValidationServicesHelper.createCaseGeneric(title: $"{qualCode} - {qualName} - TSOrgId: {tsOrgId}"
                                                                            , caseTypeCode: 2
                                                                            , type: 101996
                                                                            , customerRef: account.ToEntityReference()
                                                                            , caseStatus: tsCaseStatusCode
                                                                            , qualCodeId: qualCodeEntity.Id
                                                                            , extraCaseFields: null
                                                                            );

                    caseEntity = await DynamicsInterface.DataverseClient.RetrieveAsync("incident", caseId, new ColumnSet(true));
                }

                return caseEntity;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in retrieveOrgQualCase(Entity account). Exception message: " + Environment.NewLine + e.Message
                                                   + Environment.NewLine + "accountId: " + account.Id.ToString()
                                                   );
                return null;
            }

            
        }

        public static bool orgHasOpenOrders(string tsOrgId)
        {
            bool hasOpenOrders = false;
            try
            {
                string connString = DynamicsProcesses.Properties.Settings.Default.ServiceAdminConnectionString;
                //"Data Source=" + "pdrptdb2k14.prod.compumentor.org" + ";Initial Catalog=ServiceAdmin;Integrated Security=True;Encrypt=False";
                ServiceAdminDataContext context = new ServiceAdminDataContext(connString);
                IEnumerable<usp_orgsWithOrdersResult> query = null;

                context.Connection.Open();

                query = from orgquals in context.usp_orgsWithOrders(tsOrgId)
                        select orgquals;
                List<usp_orgsWithOrdersResult> result = query.ToList<usp_orgsWithOrdersResult>();

                if (result.Count > 0)
                    hasOpenOrders = result.First().hasOrders.Value;
                context.Dispose();
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in orgHasOpenOrders(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "tsOrgId: " + tsOrgId
                                                );
            }

            return hasOpenOrders;
        }

        public static bool isOrgEligibleDiffActivityCode(string tsOrgId, string activityCode)
        {
            bool isElig = false;
            try
            {
                string connString = DynamicsProcesses.Properties.Settings.Default.ServiceAdminConnectionString;
                //"Data Source=" + "pdrptdb2k14.prod.compumentor.org" + ";Initial Catalog=ServiceAdmin;Integrated Security=True;Encrypt=False";
                ServiceAdminDataContext context = new ServiceAdminDataContext(connString);
                IEnumerable<usp_getOrgOrderElig_newACResult> query = null;

                context.Connection.Open();

                query = from orgquals in context.usp_getOrgOrderElig_newAC(int.Parse(tsOrgId), activityCode)
                        select orgquals;
                List<usp_getOrgOrderElig_newACResult> result = query.ToList<usp_getOrgOrderElig_newACResult>();

                if (result.Count > 0)
                    isElig = result.First().isActivityCodeElig.Value;

                context.Dispose();
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in isOrgEligibleDiffActivityCode(...). Exception message: " + Environment.NewLine + e.Message
                                                 + Environment.NewLine + "tsOrgId: " + tsOrgId
                                                );
            }

            return isElig;
        }


        public static usp_getOrgQualTaskResult getOrgQualTask(int tsOrgId)
        {
            usp_getOrgQualTaskResult qualTask = null;
            try
            {
                DBAdminDataContext context = new DBAdminDataContext();
                IEnumerable<usp_getOrgQualTaskResult> recQuery = null;
                context.Connection.Open();

                recQuery = from orgquals in context.usp_getOrgQualTask(tsOrgId)
                           select orgquals;
                List<usp_getOrgQualTaskResult> recResult = recQuery.ToList<usp_getOrgQualTaskResult>();

                if (recResult.Count == 0)
                    return null;

                qualTask = recResult.First();
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getOrgQualTask(...). Exception message: " + Environment.NewLine + e.Message
                                             + Environment.NewLine + "tsOrgId: " + tsOrgId
                                            );
            }

            return qualTask;
        }

        public static bool isValidationServices(Entity account)
        {
            bool isValidationServicesSource = false;
            try
            {                
                string orgSource = !account.Contains("new_source") ? "" : account.FormattedValues["new_source"];
                string name = account.GetAttributeValue<string>("name");

                QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "nonValidationServicesSource");
                queryMapping.Criteria.AddCondition("ts_value", ConditionOperator.Equal, orgSource);

                EntityCollection mappingCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryMapping);

                if (mappingCollection.Entities.Count == 0)
                    isValidationServicesSource = true;


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in isValidationServices(...). Exception message: " + Environment.NewLine + e.Message
                                             + Environment.NewLine + "accountId: " + account.Id.ToString()
                                            );
            }

            return isValidationServicesSource;
        }
        public static Dictionary<string, bool> orgOpenOrders(string tsOrgIds)
        {
            Dictionary<string, bool> orgOrders = new Dictionary<string, bool>();
            bool hasOpenOrders = false;
            try
            {

                //"Data Source=" + "pdrptdb2k14.prod.compumentor.org" + ";Initial Catalog=ServiceAdmin;Integrated Security=True;Encrypt=False";
                ServiceAdminDataContext context = new ServiceAdminDataContext();
                IEnumerable<usp_orgsWithOrdersResult> query = null;

                context.Connection.Open();

                query = from orgquals in context.usp_orgsWithOrders(tsOrgIds)
                        select orgquals;
                List<usp_orgsWithOrdersResult> result = query.ToList<usp_orgsWithOrdersResult>();


                foreach (usp_orgsWithOrdersResult item in result)
                {
                    orgOrders[item.tsOrgId] = item.hasOrders.Value;
                }
                context.Dispose();
            }
            catch (Exception e)
            {

            }

            return orgOrders;
        }
        public static float levenshteinMatchScore(string value1, string value2)
        {
            float levenshteinScore = 0;
            try
            {
                value1 = value1 == null ? "" : value1.ToLower();
                value2 = value2 == null ? "" : value2.ToLower();

                float levenshteinDistance = Fastenshtein.Levenshtein.Distance(value1, value2);
                float topLength = Math.Max(value1.Length, value2.Length);
                levenshteinScore = (topLength - levenshteinDistance) / topLength;

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in levenshteinMatchScore(...). Exception message: " + Environment.NewLine + e.Message
                                                 + Environment.NewLine + "value1: " + value1 + "; value2: " + value2
                                                );
            }

            return levenshteinScore;
        }


        public static void removeCaseFromQueue(Guid caseId)
        {
            try
            {
                QueryExpression queryQueueItem = new QueryExpression("queueitem");
                queryQueueItem.Criteria.AddCondition("objectid", ConditionOperator.Equal, caseId);
                EntityCollection queueItemCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryQueueItem);

                if (queueItemCollection.Entities.Count > 0)
                {
                    Guid queueItemId = queueItemCollection.Entities.First().Id;
                    DynamicsInterface.DataverseClient.Delete("queueitem", queueItemId);
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in removeCaseFromQueue(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}caseId: {caseId.ToString()}"
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
                DynamicsInterface.writeToLog($"Error in processSystemNote(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}noteDesc length: {noteDesc.Length}; noteTitle: {noteTitle}; noteDesc: {noteDesc}"
                                                + $"{Environment.NewLine}{annotationParentRef.LogicalName}Id: {annotationParentRef.Id.ToString()}"
                                                );
            }
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

        public static async Task<bool> existsSystemNoteAsync(string noteTitle, EntityReference annotationParentRef)
        {
            bool existsNote = false;
            try
            {
                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryAnnotation);

                if (annotationCollection.Entities.Count() > 0)
                    existsNote = true;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in existsSystemNote(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}noteTitle: {noteTitle}"
                                                + $"{Environment.NewLine}{annotationParentRef.LogicalName}Id: {annotationParentRef.Id.ToString()}"
                                                );
            }
            return existsNote;
        }

        public static bool removeSystemNote(string noteTitle, EntityReference annotationParentRef)
        {
            bool success = false;
            try
            {
                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryAnnotation);

                if (annotationCollection.Entities.Count() > 0)
                    DynamicsInterface.DataverseClient.Delete("annotation", annotationCollection.Entities.First().Id);

                success = true;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in removeSystemNote(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}noteTitle: {noteTitle}"
                                                + $"{Environment.NewLine}{annotationParentRef.LogicalName}Id: {annotationParentRef.Id.ToString()}"
                                                );


                
            }
            return success;
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
                DynamicsInterface.writeToLog($"Error in getSystemNote(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}noteTitle: {noteTitle}"
                                                + $"{Environment.NewLine}{annotationParentRef.LogicalName}Id: {annotationParentRef.Id.ToString()}"
                                                );
            }
            return annotation;
        }

        

        public static string getUserRegistrationIpAddress(string email)
        {
            string userIpAddress = null;
            try
            {
                string connString = "Data Source=" + "cmcolorptdb2k8.compumentor.org" + ";Initial Catalog=ServiceAdmin;Integrated Security=True;Encrypt=False";
                ServiceAdminDataContext context = new ServiceAdminDataContext(connString);
                IEnumerable<usp_getUserRegIpAddressResult> query = null;

                context.Connection.Open();

                query = from userIpInfo in context.usp_getUserRegIpAddress(email)
                        select userIpInfo;
                List<usp_getUserRegIpAddressResult> result = query.ToList<usp_getUserRegIpAddressResult>();

                if (result.Count > 0)
                    userIpAddress = result.First().userIpAddress;

                context.Dispose();
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getUserRegistrationIpAddress(...). Exception message: " + Environment.NewLine + e.Message
                                                 + Environment.NewLine + "email: " + email
                                                );
            }

            return userIpAddress;
        }

        public static string getNewTSOrgContactId()
        {
            string newTSOrgContactId = null;
            try
            {
                DataAccessServiceClient client = new DataAccessServiceClient();
                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();
                objRequest.ServerName = DynamicsInterface.Sql2kServer;
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_getNextOnyxId";
                ExecuteStoredProcResponseType response = client.ExecuteStoredProc(objRequest);

                rowType[] returnXml = response.ReturnXml;
                if (returnXml.Length > 0)
                    newTSOrgContactId = returnXml.First().Any[0].InnerText;

                client.Close();

                return newTSOrgContactId;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getNewTSOrgContactId(...). Exception message: " + Environment.NewLine + e.Message);
                return null;
            }
        }
        public static string getNextTsCustomerId()
        {
            string tsCustomerId = null;
            try
            {
                DataAccessServiceClient client = new DataAccessServiceClient();
                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();
                objRequest.ServerName = DynamicsInterface.Sql2kServer;
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_getNextOnyxId";
                ExecuteStoredProcResponseType response = client.ExecuteStoredProc(objRequest);

                rowType[] returnXml = response.ReturnXml;               
                if (returnXml.Length > 0)
                    tsCustomerId = returnXml.First().Any[0].InnerText;

                client.Close();

                return tsCustomerId;
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getNextTsCustomerId(...). Exception message: " + Environment.NewLine + e.Message);
                return null;
            }            
        }
    }

}
