
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
using EDServices.DataAccessService;
using System.Xml;
using System.Collections.Generic;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using System.Net.Mail;

using System.IdentityModel.Metadata;
using System.Xml.Linq;
using System.Net.NetworkInformation;
using System.Web.Configuration;
using EDServices;

using Microsoft.PowerPlatform.Dataverse.Client;
using System.IO;
using System.Net;
using static System.Net.WebRequestMethods;
using System.Drawing;
using System.Diagnostics.Contracts;

namespace EDServices
{
    
    public class EDServicesRequest : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        //public static List<string> ErrorStack;


        

        public void Execute(IServiceProvider serviceProvider)
        {


            ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            EDServicesHelper.writeToTrace("EDServices - GeneralServices.EDServicesRequest", tracingService);

            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);


                EDServicesHelper.EnvVariables = EDServicesHelper.GetEnvironmentVariables(service);


                RetrieveCurrentOrganizationRequest request = new RetrieveCurrentOrganizationRequest();
                RetrieveCurrentOrganizationResponse response = (RetrieveCurrentOrganizationResponse)service.Execute(request);
                string DynamicsEnvironmentCurrentUrl = "https://" + response.Detail.UrlName + ".crm.dynamics.com";

                if (EDServicesHelper.DynamicsEnvironments.ContainsKey(DynamicsEnvironmentCurrentUrl))
                {
                    string DynamicsEnvironmentCurrentName = EDServicesHelper.DynamicsEnvironments[DynamicsEnvironmentCurrentUrl];
                    EDServicesHelper.DynamicsEnvironments["DynamicsEnvironmentCurrent"] = DynamicsEnvironmentCurrentName;
                }





                #region Get ts_request
                Entity tsRequest = (Entity)context.InputParameters["ts_request"];
                #endregion

                try
                {
                    string tsRequestText = JsonConvert.SerializeObject(tsRequest);
                    EDServicesHelper.writeToTrace("tsRequest: " + tsRequestText
                                                                                , tracingService);
                }
                catch (Exception ex)
                {
                    EDServicesHelper.writeToTrace("Error serializing tsRequest for trace: " + ex.Message, tracingService);
                }
                


                Entity tsResponse = new Entity();

                List<string> errorStack = new List<string>();


                string requestName = tsRequest.Contains("requestName") ? tsRequest.GetAttributeValue<string>("requestName") : string.Empty;


                if (requestName == string.Empty)
                {
                    string error = "No requestName provided";
                    EDServicesHelper.writeToTrace(error, tracingService);
                    errorStack.Add(error);

                    tsResponse.Attributes.Add("error", error);
                    context.OutputParameters["ts_response"] = tsResponse;
                    return;
                }

                /****************************************************/





                EDServicesHelper.writeToTrace("requestName: " + requestName, tracingService);

                string errorStackText = string.Empty;
                tsResponse.Attributes.Add("requestName", requestName);


