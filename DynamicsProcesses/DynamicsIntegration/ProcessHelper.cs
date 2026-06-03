using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Crm.Sdk.Messages;
using System.Xml.Linq;
using Microsoft.PowerPlatform.Dataverse.Client.Model;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Identity.Client;
using System.Data.SqlClient;
using System.Data;
using System.Configuration;

using DynamicsProcesses.DataAccessService;
using System.ServiceModel;
using System.Xml;
using System.Net.Mail;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Runtime.Remoting.Services;
using System.Web.Configuration;
using System.IdentityModel.Metadata;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http;
//using static System.Net.WebRequestMethods;
using System.Data.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Policy;
using System.Text.RegularExpressions;
using System.Web.UI.WebControls;
using System.Net;
using System.Dynamic;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;

using DataverseClientLib = Microsoft.PowerPlatform.Dataverse.Client;
using System.Web.Util;
using System.Security.Cryptography.Xml;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.ListView;

namespace DynamicsProcesses
{
    internal class ProcessHelper
    {
        public static string CTPSessionKey = ConfigurationManager.AppSettings["CTPSessionKey"];

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

        public static async Task<Dictionary<string, string>> GetEnvironmentVariables(List<string> errorStack = null)
        {
            try
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

                EntityCollection results = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(query);


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
            catch (Exception e)
            {
                string error = $"Error in GetEnvironmentVariables(). Exception message:{Environment.NewLine}{e.Message}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }
        }

        public static async Task processCtpOrg(string ctpOrgId, string tsOrgId = null)
        {
            try
            {

                IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);

                if (ctpOrgEntity == null)
                    return;

                if (!string.IsNullOrEmpty((string)ctpOrgEntity["externalReferenceOnyx"]))
                    return;

                //await findDynamicsAccountMatches(ctpOrgEntity);

                if (tsOrgId == null)
                    tsOrgId = await getNextTsCustomerId();

                /* Update CTP with tsOrgId/OnyxId
                //ctpOrgId: 5525430593717_78a1
                bool onyxRefAdded = await addOnyxExternalReferenceToCtpOrg(tsOrgId, ctpOrgEntity);

                ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);


                */


                Entity account = await createOrgForCtp(ctpOrgEntity, tsOrgId, (string)ctpOrgEntity["externalReferenceTransactionId"]);
            }
            catch (Exception e)
            {
            }


        }

        public static async Task<string> getCtpOrgOnyxExternalReference(string ctpOrgId, List<string> errorStack = null)
        {
            try
            {
                IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);
                string externalReferenceOnyx = (string)ctpOrgEntity["externalReferenceOnyx"];

                return externalReferenceOnyx;
            }
            catch (Exception e)
            {
                string error = $"Error in getCtpOrgOnyxExternalReference(). Exception message:{Environment.NewLine}{e.Message}{Environment.NewLine}ctpOrgId: {ctpOrgId}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }
        }



        public static async Task updateCtpOrgTsOrgId(string ctpOrgId, string tsOrgId)
        {
            IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);

            if (ctpOrgEntity == null)
                return;

            if (string.IsNullOrEmpty((string)ctpOrgEntity["externalReferenceOnyx"]))
                return;


            //ctpOrgId: 5525430593717_78a1
            bool onyxRefAdded = await updateCtpOrgOnyxExternalReference(tsOrgId, ctpOrgEntity);

            ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);