                switch (requestName)
                {

                    case "getOrgQualStatus":
                        getOrgQualStatus(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "edRequest":
                        processEdRequest(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "getNetSuiteToken":
                        getNetSuiteToken(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "uploadEdFile":
                        uploadEdFile(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "edFileList":
                        getEdFileList(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "edFileListByCategory":
                        getEdFileList(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "getEdFileContent":
                        getEdFileContent(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "deleteEdFile":
                        deleteEdFile(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "getEdFileVersions":
                        getFileVersions(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "getFileVersionContent":
                        getFileVersionContent(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "netsuiteApiCall":
                        makeNetSuiteCall(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;
                    case "testRequest":
                        testRequest(tsRequest, tsResponse
                                                                , context, service, tracingService, errorStack);
                        break;

                    case "findEdRequestCase":
                        findEdRequestCase(tsRequest, tsResponse
                                                            , context, service, tracingService, errorStack);
                        break;

                    case "getEdDynamicsCaseIds":
                        getEdCaseIds(tsRequest, tsResponse
                                                            , context, service, tracingService, errorStack);
                        break;

                    case "findOrganizationMatches":
                        findOrganizationMatches(tsRequest, tsResponse
                                                            , context, service, tracingService, errorStack);
                        break;

                    case "retrieveNgoFromEdRequest":
                        retrieveNgoFromEdCase(tsRequest, tsResponse
                                                            , context, service, tracingService, errorStack);
                        break;

                    case "getPngoIdFromEd":
                        getPngoIdFromEd(tsRequest, tsResponse
                                                            , context, service, tracingService, errorStack);
                        break;




                    default:
                        tsResponse.Attributes.Add("error", "Invalid requestName");
                        context.OutputParameters["ts_response"] = tsResponse;
                        return;

                }


                if (tsRequest.Contains("requestBodyText"))
                {
                    string tsServicesRequest = JsonConvert.SerializeObject(JsonConvert.DeserializeObject(tsRequest.GetAttributeValue<string>("requestBodyText")), Newtonsoft.Json.Formatting.Indented);

                    EDServicesHelper.writeToTrace("tsServicesRequest: " + Environment.NewLine + tsServicesRequest
                                                                                                                , tracingService);
                }
                else
                {
                    EDServicesHelper.writeToTrace(requestName + " did not contain requestBodyText", tracingService);
                }


                if (errorStack.Count > 0)
                    errorStackText = EDServicesHelper.getErrorsFromStack(errorStack);

                if (!string.IsNullOrEmpty(errorStackText) || tsResponse.Contains("error"))
                {
                    if (!tsResponse.Contains("resultStatus"))
                        tsResponse.Attributes.Add("resultStatus", "failure");

                    if (!tsResponse.Contains("error"))
                        tsResponse.Attributes.Add("error", errorStackText);
                }


                #region Set ts_response
                context.OutputParameters["ts_response"] = tsResponse;
                #endregion

            }

            catch (Exception e)
            {
                EDServicesHelper.writeToTrace("Error during EDServicesActionRequest: " + e.Message, tracingService);
            }

        }
        public static IOrganizationService getDynamicsOrganizationService(string targetEnv
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            IOrganizationService dataverseClient = null;

            string dynamicsEnv = EDServicesHelper.DynamicsEnvironments[targetEnv];
            string clientId = EDServicesHelper.EnvVariables["ts_TSDynamicsClientId"];
            string clientSecret = EDServicesHelper.EnvVariables["ts_TSDynamicsClientSecret"];
            try
            {
                string connectionString = $"AuthType=ClientSecret;Url={dynamicsEnv};ClientId={clientId};ClientSecret={clientSecret};";              
                
                dataverseClient = new Microsoft.PowerPlatform.Dataverse.Client.ServiceClient(connectionString);
            }
            catch (Exception e)
            {
                EDServicesHelper.writeToTrace("Error connecting to Dynamics Environment: " + dynamicsEnv + ". Exception message: " + Environment.NewLine + e.Message, tracingService);
            }
            return dataverseClient;
        }

         public static void makeNetSuiteCall(Entity tsRequest, Entity tsResponse
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                EDServicesHelper.writeToTrace("Starting makeNetSuiteCall", tracingService);

                string url = tsRequest.Contains("url") ? tsRequest.GetAttributeValue<string>("url") : "";
                string httpMethod = tsRequest.Contains("httpMethod") ? tsRequest.GetAttributeValue<string>("httpMethod") : "";

                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(httpMethod)
                    )
                {
                    tsResponse.Attributes.Add("error", "url and httpMethod must be provided for requestName: " + tsRequest.GetAttributeValue<string>("requestName"));
                    return;
                }


                if (httpMethod.ToUpper() == "POST")
                {
                    string netSuiteRequest = tsRequest.Contains("netSuiteRequest") ? tsRequest.GetAttributeValue<string>("netSuiteRequest") : "";
                    if (string.IsNullOrEmpty(netSuiteRequest))
                    {
                        tsResponse.Attributes.Add("error", "netSuiteRequest must be provided for POST httpMethod in requestName: " + tsRequest.GetAttributeValue<string>("requestName"));
                        return;
                    }

                    dynamic netSuiteRequestObj = JsonConvert.DeserializeObject(netSuiteRequest);
                    netSuiteRequest = JsonConvert.SerializeObject(netSuiteRequestObj, Newtonsoft.Json.Formatting.Indented);

                    EDServicesHelper.writeToTrace("netSuiteRequest: " + Environment.NewLine + netSuiteRequest, tracingService);


                    string authorization = getNetSuiteToken(url, httpMethod, tracingService, errorStack);
                    if (errorStack.Count > 0)
                        return;


                    Dictionary<string, string> extraHeaders = new Dictionary<string, string>();
                    extraHeaders["Authorization"] = authorization;
                    extraHeaders["User-Agent"] = "SuiteScript-Call";

                    dynamic netSuiteResponse = EDServicesHelper.makeHttpPostCall(
                                                                                netSuiteRequest
                                                                                , url
                                                                                , tracingService
                                                                                , errorStack
                                                                                , extraHeaders
                                                                                );
                    if (errorStack.Count > 0)
                        return;


                    string netSuiteResponseText = JsonConvert.SerializeObject(netSuiteResponse, Newtonsoft.Json.Formatting.None);

                    tsResponse.Attributes.Add("netSuiteResponse", netSuiteResponseText);

                    return;
                }
                else if (httpMethod.ToUpper() == "GET")
                {
                    string authorization = getNetSuiteToken(url, httpMethod, tracingService, errorStack);
                    if (errorStack.Count > 0)
                        return;


                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "GET";
                    request.ContentType = "application/json";
                    request.Headers["Authorization"] = authorization;
                    request.UserAgent = "SuiteScript-Call";

                    
                    HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                    
                    string content = "";
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    {
                        content = reader.ReadToEnd();
                    }

                    string netSuiteResponseText = content;
                    tsResponse.Attributes.Add("netSuiteResponse", netSuiteResponseText);

                    return;
                }
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At makeNetSuiteCall(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }

        public static string getErrorsFromStack(List<string> errorStack)
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


        //public static void testRequest(Entity tsRequest, Entity tsResponse
        //                                                                            , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
        //                                                                            , List<string> errorStack)
        //{
        //    try
        //    {
               

        //        testAddToList(context, service, tracingService, errorStack);

        //        string errorStackText = getErrorsFromStack(errorStack);


        //        tsResponse.Attributes.Add("resultStatus", "success");
        //        tsResponse.Attributes.Add("errorStack", errorStack.ToArray());
        //        tsResponse.Attributes.Add("errorStackText", errorStackText);
        //    }
        //    catch (Exception e)
        //    {
        //        string error = "Error: " + e.Message;
        //        EDServicesHelper.writeToTrace("At testRequest(...). " + error, tracingService);
        //        errorStack.Add(error);
        //    }
        //}

        public static void testAddToList(IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                errorStack.Add("One error");
                errorStack.Add("2 error");
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At testAddToList(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }



        public static void getOrgQualStatus(Entity tsRequest, Entity tsResponse
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                string tsOrgId = tsRequest.Contains("tsOrgId") ? tsRequest.GetAttributeValue<string>("tsOrgId") : string.Empty;

                if (string.IsNullOrEmpty(tsOrgId))
                {
                    tsResponse.Attributes.Add("error", "tsOrgId is required for action: " + tsRequest.GetAttributeValue<string>("action"));
                    return;
                }

                string orgQualStatus = EDServicesHelper.getOrgQualStatus(tsOrgId, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;


                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("tsOrgId", tsOrgId);
                tsResponse.Attributes.Add("orgQualStatus", orgQualStatus);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getOrgQualStatus(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }



        public static void testRequest(Entity tsRequest, Entity tsResponse
                                                                                , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                , List<string> errorStack)
        {
            try
            {
                Entity organization = tsRequest.GetAttributeValue<Entity>("organization");

                string ngoTsOrgId = "";
                Entity accountNgo = null;
                if (organization.Contains("tsOrgId"))
                {
                    ngoTsOrgId = organization.GetAttributeValue<string>("tsOrgId");

                    accountNgo = EDServicesHelper.getAccountByTsOrgId(ngoTsOrgId, service, tracingService, errorStack);

                    if (errorStack.Count > 0)
                        return;
                }
                else
                {
                    IDictionary<string, System.Object> accountMatchResponse = EDServicesHelper.findOrganizationAccountMatches(organization
                                                                                                                                        , service, tracingService, errorStack);

                    if (errorStack.Count > 0)
                        return;

                    if (accountMatchResponse.ContainsKey("tsOrgId"))
                    {
                        ngoTsOrgId = accountMatchResponse["tsOrgId"].ToString();

                        accountNgo = EDServicesHelper.getAccountByTsOrgId(ngoTsOrgId, service, tracingService, errorStack);

                        if (errorStack.Count > 0)
                            return;
                    }
                    else
                    {
                        //create new organization account
                        accountNgo = EDServicesHelper.createNgo(organization
                                                                            , service, tracingService, errorStack);

                        if (errorStack.Count > 0)
                            return;

                        ngoTsOrgId = accountNgo.GetAttributeValue<string>("accountnumber");
                    }

                }

                EDServicesHelper.addOptionToFieldCollection(accountNgo, "ts_accounttype", 14
                                                                                        , service, tracingService, errorStack); //14 - NGO



                EntityCollection contacts = organization.GetAttributeValue<EntityCollection>("contacts");

                foreach (Entity edContact in contacts.Entities)
                {
                    Entity contact = EDServicesHelper.addContact(edContact, service, tracingService, errorStack);
                    if (contact != null)
                        EDServicesHelper.connectContactToAccount(ngoTsOrgId, accountNgo.Id, contact.Id
                                                                                        , service, tracingService, errorStack);
                }


                Entity grantMaker = tsRequest.GetAttributeValue<Entity>("grantMaker");

                string grantMakerTsOrgId = grantMaker.GetAttributeValue<string>("tsOrgId");

                EDServicesHelper.writeToTrace("grantMakerTsOrgId: " + grantMakerTsOrgId, tracingService);

                Entity accountGrantMaker = EDServicesHelper.getAccountByTsOrgId(grantMakerTsOrgId, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                EDServicesHelper.addOptionToFieldCollection(accountGrantMaker, "ts_accounttype", 17
                                                                                       , service, tracingService, errorStack); //17 - Grantmaker
                Entity grantMakerContact = null;
                if (grantMaker.Contains("contact"))
                {
                    Entity edContact = grantMaker.GetAttributeValue<Entity>("contact");

                    grantMakerContact = EDServicesHelper.addContact(edContact, service, tracingService, errorStack);
                    if (grantMakerContact != null)
                        EDServicesHelper.connectContactToAccount(grantMakerTsOrgId, accountGrantMaker.Id, grantMakerContact.Id
                                                                                        , service, tracingService, errorStack);

                }

                tsResponse.Attributes.Add("ngoTsOrgId", ngoTsOrgId);
                tsResponse.Attributes.Add("grantMakerTsOrgId", grantMakerTsOrgId);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At testRequest(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }





        public static void processEdRequest(Entity tsRequest, Entity tsResponse
                                                                                , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                , List<string> errorStack)
        {
            try
            {
                Entity organization = tsRequest.GetAttributeValue<Entity>("organization");

                string ngoTsOrgId = "";
                Entity accountNgo = null;
                if (organization.Contains("tsOrgId") && !string.IsNullOrEmpty(organization.GetAttributeValue<string>("tsOrgId")))
                {
                    ngoTsOrgId = organization.GetAttributeValue<string>("tsOrgId");

                    accountNgo = EDServicesHelper.getAccountByTsOrgId(ngoTsOrgId, service, tracingService, errorStack);

                    if (errorStack.Count > 0)
                        return;
                }
                else
                {
                    IDictionary<string, System.Object> accountMatchResponse = EDServicesHelper.findOrganizationAccountMatches(organization
                                                                                                                                        , service, tracingService, errorStack);

                    if (errorStack.Count > 0)
                        return;

                    if (accountMatchResponse.ContainsKey("tsOrgId"))
                    {
                        ngoTsOrgId = accountMatchResponse["tsOrgId"].ToString();

                        accountNgo = EDServicesHelper.getAccountByTsOrgId(ngoTsOrgId, service, tracingService, errorStack);

                        if (errorStack.Count > 0)
                            return;
                    }
                    else
                    {
                        //create new organization account
                        accountNgo = EDServicesHelper.createNgo(organization
                                                                            , service, tracingService, errorStack);

                        if (errorStack.Count > 0)
                            return;

                        ngoTsOrgId = accountNgo.GetAttributeValue<string>("accountnumber");
                    }

                }

                EDServicesHelper.addOptionToFieldCollection(accountNgo, "ts_accounttype", 14
                                                                                        , service, tracingService, errorStack); //14 - NGO


                if (errorStack.Count > 0)
                    return;

                EntityCollection contacts = organization.GetAttributeValue<EntityCollection>("contacts");

                foreach (Entity edContact in contacts.Entities)
                {
                    Entity contact = EDServicesHelper.addContact(edContact, service, tracingService, errorStack);
                    if (contact != null)
                        EDServicesHelper.connectContactToAccount(ngoTsOrgId, accountNgo.Id, contact.Id
                                                                                        , service, tracingService, errorStack);
                }

                Entity grantMaker = tsRequest.GetAttributeValue<Entity>("grantMaker");

                string grantMakerTsOrgId = grantMaker.GetAttributeValue<string>("tsOrgId");

                EDServicesHelper.writeToTrace("grantMakerTsOrgId: " + grantMakerTsOrgId, tracingService);

                Entity accountGrantMaker = EDServicesHelper.getAccountByTsOrgId(grantMakerTsOrgId, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                EDServicesHelper.addOptionToFieldCollection(accountGrantMaker, "ts_accounttype", 17
                                                                                       , service, tracingService, errorStack); //17 - Grantmaker

                if (!grantMaker.Contains("contact"))
                {
                    string error = "Grantmaker contact was not provided";
                    EDServicesHelper.writeToTrace(error, tracingService);
                    errorStack.Add(error);
                    return;
                }

                Entity edGmContact = grantMaker.GetAttributeValue<Entity>("contact");

                if (!edGmContact.Contains("tsContactId"))
                {
                    string error = "tsContactId for Grantmaker contact was not provided";
                    EDServicesHelper.writeToTrace(error, tracingService);
                    errorStack.Add(error);
                    return;
                }

                Entity grantMakerContact = EDServicesHelper.findContact(edGmContact.GetAttributeValue<string>("tsContactId"), service, tracingService, errorStack);
                if (grantMakerContact == null)
                {
                    string error = "Grantmaker contact could not be found";
                    EDServicesHelper.writeToTrace(error, tracingService);
                    errorStack.Add(error);
                    return;
                }

                EDServicesHelper.connectContactToAccount(grantMakerTsOrgId, accountGrantMaker.Id, grantMakerContact.Id
                                                                                    , service, tracingService, errorStack);



                Entity edCase = EDServicesHelper.getEdCase(tsRequest, accountNgo
                                                                        , service, tracingService, errorStack);

                if (edCase == null)
                {
                    string error = "ED Case could not be created or retrieved";
                    EDServicesHelper.writeToTrace(error, tracingService);
                    errorStack.Add(error);
                    return;
                }

                QueryExpression queryCase = new QueryExpression("incident");
                queryCase.Criteria.AddCondition("casetypecode", ConditionOperator.Equal, 4);
                queryCase.Criteria.AddCondition("customerid", ConditionOperator.Equal, accountGrantMaker.Id);
                queryCase.Criteria.AddCondition("parentcaseid", ConditionOperator.Equal, edCase.Id);
                EntityCollection caseCollection = service.RetrieveMultiple(queryCase);


                if (caseCollection.Entities.Count > 0)
                {
                    string error = "An ED Request case already exists for Grantmaker TSOrgId: " + grantMakerTsOrgId + " under the ED Case with incidentId: " + edCase.GetAttributeValue<string>("ts_tsincidentid");
                    EDServicesHelper.writeToTrace(error, tracingService);
                    errorStack.Add(error);
                    return;
                }


                Entity qualCodeEntity = EDServicesHelper.getQualCodeEntity("NGOR-EDApp"
                                                                                    , service, tracingService, errorStack);

                string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");


                string tsIncidentId = edCase.GetAttributeValue<string>("ts_tsincidentid");


                DateTime dueDate = DateTime.Parse(tsRequest.GetAttributeValue<string>("dueDate"));


                object processInCtpObj = tsRequest.GetAttributeValue<object>("processInCtp");

                bool processInCtp = true;
                if (processInCtpObj is bool boolVar)
                    processInCtp = boolVar;
                else
                    processInCtp = processInCtpObj.ToString() == "1";


                bool checkBoardSanctions = tsRequest.Contains("checkBoardSanctions") ? tsRequest.GetAttributeValue<bool>("checkBoardSanctions") : false;
                string edRequestType = tsRequest.Contains("edType") ? tsRequest.GetAttributeValue<string>("edType") : "";
                string productId = tsRequest.Contains("productId") ? tsRequest.GetAttributeValue<string>("productId") : "";

                Dictionary<string, object> extraCaseFields = new Dictionary<string, object>();


                extraCaseFields.Add("parentcaseid", new EntityReference("incident", edCase.Id));
                extraCaseFields.Add("ts_tsincidentid", tsIncidentId + "-" + grantMakerTsOrgId);
                extraCaseFields.Add("ts_edstatus", new OptionSetValue(102244)); // ED Requested
                extraCaseFields.Add("ts_duedate", TimeZoneInfo.ConvertTimeToUtc(dueDate, pstZone));
                extraCaseFields.Add("ts_processinctp", processInCtp);
                extraCaseFields.Add("ts_edrequestcheckboardsanctions", checkBoardSanctions);
                extraCaseFields.Add("ts_edrequesttype", edRequestType);
                extraCaseFields.Add("ts_productid", productId);
                extraCaseFields.Add("ts_casecontactid", new EntityReference("contact", grantMakerContact.Id));


                Guid edRequestCaseId = EDServicesHelper.createCaseGeneric(title: $"ED Request (NGOId: {ngoTsOrgId}) (IncidentId: {tsIncidentId}) (GMTSOrgId: {grantMakerTsOrgId})"
                                                                                , caseTypeCode: 4 //ED Request Case
                                                                                , type: null
                                                                                , customerRef: accountGrantMaker.ToEntityReference()
                                                                                , caseStatus: null
                                                                                , qualCodeId: qualCodeEntity.Id
                                                                                , extraCaseFields: extraCaseFields
                                                                                , service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                Entity edRequestCase = service.Retrieve("incident", edRequestCaseId, new ColumnSet("ts_tsincidentid"));
                edRequestCase["ts_originalcaseid"] = null;
                edRequestCase["ts_previouscaseid"] = null;
                edRequestCase["ts_nextcaseid"] = null;
                service.Update(edRequestCase);

                string ngoFolderBoxId = EDServicesHelper.processBoxFolderNGO(accountNgo, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;


                IDictionary<string, Object> edFolderBoxResponse = EDServicesHelper.processBoxFolderED(edCase, accountNgo, ngoFolderBoxId, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                if ((bool)edFolderBoxResponse["createdNew"])
                {
                    Dictionary<string, string> boxEDSubfolders = EDServicesHelper.processBoxEDSubfolders((string)edFolderBoxResponse["edFolderBoxId"], service, tracingService, errorStack);

                    if (errorStack.Count > 0)
                        return;
                }


                EDServicesHelper.connectAccountToAccount(grantMakerTsOrgId, accountGrantMaker.Id, accountNgo.Id
                                                                                       , service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                EntityReference pngoAccountRef = edCase.GetAttributeValue<EntityReference>("ts_pngoaccountid");
                Entity pngoAccount = pngoAccountRef == null ? null : service.Retrieve(pngoAccountRef.LogicalName, pngoAccountRef.Id, new ColumnSet("accountnumber"));
                string pngoAdminId = pngoAccount?.GetAttributeValue<string>("accountnumber") ?? "";

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("incidentId", tsIncidentId);
                tsResponse.Attributes.Add("dynamicsCaseId", edCase.Id.ToString());
                tsResponse.Attributes.Add("pngoAdminId", pngoAdminId);
                tsResponse.Attributes.Add("ngoTsOrgId", ngoTsOrgId);
                tsResponse.Attributes.Add("grantMakerTsOrgId", grantMakerTsOrgId);
                
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At processEdRequest(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }


        



        public static void uploadEdFile(Entity tsRequest, Entity tsResponse
                                                                            , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                            , List<string> errorStack)
        {
            try
            {
                EDServicesHelper.writeToTrace("Starting uploadEdFile", tracingService);

                string tsIncidentId = tsRequest.Contains("incidentId") ? tsRequest.GetAttributeValue<string>("incidentId") : string.Empty;
                string category = tsRequest.Contains("category") ? tsRequest.GetAttributeValue<string>("category") : string.Empty;

                string fileName = tsRequest.Contains("fileName") ? tsRequest.GetAttributeValue<string>("fileName") : string.Empty;
                int? fileSize = tsRequest.Contains("fileSize") ? tsRequest.GetAttributeValue<int>("fileSize") : null;
                string contentType = tsRequest.Contains("contentType") ? tsRequest.GetAttributeValue<string>("contentType") : string.Empty;
                string fileContent = tsRequest.Contains("fileContent") ? tsRequest.GetAttributeValue<string>("fileContent") : string.Empty;
                string uploaderEmail = tsRequest.Contains("uploaderEmail") ? tsRequest.GetAttributeValue<string>("uploaderEmail") : string.Empty;
                string descriptionTag = tsRequest.Contains("description") ? tsRequest.GetAttributeValue<string>("description") : string.Empty;

                if (string.IsNullOrEmpty(tsIncidentId) || string.IsNullOrEmpty(category))
                {
                    tsResponse.Attributes.Add("error", "incidentId and category are required for requestName: " + tsRequest.GetAttributeValue<string>("requestName"));
                    return;
                }

                if (string.IsNullOrEmpty(fileName) || fileSize == null || string.IsNullOrEmpty(contentType) || string.IsNullOrEmpty(fileContent))
                {
                    tsResponse.Attributes.Add("error", "fileName, fileSize, contentType and fileContent are required for requestName: " + tsRequest.GetAttributeValue<string>("requestName"));
                    return;
                }


                string boxRootId = EDServicesHelper.EnvVariables["ts_BoxNGOSourceCertificationsFolderId"];

                QueryExpression queryCase = new QueryExpression("incident");
                queryCase.ColumnSet = new ColumnSet(true);
                queryCase.Criteria.AddCondition("ts_tsincidentid", ConditionOperator.Equal, tsIncidentId);
                EntityCollection caseCollection = service.RetrieveMultiple(queryCase);

                if (caseCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", "ED Case for incidentId: " + tsIncidentId + "not found");
                    return;
                }

                Entity edCase = caseCollection.Entities.First();

                QueryExpression externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery.ColumnSet = new ColumnSet(true);
                externalSystemRefQuery.Criteria.AddCondition("ts_objectid", ConditionOperator.Equal, edCase.Id);
                externalSystemRefQuery.Criteria.AddCondition("ts_externalsystemname", ConditionOperator.Equal, "Box");
                externalSystemRefQuery.Criteria.AddCondition("ts_referencetype", ConditionOperator.Equal, 2); //referenceType: 2 - ED Folder Id
                EntityCollection externalSystemRefCollection = service.RetrieveMultiple(externalSystemRefQuery);

                if (externalSystemRefCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", "No Box folder found for ED");
                    return;
                }

                Entity externalReferenceEdBoxFolder = externalSystemRefCollection.Entities.First();

                string edFolderBoxId = externalReferenceEdBoxFolder.GetAttributeValue<string>("ts_referencevalue");

                string folderName = category;

                //Entity boxFolderDetails = EDServicesHelper.findBoxFolderByName(folderName, edFolderBoxId);

                Entity boxFolderDetails = EDServicesHelper.findBoxFolderGoingDownTheTree(folderName, edFolderBoxId
                                                                                                                , service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                if (!boxFolderDetails.Contains("folderId") || string.IsNullOrEmpty(boxFolderDetails.GetAttributeValue<string>("folderId"))
                    || boxFolderDetails.GetAttributeValue<string>("folderId") == boxRootId
                    )
                {
                    tsResponse.Attributes.Add("error", "Folder '" + folderName + "' could not be found ");
                    return;
                }


                string targetFolderBoxId = boxFolderDetails.GetAttributeValue<string>("folderId");


                //EntityCollection folderContentsCollection = boxFolderDetails.GetAttributeValue<EntityCollection>("folderContents");

                //Entity fileMatch = folderContentsCollection.Entities.Where(item => item.GetAttributeValue<string>("name").ToLower() == fileName.ToLower())?.FirstOrDefault();

                //if (fileMatch != null)
                //{

                //    string fileId = fileMatch.GetAttributeValue<string>("id");
                //    string createdOn = fileMatch.GetAttributeValue<string>("createdOn");


                //    if (folderName.ToUpper() == "SYSTEM ADDED")
                //    {
                //        string timestamp = EDServicesHelper.regexReplace(@"-\d{2}:\d{2}", createdOn, "");
                //        timestamp = "_" + timestamp.Replace("T", "").Replace("-", "").Replace(":", "");


                //        string extension = System.IO.Path.GetExtension(fileName);

                //        string versionedFileName = fileName.Replace(extension, timestamp + extension);

                //bool success = EDServicesHelper.renameBoxItem("File", fileId, versionedFileName, service, tracingService);

                //        if (!success)
                //        {
                //            tsResponse.Attributes.Add("error", "Error renaming existing file with the same name in Box folder");
                //            return;
                //        }
                //    }
                //    else
                //    {
                //        bool success = EDServicesHelper.deleteBoxItem("File", fileId, service, tracingService);

                //        if (!success)
                //        {
                //            tsResponse.Attributes.Add("error", "Error deleting existing file with the same name in Box folder");
                //            return;
                //        }
                //    }
                //}


                string fileId = EDServicesHelper.fileUpload(targetFolderBoxId, fileName
                                                                            , fileSize.Value, fileContent, contentType
                                                                            , uploaderEmail, descriptionTag
                                                                            , service, tracingService, errorStack);             
                            
                if (errorStack.Count > 0 || string.IsNullOrWhiteSpace(fileId))
                    {
                        tsResponse.Attributes.Add("error", "Error uploading file to Box folder");
                        return;
                    }

                
                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("fileId", fileId);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At uploadEdFile(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }

        public static void getEdFileListForNGO(Entity tsRequest, Entity tsResponse, string ngoTsOrgId
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                QueryExpression queryAccountNgo = new QueryExpression("account");
                queryAccountNgo.ColumnSet = new ColumnSet(true);
                queryAccountNgo.Criteria.AddCondition("accountnumber", ConditionOperator.Equal, ngoTsOrgId);
                EntityCollection accountNgoCollection = service.RetrieveMultiple(queryAccountNgo);


                if (accountNgoCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", "NGO TSOrgId not found");
                    return;
                }

                Entity accountNgo = accountNgoCollection.Entities.First();

                QueryExpression externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery.ColumnSet = new ColumnSet(true);
                externalSystemRefQuery.Criteria.AddCondition("ts_objectid", ConditionOperator.Equal, accountNgo.Id);
                externalSystemRefQuery.Criteria.AddCondition("ts_externalsystemname", ConditionOperator.Equal, "Box");
                externalSystemRefQuery.Criteria.AddCondition("ts_referencetype", ConditionOperator.Equal, 1); //1 - NGO Folder Id
                EntityCollection externalSystemRefCollection = service.RetrieveMultiple(externalSystemRefQuery);

                if (externalSystemRefCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", "No Box folder found for NGO");
                    return;
                }


                Entity externalReferenceNgoBoxFolder = externalSystemRefCollection.Entities.First();

                string ngoFolderBoxId = externalReferenceNgoBoxFolder.GetAttributeValue<string>("ts_referencevalue");

                IDictionary<string, Object> boxParameters = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxParameters["itemType"] = "Folder";
                boxParameters["itemId"] = ngoFolderBoxId;

                Entity boxResponseEntity = EDServicesHelper.makeBoxItemBasedRequest(boxParameters, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                EntityCollection edFileList = new EntityCollection();

                EntityCollection ngoEdFolders = boxResponseEntity.GetAttributeValue<EntityCollection>("folderContents");               

                foreach (Entity ngoEdFolder in ngoEdFolders.Entities)
                {
                    if (ngoEdFolder.GetAttributeValue<string>("type") != "folder")
                        continue;

                    string edFolderBoxId = ngoEdFolder.GetAttributeValue<string>("id");

                    externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                    externalSystemRefQuery.ColumnSet = new ColumnSet(true);
                    externalSystemRefQuery.Criteria.AddCondition("ts_referencevalue", ConditionOperator.Equal, edFolderBoxId);
                    externalSystemRefQuery.Criteria.AddCondition("ts_externalsystemname", ConditionOperator.Equal, "Box");
                    externalSystemRefQuery.Criteria.AddCondition("ts_referencetype", ConditionOperator.Equal, 2); //referenceType: 2 - ED Folder Id
                    externalSystemRefCollection = service.RetrieveMultiple(externalSystemRefQuery);

                    if (externalSystemRefCollection.Entities.Count == 0)
                    {
                        //tsResponse.Attributes.Add("error", "No ED Case ");
                        continue;
                    }

                    Entity externalReferenceEdBoxFolder = externalSystemRefCollection.Entities.First();

                    Guid? edCaseId =externalReferenceEdBoxFolder.GetAttributeValue<EntityReference>("ts_objectid")?.Id;

                    if (edCaseId == null)
                        continue;

                    Entity edCase = service.Retrieve("incident", edCaseId.Value, new ColumnSet("ts_tsincidentid"));

                    string tsIncidentId = edCase.GetAttributeValue<string>("ts_tsincidentid");

                    EDServicesHelper.getAllFilesInSubTree(edFolderBoxId, edFileList, service, tracingService, errorStack, tsIncidentId);

                    if (errorStack.Count > 0)
                        return;
                }

                EntityCollection categories = tsRequest.Contains("categories") ? tsRequest.GetAttributeValue<EntityCollection>("categories") : null;

                if (categories != null && categories.Entities.Count > 0)
                {
                    List<string> categoryNames = new List<string>();
                    string[] categoriesToInclude = categories.Entities.Select(category => category.GetAttributeValue<string>("value").ToLower()).ToArray();

                    List<Entity> selectedCategories = edFileList.Entities.Where(file => categoriesToInclude.Contains(file.GetAttributeValue<string>("category").ToLower())
                                                                                 ).ToList();

                    edFileList = new EntityCollection(selectedCategories);
                }

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("edFileList", edFileList);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getEdFileListForNGO(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }

        public static void getEdFileListForNGOByEd(Entity tsRequest, Entity tsResponse, string ngoTsOrgId
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                QueryExpression queryAccountNgo = new QueryExpression("account");
                queryAccountNgo.ColumnSet = new ColumnSet(true);
                queryAccountNgo.Criteria.AddCondition("accountnumber", ConditionOperator.Equal, ngoTsOrgId);
                EntityCollection accountNgoCollection = service.RetrieveMultiple(queryAccountNgo);


                if (accountNgoCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", "NGO TSOrgId not found");
                    return;
                }

                Entity accountNgo = accountNgoCollection.Entities.First();

                QueryExpression externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery.ColumnSet = new ColumnSet(true);
                externalSystemRefQuery.Criteria.AddCondition("ts_objectid", ConditionOperator.Equal, accountNgo.Id);
                externalSystemRefQuery.Criteria.AddCondition("ts_externalsystemname", ConditionOperator.Equal, "Box");
                externalSystemRefQuery.Criteria.AddCondition("ts_referencetype", ConditionOperator.Equal, 1); //1 - NGO Folder Id
                EntityCollection externalSystemRefCollection = service.RetrieveMultiple(externalSystemRefQuery);

                if (externalSystemRefCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", "No Box folder found for NGO");
                    return;
                }


                Entity externalReferenceNgoBoxFolder = externalSystemRefCollection.Entities.First();

                string ngoFolderBoxId = externalReferenceNgoBoxFolder.GetAttributeValue<string>("ts_referencevalue");

                IDictionary<string, Object> boxParameters = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxParameters["itemType"] = "Folder";
                boxParameters["itemId"] = ngoFolderBoxId;

                Entity boxResponseEntity = EDServicesHelper.makeBoxItemBasedRequest(boxParameters, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                EntityCollection edFileList = new EntityCollection();

                EntityCollection ngoEdFolders = boxResponseEntity.GetAttributeValue<EntityCollection>("folderContents");

                foreach (Entity ngoEdFolder in ngoEdFolders.Entities)
                {
                    if (ngoEdFolder.GetAttributeValue<string>("type") != "folder")
                        continue;

                    string edFolderBoxId = ngoEdFolder.GetAttributeValue<string>("id");

                    externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                    externalSystemRefQuery.ColumnSet = new ColumnSet(true);
                    externalSystemRefQuery.Criteria.AddCondition("ts_referencevalue", ConditionOperator.Equal, edFolderBoxId);
                    externalSystemRefQuery.Criteria.AddCondition("ts_externalsystemname", ConditionOperator.Equal, "Box");
                    externalSystemRefQuery.Criteria.AddCondition("ts_referencetype", ConditionOperator.Equal, 2); //referenceType: 2 - ED Folder Id
                    externalSystemRefCollection = service.RetrieveMultiple(externalSystemRefQuery);

                    if (externalSystemRefCollection.Entities.Count == 0)
                    {
                        //tsResponse.Attributes.Add("error", "No ED Case ");
                        continue;
                    }

                    Entity externalReferenceEdBoxFolder = externalSystemRefCollection.Entities.First();

                    Guid? edCaseId = externalReferenceEdBoxFolder.GetAttributeValue<EntityReference>("ts_objectid")?.Id;

                    if (edCaseId == null)
                        continue;

                    Entity edCase = service.Retrieve("incident", edCaseId.Value, new ColumnSet("ts_tsincidentid"));

                    string tsIncidentId = edCase.GetAttributeValue<string>("ts_tsincidentid");

                    
                    EntityCollection edFiles = EDServicesHelper.getAllFolderFilesInSubTree(edFolderBoxId, service, tracingService, errorStack);
                    if (errorStack.Count > 0)
                        return;


                    Entity ed = new Entity();
                    ed["incidentId"] = tsIncidentId;
                    ed["edFiles"] = edFiles;

                    edFileList.Entities.Add(ed);
                }

                EntityCollection categories = tsRequest.Contains("categories") ? tsRequest.GetAttributeValue<EntityCollection>("categories") : null;

                if (categories != null && categories.Entities.Count > 0)
                {
                    List<string> categoryNames = new List<string>();
                    string[] categoriesToInclude = categories.Entities.Select(category => category.GetAttributeValue<string>("value").ToLower()).ToArray();

                    List<Entity> selectedCategories = edFileList.Entities.Where(file => categoriesToInclude.Contains(file.GetAttributeValue<string>("category").ToLower())
                                                                                 ).ToList();

                    edFileList = new EntityCollection(selectedCategories);
                }

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("edFileList", edFileList);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getEdFileListForNGOByEd(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }
        public static void getEdFileList(Entity tsRequest, Entity tsResponse
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                EDServicesHelper.writeToTrace("Starting getEdFileList", tracingService);

                string tsIncidentId = tsRequest.Contains("incidentId") ? tsRequest.GetAttributeValue<string>("incidentId") : string.Empty;
                string ngoTsOrgId = tsRequest.Contains("ngoId") ? tsRequest.GetAttributeValue<string>("ngoId") : string.Empty;


                if (string.IsNullOrEmpty(tsIncidentId) && string.IsNullOrEmpty(ngoTsOrgId))
                {
                    tsResponse.Attributes.Add("error", "incidentId or ngoId need to be provided for requestName: " + tsRequest.GetAttributeValue<string>("requestName"));
                    return;
                }


                if (!string.IsNullOrEmpty(ngoTsOrgId) && string.IsNullOrEmpty(tsIncidentId))
                {
                    if (tsRequest.GetAttributeValue<string>("requestName") == "edFileListByCategory")
                        getEdFileListForNGOByEd(tsRequest, tsResponse, ngoTsOrgId
                                                                                     , context, service, tracingService, errorStack);
                    else
                        getEdFileListForNGO(tsRequest, tsResponse, ngoTsOrgId
                                                                                , context, service, tracingService, errorStack);

                    return;
                }

                string boxRootId = EDServicesHelper.EnvVariables["ts_BoxNGOSourceCertificationsFolderId"];

                QueryExpression queryCase = new QueryExpression("incident");
                queryCase.ColumnSet = new ColumnSet(true);
                queryCase.Criteria.AddCondition("ts_tsincidentid", ConditionOperator.Equal, tsIncidentId);
                EntityCollection caseCollection = service.RetrieveMultiple(queryCase);

                if (caseCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", "ED Case for incidentId: " + tsIncidentId + "not found");
                    return;
                }

                Entity edCase = caseCollection.Entities.First();

                QueryExpression externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery = new QueryExpression("ts_externalsystemreference");
                externalSystemRefQuery.ColumnSet = new ColumnSet(true);
                externalSystemRefQuery.Criteria.AddCondition("ts_objectid", ConditionOperator.Equal, edCase.Id);
                externalSystemRefQuery.Criteria.AddCondition("ts_externalsystemname", ConditionOperator.Equal, "Box");
                externalSystemRefQuery.Criteria.AddCondition("ts_referencetype", ConditionOperator.Equal, 2); //referenceType: 2 - ED Folder Id
                EntityCollection externalSystemRefCollection = service.RetrieveMultiple(externalSystemRefQuery);

                if (externalSystemRefCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", "No Box folder found for ED");
                    return;
                }

                Entity externalReferenceEdBoxFolder = externalSystemRefCollection.Entities.First();

                string edFolderBoxId = externalReferenceEdBoxFolder.GetAttributeValue<string>("ts_referencevalue");


                EntityCollection edFileList = null;
                if (tsRequest.GetAttributeValue<string>("requestName") == "edFileListByCategory")
                    edFileList = EDServicesHelper.getAllFolderFilesInSubTree(edFolderBoxId, service, tracingService, errorStack);
                else
                    edFileList = EDServicesHelper.getAllFilesInSubTree(edFolderBoxId, service, tracingService, errorStack);


                if (errorStack.Count > 0)
                    return;

                EntityCollection categories = tsRequest.Contains("categories") ? tsRequest.GetAttributeValue<EntityCollection>("categories") : null;

                if (categories != null && categories.Entities.Count > 0)
                {
                    List<string> categoryNames = new List<string>();
                    string[] categoriesToInclude =categories.Entities.Select(category => category.GetAttributeValue<string>("value").ToLower()).ToArray();

                    List<Entity> selectedCategories = edFileList.Entities.Where(file => categoriesToInclude.Contains(file.GetAttributeValue<string>("category").ToLower())
                                                                                 ).ToList();

                    edFileList = new EntityCollection(selectedCategories);
                }

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("edFileList", edFileList);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getEdFileList(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }

        public static void getEdFileContent(Entity tsRequest, Entity tsResponse
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                EDServicesHelper.writeToTrace("Starting getEdFileContent", tracingService);

                string fileId = tsRequest.Contains("fileId") ? tsRequest.GetAttributeValue<string>("fileId") : string.Empty;

                if (string.IsNullOrEmpty(fileId))
                {
                    tsResponse.Attributes.Add("error", "fileId is required for requestName: " + tsRequest.GetAttributeValue<string>("requestName"));
                    return;
                }

                IDictionary<string, Object> boxParameters = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxParameters["itemType"] = "File";
                boxParameters["itemId"] = fileId;

                Entity boxResponseEntity = EDServicesHelper.makeBoxItemBasedRequest(boxParameters, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                Entity fileDetailsEntity = boxResponseEntity.GetAttributeValue<Entity>("fileDetails");

                string fileContent = fileDetailsEntity.GetAttributeValue<string>("fileContent");

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("fileId", fileId);
                tsResponse.Attributes.Add("fileContent", fileContent);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getEdFileContent(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }

        public static void getFileVersions(Entity tsRequest, Entity tsResponse
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                EDServicesHelper.writeToTrace("Starting getFileVersions", tracingService);

                string fileId = tsRequest.Contains("fileId") ? tsRequest.GetAttributeValue<string>("fileId") : string.Empty;

                if (string.IsNullOrEmpty(fileId))
                {
                    tsResponse.Attributes.Add("error", "fileId is required for requestName: " + tsRequest.GetAttributeValue<string>("requestName"));
                    return;
                }

                IDictionary<string, Object> boxParameters = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxParameters["itemType"] = "File";
                boxParameters["itemId"] = fileId;
                boxParameters["itemOperation"] = "getVersions";

                Entity boxResponseEntity = EDServicesHelper.makeBoxItemBasedRequest(boxParameters, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                Entity fileDetailsEntity = boxResponseEntity.GetAttributeValue<Entity>("fileDetails");

                fileDetailsEntity.Attributes.Remove("fileContent");
                fileDetailsEntity.Attributes.Remove("itemIndex");
                fileDetailsEntity.Attributes.Remove("folderId");
                fileDetailsEntity.Attributes.Remove("folderName");
                fileDetailsEntity.Attributes.Remove("subFolderTree");


                EntityCollection fileVersions = boxResponseEntity.GetAttributeValue<EntityCollection>("versions");


                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("fileId", fileId);
                tsResponse.Attributes.Add("fileDetails", fileDetailsEntity);
                tsResponse.Attributes.Add("fileVersions", fileVersions);

            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getFileVersions(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }

        public static void getFileVersionContent(Entity tsRequest, Entity tsResponse
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                EDServicesHelper.writeToTrace("Starting getFileVersionContent", tracingService);

                string fileId = tsRequest.Contains("fileId") ? tsRequest.GetAttributeValue<string>("fileId") : string.Empty;
                string versionId = tsRequest.Contains("versionId") ? tsRequest.GetAttributeValue<string>("versionId") : string.Empty;

                if (string.IsNullOrWhiteSpace(fileId) || string.IsNullOrWhiteSpace(versionId))
                {
                    tsResponse.Attributes.Add("error", "fileId and versionId are required for requestName: " + tsRequest.GetAttributeValue<string>("requestName"));
                    return;
                }

                IDictionary<string, Object> boxParameters = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;
                boxParameters["itemType"] = "File";
                boxParameters["itemId"] = fileId;
                boxParameters["itemOperation"] = "getVersionFileContent";
                boxParameters["versionId"] = versionId;


                Entity boxResponseEntity = EDServicesHelper.makeBoxItemBasedRequest(boxParameters, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                Entity fileDetailsEntity = boxResponseEntity.GetAttributeValue<Entity>("fileDetails");              

                Entity fileDetails = new Entity();
                fileDetails.Attributes.Add("fileId", fileId);
                fileDetails.Attributes.Add("versionId", versionId);
                fileDetails.Attributes.Add("name", fileDetailsEntity.GetAttributeValue<string>("name"));
                fileDetails.Attributes.Add("size", fileDetailsEntity.GetAttributeValue<int>("size"));
                fileDetails.Attributes.Add("contentType", fileDetailsEntity.GetAttributeValue<string>("contentType"));
                fileDetails.Attributes.Add("fileContent", fileDetailsEntity.GetAttributeValue<string>("fileContent"));


                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("fileDetails", fileDetails);
                
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getFileVersionContent(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }
        public static void deleteEdFile(Entity tsRequest, Entity tsResponse
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                EDServicesHelper.writeToTrace("Starting deleteEdFile", tracingService);

                string fileId = tsRequest.Contains("fileId") ? tsRequest.GetAttributeValue<string>("fileId") : string.Empty;

                if (string.IsNullOrEmpty(fileId))
                {
                    tsResponse.Attributes.Add("error", "fileId is required for requestName: " + tsRequest.GetAttributeValue<string>("requestName"));
                    return;
                }

                bool success = EDServicesHelper.deleteBoxItem("File", fileId, service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                
                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("deletedFileId", fileId);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At deleteEdFile(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }
        public static void getNetSuiteToken(Entity tsRequest, Entity tsResponse
                                                                                    , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                    , List<string> errorStack)
        {
            try
            {
                EDServicesHelper.writeToTrace("Starting getNetSuiteToken", tracingService);

                string netSuiteCallType = tsRequest.GetAttributeValue<string>("netSuiteCallType");
                if (string.IsNullOrEmpty(netSuiteCallType)
                    )
                {
                    tsResponse.Attributes.Add("error", "netSuiteCallType, 'soap' or 'rest', needs to be provided");
                    return;
                }

                //EDServicesHelper.EnvVariables[""];
                string accountId = EDServicesHelper.EnvVariables["ts_NetSuiteAccountId"];
                string consumerKey = EDServicesHelper.EnvVariables["ts_NetSuiteConsumerKey"];
                string tokenId = EDServicesHelper.EnvVariables["ts_NetSuiteTokenId"];


                EDServicesHelper.writeToTrace("accountId: " + accountId + Environment.NewLine + "consumerKey: " + consumerKey + Environment.NewLine + "tokenId: " + tokenId, tracingService);

                if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(consumerKey) || string.IsNullOrEmpty(tokenId)
                            )
                {
                    tsResponse.Attributes.Add("error", " Error retrieving accountId, consumerKey and tokenId from Environment Variables. " 
                                                + "accountId: " + accountId + Environment.NewLine + "consumerKey: " + consumerKey + Environment.NewLine + "tokenId: " + tokenId);
                    return;
                }

                switch (netSuiteCallType.ToLower())
                {
                    case "soap":
                        SOAPTokenRequestType soapTokenRequest = new SOAPTokenRequestType();

                        soapTokenRequest.accountId = accountId;
                        soapTokenRequest.consumerKey = consumerKey;
                        soapTokenRequest.tokenId = tokenId;

                        SOAPTokenResponseType soapTokenResponse = NetSuiteTokenGenerator.GetSOAPToken(soapTokenRequest, tracingService, errorStack);

                        if (errorStack.Count > 0)
                            return;

                        tsResponse.Attributes.Add("resultStatus", "success");
                        tsResponse.Attributes.Add("netSuiteCallType", netSuiteCallType.ToLower());
                        tsResponse.Attributes.Add("accountId", accountId);
                        tsResponse.Attributes.Add("consumerKey", consumerKey);
                        tsResponse.Attributes.Add("tokenId", tokenId);
                        tsResponse.Attributes.Add("nonce", soapTokenResponse.nonce);                        
                        tsResponse.Attributes.Add("timeStamp", soapTokenResponse.timeStamp);
                        tsResponse.Attributes.Add("signature", soapTokenResponse.signature);
                        
                        return;
                    case "rest":                        
                        string url = tsRequest.GetAttributeValue<string>("url");
                        string httpMethod = tsRequest.GetAttributeValue<string>("httpMethod");

                        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(httpMethod)
                            )
                        {
                            tsResponse.Attributes.Add("error", "url and httpMethod must be provided for a NetSuite Rest call");
                            return;
                        }

                        RESTTokenRequestType tokenRequest = new RESTTokenRequestType();

                        tokenRequest.accountId = accountId;
                        tokenRequest.consumerKey = consumerKey;
                        tokenRequest.tokenId = tokenId;
                        tokenRequest.url = url;
                        tokenRequest.httpMethod = httpMethod.ToUpper();

                        RESTTokenResponseType tokenResponse = NetSuiteTokenGenerator.GetRESTSToken(tokenRequest, tracingService, errorStack);

                        if (errorStack.Count > 0)
                            return;

                        tsResponse.Attributes.Add("resultStatus", "success");
                        tsResponse.Attributes.Add("netSuiteCallType", netSuiteCallType.ToLower());
                        tsResponse.Attributes.Add("accountId", accountId);
                        tsResponse.Attributes.Add("consumerKey", consumerKey);
                        tsResponse.Attributes.Add("tokenId", tokenId);
                        tsResponse.Attributes.Add("authorization", tokenResponse.authorization);


                        return;
                    default:
                        tsResponse.Attributes.Add("error", "Invalid netSuiteCallType, must be 'soap' or 'rest'");
                        return;
                }
                

                
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getNetSuiteToken(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }


        public static string getNetSuiteToken(string url, string httpMethod, ITracingService tracingService, List<string> errorStack, string netSuiteCallType = "rest"
                                                                                    )
        {
            try
            {
                EDServicesHelper.writeToTrace("Starting getNetSuiteToken(string url, string httpMethod)", tracingService);

                string accountId = EDServicesHelper.EnvVariables["ts_NetSuiteAccountId"];
                string consumerKey = EDServicesHelper.EnvVariables["ts_NetSuiteConsumerKey"];
                string tokenId = EDServicesHelper.EnvVariables["ts_NetSuiteTokenId"];


                EDServicesHelper.writeToTrace("accountId: " + accountId + Environment.NewLine + "consumerKey: " + consumerKey + Environment.NewLine + "tokenId: " + tokenId, tracingService);

                if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(consumerKey) || string.IsNullOrEmpty(tokenId)
                            )
                {
                    errorStack.Add("Error retrieving accountId, consumerKey and tokenId from Environment Variables. "
                                                + "accountId: " + accountId + Environment.NewLine + "consumerKey: " + consumerKey + Environment.NewLine + "tokenId: " + tokenId);
                    return "";
                }

                switch (netSuiteCallType.ToLower())
                {
                    case "soap":
                        SOAPTokenRequestType soapTokenRequest = new SOAPTokenRequestType();

                        soapTokenRequest.accountId = accountId;
                        soapTokenRequest.consumerKey = consumerKey;
                        soapTokenRequest.tokenId = tokenId;

                        SOAPTokenResponseType soapTokenResponse = NetSuiteTokenGenerator.GetSOAPToken(soapTokenRequest, tracingService, errorStack);

                        if (errorStack.Count > 0)
                            return "";

                        string signature = soapTokenResponse.signature;

                        return signature;
                    case "rest":
        
                        RESTTokenRequestType tokenRequest = new RESTTokenRequestType();

                        tokenRequest.accountId = accountId;
                        tokenRequest.consumerKey = consumerKey;
                        tokenRequest.tokenId = tokenId;
                        tokenRequest.url = url;
                        tokenRequest.httpMethod = httpMethod.ToUpper();

                        RESTTokenResponseType tokenResponse = NetSuiteTokenGenerator.GetRESTSToken(tokenRequest, tracingService, errorStack);

                        if (errorStack.Count > 0)
                            return "";

                        string authorization = tokenResponse.authorization;
                        return authorization;

                    default:
                        errorStack.Add("Invalid netSuiteCallType, must be 'soap' or 'rest'");
                        return null;
                }

            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getNetSuiteToken(string url, string httpMethod, string netSuiteCallType = \"rest\"). " + error, tracingService);
                errorStack.Add(error);
            }
            return "";
        }

        public static void findOrganizationMatches(Entity tsRequest, Entity tsResponse
                                                                                , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                , List<string> errorStack)
        {
            try
            {
                Entity organization = tsRequest.Contains("organization") ? tsRequest.GetAttributeValue<Entity>("organization") : null;

                if (organization == null)
                {
                    tsResponse.Attributes.Add("error", "organization is required for action: " + tsRequest.GetAttributeValue<string>("action"));
                    return;
                }


                List<AccountMatch> accountMatches = EDServicesHelper.getOrganizationMatchList(organization
                                                                                                            , service, tracingService, errorStack);

                if (errorStack.Count > 0)
                    return;

                EntityCollection matchAccounts = new EntityCollection();

                foreach (AccountMatch accountMatch in accountMatches)
                {                   
                    Entity account = service.Retrieve("account", accountMatch.AccountId, new ColumnSet(true));
                    Entity organizationEntity = new Entity();


                    organizationEntity["TSOrgId"] = account.GetAttributeValue<string>("accountnumber");
                    organizationEntity["orgName"] = account.GetAttributeValue<string>("name");
                    organizationEntity["phone"] = account.GetAttributeValue<string>("telephone1") ?? "";
                    organizationEntity["email"] = account.GetAttributeValue<string>("emailaddress1") ?? "";
                    organizationEntity["url"] = account.GetAttributeValue<string>("websiteurl") ?? "";


                    Entity address = new Entity();

                    address["address1"] = account.GetAttributeValue<string>("address1_line1") ?? "";
                    address["address2"] = account.GetAttributeValue<string>("address1_line2") ?? "";
                    //address["address3"] = account.GetAttributeValue<AliasedValue>("address1_line3");
                    address["city"] = account.GetAttributeValue<string>("address1_city") ?? "";
                    address["regionCode"] = account.GetAttributeValue<string>("address1_stateorprovince") ?? "";
                    address["countryCode"] = account.GetAttributeValue<string>("address1_country") ?? "";
                    address["postalCode"] = account.GetAttributeValue<string>("naddress1_postalcode") ?? "";


                    organizationEntity["address"] = address;
                    matchAccounts.Entities.Add(organizationEntity);
                }

                EntityCollection orgContacts = organization.GetAttributeValue<EntityCollection>("contacts");

                EntityCollection matchContacts = new EntityCollection();

                foreach (Entity orgContact in orgContacts.Entities)
                {

                    QueryExpression queryContact = new QueryExpression("contact");
                    queryContact.ColumnSet = new ColumnSet(true);
                    queryContact.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, orgContact.GetAttributeValue<string>("email"));
                    EntityCollection contactCollection = service.RetrieveMultiple(queryContact);

                    if (contactCollection.Entities.Count == 0)
                        continue;

                    Entity contact = contactCollection.Entities.First();

                    Entity matchContact = new Entity();

                    matchContact["TSContactId"] = contact.GetAttributeValue<string>("new_contactaccountnumber");
                    matchContact["firstname"] = contact.GetAttributeValue<string>("firstname") ?? "";
                    matchContact["lastname"] = contact.GetAttributeValue<string>("lastname") ?? "";
                    matchContact["email"] = contact.GetAttributeValue<string>("emailaddress1") ?? "";
                    
                    Entity address = new Entity();

                    address["address1"] = contact.GetAttributeValue<string>("address1_line1");
                    address["address2"] = contact.GetAttributeValue<string>("address1_line2");
                    address["city"] = contact.GetAttributeValue<string>("address1_city");
                    address["regionCode"] = contact.GetAttributeValue<string>("address1_stateorprovince");
                    address["countryCode"] = contact.GetAttributeValue<string>("address1_country");
                    address["postalCode"] = contact.GetAttributeValue<string>("address1_postalcode");

                    matchContact["address"] = address;

                    matchContacts.Entities.Add(matchContact);
                }



                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("organizations", matchAccounts);
                tsResponse.Attributes.Add("contacts", matchContacts);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At findOrganizationMatches(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }

        public static void getPngoIdFromEd(Entity tsRequest, Entity tsResponse
                                                                                , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService
                                                                                , List<string> errorStack)
        {
            try
            {
                string incidentId = tsRequest.Contains("incidentId") ? tsRequest.GetAttributeValue<string>("incidentId") : string.Empty;

                if (string.IsNullOrEmpty(incidentId))
                {
                    tsResponse.Attributes.Add("error", "incidentId is required for action: " + tsRequest.GetAttributeValue<string>("action"));
                    return;
                }

                QueryExpression queryCase = new QueryExpression("incident");
                queryCase.ColumnSet = new ColumnSet(true);
                queryCase.Criteria.AddCondition("ts_tsincidentid", ConditionOperator.Equal, incidentId);
                EntityCollection caseCollection = service.RetrieveMultiple(queryCase);

                if (caseCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", "ED Case for incidentId: " + incidentId + "not found");
                    return;
                }

                Entity edCase = caseCollection.Entities.First();


                EntityReference pngoAccountRef = edCase.GetAttributeValue<EntityReference>("ts_pngoaccountid");
                Entity pngoAccount = pngoAccountRef == null ? null : service.Retrieve(pngoAccountRef.LogicalName, pngoAccountRef.Id, new ColumnSet("accountnumber"));
                string pngoAdminId = pngoAccount?.GetAttributeValue<string>("accountnumber") ?? "";

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("incidentId", incidentId);
                tsResponse.Attributes.Add("pngoId", pngoAdminId);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getPngoIdFromEd(...). " + error, tracingService);
                errorStack.Add(error);
            }
        }

        public static void findEdRequestCase(Entity tsRequest, Entity tsResponse
                                                                        , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                string tsIncidentId = tsRequest.Contains("incidentId") ? tsRequest.GetAttributeValue<string>("incidentId") : string.Empty;
                string grantMakerTsOrgId = tsRequest.Contains("grantMakerTsOrgId") ? tsRequest.GetAttributeValue<string>("grantMakerTsOrgId") : string.Empty;

                if (string.IsNullOrEmpty(tsIncidentId) || string.IsNullOrEmpty(grantMakerTsOrgId))
                {
                    tsResponse.Attributes.Add("error", "incidentId and grantMakerTsOrgId are required for action: " + tsRequest.GetAttributeValue<string>("action"));
                    return;
                }


                string fetchExpressionQuery = @"
                    <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""1"" no-lock=""false"">
                        <entity name=""incident"">
                            <attribute name=""incidentid""/>
                            <filter type=""and"">
                                <condition attribute=""ts_tsincidentid"" operator=""eq"" value=""" + tsIncidentId + @"""/>
                            </filter>
                            <link-entity name=""incident"" alias=""childinc"" from=""parentcaseid"" to=""incidentid"" link-type=""inner"">
                                <link-entity name=""account"" alias=""gmacc"" from=""accountid"" to=""customerid"" link-type=""inner"">
                                    <filter type=""and"">
                                        <condition attribute=""accountnumber"" operator=""eq"" value=""" + grantMakerTsOrgId + @"""/>
                                    </filter>
                                </link-entity>
                            </link-entity>
                        </entity>
                    </fetch>";

                EntityCollection incidentCollection = service.RetrieveMultiple(new FetchExpression(fetchExpressionQuery));

                bool edRequestCaseExists = incidentCollection.Entities.Count > 0;

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("tsIncidentId", tsIncidentId);
                tsResponse.Attributes.Add("grantMakerTsOrgId", grantMakerTsOrgId);
                tsResponse.Attributes.Add("edRequestCaseExists", edRequestCaseExists);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At findEdRequestCase(...). " + error
                                                                                        , tracingService);
                errorStack.Add(error);
            }
        }

        public static void retrieveNgoFromEdCase(Entity tsRequest, Entity tsResponse
                                               , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                string incidentId = tsRequest.Contains("incidentId") ? tsRequest.GetAttributeValue<string>("incidentId") : string.Empty;
                string grantMakerTsOrgId = tsRequest.Contains("grantMakerTsOrgId") ? tsRequest.GetAttributeValue<string>("grantMakerTsOrgId") : string.Empty;

                if (string.IsNullOrEmpty(incidentId) || string.IsNullOrEmpty(grantMakerTsOrgId))
                {
                    tsResponse.Attributes.Add("error", "incidentId and grantMakerTsOrgId are required for action: " + tsRequest.GetAttributeValue<string>("action"));
                    return;
                }

                string fetchExpressionQuery = @"
                    <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""1"" no-lock=""false"">
                        <entity name=""incident"">
                            <attribute name=""incidentid""/>
                            <filter type=""and"">
                                <condition attribute=""ts_tsincidentid"" operator=""eq"" value=""" + incidentId + @"""/>
                            </filter>
                            <link-entity name=""incident"" alias=""childinc"" from=""parentcaseid"" to=""incidentid"" link-type=""inner"">
                                <link-entity name=""account"" alias=""gmacc"" from=""accountid"" to=""customerid"" link-type=""inner"">
                                    <filter type=""and"">
                                        <condition attribute=""accountnumber"" operator=""eq"" value=""" + grantMakerTsOrgId + @"""/>
                                    </filter>
                                </link-entity>
                            </link-entity>
                            <link-entity name=""account"" alias=""ngo"" from=""accountid"" to=""customerid"" link-type=""inner"">
                                <attribute name=""accountnumber""/>
                                <attribute name=""name""/>
                                <attribute name=""telephone1""/>
                                <attribute name=""emailaddress1""/>
                                <attribute name=""websiteurl""/>
                                <attribute name=""address1_line1""/>
                                <attribute name=""address1_line2""/>
                                <attribute name=""address1_line3""/>
                                <attribute name=""address1_city""/>
                                <attribute name=""address1_stateorprovince""/>
                                <attribute name=""address1_postalcode""/>
                                <attribute name=""address1_country""/>
                            </link-entity>
                        </entity>
                    </fetch>";

                EntityCollection incidentCollection = service.RetrieveMultiple(new FetchExpression(fetchExpressionQuery));

                if (incidentCollection.Entities.Count == 0)
                {
                    tsResponse.Attributes.Add("error", $"retrieveNgoFromEdCase: No case found with tsIncidentId={incidentId}, grantMakerTsOrgId={grantMakerTsOrgId}");
                    return;
                }

                Entity incidentEntity = incidentCollection.Entities.First();

                
                Entity organization = new Entity();

                
                AliasedValue accountNumberAlias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.accountnumber");
                organization["TSOrgId"] = accountNumberAlias?.Value?.ToString() ?? string.Empty;

                AliasedValue nameAlias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.name");
                organization["orgName"] = nameAlias?.Value?.ToString() ?? string.Empty;

                AliasedValue telephone1Alias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.telephone1");
                organization["phone"] = telephone1Alias?.Value?.ToString() ?? string.Empty;

                AliasedValue emailAlias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.emailaddress1");
                organization["email"] = emailAlias?.Value?.ToString() ?? string.Empty;

                AliasedValue websiteAlias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.websiteurl");
                organization["url"] = websiteAlias?.Value?.ToString() ?? string.Empty;

                
                Entity address = new Entity();

                AliasedValue address1Alias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.address1_line1");
                address["address1"] = address1Alias?.Value?.ToString() ?? string.Empty;

                AliasedValue address2Alias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.address1_line2");
                address["address2"] = address2Alias?.Value?.ToString() ?? string.Empty;

                AliasedValue address3Alias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.address1_line3");
                address["address3"] = address3Alias?.Value?.ToString() ?? string.Empty;

                AliasedValue cityAlias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.address1_city");
                address["city"] = cityAlias?.Value?.ToString() ?? string.Empty;

                AliasedValue regionAlias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.address1_stateorprovince");
                address["regionCode"] = regionAlias?.Value?.ToString() ?? string.Empty;

                AliasedValue countryAlias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.address1_country");
                address["countryCode"] = countryAlias?.Value?.ToString() ?? string.Empty;

                AliasedValue postalCodeAlias = incidentEntity.GetAttributeValue<AliasedValue>("ngo.address1_postalcode");
                address["postalCode"] = postalCodeAlias?.Value?.ToString() ?? string.Empty;

                
                organization["address"] = address;

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("incidentId", incidentId);
                tsResponse.Attributes.Add("grantMakerTsOrgId", grantMakerTsOrgId);
                tsResponse.Attributes.Add("organization", organization);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At retrieveNgoFromEdCase(...). " + error
                                                                                        , tracingService);
                errorStack.Add(error);
                return;
            }
        }


        public static void getEdCaseIds(Entity tsRequest, Entity tsResponse
                                                                        , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService, List<string> errorStack)

        {
            try
            {
                EntityCollection incidentIds = tsRequest.Contains("incidentIds") ? tsRequest.GetAttributeValue<EntityCollection>("incidentIds") : null;

                if (incidentIds == null)
                {
                    tsResponse.Attributes.Add("error", "incidentIds is required for action: " + tsRequest.GetAttributeValue<string>("action"));
                    return;
                }


                string[] tsIncidentIds = incidentIds.Entities.Select(item => item.GetAttributeValue<string>("value").ToLower()).ToArray();

                List<XElement> tsIncidentIdElements = tsIncidentIds.Select(id =>
                                                                                new XElement("value", id)
                                                                            ).ToList();


                XElement tsIncidentIdCondition = new XElement("condition",
                                                                    new XAttribute("attribute", "ts_tsincidentid"),
                                                                    new XAttribute("operator", "in")
                                                                );
                tsIncidentIdCondition.Add(tsIncidentIdElements);


                string fetchExpressionQuery = @"
                    <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""5000"" no-lock=""false"">
                        <entity name=""incident"">
                            <attribute name=""incidentid""/>
                            <attribute name=""ts_tsincidentid""/>
                        </entity>
                    </fetch>
                    ";


                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery);


                XElement entityElement = fetchXmlDoc.Descendants("entity").FirstOrDefault();


                XElement filterElement = entityElement.Descendants().ToList().Find(element => element.Name == "filter");
                if (filterElement == null)
                {
                    entityElement.Add(
                                        new XElement("filter",
                                                new XAttribute("type", "and")
                                                )
                                        );

                    filterElement = entityElement.Descendants().Where(element => element.Name == "filter").First();
                }


                filterElement.Add(tsIncidentIdCondition);


                string finalFetchExpression = fetchXmlDoc.ToString();


                EntityCollection incidentCollection = service.RetrieveMultiple(new FetchExpression(finalFetchExpression));

                EntityCollection edCaseIdsCollection = new EntityCollection();
                foreach (Entity incident in incidentCollection.Entities)
                {
                    Entity edCaseId = new Entity();

                    edCaseId.Attributes.Add("incidentId", incident.GetAttributeValue<string>("ts_tsincidentid"));
                    edCaseId.Attributes.Add("edDynamicsCaseId", incident.GetAttributeValue<Guid>("incidentid").ToString());

                    edCaseIdsCollection.Entities.Add(edCaseId);
                }

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("edDynamicsCaseIds", edCaseIdsCollection);
            }
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At getEdCaseIds(...). " + error
                                                                                    , tracingService);
                errorStack.Add(error);
            }
        }
    }
}