            string externalReferenceOnyx = (string)ctpOrgEntity["externalReferenceOnyx"];

        }

        public static async Task<bool> addOnyxExternalReferenceToCtpOrg(string tsOrgId, IDictionary<string, Object> ctpOrgEntity, List<string> errorStack = null)
        {
            try
            {
                string ctpOrgId = (string)ctpOrgEntity["ctpOrgId"];

                Dictionary<string, string> ctpObjectIds = new Dictionary<string, string>();
                ctpObjectIds["rootId"] = (string)ctpOrgEntity["rootId"];
                ctpObjectIds["associateId"] = (string)ctpOrgEntity["associateId"];
                ctpObjectIds["tsOrgId"] = tsOrgId;

                Dictionary<string, string> ctpNewObjectIds = await getCTPNewRootInstance(errorStack);
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
                //"0948d6b4-9276-45e7-a345-7de662c3fad5";
                //"61695af7-1652-4b08-b786-192de1884f61";
                string endPointPath = $"/services/object/v_001/{ctpSessionKey}/{ctpOrgId}/any/all";
                dynamic ctpOrgUpdateResponse = await makeHttpPostCall(ctpNewOrgObjectText
                                                               , ctpUrl, endPointPath
                                                                , queryParams: null
                                                                );

                if (ctpOrgUpdateResponse == null)
                {
                    errorStack?.Add($"addOnyxExternalReferenceToCtpOrg(). Call to add OnyxExternalReference, {tsOrgId}, to CTPOrg object, ctpOrgId: {ctpOrgId}, returned null");
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                string error = $"Error in addOnyxExternalReferenceToCtpOrg(). Exception message:{Environment.NewLine}{e.Message}{Environment.NewLine}tsOrgId: {tsOrgId}; ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return false;
        }

        public static async Task<bool> updateCtpOrgOnyxExternalReference(string tsOrgId, IDictionary<string, Object> ctpOrgEntity, List<string> errorStack = null)
        {
            try
            {
                string ctpOrgId = (string)ctpOrgEntity["ctpOrgId"];

                dynamic onyxExternalReference = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(ctpOrgEntity["externalReferenceObjectOnyx"]));
                onyxExternalReference.typeValue = tsOrgId;

                List<dynamic> io = new List<dynamic>();
                io.Add(onyxExternalReference);

                IDictionary<string, Object> ctpOrgObjectExpando = JsonConvert.DeserializeObject<ExpandoObject>((string)ctpOrgEntity["orgObjectText"]) as IDictionary<string, Object>;
                ctpOrgObjectExpando.Remove("io");
                ctpOrgObjectExpando.Add("io", io);

                string ctpNewOrgObjectText = JsonConvert.SerializeObject(ctpOrgObjectExpando, Newtonsoft.Json.Formatting.None);


                string ctpUrl = "https://objects-sysrw.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                string endPointPath = $"/services/object/v_001/{ctpSessionKey}/{ctpOrgId}/any/all";
                dynamic ctpOrgUpdateResponse = await makeHttpPostCall(ctpNewOrgObjectText
                                                               , ctpUrl, endPointPath
                                                                , queryParams: null
                                                                );

                return true;
            }
            catch (Exception e)
            {
                string error = $"Error in updateCtpOrgOnyxExternalReference(). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}tsOrgId: {tsOrgId}; ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return false;
        }

        public static async Task<bool> addObject_001ToCtpOrg(string signature, string type, string typeValue, IDictionary<string, Object> ctpOrgEntity, string typeSource = "nil", List<string> errorStack = null)
        {
            try
            {
                string ctpOrgId = (string)ctpOrgEntity["ctpOrgId"];

                Dictionary<string, string> ctpObjectIds = new Dictionary<string, string>();
                ctpObjectIds["rootId"] = (string)ctpOrgEntity["rootId"];
                ctpObjectIds["associateId"] = (string)ctpOrgEntity["associateId"];


                Dictionary<string, string> ctpNewObjectIds = await getCTPNewRootInstance(errorStack);
                ctpObjectIds["instanceId"] = ctpNewObjectIds["instanceId"];
                ctpObjectIds["crc"] = ctpNewObjectIds["crc"];

                ctpObjectIds["signature"] = signature;
                ctpObjectIds["type"] = type;
                ctpObjectIds["typeValue"] = typeValue;
                ctpObjectIds["typeSource"] = typeSource;


                List<dynamic> io = new List<dynamic>();
                dynamic newOnyxExternalReference = getCtpObject_001(ctpObjectIds);
                io.Add(newOnyxExternalReference);

                IDictionary<string, Object> ctpOrgObjectExpando = JsonConvert.DeserializeObject<ExpandoObject>((string)ctpOrgEntity["orgObjectText"]) as IDictionary<string, Object>;
                ctpOrgObjectExpando.Remove("io");
                ctpOrgObjectExpando.Add("io", io);

                string ctpNewOrgObjectText = JsonConvert.SerializeObject(ctpOrgObjectExpando, Newtonsoft.Json.Formatting.None);


                string ctpUrl = "https://objects-sysrw.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                string endPointPath = $"/services/object/v_001/{ctpSessionKey}/{ctpOrgId}/any/all";
                dynamic ctpOrgUpdateResponse = await makeHttpPostCall(ctpNewOrgObjectText
                                                               , ctpUrl, endPointPath
                                                                , queryParams: null
                                                                );

                string ctpOrgRespText = JsonConvert.SerializeObject(ctpOrgUpdateResponse, Newtonsoft.Json.Formatting.Indented);

                if (ctpOrgUpdateResponse == null)
                {
                    errorStack?.Add($"addObject_001ToCtpOrg(). Call to add addObject_001ToCtpOrg:{Environment.NewLine}signature: {signature}; type: {type}; typeValue: {typeValue}; ctpOrgId: {ctpOrgId}, returned null"
                        );
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                string error = $"Error in addObject_001ToCtpOrg(). Exception message:{Environment.NewLine}{e.Message}" 
                    + $"{Environment.NewLine}signature: {signature}; type: {type}; typeValue: {typeValue}; ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return false;
        }


        public static async Task<bool> addObject_001ToCtpOrg(Dictionary<string, string> ctpObjectIds, IDictionary<string, Object> ctpOrgEntity, List<string> errorStack = null)
        {
            try
            {
                string ctpOrgId = (string)ctpOrgEntity["ctpOrgId"];

                ctpObjectIds["rootId"] = (string)ctpOrgEntity["rootId"];

                if (!ctpObjectIds.ContainsKey("associateId"))
                    ctpObjectIds["associateId"] = (string)ctpOrgEntity["associateId"];


                Dictionary<string, string> ctpNewObjectIds = await getCTPNewRootInstance(errorStack);
                ctpObjectIds["instanceId"] = ctpNewObjectIds["instanceId"];
                ctpObjectIds["crc"] = ctpNewObjectIds["crc"]; 


                List<dynamic> io = new List<dynamic>();
                dynamic newOnyxExternalReference = getCtpObject_001(ctpObjectIds);
                io.Add(newOnyxExternalReference);

                IDictionary<string, Object> ctpOrgObjectExpando = JsonConvert.DeserializeObject<ExpandoObject>((string)ctpOrgEntity["orgObjectText"]) as IDictionary<string, Object>;
                ctpOrgObjectExpando.Remove("io");
                ctpOrgObjectExpando.Add("io", io);

                string ctpNewOrgObjectText = JsonConvert.SerializeObject(ctpOrgObjectExpando, Newtonsoft.Json.Formatting.None);


                string ctpUrl = "https://objects-sysrw.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                string endPointPath = $"/services/object/v_001/{ctpSessionKey}/{ctpOrgId}/any/all";
                dynamic ctpOrgUpdateResponse = await makeHttpPostCall(ctpNewOrgObjectText
                                                               , ctpUrl, endPointPath
                                                                , queryParams: null
                                                                );

                string ctpOrgRespText = JsonConvert.SerializeObject(ctpOrgUpdateResponse, Newtonsoft.Json.Formatting.Indented);

                if (ctpOrgUpdateResponse == null)
                {
                    errorStack?.Add($"addObject_001ToCtpOrg(). Call to add addObject_001ToCtpOrg:{Environment.NewLine}signature: {ctpObjectIds["signature"]}; type: {ctpObjectIds["type"]}; typeValue: {ctpObjectIds["typeValue"]}; ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}, returned null"
                        );
                    return false;
                }

                if (ctpObjectIds["typeValue"] == "Agent")
                {
                    ctpObjectIds["agentInstanceId"] = ctpObjectIds["instanceId"];
                    ctpObjectIds["associateId"] = ctpObjectIds["instanceId"];
                }


                return true;
            }
            catch (Exception e)
            {
                string error = $"Error in addObject_001ToCtpOrg(ctpObjectIds). Exception message:{Environment.NewLine}{e.Message}"
                    + $"{Environment.NewLine}signature: {ctpObjectIds["signature"]}; type: {ctpObjectIds["type"]}; typeValue: {ctpObjectIds["typeValue"]}; ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return false;
        }



        public static async Task<bool> apply_io_toCtpOrg(Dictionary<string, string> ctpObjectIds, IDictionary<string, Object> ctpOrgEntity, List<dynamic> io, List<string> errorStack = null)
        {
            try
            {
                string ctpOrgId = (string)ctpOrgEntity["ctpOrgId"];

               
                IDictionary<string, Object> ctpOrgObjectExpando = JsonConvert.DeserializeObject<ExpandoObject>((string)ctpOrgEntity["orgObjectText"]) as IDictionary<string, Object>;
                ctpOrgObjectExpando.Remove("io");
                ctpOrgObjectExpando.Add("io", io);

                string ctpNewOrgObjectText = JsonConvert.SerializeObject(ctpOrgObjectExpando, Newtonsoft.Json.Formatting.None);

                DynamicsInterface.writeToLog($"apply_io_toCtpOrg(). Request:{Environment.NewLine}{JsonConvert.SerializeObject(ctpOrgObjectExpando, Newtonsoft.Json.Formatting.Indented)}");

                string ctpUrl = "https://objects-sysrw.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                string endPointPath = $"/services/object/v_001/{ctpSessionKey}/{ctpOrgId}/any/all";
                dynamic ctpOrgUpdateResponse = await makeHttpPostCall(ctpNewOrgObjectText
                                                               , ctpUrl, endPointPath
                                                                , queryParams: null
                                                                );

                string ctpOrgRespText = JsonConvert.SerializeObject(ctpOrgUpdateResponse, Newtonsoft.Json.Formatting.Indented);

                if (ctpOrgUpdateResponse == null)
                {
                    errorStack?.Add($"apply_io_toCtpOrg(). Call returned null. Request:{Environment.NewLine}{JsonConvert.SerializeObject(ctpOrgObjectExpando, Newtonsoft.Json.Formatting.Indented)}"
                        );
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                string error = $"Error in apply_io_toCtpOrg. Exception message:{Environment.NewLine}{e.Message}"
                    + $"{Environment.NewLine}io: {Environment.NewLine}{JsonConvert.SerializeObject(io, Newtonsoft.Json.Formatting.Indented)}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return false;
        }



        public static async Task<bool> addObject_001_to_io(Dictionary<string, string> ctpObjectIds, IDictionary<string, Object> ctpOrgEntity, List<dynamic> io, List<string> errorStack = null)
        {
            try
            {
                string ctpOrgId = (string)ctpOrgEntity["ctpOrgId"];

                ctpObjectIds["rootId"] = (string)ctpOrgEntity["rootId"];

                if (!ctpObjectIds.ContainsKey("associateId"))
                    ctpObjectIds["associateId"] = (string)ctpOrgEntity["associateId"];


                Dictionary<string, string> ctpNewObjectIds = await getCTPNewRootInstance(errorStack);
                ctpObjectIds["instanceId"] = ctpNewObjectIds["instanceId"];
                ctpObjectIds["crc"] = ctpNewObjectIds["crc"];


                //List<dynamic> io = new List<dynamic>();
                dynamic newOnyxExternalReference = getCtpObject_001(ctpObjectIds);
                io.Add(newOnyxExternalReference);

                if (ctpObjectIds["typeValue"] == "Agent")
                {
                    ctpObjectIds["agentInstanceId"] = ctpObjectIds["instanceId"];
                    ctpObjectIds["associateId"] = ctpObjectIds["instanceId"];
                }
            }
            catch (Exception e)
            {
                string error = $"Error in addObject_001_to_io. Exception message:{Environment.NewLine}{e.Message}"
                    + $"{Environment.NewLine}signature: {ctpObjectIds["signature"]}; type: {ctpObjectIds["type"]}; typeValue: {ctpObjectIds["typeValue"]}; ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return false;
        }



        public static async Task<bool> updateObject_001ToCtpOrg(Dictionary<string, string> ctpObjectIds, dynamic object_001, IDictionary<string, Object> ctpOrgEntity, List<string> errorStack = null)
        {
            try
            {
                string ctpOrgId = (string)ctpOrgEntity["ctpOrgId"];

               
                if (object_001 == null)
                    return false;


                object_001.typeValue = ctpObjectIds["typeValue"];

                List<dynamic> io = new List<dynamic>();
                io.Add(object_001);

                IDictionary<string, Object> ctpOrgObjectExpando = JsonConvert.DeserializeObject<ExpandoObject>((string)ctpOrgEntity["orgObjectText"]) as IDictionary<string, Object>;
                ctpOrgObjectExpando.Remove("io");
                ctpOrgObjectExpando.Add("io", io);

                string ctpNewOrgObjectText = JsonConvert.SerializeObject(ctpOrgObjectExpando, Newtonsoft.Json.Formatting.None);


                string ctpUrl = "https://objects-sysrw.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                string endPointPath = $"/services/object/v_001/{ctpSessionKey}/{ctpOrgId}/any/all";
                dynamic ctpOrgUpdateResponse = await makeHttpPostCall(ctpNewOrgObjectText
                                                               , ctpUrl, endPointPath
                                                                , queryParams: null
                                                                );

                return true;
            }
            catch (Exception e)
            {
                string error = $"Error in updateObject_001ToCtpOrg(ctpObjectIds). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}signature: {ctpObjectIds["signature"]}; type: {ctpObjectIds["type"]}; typeValue: {ctpObjectIds["typeValue"]}; ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return false;
        }


        public static async Task<bool> updateObject_001ToCtpOrg(string signature, string type, string typeValue, dynamic object_001, IDictionary<string, Object> ctpOrgEntity, List<string> errorStack = null)
        {
            try
            {
                string ctpOrgId = (string)ctpOrgEntity["ctpOrgId"];


                if (object_001 == null)
                    return false;


                object_001.typeValue = typeValue;

                List<dynamic> io = new List<dynamic>();
                io.Add(object_001);

                IDictionary<string, Object> ctpOrgObjectExpando = JsonConvert.DeserializeObject<ExpandoObject>((string)ctpOrgEntity["orgObjectText"]) as IDictionary<string, Object>;
                ctpOrgObjectExpando.Remove("io");
                ctpOrgObjectExpando.Add("io", io);

                string ctpNewOrgObjectText = JsonConvert.SerializeObject(ctpOrgObjectExpando, Newtonsoft.Json.Formatting.None);


                string ctpUrl = "https://objects-sysrw.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                string endPointPath = $"/services/object/v_001/{ctpSessionKey}/{ctpOrgId}/any/all";
                dynamic ctpOrgUpdateResponse = await makeHttpPostCall(ctpNewOrgObjectText
                                                               , ctpUrl, endPointPath
                                                                , queryParams: null
                                                                );

                return true;
            }
            catch (Exception e)
            {
                string error = $"Error in updateObject_001ToCtpOrg(). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}signature: {signature}; type: {type}; typeValue: {typeValue}; ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return false;
        }
        public static dynamic getCtpObject_001(Dictionary<string, string> ctpObjectIds, List<string> errorStack = null)
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
				        ""typeSource"": """"
			        },
			        ""version"": 1,
			        ""timestamp"": 1,
			        ""public"": true,
			        ""protected"": false,
			        ""type"": """",
			        ""signature"": """",
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

                if (ctpObjectIds.ContainsKey("typeSource"))
                {
                    if (ctpObjectIds["typeSource"] != "exclude")
                        onyxExternalReferenceObject.instance.typeSource = ctpObjectIds["typeSource"];
                }
                else
                {
                    onyxExternalReferenceObject.instance.typeSource = "nil";
                }

                onyxExternalReferenceObject.timestamp = externalObjectTimeStamp;
                onyxExternalReferenceObject.crc = ctpObjectIds["crc"];
                onyxExternalReferenceObject.instanceId = ctpObjectIds["instanceId"];
                onyxExternalReferenceObject.rootId = ctpObjectIds["rootId"];
                onyxExternalReferenceObject.associateId = ctpObjectIds["associateId"];


                onyxExternalReferenceObject.signature = ctpObjectIds["signature"];
                onyxExternalReferenceObject.type = ctpObjectIds["type"];
                onyxExternalReferenceObject.typeValue = ctpObjectIds["typeValue"];

                return onyxExternalReferenceObject;

            }
            catch (Exception e)
            {
                string error = $"Error in getCtpObject_001(). Exception message:{Environment.NewLine}{e.Message}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }

        }

        public static async Task<string> getNextTsCustomerId(Dictionary<string, string> envVariables = null, List<string> errorStack = null)
        {
            string tsCustomerId = string.Empty;
            try
            {
                if (envVariables == null)
                    envVariables = await GetEnvironmentVariables();

                //X509Certificate2 cer = GetVaultCertificate(envVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                //binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;

                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(envVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                //dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();

                objRequest.ServerName = envVariables["ts_Sql2kServer"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_getNextOnyxId";



                executeStoredProcResponse dataAccessresponse = await dataAccessClient.ExecuteStoredProcAsync(objRequest);

                rowType[] returnXml = dataAccessresponse.ExecuteStoredProcResponse1.ReturnXml;

                if (returnXml.Length > 0)
                    tsCustomerId = returnXml.First().Any[0].InnerText;

            }
            catch (Exception e)
            {
                string error = $"Error in getNextTsCustomerId(). Exception message:{Environment.NewLine}{e.Message}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return tsCustomerId;
        }

        public static async Task addAgentToCtpOrgObjects(Entity validationRequestCase, IDictionary<string, Object> ctpOrgEntity , List<string> errorStack = null)
        {
            string tsCustomerId = string.Empty;
            try
            {
                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationagentemail");

                string valReqAgentValidationStatusText = validationRequestCase.GetAttributeValue<OptionSetValue>("ts_validationrequestagentverification") == null ? ""
                                                                                                                                : validationRequestCase.FormattedValues["ts_validationrequestagentverification"];

                valReqAgentValidationStatusText = valReqAgentValidationStatusText.ToLower() == "verified" ? "Verified" : "Pending";

                if (string.IsNullOrEmpty(agentEmail))
                    return;
                dynamic ctpOrgEntityJson = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(ctpOrgEntity));


                dynamic orgAgent = ((JArray)ctpOrgEntityJson.orgAgents)?.ToList<dynamic>()?.Where(agent => (string)agent.email == agentEmail)?.FirstOrDefault();

                string agentInstanceId = (string)orgAgent?.agentInstanceId;

                Dictionary<string, string> ctpObjectIds = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(agentInstanceId))
                {
                    ctpObjectIds["associateId"] = agentInstanceId;

                    if (orgAgent["firstName"] == null)
                    {
                        ctpObjectIds["signature"] = "NameObject_001";
                        ctpObjectIds["type"] = "firstName";
                        ctpObjectIds["typeValue"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentfirstname") ?? "";
                        ctpObjectIds["typeSource"] = "exclude";

                        await ProcessHelper.addObject_001ToCtpOrg(ctpObjectIds, ctpOrgEntity);
                    }

                    if (orgAgent["lastName"] == null)
                    {
                        ctpObjectIds["signature"] = "NameObject_001";
                        ctpObjectIds["type"] = "lastName";
                        ctpObjectIds["typeValue"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentlastname") ?? "";
                        ctpObjectIds["typeSource"] = "exclude";
                        await ProcessHelper.addObject_001ToCtpOrg(ctpObjectIds, ctpOrgEntity);
                    }

                    ctpObjectIds["signature"] = "StatusObject_001";
                    ctpObjectIds["type"] = "agent";
                    ctpObjectIds["typeValue"] = valReqAgentValidationStatusText;
                    ctpObjectIds["typeSource"] = "exclude";


                    if (orgAgent["status"] == null)
                    {
                        await ProcessHelper.addObject_001ToCtpOrg(ctpObjectIds, ctpOrgEntity);
                    }
                    else
                    {
                        await ProcessHelper.updateObject_001ToCtpOrg(ctpObjectIds, (dynamic)orgAgent["agentStatusObject"], ctpOrgEntity);

                    }
                }
                else
                {

                    List<dynamic> io = new List<dynamic>();


                    ctpObjectIds["signature"] = "EntityObject_001";
                    ctpObjectIds["type"] = "Person";
                    ctpObjectIds["typeValue"] = "Agent";
                    ctpObjectIds["typeSource"] = "exclude";

                    await ProcessHelper.addObject_001_to_io(ctpObjectIds, ctpOrgEntity, io);

                    ctpObjectIds["signature"] = "EmailObject_001";
                    ctpObjectIds["type"] = "main";
                    ctpObjectIds["typeValue"] = agentEmail;
                    ctpObjectIds["typeSource"] = "exclude";

                    await ProcessHelper.addObject_001_to_io(ctpObjectIds, ctpOrgEntity, io);

                    ctpObjectIds["signature"] = "NameObject_001";
                    ctpObjectIds["type"] = "firstName";
                    ctpObjectIds["typeValue"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentfirstname") ?? "";
                    ctpObjectIds["typeSource"] = "exclude";

                    await ProcessHelper.addObject_001_to_io(ctpObjectIds, ctpOrgEntity, io);

                    ctpObjectIds["signature"] = "NameObject_001";
                    ctpObjectIds["type"] = "lastName";
                    ctpObjectIds["typeValue"] = validationRequestCase.GetAttributeValue<string>("ts_validationrequestagentlastname") ?? "";
                    ctpObjectIds["typeSource"] = "exclude";

                    await ProcessHelper.addObject_001_to_io(ctpObjectIds, ctpOrgEntity, io);                    

                    ctpObjectIds["signature"] = "StatusObject_001";
                    ctpObjectIds["type"] = "agent";
                    ctpObjectIds["typeValue"] = valReqAgentValidationStatusText;
                    ctpObjectIds["typeSource"] = "exclude";

                    await ProcessHelper.addObject_001_to_io(ctpObjectIds, ctpOrgEntity, io);

                    await ProcessHelper.apply_io_toCtpOrg(ctpObjectIds, ctpOrgEntity, io);


                }

            }
            catch (Exception e)
            {
                string error = $"Error in addAgentToCtpOrgObjects(). Exception message:{Environment.NewLine}{e.Message}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

        }

        public static DateTime convertTimestampToDatetime(long unixTimestampMilliseconds)
        {
            DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return unixEpoch.AddMilliseconds(unixTimestampMilliseconds);
        }

        public static async Task<IDictionary<string, Object>> getCTPOrgObjects(string ctpOrgId, List<string> errorStack = null)
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
                    errorStack?.Add(error);
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

                dynamic languageEnglish = ((JArray)ctpOrgObject?.io)?.ToList<dynamic>()?.Where(instance => instance.signature == "LanguageObject_001")?.FirstOrDefault();
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
                errorStack?.Add(error);
            }

            return null;
        }

        public static async Task<int> getOptionSetValue(string optionSetName, string optionSetLabel, List<string> errorStack = null)
        {
            int optionSetValue = 0;
            try
            {

                RetrieveOptionSetRequest retrieveOptionSetRequest = new RetrieveOptionSetRequest
                {
                    Name = optionSetName
                };

                RetrieveOptionSetResponse retrieveOptionSetResponse = (RetrieveOptionSetResponse)await DynamicsInterface.DataverseClient.ExecuteAsync(retrieveOptionSetRequest);
                OptionSetMetadata retrievedOptionSetMetadata = (OptionSetMetadata)retrieveOptionSetResponse.OptionSetMetadata;
                OptionMetadataCollection options = retrievedOptionSetMetadata.Options;
                OptionMetadata option = options.ToList().Find(item => item.Label.UserLocalizedLabel.Label.ToLower() == optionSetLabel.ToLower());

                if (option != null)
                    optionSetValue = option.Value.Value;

            }
            catch (Exception e)
            {
                string error = $"Error in getOptionSetValue(). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}optionSetName: {optionSetName}; optionSetLabel: {optionSetLabel}"
                                ;
                
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);

               
            }

            return optionSetValue;
        }

        public static string getOrgQualStatus(Guid accountId, List<string> errorStack = null)
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
                string error = $"Error in getOrgQualStatus(Guid accountId). Exception message:{Environment.NewLine}{e.Message}"
                                    + $"{Environment.NewLine}accountId: {accountId}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return orgQualStatus;
        }
        public static async Task findDynamicsAccountMatches(string ctpOrgId)
        {
            IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);

            await findDynamicsAccountMatches(ctpOrgEntity);
        }

        public static async Task createValidationRequestFromCtpOrgEntity(IDictionary<string, Object> ctpOrgEntity, List<string> errorStack = null)
        {
            try
            {
                dynamic ctpOrgEntityJson = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(ctpOrgEntity));


                dynamic orgAgent = ((JArray)ctpOrgEntityJson.orgAgents)?.ToList<dynamic>()?.Where(agent => (string)agent.status == "Verified")?.FirstOrDefault();

                orgAgent = orgAgent ?? ((JArray)ctpOrgEntityJson.orgAgents)?.ToList<dynamic>()?.FirstOrDefault();




                IDictionary<string, Object> addressMain = (IDictionary<string, Object>)ctpOrgEntity["addressMain"];



                IDictionary<string, Object> validationRequest = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;

                validationRequest.Add("LegalName", ctpOrgEntity["orgLegalName"]);
                validationRequest.Add("AddressLine1", addressMain["address"]);
                validationRequest.Add("AddressOther", addressMain["addressExt"] ?? "");
                validationRequest.Add("AddressCity", addressMain["city"]);
                validationRequest.Add("AddressStateRegion", addressMain["stateRegion"]);
                validationRequest.Add("AddressPostalCode", addressMain["postalCode"]);
                validationRequest.Add("AddressCountryId", addressMain["countryId"]);
                validationRequest.Add("Email", ctpOrgEntity["emailMain"] ?? "");
                validationRequest.Add("Phone", ctpOrgEntity["phoneMain"] ?? "");
                validationRequest.Add("Website", ctpOrgEntity["webSiteMain"] ?? "");
                validationRequest.Add("MissionStatement", ctpOrgEntity["descriptiveObjectMission"] ?? "");
                validationRequest.Add("OperatingBudget", ctpOrgEntity["operatingBudget"] ?? "");
                validationRequest.Add("ActivityCode", ctpOrgEntity["purposeActivityCode"]);
                validationRequest.Add("AgentFirstName", orgAgent?.firstName ?? "");
                validationRequest.Add("AgentLastName", orgAgent?.lastName ?? "");
                validationRequest.Add("AgentEmail", orgAgent?.email ?? "");
                validationRequest.Add("EffectiveDatetime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                validationRequest.Add("TransactionId", $"ts_{DateTime.UtcNow.ToString("yyyyMMddHHmmssffff")}");

                List<dynamic> registrationIdentifiers = new List<dynamic>();


                var registrationIdentifier = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;

                registrationIdentifier.Add("LegalIdentifier", ctpOrgEntity["legalIdentifier"]);
                registrationIdentifier.Add("RegulatoryBody", "EIN");
                registrationIdentifier.Add("ArtifactURI", false);
                registrationIdentifiers.Add(registrationIdentifier);

                validationRequest.Add("RegistrationIdentifiers", registrationIdentifiers);


                string validationRequestJson = JsonConvert.SerializeObject(validationRequest, Newtonsoft.Json.Formatting.Indented);


                string CTPUrl = "https://qa.techsoupvalidationservices.org";
                //5d1df689-0470-4d4e-aa1e-61b4d846d866 - techsoup
                //34613f15-c6eb-4952-9636-c79110b1ffc0 - benevity
                //a443e1f3-e055-4fb9-b732-64d9cc127891 - slack
                string accessKey = "34613f15-c6eb-4952-9636-c79110b1ffc0";
                string endPointPath = $"/{accessKey}/queue/validationrequest";
                dynamic ctpOrgUpdateResponse = await makeHttpPostCall(validationRequestJson
                                                               , CTPUrl, endPointPath
                                                                , queryParams: null
                                                                );

                string validationRequestResponse = JsonConvert.SerializeObject(ctpOrgUpdateResponse, Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception e)
            {
                string error = $"Error in createValidationRequestFromCtpOrgEntity(). Exception message:{Environment.NewLine}{e.Message}"
                                    + $"{Environment.NewLine}ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
        }


        public static async Task findDynamicsAccountMatches(IDictionary<string, Object> ctpOrgEntity, List<string> errorStack = null)
        {
            try
            {
                Dictionary<string, string> envVariables = await GetEnvironmentVariables();


                IDictionary<string, Object> addressMain = (IDictionary<string, Object>)ctpOrgEntity["addressMain"];

                string legalId = (string)ctpOrgEntity["legalIdentifier"];
                string name = (string)ctpOrgEntity["orgLegalName"];
                string address1 = (string)addressMain["address"];
                string stateProvince = (string)addressMain["stateRegion"];
                string postalCode = (string)addressMain["postalCode"];
                string countryCode = (string)addressMain["countryId"];
                string website = (string)ctpOrgEntity["webSiteMain"];
                string phone = (string)ctpOrgEntity["phoneMain"];


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
                                                                                    , DynamicsInterface.DynamicsEnvironments
                                                                                    , envVariables
                                                                                    );

                AccountMatchResponse accountMatchResponse = accountMatchService.FindMatches(matchRequestText);

                List<AccountMatch> accountMatches = accountMatchResponse.Matches.Where(Match => Match.OverallScore >= 0.70
                                                                                                    //&& getOrgQualStatus(Match.AccountId) != "Canceled"
                                                                                                    && !Match.AccountName.StartsWith("["
                                                                                                    )
                                                                                        )?.ToList();
            }
            catch (Exception e)
            {
                string error = $"Error in findDynamicsAccountMatches(). Exception message:{Environment.NewLine}{e.Message}"
                                    + $"{Environment.NewLine}ctpOrgId: {(string)ctpOrgEntity["ctpOrgId"]}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
        }
        public static async Task getOrgConstraints(Entity tsResponse, string ctpOrgId, string constraintIds)
        {
            try
            {
                Dictionary<string, string> constraintsErrorCodes = new Dictionary<string, string>
                {
                    {"E00_1","Unknown client program"},
                    {"E00_2","Offer not available for this entity type"},
                    {"E00_3","Entity not qualified"},
                    {"E00_4","Entity pending qualification"},
                    {"E00_5","Timestamp error"},
                    {"E00_6","Offer not available for this location"},
                    {"E00_7","Offer not available for this activity code"},
                    {"E00_8","Offer not available for this budget"},
                    {"E00_11","Organization not found"},
                    {"E00_12","Agent not found"}
                };


                string ctpUrl = "https://tsvc.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                string endPointPath = $"/services/constraint/v_001/{ctpSessionKey}";

                Dictionary<string, string> queryParams = new Dictionary<string, string>();
                queryParams.Add("org_id", ctpOrgId);
                queryParams.Add("constraint_id", constraintIds);

                dynamic responseJson = await makeHttpGetCall(
                                                            ctpUrl, endPointPath
                                                            , queryParams: queryParams
                                                            );


                List<dynamic> cptConstraints = ((JArray)responseJson.returnStatus?.data)?.ToList<dynamic>() ?? new List<dynamic>();

                EntityCollection constraints = new EntityCollection();


                foreach (dynamic cptConstraint in cptConstraints)
                {
                    Entity constraintEntity = new Entity();
                    constraintEntity["programCode"] = (string)cptConstraint.program_code;
                    constraintEntity["eligibilityStatus"] = (bool)cptConstraint.eligibility_status ? "eligible" : "not eligible";


                    List<dynamic> contraintErrors = ((JArray)cptConstraint.error_code).ToList<dynamic>();

                    string constraintErrorsText = string.Empty;
                    foreach (dynamic constraintError in contraintErrors)
                    {
                        string errorCode = (string)constraintError;
                        string errorDescription = constraintsErrorCodes.ContainsKey(errorCode) ? constraintsErrorCodes[errorCode] : "Unknown error code";
                        //constraintErrorsText += errorCode + " - " + errorDescription + "; ";
                        constraintErrorsText += errorDescription + "; ";
                    }

                    constraintEntity["errorCodes"] = constraintErrorsText.TrimEnd(' ', ';');

                    constraints.Entities.Add(constraintEntity);
                }

                tsResponse.Attributes.Add("resultStatus", "success");
                tsResponse.Attributes.Add("constraints", constraints);
            }
            catch (Exception e)
            {

            }


        }
        public static async Task<Entity> createOrgForCtp(string ctpOrgId, string tsOrgId = null)
        {
            IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);
            Entity account = await createOrgForCtp(ctpOrgEntity, tsOrgId, (string)ctpOrgEntity["externalReferenceTransactionId"]);
            return account;


        }

        public static async Task addQualificationAddressCaseNoteToAccount(string ctpOrgId, string tsOrgId = null)
        {
            try
            {
                IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);

                string externalReferenceOnyx = (string)ctpOrgEntity["externalReferenceOnyx"];

                if (tsOrgId == null)
                    tsOrgId = externalReferenceOnyx;

                Entity account = await getAccountForTsOrgId(tsOrgId);

                Guid accountId = account.Id;

                Entity orgDesignationCodeEntity = account.GetAttributeValue<EntityReference>("new_orgdesignation") == null ? null :
                                                           DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", account.GetAttributeValue<EntityReference>("new_orgdesignation").Id, new ColumnSet(true));

                Guid orgDesigId = orgDesignationCodeEntity == null ? Guid.Empty : orgDesignationCodeEntity.Id;
                string orgDesigCode = orgDesignationCodeEntity?.GetAttributeValue<string>("new_qualcode");
                string qualName = orgDesignationCodeEntity.GetAttributeValue<string>("new_qualname");

                DateTime qualDate = (DateTime)ctpOrgEntity["statusDate"];

                //ctpOrgEntity["entityOrganizationDate"]
                qualDate = qualDate > DateTime.UtcNow.AddDays(-10) ? (DateTime)ctpOrgEntity["trackerObjectDate"] : qualDate;

                int qualStatusCode = ((string)ctpOrgEntity["statusOrg"]).ToLower() == "qualified" ? 1 : 5; //1 - qualified, 5 - disqualified
                                                                                                           //await createOrgQualification(accountId, orgDesigId, qualStatusCode, qualDate, int.Parse(tsOrgId), orgDesigCode);

                //IDictionary<string, Object> addressLegal = (IDictionary<string, Object>)(ctpOrgEntity["addressLegal"] ?? ctpOrgEntity["addressMain"]);

                //await addLegalAddress(accountId, addressLegal);


                //await processSystemNote(" --- ctpOrgObject --- ", (string)ctpOrgEntity["orgObjectText"], account.ToEntityReference());

                DateTime accountCreatedOn = account.GetAttributeValue<DateTime>("createdon");
                Dictionary<string, object> extraCaseFields = new Dictionary<string, object>();
                extraCaseFields.Add("overriddencreatedon", accountCreatedOn);

                int tsCaseStatusCode = ((string)ctpOrgEntity["statusOrg"]).ToLower() == "qualified" ? 102056 : 102057;//102056 - OQ - Qualified; 102057 - OQ - Disqualified; 102050 - OQ - Not Started
                Guid caseId = await createCaseGeneric(title: $"{orgDesigCode} - {qualName} - TSOrgId: {tsOrgId}"
                                                                            , caseTypeCode: 2
                                                                            , type: 101996
                                                                            , customerRef: account.ToEntityReference()
                                                                            , caseStatus: tsCaseStatusCode
                                                                            , qualCodeId: orgDesigId
                                                                            , extraCaseFields: extraCaseFields
                                                                            );
            }
            catch (Exception e)
            {
            }
        }

        public static async Task<Entity> getOrgCurrentQualCase(string ctpOrgId, string tsOrgId = null, List<string> errorStack = null)
        {
            Entity caseEntity = null;

            try
            {
                IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);

                string externalReferenceOnyx = (string)ctpOrgEntity["externalReferenceOnyx"];

                if (tsOrgId == null)
                    tsOrgId = externalReferenceOnyx;

                Entity account = await getAccountForTsOrgId(tsOrgId);

                Guid accountId = account.Id;

                Entity orgDesignationCodeEntity = account.GetAttributeValue<EntityReference>("new_orgdesignation") == null ? null :
                                                           DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", account.GetAttributeValue<EntityReference>("new_orgdesignation").Id, new ColumnSet(true));

                Guid orgDesigId = orgDesignationCodeEntity == null ? Guid.Empty : orgDesignationCodeEntity.Id;
                string orgDesigCode = orgDesignationCodeEntity?.GetAttributeValue<string>("new_qualcode");

                if (orgDesignationCodeEntity == null)
                    return null;

                QueryExpression queryQualCase = new QueryExpression("incident");
                queryQualCase.ColumnSet = new ColumnSet(true);
                queryQualCase.Criteria.AddCondition("casetypecode", ConditionOperator.Equal, 2); //Qualification Case
                queryQualCase.Criteria.AddCondition("ts_type", ConditionOperator.Equal, 101996);// Organization Qualification
                queryQualCase.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, orgDesigId);
                queryQualCase.Criteria.AddCondition("customerid", ConditionOperator.Equal, accountId);
                queryQualCase.AddOrder("createdon", OrderType.Descending);
                queryQualCase.TopCount = 1;
                EntityCollection qualCaseCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryQualCase);
                if (qualCaseCollection.Entities.Count > 0)
                {
                    caseEntity = qualCaseCollection.Entities.First();
                    return caseEntity;
                }
            }
            catch (Exception e)
            {
                string error = $"Error in getOrgCurrentQualCase(). Exception message:{Environment.NewLine}{e.Message}"
                                    + $"{Environment.NewLine}ctpOrgId: {ctpOrgId}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

            return caseEntity;
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
                                                            , List<string> errorStack = null
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
                string error = $"Error in createCaseGeneric(...). Exception message:{Environment.NewLine}{e.Message}"
                                   + $"For {customerRef.LogicalName}. {customerRef.LogicalName}Id:{customerRef.Id.ToString()}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
            return caseId;
        }

        public static async Task<bool> updateCaseGeneric(
                                                            Entity caseEntity
                                                            , string title
                                                            , int caseTypeCode
                                                            , int? type
                                                            , EntityReference customerRef
                                                            , int? caseStatus
                                                            , Guid? qualCodeId
                                                            , Dictionary<string, object> extraCaseFields
                                                            , DataverseClientLib.ServiceClient dataverseClient = null
                                                            , List<string> errorStack = null
                                                            )
        {
            dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;

            try
            {
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

                await dataverseClient.UpdateAsync(caseEntity);

                return true;
            }
            catch (Exception e)
            {
                string error = $"Error in updateCaseGeneric(...). Exception message:{Environment.NewLine}{e.Message}"
                                    + $"caseId: {caseEntity.Id.ToString()}"
                                    + $"For {customerRef.LogicalName}. {customerRef.LogicalName}Id:{customerRef.Id.ToString()}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return false;
            }
        }

        public static void assignValueToAttribute(Entity caseEntity, KeyValuePair<string, object> caseField, List<string> errorStack = null)
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
                string error = $"Error in assignValueToAttribute(). Exception message:{Environment.NewLine}{e.Message}"
                                    ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

        }
        public static async Task<Entity> retrieveOrgQualCase(Entity account
                                                    , DataverseClientLib.ServiceClient dataverseClient = null
                                                    , List<string> errorStack = null)
        {
            try
            {
                dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;
                Entity caseEntity = null;
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
                EntityCollection qualCaseCollection = await dataverseClient.RetrieveMultipleAsync(queryQualCase);

                if (qualCaseCollection.Entities.Count > 0)
                {
                    caseEntity = qualCaseCollection.Entities.First();
                }
                else
                {
                    string qualStatus = getOrgQualStatus(account.Id);

                    QueryExpression queryMapping = new QueryExpression("ts_fieldhierarchyandmapping");
                    queryMapping.ColumnSet = new ColumnSet("ts_value", "ts_valuecode");
                    queryMapping.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "ts_casestatus");
                    queryMapping.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, "Organization Qualification");
                    queryMapping.Criteria.AddCondition("ts_mappedfieldvalue", ConditionOperator.Equal, qualStatus);
                    EntityCollection mappingCollection = await dataverseClient.RetrieveMultipleAsync(queryMapping);

                    if (mappingCollection.Entities.Count == 0)
                    {
                        //QualificationHelper.writeToTrace("At retrieveOrgQualCase(Entity account). No ts_fieldhierarchyandmapping found for ts_casestatus with ts_parentfieldvalue = 'Organization Qualification' and ts_mappedfieldvalue = " + qualStatus
                        //, tracingService);
                        return null;
                    }

                    Entity fieldMapping = mappingCollection.Entities.First();
                    string tsCaseStatus = fieldMapping.GetAttributeValue<string>("ts_value");//Case status
                    int tsCaseStatusCode = fieldMapping.GetAttributeValue<int>("ts_valuecode");//Case status option value


                    Entity qualCodeEntity = await dataverseClient.RetrieveAsync("new_qualificationcode", orgDesigId, new ColumnSet(true));
                    string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");
                    string qualTerm = qualCodeEntity.FormattedValues["new_qualterm"];
                    string qualCategory = qualCodeEntity.GetAttributeValue<string>("new_qualcategory");
                    string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                    string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                    Guid caseId = await createCaseGeneric(title: $"{qualCode} - {qualName} - TSOrgId: {tsOrgId}"
                                                                            , caseTypeCode: 2
                                                                            , type: 101996
                                                                            , customerRef: new EntityReference(account.LogicalName, account.Id)
                                                                            , caseStatus: tsCaseStatusCode
                                                                            , qualCodeId: orgDesigId
                                                                            , extraCaseFields: null
                                                                            , dataverseClient);

                    caseEntity = await dataverseClient.RetrieveAsync("incident", caseId, new ColumnSet(true));
                }

                return caseEntity;

            }
            catch (Exception e)
            {
                string error = $"Error in retrieveOrgQualCase(). Exception message:{Environment.NewLine}{e.Message}{Environment.NewLine}accountId: {account.Id}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }


        }


        public static async Task updateOrg(string ctpOrgId, string tsOrgId = null)
        {
            try
            {
                IDictionary<string, Object> ctpOrgEntity = await getCTPOrgObjects(ctpOrgId);

                string externalReferenceOnyx = (string)ctpOrgEntity["externalReferenceOnyx"];

                if (tsOrgId == null)
                    tsOrgId = externalReferenceOnyx;

                Entity account = await getAccountForTsOrgId(tsOrgId);

                Guid accountId = account.Id;



                QueryExpression queryFieldMap = new QueryExpression("ts_fieldhierarchyandmapping");
                queryFieldMap.ColumnSet = new ColumnSet(true);
                queryFieldMap.Criteria.AddCondition("ts_fieldname", ConditionOperator.Equal, "stateorprovince");
                queryFieldMap.Criteria.AddCondition("ts_parentfieldvalue", ConditionOperator.Equal, account.GetAttributeValue<string>("address1_country"));
                queryFieldMap.Criteria.AddCondition("ts_value", ConditionOperator.Equal, account.GetAttributeValue<string>("address1_stateorprovince"));
                EntityCollection fieldMapCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryFieldMap);

                if (fieldMapCollection.Entities.Count > 0)
                {
                    Entity fieldHierarchy = fieldMapCollection.Entities.First();
                    int countryOptionValue = fieldHierarchy.GetAttributeValue<int>("ts_valuecode");
                    account["ts_stateprovdesc"] = new OptionSetValue(countryOptionValue);
                }


                await DynamicsInterface.DataverseClient.UpdateAsync(account);

                //IDictionary<string, Object> addressLegal = (IDictionary<string, Object>)(ctpOrgEntity["addressLegal"] ?? ctpOrgEntity["addressMain"]);


                //await addLegalAddress(accountId, addressLegal);


            }
            catch (Exception e)
            {
            }
        }

        public static async Task<Entity> getAccountForTsPngoId(string tsPngoId, List<string> errorStack = null)
        {
            try
            {
                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(true);
                queryAccount.Criteria.AddCondition("ts_tspngoid", ConditionOperator.Equal, tsPngoId);
                EntityCollection accountCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryAccount);


                if (accountCollection.Entities.Count == 0)
                {
                    string error = $"getAccountForTsPngoId). No account found with ts_tspngoid: {tsPngoId}";
                    DynamicsInterface.writeToLog(error);
                    errorStack?.Add(error);
                    return null;
                }

                Entity account = accountCollection.Entities.First();
                return account;
            }
            catch (Exception e)
            {
                string error = $"Error in getAccountForTsOrgId(). Exception message:{Environment.NewLine}{e.Message}{Environment.NewLine}tsPngoId: {tsPngoId}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
            return null;
        }

        public static async Task<Entity> getAccountForTsOrgId(string tsOrgId, List<string> errorStack = null)
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
                string error = $"Error in getAccountForTsOrgId(). Exception message:{Environment.NewLine}{e.Message}{Environment.NewLine}tsOrgId: {tsOrgId}"
                    ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }
        }

        public static async Task<Entity> getAccountForCtpOrgId(string ctpOrgId, List<string> errorStack = null)
        {
            try
            {
                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(true);
                queryAccount.Criteria.AddCondition("ts_ctporgid", ConditionOperator.Equal, ctpOrgId);
                EntityCollection accountCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryAccount);

                if (accountCollection.Entities.Count == 0)
                    return null;

                Entity account = accountCollection.Entities.First();
                return account;
            }
            catch (Exception e)
            {
                string error = $"Error in getAccountForCtpOrgId(). Exception message:{Environment.NewLine}{e.Message}{Environment.NewLine}ctpOrgId: {ctpOrgId}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }
        }
        public static async Task<Entity> createOrgForCtp(IDictionary<string, Object> ctpOrgEntity
                                                            , string tsOrgId
                                                            , string validationReqTransactionId
                                                            , DataverseClientLib.ServiceClient dataverseClient = null
                                                            , List<string> errorStack = null
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

                int orgSource = await ProcessHelper.getOptionSetValue("new_tsgsource", sourceTytpe);

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


                Guid caseId = await ProcessHelper.createCaseGeneric(title: $"{qualCode} - {qualName} - TSOrgId: {tsOrgId}"
                                                                            , caseTypeCode: 2
                                                                            , type: 101996
                                                                            , customerRef: account.ToEntityReference()
                                                                            , caseStatus: tsCaseStatusCode
                                                                            , qualCodeId: qualCodeEntity.Id
                                                                            , extraCaseFields: extraCaseFields
                                                                            , dataverseClient
                                                                            );


                if (QualStatus[qualStatusCode.ToString()] != "qualified")
                    caseId = await ProcessHelper.createCaseGeneric(title: $"{qualCode} - {qualName} - TSOrgId: {tsOrgId}"
                                                                            , caseTypeCode: 2
                                                                            , type: 101996
                                                                            , customerRef: account.ToEntityReference()
                                                                            , caseStatus: 102050 //102050 - OQ - Not Started
                                                                            , qualCodeId: qualCodeEntity.Id
                                                                            , extraCaseFields: null
                                                                            , dataverseClient
                                                                            );


                IDictionary<string, Object> addressLegal = (IDictionary<string, Object>)(ctpOrgEntity["addressLegal"] ?? ctpOrgEntity["addressMain"]);
                await ProcessHelper.addLegalAddress(accountId, addressLegal
                                                            , dataverseClient
                                        );

                await ProcessHelper.processSystemNote(" --- ctpOrgObject --- ", (string)ctpOrgEntity["orgObjectText"], account.ToEntityReference());




                dynamic ctpOrgEntityJson = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(ctpOrgEntity));
                List<dynamic> orgAgents = ((JArray)ctpOrgEntityJson.orgAgents)?.ToList<dynamic>() ?? new List<dynamic>();

                foreach (dynamic orgAgent in orgAgents)
                {
                    OptionSetValue agentVerificationStatusOption = orgAgent.status == "Verified" ? new OptionSetValue(1) : new OptionSetValue(0); //0 - not verified; 1 - verified

                    Entity agentContact = await createAgentContact(orgAgent, validationReqTransactionId);

                    if (agentContact != null)
                        await connectAgentToAccount(accountId, agentContact.Id, agentVerificationStatusOption, validationReqTransactionId);
                }



                return account;
            }
            #region Catch
            catch (Exception e)
            {
                string error = $"Error in createOrgForCtp(). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}transactionId: {validationReqTransactionId}; ctpOrgId: {ctpOrgEntity["ctpOrgId"]}; tsOrgId: {tsOrgId ?? ""}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }
            #endregion

        }
        public static async Task<Guid> createOrgQualification(Guid accountId, Guid qualCodeId, int qualStatusCode, DateTime qualStatusDateUTC
                                                                             , int tsOrgId, string qualCode
                                                                             , DataverseClientLib.ServiceClient dataverseClient = null
                                                                             , List<string> errorStack = null)
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
                string error = $"Error in createOrgQualification(). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}accountId: {accountId.ToString()}; qualCodeId: {qualCodeId.ToString()}{Environment.NewLine}tsOrgId: {tsOrgId.ToString()}; qualCode: {qualCode}"
                                + $"; qualStatusCode: {qualStatusCode.ToString()}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return Guid.Empty;
            }

            return orgQualId;
        }


        public static async Task processQualHistory(Guid accountId, Guid qualCodeId, int qualStatusCode, DateTime qualStatusDateUTC
                                                                             , int tsOrgId, string qualCode
                                                                             , DataverseClientLib.ServiceClient dataverseClient = null
                                                                             , List<string> errorStack = null)
        {
            try
            {
                dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;

                Entity orgQualification = null;

                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                EntityCollection orgQualificationCollection = await dataverseClient.RetrieveMultipleAsync(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                {
                    orgQualification = orgQualificationCollection.Entities.First();

                }
                else
                {
                    Guid orgQualId = await createOrgQualification(accountId, qualCodeId, qualStatusCode, qualStatusDateUTC, tsOrgId
                                                                , qualCode
                                                                , dataverseClient
                                                                , errorStack
                                                                 );
                    orgQualification = await dataverseClient.RetrieveAsync("ts_organizationqualification", orgQualId, new ColumnSet(true));
                }


                QueryExpression queryQualHistory = new QueryExpression("ts_organizationqualificationhistory");
                queryQualHistory.Criteria.AddCondition("ts_organizationqualificationid", ConditionOperator.Equal, orgQualification.Id);
                queryQualHistory.Criteria.AddCondition("ts_qualificationactiondate", ConditionOperator.Equal, qualStatusDateUTC);
                EntityCollection qualHistoryCollection = await dataverseClient.RetrieveMultipleAsync(queryQualHistory);

                if (qualHistoryCollection.Entities.Count > 0)
                    return;

                Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");

                orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQualification.Id);
                orgQualHistory["ts_qualificationstatusaction"] = new OptionSetValue(qualStatusCode);
                orgQualHistory["ts_qualificationactiondate"] = qualStatusDateUTC;
                orgQualHistory["ts_name"] = orgQualification.Id.ToString() + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                await dataverseClient.CreateAsync(orgQualHistory);
            }
            catch (Exception e)
            {
                string error = $"Error in processQualHistory(). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}accountId: {accountId.ToString()}; qualCodeId: {qualCodeId.ToString()}{Environment.NewLine}tsOrgId: {tsOrgId.ToString()}; qualCode: {qualCode}"
                                + $"; qualStatusCode: {qualStatusCode.ToString()}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
        }

        public static async Task processOrgQualification(Guid accountId, Guid qualCodeId, int qualStatusCode, DateTime qualStatusDateUTC
                                                                             , int tsOrgId, string qualCode
                                                                             , DataverseClientLib.ServiceClient dataverseClient = null
                                                                             , List<string> errorStack = null)
        {
            try
            {
                dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;

                QueryExpression queryOrgQualification = new QueryExpression("ts_organizationqualification");
                queryOrgQualification.ColumnSet.AddColumns("ts_qualificationstatus");
                queryOrgQualification.Criteria.AddCondition("ts_qualificationcodeid", ConditionOperator.Equal, qualCodeId);
                queryOrgQualification.Criteria.AddCondition("ts_accountid", ConditionOperator.Equal, accountId);
                EntityCollection orgQualificationCollection = await dataverseClient.RetrieveMultipleAsync(queryOrgQualification);

                if (orgQualificationCollection.Entities.Count > 0)
                {
                    Entity orgQualification = orgQualificationCollection.Entities.First();

                    int currentQualStatusCode = orgQualification.GetAttributeValue<OptionSetValue>("ts_qualificationstatus")?.Value ?? 0;
                    if (currentQualStatusCode != qualStatusCode)
                    {
                        orgQualification["ts_qualificationstatus"] = new OptionSetValue(qualStatusCode);
                        orgQualification["ts_qualificationstatusdate"] = qualStatusDateUTC;
                        await dataverseClient.UpdateAsync(orgQualification);

                        QueryExpression queryQualHistory = new QueryExpression("ts_organizationqualificationhistory");
                        queryQualHistory.Criteria.AddCondition("ts_organizationqualificationid", ConditionOperator.Equal, orgQualification.Id);
                        queryQualHistory.Criteria.AddCondition("ts_qualificationactiondate", ConditionOperator.Equal, qualStatusDateUTC);
                        EntityCollection qualHistoryCollection = await dataverseClient.RetrieveMultipleAsync(queryQualHistory);

                        if (qualHistoryCollection.Entities.Count > 0)
                            return;

                        Entity orgQualHistory = new Entity("ts_organizationqualificationhistory");

                        orgQualHistory["ts_organizationqualificationid"] = new EntityReference("ts_organizationqualification", orgQualification.Id);
                        orgQualHistory["ts_qualificationstatusaction"] = new OptionSetValue(qualStatusCode);
                        orgQualHistory["ts_qualificationactiondate"] = qualStatusDateUTC;
                        orgQualHistory["ts_name"] = orgQualification.Id.ToString() + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                        await dataverseClient.CreateAsync(orgQualHistory);
                    }
                }
                else
                {
                    await createOrgQualification(accountId, qualCodeId, qualStatusCode, qualStatusDateUTC, tsOrgId
                                                                , qualCode
                                                                , dataverseClient
                                                                , errorStack
                                                                 );
                }

            }
            catch (Exception e)
            {
                string error = $"Error in processOrgQualification(). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}accountId: {accountId.ToString()}; qualCodeId: {qualCodeId.ToString()}{Environment.NewLine}tsOrgId: {tsOrgId.ToString()}; qualCode: {qualCode}"
                                + $"; qualStatusCode: {qualStatusCode.ToString()}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
        }

        public static async Task<Entity> connectAgentToAccount(Guid accountId, Guid contactId, OptionSetValue agentVerificationStatusOption, string validationReqTransactionId, List<string> errorStack = null)
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
                EntityCollection connectionCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryConnection);

                if (connectionCollection.Entities.Count > 0)
                {
                    connectionEntity = connectionCollection.Entities.First();
                    int? agentVerificationStatusCurrent = connectionEntity.GetAttributeValue<OptionSetValue>("ts_agentverificationstatus")?.Value;

                    if (agentVerificationStatusCurrent == null || agentVerificationStatusCurrent != agentVerificationStatusOption.Value)
                    {
                        connectionEntity["ts_agentverificationstatus"] = agentVerificationStatusOption;
                        await DynamicsInterface.DataverseClient.UpdateAsync(connectionEntity);
                    }
                    /*Todo: handle the other scenarios where the connection already exists but the agent verification status is not the same as the one provided*/
                    return connectionEntity;
                }
                #endregion


                connectionEntity = new Entity("connection");


                #region Connection Roles
                QueryExpression queryConnectionRole = new QueryExpression("connectionrole");
                queryConnectionRole.Criteria.AddCondition("name", ConditionOperator.Equal, connectionRoleToName);
                EntityCollection connectionRoleCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryConnectionRole);

                if (connectionRoleCollection.Entities.Count == 0)
                    return null;

                Guid connectionRoleToId = connectionRoleCollection.Entities.First().Id;


                QueryExpression queryConnectionFromRole = new QueryExpression("connectionrole");
                queryConnectionFromRole.Criteria.AddCondition("name", ConditionOperator.Equal, connectionRoleFromName);
                EntityCollection connectionRoleFromCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryConnectionFromRole);

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

                Guid connectionId = await DynamicsInterface.DataverseClient.CreateAsync(connectionEntity);

                connectionEntity = await DynamicsInterface.DataverseClient.RetrieveAsync("connection", connectionId, new ColumnSet(true));
                return connectionEntity;
                #endregion
            }
            #region Catch
            catch (Exception e)
            {
                string error = $"Error in connectAgentToAccount(). Exception message:{Environment.NewLine}{e.Message}"
                                  + $"{Environment.NewLine} validationReqTransactionId: {validationReqTransactionId}"
                                  ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }
            #endregion
        }

        public static async Task<IDictionary<string, Object>> getAgentContact(Entity validationRequestCase, Entity orgAccount, string validationReqTransactionId, List<string> errorStack = null)
        {
            try
            {
                string agentEmail = validationRequestCase.GetAttributeValue<string>("ts_validationagentemail");
                IDictionary<string, Object> agentContact = new System.Dynamic.ExpandoObject() as IDictionary<string, Object>;

                if (string.IsNullOrEmpty(agentEmail))
                    return agentContact;

                #region Find If Exists
                QueryExpression queryContact = new QueryExpression("contact");
                queryContact.ColumnSet = new ColumnSet(true);
                queryContact.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, agentEmail);
                EntityCollection contactCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryContact);

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
                EntityCollection connectionCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryConnection);

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
                string error = $"Error in getAgentContact(). Exception message:{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine} validationReqTransactionId: {validationReqTransactionId}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }
            #endregion
        }


        public static async Task<Entity> createAgentContact(dynamic orgAgent, string validationReqTransactionId, List<string> errorStack = null)
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

                string tsContactId = await ProcessHelper.getNextTsCustomerId();
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
                string error = $"Error in createAgentContact(orgAgent). Exception message:{Environment.NewLine}{e.Message}"
                    + $"{Environment.NewLine}transactionId: {validationReqTransactionId}; agentEmail: {orgAgent.email ?? ""}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }
            #endregion
        }



        public static async Task<bool> createNote(string noteTitle, string noteDesc, EntityReference annotationParentRef, DateTime? createdDateUTC = null, List<string> errorStack = null)
        {
            try
            {
                createdDateUTC = createdDateUTC ?? DateTime.UtcNow;

                Entity annotation = new Entity("annotation");

                annotation["subject"] = noteTitle;
                annotation["notetext"] = noteDesc;
                annotation["objectid"] = annotationParentRef;
                annotation["overriddencreatedon"] = createdDateUTC;

                Guid annotationId = await DynamicsInterface.DataverseClient.CreateAsync(annotation);

                return true;
            }
            catch (Exception e)
            {
                string error = $"Error in createNote(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}noteTitle: {noteTitle}; noteDesc: {noteDesc}"
                                                + $"{Environment.NewLine}{annotationParentRef.LogicalName}Id: {annotationParentRef.Id.ToString()}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return false;
            }
        }


        public static async Task processSystemNote(string noteTitle, string noteDesc, EntityReference annotationParentRef, List<string> errorStack = null)
        {
            try
            {
                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryAnnotation);

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
                    await DynamicsInterface.DataverseClient.UpdateAsync(annotation);
                }
                else
                {
                    Guid annotationId = await DynamicsInterface.DataverseClient.CreateAsync(annotation);
                }
            }
            catch (Exception e)
            {
                string error = $"Error in processSystemNote(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}noteDesc length: {noteDesc.Length}; noteTitle: {noteTitle}; noteDesc: {noteDesc}"
                                                + $"{Environment.NewLine}{annotationParentRef.LogicalName}Id: {annotationParentRef.Id.ToString()}"
                                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
        }


        public static async Task<Entity> getSystemNote(string noteTitle, EntityReference annotationParentRef, List<string> errorStack = null)
        {
            Entity annotation = null;
            try
            {
                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryAnnotation);

                if (annotationCollection.Entities.Count() > 0)
                    annotation = annotationCollection.Entities.First();
            }
            catch (Exception e)
            {
                string error = $"Error in getSystemNote(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}noteTitle: {noteTitle}"
                                                + $"{Environment.NewLine}{annotationParentRef.LogicalName}Id: {annotationParentRef.Id.ToString()}"
                                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
            return annotation;
        }

        public static async Task<bool> existsSystemNote(string noteTitle, EntityReference annotationParentRef, List<string> errorStack = null)
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
                string error = $"Error in existsSystemNote(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}noteTitle: {noteTitle}"
                                                + $"{Environment.NewLine}{annotationParentRef.LogicalName}Id: {annotationParentRef.Id.ToString()}"
                                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
            return existsNote;
        }

        public static async Task<bool> removeSystemNote(string noteTitle, EntityReference annotationParentRef, List<string> errorStack = null)
        {
            bool success = false;
            try
            {
                QueryExpression queryAnnotation = new QueryExpression("annotation");
                queryAnnotation.ColumnSet = new ColumnSet(true);
                queryAnnotation.Criteria.AddCondition("subject", ConditionOperator.Equal, noteTitle);
                queryAnnotation.Criteria.AddCondition("objectid", ConditionOperator.Equal, annotationParentRef.Id);
                EntityCollection annotationCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryAnnotation);

                if (annotationCollection.Entities.Count() > 0)
                    DynamicsInterface.DataverseClient.Delete("annotation", annotationCollection.Entities.First().Id);

                success = true;
            }
            catch (Exception e)
            {
                string error = $"Error in removeSystemNote(). Exception message:{Environment.NewLine}{e.Message}"
                                                + $"{Environment.NewLine}noteTitle: {noteTitle}"
                                                + $"{Environment.NewLine}{annotationParentRef.LogicalName}Id: {annotationParentRef.Id.ToString()}"
                                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
            return success;
        }


        public static async Task addLegalAddress(Guid accountId, IDictionary<string, Object> addressLegal
                                                                                            , DataverseClientLib.ServiceClient dataverseClient = null
                                                                                            , List<string> errorStack = null)
        {
            try
            {
                dataverseClient = dataverseClient ?? DynamicsInterface.DataverseClient;

                QueryExpression queryEntity = new QueryExpression("customeraddress");
                queryEntity.Criteria.AddCondition("parentid", ConditionOperator.Equal, accountId);
                queryEntity.Criteria.AddCondition("addresstypecode", ConditionOperator.Equal, 5);
                EntityCollection entityCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryEntity);

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
                EntityCollection fieldMapCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryFieldMap);

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
                fieldMapCollection = await DynamicsInterface.DataverseClient.RetrieveMultipleAsync(queryFieldMap);

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
                string error = $"Error in addLegalAddress(). Exception message:{Environment.NewLine}{e.Message}"
                                                    + $"{Environment.NewLine}accountId: {accountId.ToString()}"
                                                    ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }

        }


        public static string cleanCtpString(string fieldValue)
        {
            fieldValue = fieldValue == null ? null :
                                            (fieldValue.Trim() == "nil" ? "" : fieldValue.Trim());

            return fieldValue;
        }
        public static async Task<string> getCTPOrgIdFromTransaction(string transactionId, List<string> errorStack = null)
        {
            Dictionary<string, string> ctpObjectIds = new Dictionary<string, string>();
            try
            {
                //var dateTimeOffset = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                string ctpUrl = "https://tsvc.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                //"61695af7-1652-4b08-b786-192de1884f61";
                string endPointPath = $"/services/validation/v_001/{ctpSessionKey}/{transactionId}";

                dynamic responseJson = await makeHttpGetCall(
                                                            ctpUrl, endPointPath
                                                            , queryParams: null
                                                            );


                dynamic ctpTransactionObject = responseJson.returnStatus?.data;


                JArray ctpOrgIdArray = (JArray)ctpTransactionObject?.OrgId;

                string transactionStatus = ctpTransactionObject?.Transaction ?? "";
                string qualificationStatus = ctpTransactionObject?.Qualification ?? "";

                if (ctpOrgIdArray == null || ctpOrgIdArray.Count != 1 || transactionStatus.ToLower() != "closed" || qualificationStatus.ToLower() != "qualified")
                    return "";

                string ctpOrgId = ctpOrgIdArray.First().ToString();

                return ctpOrgId;
            }
            catch (Exception e)
            {
                string error = $"Error in getCTPOrgIdFromTransaction(). Exception message:{Environment.NewLine}{e.Message}"
                                                    + $"{Environment.NewLine}transactionId: {transactionId}"
                                                    ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return "";
            }

        }

        public static async Task<Dictionary<string, string>> getCTPNewRootInstance(List<string> errorStack = null)
        {
            Dictionary<string, string> ctpObjectIds = new Dictionary<string, string>();
            try
            {
                string ctpUrl = "https://resource.tsgctp.org";
                string ctpSessionKey = CTPSessionKey;
                //"61695af7-1652-4b08-b786-192de1884f61";
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
                string error = $"Error in getCTPNewRootInstance(). Exception message:{Environment.NewLine}{e.Message}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
            }
            return ctpObjectIds;
        }

        public static async Task<dynamic> getCTPOnyxExternalReferenceObject(Dictionary<string, string> ctpObjectIds, List<string> errorStack = null)
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
                string error = $"Error in getCTPOnyxExternalReferenceObject(). Exception message:{Environment.NewLine}{e.Message}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }

        }
       
        public static async Task<dynamic> makeHttpGetCall(
                                               string baseUrl, string endPointPath
                                               , Dictionary<string, string> queryParams
                                               , List<string> errorStack = null
                                               )
        {
            dynamic respDynObject = null;
            int maxRetries = 3;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                        await Task.Delay(15000);

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

                    if (DynamicsInterface.VerboseLog)
                        DynamicsInterface.writeToLog($"makeHttpGetCall()"
                                                        + $"{Environment.NewLine}requestUrl: {requestUrl}"
                                                        );



                    HttpResponseMessage response = await client.GetAsync(
                                                                    requestUrl
                                                                    );


                    if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        string warning = $"makeHttpGetCall() returned status code {(int)response.StatusCode} (attempt {attempt + 1} of {maxRetries + 1})";
                        DynamicsInterface.writeToLog(warning);
                        client.Dispose();
                        if (attempt == maxRetries)
                            errorStack?.Add(warning);
                        continue;
                    }


                    string responseTxt = await response.Content.ReadAsStringAsync();
                    respDynObject = JsonConvert.DeserializeObject(responseTxt);

                    client.Dispose();
                    break;
                }
                catch (Exception e)
                {
                    string error = $"Error in makeHttpGetCall() (attempt {attempt + 1} of {maxRetries + 1}). Exception message:{Environment.NewLine}{e.Message}";
                    DynamicsInterface.writeToLog(error);
                    if (attempt == maxRetries)
                        errorStack?.Add(error);
                }
            }

            return respDynObject;
        }

        public static async Task<dynamic> makeHttpPostCall(string requestJson
                                                           , string baseUrl, string endPointPath
                                                            , Dictionary<string, string> queryParams
                                                            , Dictionary<string, string> extraHeaders = null
                                                            , List<string> errorStack = null
                                                            )
        {
            int maxRetries = 3;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                        await Task.Delay(15000);

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

                    if (DynamicsInterface.VerboseLog)
                        DynamicsInterface.writeToLog($"makeHttpPostCall()"
                                                        + $"{Environment.NewLine}requestUrl: {requestUrl}"
                                                        + $"{Environment.NewLine}requestJson: {requestJson}"
                                                        );


                    #region Create Request Content & Send POST Request
                    StringContent contentRequest = new StringContent(requestJson, Encoding.UTF8, "application/json");

                    HttpResponseMessage response = await client.PostAsync(
                                                                    requestUrl
                                                                    , contentRequest
                                                                     );
                    #endregion

                    if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        string warning = $"makeHttpPostCall() returned status code {(int)response.StatusCode} (attempt {attempt + 1} of {maxRetries + 1})";
                        DynamicsInterface.writeToLog(warning);
                        client.Dispose();
                        if (attempt == maxRetries)
                            errorStack?.Add(warning);
                        continue;
                    }

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
                    string error = $"Error in makeHttpPostCall() (attempt {attempt + 1} of {maxRetries + 1}). Exception message:{Environment.NewLine}{e.Message}";
                    DynamicsInterface.writeToLog(error);
                    if (attempt == maxRetries)
                        errorStack?.Add(error);
                }
                #endregion
            }

            return null;
        }

        public static async Task<Entity> findCaseGenericFilterInAndOut(Dictionary<string, object> filterFieldsIn, Dictionary<string, object> filterFieldsOut, List<string> errorStack = null)
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
                string error = $"Error in findCaseGenericFilterInAndOut(). Exception message:{Environment.NewLine}{e.Message}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }

            return caseEntity;
        }


        public static async Task<EntityCollection> findEntityeGenericFilterInAndOut(string entityLogicalName, Dictionary<string, object> filterFieldsIn, Dictionary<string, object> filterFieldsOut
                                                                                    , List<string> errorStack = null)
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
                string error = $"Error in findEntityeGenericFilterInAndOut(). Exception message:{Environment.NewLine}{e.Message}";
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);
                return null;
            }
        }


        public static void assignValueToQueryExpressionCondition(QueryExpression queryQualCase, KeyValuePair<string, object> criteriaField, string conditionOperator = "equal"
                                                                        , List<string> errorStack = null)
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
                string error = $"Error in assignValueToQueryExpressionCondition(QueryExpression queryQualCase, KeyValuePair<string, object> criteriaField, string conditionOperator = \"equal\"). Exception message: "
                                + $"{Environment.NewLine}{e.Message}"
                                + $"{Environment.NewLine}fieldName: {criteriaField.Key}"
                                + $"{Environment.NewLine}fieldValue: {criteriaField.Value.ToString()}"
                                ;
                DynamicsInterface.writeToLog(error);
                errorStack?.Add(error);

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
    }

}



