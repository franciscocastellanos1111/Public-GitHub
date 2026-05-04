using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.ServiceModel;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Dynamic;
using System.IO;
using Microsoft.Crm.Sdk.Messages;

namespace EDServices
{
    public class BoxInterface : IPlugin
    {
        private const string BoxAccessToken = "REDACTED";

        private const string BoxClientId = "REDACTED";
        private const string BoxClientSecret = "REDACTED";
        private static readonly TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");



        public static Dictionary<string, string> DynamicsEnvironments = new Dictionary<string, string>()
        {
           { "dev" , "https://org90a61c80.crm.dynamics.com" },
            { "qa", "https://tsdynamicsqa.crm.dynamics.com"},
            { "stage" , "https://tsdynamicsstage.crm.dynamics.com" },
            { "prod" , "https://techsoup.crm.dynamics.com" },
            { "https://org90a61c80.crm.dynamics.com" , "dev" },
            { "https://tsdynamicsqa.crm.dynamics.com", "qa" },
            {  "https://tsdynamicsstage.crm.dynamics.com" , "stage"},
            { "https://techsoup.crm.dynamics.com" , "prod" }

        };
        public void Execute(IServiceProvider serviceProvider)
        {


            ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));




            EDServicesHelper.writeToTrace("Starting - EDServices.BoxInterface"
                                                                                , tracingService);

            try
            {
                #region Parameter Initialization
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EDServicesHelper.EnvVariables = EDServicesHelper.GetEnvironmentVariables(service);

                RetrieveCurrentOrganizationRequest request = new RetrieveCurrentOrganizationRequest();
                RetrieveCurrentOrganizationResponse response = (RetrieveCurrentOrganizationResponse)service.Execute(request);
                string DynamicsEnvironmentCurrentUrl = "https://" + response.Detail.UrlName + ".crm.dynamics.com";

                if (DynamicsEnvironments.ContainsKey(DynamicsEnvironmentCurrentUrl))
                {
                    string DynamicsEnvironmentCurrentName = DynamicsEnvironments[DynamicsEnvironmentCurrentUrl];
                    DynamicsEnvironments["DynamicsEnvironmentCurrent"] = DynamicsEnvironmentCurrentName;
                }
                #endregion





                #region Get ts_request
                Entity tsRequest = (Entity)context.InputParameters["ts_request"];
                #endregion


                #region Convert ts_request To Clean JSON & Call BoxApiService
                string cleanJson = ConvertExpandoToJson(tsRequest);

                EDServicesHelper.writeToTrace($"Converted input to clean JSON: {cleanJson}"
                                                                                        , tracingService);


                Entity tsResponse = CallBoxApiServiceDirect(cleanJson, tracingService);

                EDServicesHelper.writeToTrace("Received Entity response directly from BoxApiService"
                                                                                        , tracingService);
                #endregion


                #region Set ts_response
                context.OutputParameters["ts_response"] = tsResponse;

                EDServicesHelper.writeToTrace("BoxInterface completed successfully", tracingService);
                #endregion
            }
            #region Catch
            catch (Exception ex)
            {
                EDServicesHelper.writeToTrace($"Error in BoxInterface: {ex.Message}", tracingService);
                EDServicesHelper.writeToTrace($"Stack trace: {ex.StackTrace}", tracingService);

            }
            #endregion
        }



        private Entity CallBoxApiServiceDirect(string jsonRequest, ITracingService tracingService)
        {
            try
            {
                #region Initialization & Parameters
                string accessToken = GetBoxAccessToken(tracingService);

                EmbeddedBoxApiService boxService = new EmbeddedBoxApiService(accessToken, tracingService);
                #endregion


                #region Call BoxApiService.ProcessBoxRequestForDynamics
                EDServicesHelper.writeToTrace("Calling BoxApiService.ProcessBoxRequestForDynamics directly"
                                                                                                         , tracingService);

                Entity resultEntity = boxService.ProcessBoxRequestForDynamics(jsonRequest);

                EDServicesHelper.writeToTrace("BoxApiService direct call completed", tracingService);
                #endregion

                return resultEntity;
            }
            #region Catch
            catch (Exception ex)
            {
                EDServicesHelper.writeToTrace($"Error calling BoxApiService direct: {ex.Message}", tracingService);

                Entity errorEntity = new Entity();
                if (!errorEntity.Attributes.Contains("success"))
                {
                    errorEntity.Attributes.Add("success", false);
                }
                if (!errorEntity.Attributes.Contains("message"))
                {
                    errorEntity.Attributes.Add("message", $"BoxApiService direct call failed: {ex.Message}");
                }
                if (!errorEntity.Attributes.Contains("error"))
                {
                    errorEntity.Attributes.Add("error", ex.ToString());
                }

                return errorEntity;
            }
            #endregion
        }

        private string GetBoxAccessToken(ITracingService tracingService)
        {
            try
            {
                tracingService.Trace($"{GetPSTTime()}: Starting Box Client Credentials Grant authentication");

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    // Set up the token endpoint
                    string tokenEndpoint = "https://api.box.com/oauth2/token";

                    // Prepare the request body for Client Credentials Grant
                    var requestBody = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("grant_type", "client_credentials"),
                        new KeyValuePair<string, string>("client_id", BoxClientId),
                        new KeyValuePair<string, string>("client_secret", BoxClientSecret),
                        new KeyValuePair<string, string>("box_subject_type", "enterprise"),
                        new KeyValuePair<string, string>("box_subject_id", "555277")
                    };

                    tracingService.Trace($"{GetPSTTime()}: Sending Client Credentials Grant request to Box");

                    // Create form-encoded content
                    var formContent = new System.Net.Http.FormUrlEncodedContent(requestBody);

                    // Set headers
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "BoxInterface/1.0");
                    httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

                    // Make the token request
                    var response = httpClient.PostAsync(tokenEndpoint, formContent).GetAwaiter().GetResult();
                    var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    tracingService.Trace($"{GetPSTTime()}: Box token response status: {response.StatusCode}");

                    if (response.IsSuccessStatusCode)
                    {
                        // Parse the token response
                        var tokenResponse = JsonConvert.DeserializeObject<BoxTokenResponse>(responseContent);

                        if (tokenResponse != null && !string.IsNullOrEmpty(tokenResponse.AccessToken))
                        {
                            tracingService.Trace($"{GetPSTTime()}: Successfully obtained Box access token, expires in {tokenResponse.ExpiresIn} seconds");
                            return tokenResponse.AccessToken;
                        }
                        else
                        {
                            throw new Exception("Invalid token response from Box API");
                        }
                    }
                    else
                    {
                        tracingService.Trace($"{GetPSTTime()}: Box token request failed: {responseContent}");
                        throw new Exception($"Box authentication failed: {response.StatusCode} - {responseContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                tracingService.Trace($"{GetPSTTime()}: Error getting Box access token: {ex.Message}");
                tracingService.Trace($"{GetPSTTime()}: Falling back to hardcoded access token");

                // Fallback to hardcoded token if Client Credentials Grant fails
                return BoxAccessToken;
            }
        }

        private bool TestBoxAuthentication(ITracingService tracingService)
        {
            try
            {
                tracingService.Trace($"{GetPSTTime()}: Testing Box authentication");

                string accessToken = GetBoxAccessToken(tracingService);

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "BoxInterface/1.0");

                    // Test with a simple API call (get current user info)
                    var response = httpClient.GetAsync("https://api.box.com/2.0/users/me").GetAwaiter().GetResult();
                    var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (response.IsSuccessStatusCode)
                    {
                        tracingService.Trace($"{GetPSTTime()}: Box authentication test successful");
                        tracingService.Trace($"{GetPSTTime()}: User info: {responseContent}");
                        return true;
                    }
                    else
                    {
                        tracingService.Trace($"{GetPSTTime()}: Box authentication test failed: {response.StatusCode} - {responseContent}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                tracingService.Trace($"{GetPSTTime()}: Error testing Box authentication: {ex.Message}");
                return false;
            }
        }



        public static Entity convertExpandoToEntity(IDictionary<string, Object> expandoObject, ITracingService tracingService)
        {
            Entity tsResponse = new Entity();

            try
            {

                foreach (KeyValuePair<string, Object>? item in expandoObject)
                {
                    string elementType = item?.Value?.GetType()?.Name;
                    try
                    {
                        if (elementType != null && elementType.StartsWith("List"))
                        {
                            string listName = item?.Key;

                            EntityCollection entityList = new EntityCollection();

                            string listText = JsonConvert.SerializeObject((List<dynamic>)item?.Value);

                            List<dynamic> list = ((JArray)JsonConvert.DeserializeObject(listText)).ToList<dynamic>();

                            string listType = item.Value.GetType().Name;

                            foreach (var listElement in list)
                            {
                                IDictionary<string, Object> expandoListItem = (JsonConvert.DeserializeObject<ExpandoObject>(JsonConvert.SerializeObject(listElement))) as IDictionary<string, Object>;



                                Entity listElementEntity = convertExpandoToEntity(expandoListItem, tracingService);
                                entityList.Entities.Add(listElementEntity);
                            }

                            if (!tsResponse.Attributes.Contains(item?.Key))
                            {
                                tsResponse.Attributes.Add(item?.Key, entityList);
                            }
                        }
                        else
                        {
                            if (!tsResponse.Attributes.Contains(item?.Key))
                            {
                                tsResponse.Attributes.Add(item?.Key, item.Value);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        tracingService.Trace($"Error processing item '{item?.Key}': {ex.Message}");
                        // Optionally, you can choose to skip this item or handle it differently
                        continue;

                    }

                }
            }
            catch (Exception ex)
            {

                tracingService.Trace($"Error in convertExpandoToEntity: expandoObject.key {ex.Message}");
            }
            return tsResponse;
        }

        private string GetPSTTime()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss");
        }


        private string ConvertEntityToJson(Entity entity, ITracingService tracingService)
        {
            try
            {
                var jsonObject = new Dictionary<string, object>();

                foreach (var attribute in entity.Attributes)
                {
                    string key = attribute.Key;
                    object value = attribute.Value;

                    if (value is EntityCollection entityCollection)
                    {
                        // Convert EntityCollection to List of dictionaries
                        var list = new List<Dictionary<string, object>>();

                        foreach (var subEntity in entityCollection.Entities)
                        {
                            var subDict = new Dictionary<string, object>();
                            foreach (var subAttribute in subEntity.Attributes)
                            {
                                // Handle nested entities if they exist
                                if (subAttribute.Value is Entity nestedEntity)
                                {
                                    var nestedDict = new Dictionary<string, object>();
                                    foreach (var nestedAttr in nestedEntity.Attributes)
                                    {
                                        nestedDict[nestedAttr.Key] = nestedAttr.Value;
                                    }
                                    subDict[subAttribute.Key] = nestedDict;
                                }
                                else if (subAttribute.Value is EntityCollection nestedCollection)
                                {
                                    // Handle nested EntityCollections (recursive)
                                    var nestedList = new List<Dictionary<string, object>>();
                                    foreach (var nestedSubEntity in nestedCollection.Entities)
                                    {
                                        var nestedSubDict = new Dictionary<string, object>();
                                        foreach (var nestedSubAttr in nestedSubEntity.Attributes)
                                        {
                                            nestedSubDict[nestedSubAttr.Key] = nestedSubAttr.Value;
                                        }
                                        nestedList.Add(nestedSubDict);
                                    }
                                    subDict[subAttribute.Key] = nestedList;
                                }
                                else
                                {
                                    subDict[subAttribute.Key] = subAttribute.Value;
                                }
                            }
                            list.Add(subDict);
                        }

                        jsonObject[key] = list;
                    }
                    else if (value is Entity nestedEntity)
                    {
                        // Convert nested Entity to dictionary
                        var nestedDict = new Dictionary<string, object>();
                        foreach (var nestedAttribute in nestedEntity.Attributes)
                        {
                            nestedDict[nestedAttribute.Key] = nestedAttribute.Value;
                        }
                        jsonObject[key] = nestedDict;
                    }
                    else
                    {
                        // Simple value
                        jsonObject[key] = value;
                    }
                }

                return JsonConvert.SerializeObject(jsonObject, Formatting.Indented);
            }
            catch (Exception ex)
            {
                tracingService.Trace($"Error converting Entity to JSON: {ex.Message}");

                // Return error JSON
                var errorObject = new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["message"] = $"Error converting response to JSON: {ex.Message}",
                    ["error"] = ex.ToString()
                };

                return JsonConvert.SerializeObject(errorObject, Formatting.Indented);
            }
        }





        private string ConvertExpandoToJson(Entity entity)
        {
            var cleanObject = ConvertExpandoObjectToClean(entity.Attributes);
            return JsonConvert.SerializeObject(cleanObject, Formatting.Indented);
        }

        private object ConvertExpandoObjectToClean(object obj)
        {
            if (obj is AttributeCollection attributes)
            {
                return ConvertExpandoToClean(attributes);
            }
            else if (obj is Dictionary<string, object> dict)
            {
                return ConvertExpandoToClean(dict);
            }
            else if (obj is System.Collections.IEnumerable enumerable && !(obj is string))
            {
                var cleanArray = new List<object>();
                foreach (var item in enumerable)
                {
                    cleanArray.Add(ConvertExpandoObjectToClean(item));
                }
                return cleanArray;
            }
            else
            {
                return obj;
            }
        }


        private Dictionary<string, object> ConvertExpandoToClean(object expandoObject)
        {
            var cleanDict = new Dictionary<string, object>();

            // Handle both AttributeCollection and Dictionary<string, object>
            IEnumerable<KeyValuePair<string, object>> items;

            if (expandoObject is AttributeCollection attrCollection)
            {
                items = attrCollection;
            }
            else if (expandoObject is IDictionary<string, object> dict)
            {
                items = dict;
            }
            else
            {
                return cleanDict; // Return empty if unknown type
            }

            foreach (var item in items)
            {
                string key = item.Key;
                object value = item.Value;

                // Skip @odata keys (equivalent to !item.Key.Contains("@odata"))
                if (!key.Contains("@odata"))
                {
                    if (value == null)
                        continue;

                    string elementType = value.GetType().Name;
                    object cleanElement = null;

                    // Handle nested objects (equivalent to elementType.ToLower().Contains("object"))
                    if (elementType.ToLower().Contains("object"))
                    {
                        if (value is AttributeCollection attrCollection2)
                        {
                            cleanElement = ConvertExpandoToClean(attrCollection2);
                        }
                        else if (value is IDictionary<string, object> itemExpando)
                        {
                            cleanElement = ConvertExpandoToClean(itemExpando);
                        }
                        else
                        {
                            cleanElement = value; // Keep as-is if it's some other object type
                        }
                    }
                    // Handle lists (equivalent to elementType.StartsWith("List"))
                    else if (elementType.StartsWith("List") || value is System.Collections.IEnumerable && !(value is string))
                    {
                        var cleanList = new List<object>();

                        foreach (var listItem in (System.Collections.IEnumerable)value)
                        {
                            if (listItem != null)
                            {
                                string listItemType = listItem.GetType().Name;
                                if (listItemType.ToLower().Contains("object") || listItem is IDictionary<string, object>)
                                {
                                    if (listItem is AttributeCollection listAttrCollection)
                                    {
                                        cleanList.Add(ConvertExpandoToClean(listAttrCollection));
                                    }
                                    else if (listItem is IDictionary<string, object> listDict)
                                    {
                                        cleanList.Add(ConvertExpandoToClean(listDict));
                                    }
                                    else
                                    {
                                        cleanList.Add(listItem);
                                    }
                                }
                                else
                                {
                                    cleanList.Add(listItem);
                                }
                            }
                        }
                        cleanElement = cleanList;
                    }
                    // Handle simple values (equivalent to !(elementType.StartsWith("List") || elementType.ToLower().Contains("object")))
                    else
                    {
                        cleanElement = value;
                    }

                    // Add the element if it's not null
                    if (cleanElement != null)
                    {
                        cleanDict[key] = cleanElement;
                    }
                }
            }

            return cleanDict;
        }



        private object ConvertJTokenToObject(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    return token.ToObject<Dictionary<string, object>>();
                case JTokenType.Array:
                    return token.ToObject<List<object>>();
                case JTokenType.String:
                    return token.Value<string>();
                case JTokenType.Integer:
                    return token.Value<long>();
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.Date:
                    return token.Value<DateTime>();
                case JTokenType.Null:
                    return null;
                default:
                    return token.ToString();
            }
        }
    }

    internal class EmbeddedBoxApiService
    {
        private readonly string _accessToken;
        private readonly ITracingService _tracingService;
        private readonly System.Net.Http.HttpClient _httpClient;
        private string _boxRootId = EDServicesHelper.EnvVariables["ts_BoxNGOSourceCertificationsFolderId"];//"91518490912";
        private const string BoxApiUrl = "https://api.box.com/2.0";

        public EmbeddedBoxApiService(string accessToken, ITracingService tracingService)
        {
            _accessToken = accessToken;
            _tracingService = tracingService;
            _httpClient = new System.Net.Http.HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "BoxInterface/1.0");
        }


        public Entity ProcessBoxRequestForDynamics(string jsonRequest)
        {
            try
            {
                _tracingService.Trace("Parsing JSON request for Dynamics response");
                var request = JsonConvert.DeserializeObject<BoxFolderRequest>(jsonRequest);

                if (request == null)
                    throw new Exception("Invalid request format");

                // Create the main response Entity
                Entity responseEntity = CreateBoxFolderAnalysisResponseEntity();
                if (!responseEntity.Attributes.Contains("itemType"))
                {
                    responseEntity.Attributes.Add("itemType", request.ItemType);
                }
                if (!responseEntity.Attributes.Contains("requestedOn"))
                {
                    responseEntity.Attributes.Add("requestedOn", DateTime.UtcNow);
                }

                try
                {
                    // Check for new request structure with requestCategory
                    var requestCategory = request.RequestCategory?.ToLower();

                    if (requestCategory == "searchservice")
                    {
                        // Handle search requests
                        if (string.IsNullOrWhiteSpace(request.SearchQuery))
                            throw new Exception("SearchQuery is required for search operations");

                        var requestType = request.RequestType?.ToLower();

                        if (requestType == "boxnative")
                        {
                            HandleBoxNativeSearchForDynamics(responseEntity, request);
                        }
                        else if (requestType == "boxaienhanced")
                        {
                            HandleBoxAIEnhancedSearchForDynamics(responseEntity, request);
                        }
                        else
                        {
                            throw new Exception("RequestType must be either 'BoxNative' or 'BoxAIEnhanced' for search operations");
                        }
                    }
                    else if (requestCategory == "list")
                    {
                        // Handle list requests
                        var requestType = request.RequestType?.ToLower();

                        if (requestType == "allfilesinsubtree")
                        {
                            if (string.IsNullOrWhiteSpace(request.FolderId))
                                throw new Exception("FolderId is required for allFilesInSubTree operations");

                            HandleAllFilesInSubTreeForDynamics(responseEntity, request);
                        }
                        else if (requestType == "allfolderfilesinsubtree")
                        {
                            if (string.IsNullOrWhiteSpace(request.FolderId))
                                throw new Exception("FolderId is required for allFolderFilesInSubTree operations");

                            HandleAllFolderFilesInSubTreeForDynamics(responseEntity, request);
                        }
                        else
                        {
                            throw new Exception("RequestType must be 'allFilesInSubTree' or 'allFolderFilesInSubTree' for list operations");
                        }
                    }
                    else if (requestCategory == "item")
                    {
                        // Handle item operations (create, delete, etc.)
                        var requestType = request.RequestType?.ToLower();
                        var itemType = request.ItemType?.ToLower();

                        if (requestType == "create" && itemType == "folder")
                        {
                            // Handle folder creation
                            if (string.IsNullOrWhiteSpace(request.FolderId))
                                throw new Exception("FolderId (parent folder) is required for folder creation operations");

                            if (string.IsNullOrWhiteSpace(request.FolderName))
                                throw new Exception("FolderName is required for folder creation operations");

                            HandleFolderCreationForDynamics(responseEntity, request);
                        }
                        else if (requestType == "delete" && itemType == "folder")
                        {
                            // Handle folder deletion
                            if (string.IsNullOrWhiteSpace(request.FolderId))
                                throw new Exception("FolderId is required for folder deletion operations");

                            HandleFolderDeleteForDynamics(responseEntity, request.FolderId);
                        }
                        else
                        {
                            throw new Exception("Unsupported item operation. Currently supported: 'Create' requestType with 'Folder' itemType, 'Delete' requestType with 'Folder' itemType.");
                        }
                    }
                    else
                    {

                        var itemType = request.ItemType?.ToLower();

                        if (itemType == "file")
                        {
                            var fileOperation = request.FileOperation?.ToLower();
                            var itemOperation = request.ItemOperation?.ToLower();

                            if (fileOperation == "upload" || itemOperation == "upload")
                            {
                                // Handle file upload
                                // if (string.IsNullOrWhiteSpace(request.FileFullPath))
                                //     throw new Exception("FileFullPath is required for upload operations");

                                if (string.IsNullOrWhiteSpace(request.FolderId))
                                    throw new Exception("FolderId is required for upload operations");

                                HandleFileUploadForDynamics(responseEntity, request);
                            }
                            else if (fileOperation == "delete" || itemOperation == "delete")
                            {
                                // Handle file deletion (supports both fileOperation and itemOperation)
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for file deletion operations");

                                HandleFileDeleteForDynamics(responseEntity, itemId);
                            }
                            else if (itemOperation == "move")
                            {
                                // Handle file move
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for file move operations");

                                if (string.IsNullOrWhiteSpace(request.FolderId))
                                    throw new Exception("FolderId (destination folder) is required for file move operations");

                                HandleFileMoveForDynamics(responseEntity, itemId, request.FolderId);
                            }
                            else if (itemOperation == "copy")
                            {
                                // Handle file copy
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for file copy operations");

                                if (string.IsNullOrWhiteSpace(request.FolderId))
                                    throw new Exception("FolderId (destination folder) is required for file copy operations");

                                HandleFileCopyForDynamics(responseEntity, itemId, request.FolderId, request.NewItemName);
                            }
                            else if (itemOperation == "rename")
                            {
                                // Handle file rename
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for file rename operations");

                                if (string.IsNullOrWhiteSpace(request.NewItemName))
                                    throw new Exception("NewItemName is required for file rename operations");

                                HandleFileRenameForDynamics(responseEntity, itemId, request.NewItemName);
                            }
                            else if (itemOperation == "getversions")
                            {
                                // Handle file versions retrieval
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for file versions retrieval operations");

                                HandleFileGetVersionsForDynamics(responseEntity, itemId);
                            }
                            else if (itemOperation == "getversionfilecontent")
                            {
                                // Handle file version content retrieval
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId (file ID) is required for file version content retrieval operations");

                                if (string.IsNullOrWhiteSpace(request.VersionId))
                                    throw new Exception("VersionId is required for file version content retrieval operations");

                                HandleFileGetVersionFileContentForDynamics(responseEntity, itemId, request.VersionId);
                            }
                            else
                            {
                                // Handle file retrieval
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for file retrieval operations");

                                if (!responseEntity.Attributes.Contains("folderId"))
                                {
                                    responseEntity.Attributes.Add("folderId", itemId);
                                }
                                AnalyzeFileItemForDynamics(responseEntity, itemId);
                            }
                        }
                        else if (itemType == "folder")
                        {
                            var itemOperation = request.ItemOperation?.ToLower();

                            if (itemOperation == "delete")
                            {
                                // Handle folder deletion
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for folder deletion operations");

                                HandleFolderDeleteForDynamics(responseEntity, itemId);
                            }
                            else if (itemOperation == "move")
                            {
                                // Handle folder move
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for folder move operations");

                                if (string.IsNullOrWhiteSpace(request.FolderId))
                                    throw new Exception("FolderId (destination folder) is required for folder move operations");

                                HandleFolderMoveForDynamics(responseEntity, itemId, request.FolderId);
                            }
                            else if (itemOperation == "copy")
                            {
                                // Handle folder copy
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for folder copy operations");

                                if (string.IsNullOrWhiteSpace(request.FolderId))
                                    throw new Exception("FolderId (destination folder) is required for folder copy operations");

                                HandleFolderCopyForDynamics(responseEntity, itemId, request.FolderId, request.NewItemName);
                            }
                            else if (itemOperation == "rename")
                            {
                                // Handle folder rename
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for folder rename operations");

                                if (string.IsNullOrWhiteSpace(request.NewItemName))
                                    throw new Exception("NewItemName is required for folder rename operations");

                                HandleFolderRenameForDynamics(responseEntity, itemId, request.NewItemName);
                            }
                            else
                            {
                                // Handle folder analysis (default behavior)

                                // Check if a custom rootId is provided in the request
                                if (!string.IsNullOrWhiteSpace(request.RootId))
                                {
                                    _boxRootId = request.RootId;
                                    _tracingService.Trace($"Custom rootId provided, setting _boxRootId to: {_boxRootId}");
                                }

                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    itemId = _boxRootId;

                                if (!responseEntity.Attributes.Contains("folderId"))
                                {
                                    responseEntity.Attributes.Add("folderId", itemId);
                                }

                                // Extract sorting parameters from request
                                string sortByField = request.SortByField;
                                string sortDirection = request.SortDirection;

                                // Extract pagination parameters from request
                                int? pageOffset = request.PageOffset;
                                int? pageLimit = request.PageLimit;

                                _tracingService.Trace($"Processing folder request with sorting - Field: {sortByField}, Direction: {sortDirection}");
                                _tracingService.Trace($"Processing folder request with pagination - Offset: {pageOffset}, Limit: {pageLimit}");

                                AnalyzeFolderItemForDynamics(responseEntity, itemId, sortByField, sortDirection, pageOffset, pageLimit);
                            }
                        }
                        else
                        {
                            throw new Exception("ItemType must be either 'Folder' or 'File'");
                        }
                    }

                    if (!responseEntity.Attributes.Contains("success"))
                    {
                        responseEntity.Attributes.Add("success", true);
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        responseEntity.Attributes.Add("message", "Operation completed successfully");
                    }
                }
                catch (Exception ex)
                {
                    if (!responseEntity.Attributes.Contains("success"))
                    {
                        responseEntity.Attributes.Add("success", false);
                    }
                    else
                    {
                        responseEntity.Attributes["success"] = false;
                    }

                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        responseEntity.Attributes.Add("message", $"Error processing {request.ItemType}: {ex.Message}");
                    }
                    else
                    {
                        responseEntity.Attributes["message"] = $"Error processing {request.ItemType}: {ex.Message}";
                    }

                    if (!responseEntity.Attributes.Contains("error"))
                    {
                        responseEntity.Attributes.Add("error", ex.ToString());
                    }
                    else
                    {
                        responseEntity.Attributes["error"] = ex.ToString();
                    }

                    _tracingService.Trace($"Error in ProcessBoxRequestForDynamics: {ex.Message}");
                }

                return responseEntity;
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Critical error in ProcessBoxRequestForDynamics: {ex.Message}");

                // Return error entity
                Entity errorEntity = CreateBoxFolderAnalysisResponseEntity();
                if (!errorEntity.Attributes.Contains("success"))
                {
                    errorEntity.Attributes.Add("success", false);
                }
                if (!errorEntity.Attributes.Contains("message"))
                {
                    errorEntity.Attributes.Add("message", $"Critical error: {ex.Message}");
                }
                if (!errorEntity.Attributes.Contains("error"))
                {
                    errorEntity.Attributes.Add("error", ex.ToString());
                }

                return errorEntity;
            }
        }

        private Entity CreateBoxFolderAnalysisResponseEntity()
        {
            Entity responseEntity = new Entity();

            // Initialize empty collections for all possible response data
            responseEntity.Attributes.Add("folderContents", new EntityCollection());
            responseEntity.Attributes.Add("collaborators", new EntityCollection());
            responseEntity.Attributes.Add("folderTree", new EntityCollection());
            responseEntity.Attributes.Add("tags", new EntityCollection());
            responseEntity.Attributes.Add("foldersInSubTree", new EntityCollection());
            responseEntity.Attributes.Add("versions", new EntityCollection());
            // Add fileDetails attribute for file responses (will be null for folder responses)
            responseEntity.Attributes.Add("fileDetails", null);

            // Add pagination metadata attributes
            responseEntity.Attributes.Add("totalCount", 0);
            responseEntity.Attributes.Add("currentOffset", 0);
            responseEntity.Attributes.Add("currentLimit", 0);
            responseEntity.Attributes.Add("nextOffset", (int?)null);
            responseEntity.Attributes.Add("hasMoreItems", false);

            return responseEntity;
        }

        private void HandleFileUploadForDynamics(Entity responseEntity, BoxFolderRequest request)
        {
            _tracingService.Trace($"Starting file upload for Dynamics: {request.FileName}");

            try
            {
                // Validate required parameters for base64 upload
                if (string.IsNullOrWhiteSpace(request.FileContent))
                    throw new ArgumentException("FileContent (base64) is required for upload operations");

                if (string.IsNullOrWhiteSpace(request.FileName))
                    throw new ArgumentException("FileName is required for upload operations");

                if (!request.FileSize.HasValue || request.FileSize <= 0)
                    throw new ArgumentException("FileSize is required and must be greater than 0 for upload operations");

                if (string.IsNullOrWhiteSpace(request.FolderId))
                    throw new ArgumentException("FolderId is required for upload operations");

                // Decode base64 content
                byte[] fileBytes;
                try
                {
                    fileBytes = Convert.FromBase64String(request.FileContent);
                }
                catch (FormatException ex)
                {
                    throw new ArgumentException($"Invalid base64 content in FileContent: {ex.Message}", ex);
                }

                // Validate file size matches the decoded content
                if (fileBytes.Length != request.FileSize.Value)
                {
                    _tracingService.Trace($"File size mismatch: provided size {request.FileSize.Value} bytes, actual content size {fileBytes.Length} bytes");
                    throw new ArgumentException($"File size mismatch: provided size {request.FileSize.Value} bytes, actual content size {fileBytes.Length} bytes");
                }

                _tracingService.Trace($"File validated: {request.FileName}, Size: {fileBytes.Length} bytes, Type: {request.ContentType ?? "unknown"}");

                string uploadedFileId;
                bool isVersionUpload = false;

                // Check if versionUploadOnExistingFile is enabled
                if (request.VersionUploadOnExistingFile.HasValue && request.VersionUploadOnExistingFile.Value)
                {
                    _tracingService.Trace($"Version upload mode enabled - checking for existing file: {request.FileName} in folder {request.FolderId}");

                    // Check if file exists in the target folder
                    var existingFileId = CheckFileExistsInFolder(request.FolderId, request.FileName);

                    if (!string.IsNullOrWhiteSpace(existingFileId))
                    {
                        // File exists - upload as new version
                        _tracingService.Trace($"File exists with ID {existingFileId} - uploading new version");
                        uploadedFileId = UploadFileVersion(existingFileId, request.FileName, fileBytes, request.ContentType);
                        isVersionUpload = true;
                    }
                    else
                    {
                        // File doesn't exist - do normal upload
                        _tracingService.Trace($"File does not exist - performing normal upload");
                        uploadedFileId = UploadFileToBox(request.FolderId, request.FileName, fileBytes, request.ContentType);
                    }
                }
                else
                {
                    // Normal upload (no version check)
                    uploadedFileId = UploadFileToBox(request.FolderId, request.FileName, fileBytes, request.ContentType);
                }

                _tracingService.Trace($"File uploaded successfully with ID: {uploadedFileId}");

                // Collect tags to add to the file
                List<string> fileTags = new List<string>();

                // Add uploader email tag if provided
                if (!string.IsNullOrWhiteSpace(request.UploaderEmail))
                {
                    var tagValue = $"uploaderEmail:{request.UploaderEmail}";
                    fileTags.Add(tagValue);
                    _tracingService.Trace($"Adding uploader email tag: {tagValue}");
                }

                // Add description tag if provided
                if (!string.IsNullOrWhiteSpace(request.DescriptionTag))
                {
                    var tagValue = $"descriptionTag:{request.DescriptionTag}";
                    fileTags.Add(tagValue);
                    _tracingService.Trace($"Adding description tag: {tagValue}");
                }

                // Add all tags at once if any were collected
                if (fileTags.Count > 0)
                {
                    _tracingService.Trace($"Adding {fileTags.Count} tag(s) to file: {uploadedFileId}");
                    AddTagsToFile(uploadedFileId, fileTags);
                    _tracingService.Trace($"Tags added successfully to file: {uploadedFileId}");
                }

                // Update file description if provided
                if (!string.IsNullOrWhiteSpace(request.Description))
                {
                    _tracingService.Trace($"Updating description for file: {uploadedFileId}");
                    UpdateFileDescription(uploadedFileId, request.Description);
                    _tracingService.Trace($"Description updated successfully for file: {uploadedFileId}");
                }

                // Set response details
                if (!responseEntity.Attributes.Contains("itemId"))
                {
                    responseEntity.Attributes.Add("itemId", uploadedFileId);
                }
                if (!responseEntity.Attributes.Contains("folderId"))
                {
                    responseEntity.Attributes.Add("folderId", request.FolderId);
                }
                if (!responseEntity.Attributes.Contains("fileOperation"))
                {
                    responseEntity.Attributes.Add("fileOperation", isVersionUpload ? "uploadVersion" : "upload");
                }
                if (!responseEntity.Attributes.Contains("isVersionUpload"))
                {
                    responseEntity.Attributes.Add("isVersionUpload", isVersionUpload);
                }
                if (!responseEntity.Attributes.Contains("message"))
                {
                    string operationType = isVersionUpload ? "uploaded as new version" : "uploaded successfully";
                    responseEntity.Attributes.Add("message", $"File '{request.FileName}' {operationType} to Box (Size: {request.FileSize:N0} bytes, Type: {request.ContentType ?? "unknown"})");
                }

                // Create uploaded file entity and add to collection
                Entity uploadedFileEntity = CreateBoxFolderItemEntity();
                uploadedFileEntity.Attributes["id"] = uploadedFileId;
                uploadedFileEntity.Attributes["name"] = request.FileName;
                uploadedFileEntity.Attributes["type"] = "file";
                uploadedFileEntity.Attributes["size"] = (int)request.FileSize.Value;
                uploadedFileEntity.Attributes["createdOn"] = request.DateCreated?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                uploadedFileEntity.Attributes["modifiedAOn"] = request.LastModified?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                uploadedFileEntity.Attributes["contentType"] = request.ContentType ?? "application/octet-stream";

                EntityCollection folderContents = (EntityCollection)responseEntity.Attributes["folderContents"];
                folderContents.Entities.Add(uploadedFileEntity);

                // Get folder tree path for the upload folder
                _tracingService.Trace($"Getting folder tree for upload folder: {request.FolderId}");
                GetFolderTreePathForDynamics(responseEntity, request.FolderId);

                _tracingService.Trace($"File upload completed successfully for Dynamics");
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in file upload for Dynamics: {ex.Message}");
                throw new Exception($"Failed to upload file: {ex.Message}", ex);
            }
        }

        private void HandleFolderCreationForDynamics(Entity responseEntity, BoxFolderRequest request)
        {
            _tracingService.Trace($"Starting folder creation for Dynamics: {request.FolderName} in parent folder {request.FolderId}");

            try
            {
                // Validate required parameters
                if (string.IsNullOrWhiteSpace(request.FolderId))
                    throw new ArgumentException("FolderId (parent folder) is required for folder creation operations");

                if (string.IsNullOrWhiteSpace(request.FolderName))
                    throw new ArgumentException("FolderName is required for folder creation operations");

                _tracingService.Trace($"Creating folder '{request.FolderName}' in parent folder '{request.FolderId}'");

                // Create the folder in Box
                var createdFolderId = CreateFolderInBox(request.FolderId, request.FolderName);

                _tracingService.Trace($"Folder created successfully with ID: {createdFolderId}");

                // Set response details
                if (!responseEntity.Attributes.Contains("itemId"))
                {
                    responseEntity.Attributes.Add("itemId", createdFolderId);
                }
                if (!responseEntity.Attributes.Contains("folderId"))
                {
                    responseEntity.Attributes.Add("folderId", request.FolderId);
                }
                if (!responseEntity.Attributes.Contains("folderName"))
                {
                    responseEntity.Attributes.Add("folderName", request.FolderName);
                }
                if (!responseEntity.Attributes.Contains("message"))
                {
                    responseEntity.Attributes.Add("message", $"Folder '{request.FolderName}' created successfully in Box");
                }

                // Create folder entity and add to collection
                Entity createdFolderEntity = CreateBoxFolderItemEntity();
                createdFolderEntity.Attributes["id"] = createdFolderId;
                createdFolderEntity.Attributes["name"] = request.FolderName;
                createdFolderEntity.Attributes["type"] = "folder";
                createdFolderEntity.Attributes["size"] = 0;
                createdFolderEntity.Attributes["createdOn"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                createdFolderEntity.Attributes["modifiedAOn"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                // Initialize folder contents if not exists
                if (!responseEntity.Attributes.Contains("folderContents"))
                {
                    responseEntity.Attributes.Add("folderContents", new EntityCollection());
                }

                EntityCollection folderContents = (EntityCollection)responseEntity.Attributes["folderContents"];
                folderContents.Entities.Add(createdFolderEntity);

                // Get folder tree path for the parent folder
                _tracingService.Trace($"Getting folder tree for parent folder: {request.FolderId}");
                GetFolderTreePathForDynamics(responseEntity, request.FolderId);

                _tracingService.Trace($"Folder creation completed successfully for Dynamics");
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in folder creation for Dynamics: {ex.Message}");
                throw new Exception($"Failed to create folder: {ex.Message}", ex);
            }
        }

        private void AnalyzeFileItemForDynamics(Entity responseEntity, string fileId)
        {
            _tracingService.Trace($"Analyzing file for Dynamics: {fileId}");

            // Get basic file info including parent folder information
            var fileUrl = $"{BoxApiUrl}/files/{fileId}?fields=id,name,type,size,created_at,modified_at,parent";
            var fileResponse = _httpClient.GetAsync(fileUrl).GetAwaiter().GetResult();
            var fileContent = fileResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (fileResponse.IsSuccessStatusCode)
            {
                var fileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(fileContent);

                // Get file content as base64
                var contentUrl = $"{BoxApiUrl}/files/{fileId}/content";
                var contentResponse = _httpClient.GetAsync(contentUrl).GetAwaiter().GetResult();

                string base64Content = "";
                string contentType = GetMimeTypeFromFileName(fileData.Name);

                if (contentResponse.IsSuccessStatusCode)
                {
                    var fileBytes = contentResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    base64Content = Convert.ToBase64String(fileBytes);
                    contentType = contentResponse.Content.Headers.ContentType?.MediaType ?? contentType;
                }

                // Create file details entity (NOT as part of folderContents)
                Entity fileEntity = CreateBoxFolderItemEntity();
                fileEntity.Attributes["id"] = fileData.Id;
                fileEntity.Attributes["name"] = fileData.Name;
                fileEntity.Attributes["type"] = fileData.Type;
                fileEntity.Attributes["size"] = (int)(fileData.Size ?? 0);
                fileEntity.Attributes["createdOn"] = fileData.CreatedAt;
                fileEntity.Attributes["modifiedOn"] = fileData.ModifiedAt;
                fileEntity.Attributes["contentType"] = contentType;
                fileEntity.Attributes["fileContent"] = base64Content; // Add the base64 content

                // Add as fileDetails, not folderContents
                responseEntity.Attributes["fileDetails"] = fileEntity;

                // Get tags for the file
                _tracingService.Trace($"Getting tags for file: {fileId}");
                GetItemTagsForDynamics(responseEntity, fileId, "file");

                // Get folder tree path using the file's parent folder
                if (fileData.Parent != null && !string.IsNullOrEmpty(fileData.Parent.Id))
                {
                    _tracingService.Trace($"Getting folder tree for file's parent folder: {fileData.Parent.Id}");
                    GetFolderTreePathForDynamics(responseEntity, fileData.Parent.Id);
                }
            }
            else
            {
                throw new Exception($"Failed to get file details: {fileResponse.StatusCode} - {fileContent}");
            }
        }

        private void AnalyzeFolderItemForDynamics(Entity responseEntity, string folderId, string sortByField = null, string sortDirection = null, int? pageOffset = null, int? pageLimit = null)
        {
            _tracingService.Trace($"Analyzing folder for Dynamics: {folderId}");

            // Determine page limit (default to 100, max 1000 for Box API)
            int limit = Math.Min(pageLimit ?? 100, 1000);

            // Build the Box API URL with optional sorting and pagination
            var folderUrlBuilder = new StringBuilder($"{BoxApiUrl}/folders/{folderId}/items?limit={limit}&fields=id,name,type,size,created_at,modified_at");

            // Add pagination parameters if provided
            if (pageOffset.HasValue && pageOffset.Value > 0)
            {
                folderUrlBuilder.Append($"&offset={pageOffset.Value}");
                _tracingService.Trace($"Added pagination offset: {pageOffset.Value}");
            }

            // Add sorting parameters if provided
            if (!string.IsNullOrWhiteSpace(sortByField))
            {
                string boxSortField = ConvertSortFieldToBoxFormat(sortByField);
                string boxSortDirection = ConvertSortDirectionToBoxFormat(sortDirection);

                folderUrlBuilder.Append($"&sort={boxSortField}&direction={boxSortDirection}");
                _tracingService.Trace($"Added sorting: sort={boxSortField}&direction={boxSortDirection}");
            }

            var folderUrl = folderUrlBuilder.ToString();
            _tracingService.Trace($"Folder URL: {folderUrl}");

            var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
            var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            _tracingService.Trace($"folderContent: {folderContent}");

            if (folderResponse.IsSuccessStatusCode)
            {
                var folderData = JsonConvert.DeserializeObject<FolderItemsResponse>(folderContent);
                EntityCollection folderContents = (EntityCollection)responseEntity.Attributes["folderContents"];

                // Get all items from the response
                var allItems = folderData.Entries ?? new List<BoxItem>();

                // Separate folders and files for custom sorting (folders first, then files)
                var folders = allItems.Where(item => item.Type == "folder").ToList();
                var files = allItems.Where(item => item.Type == "file").ToList();

                // Apply custom sorting - always needed for folders-first behavior and specific date field sorting
                if (!string.IsNullOrWhiteSpace(sortByField))
                {
                    // Box API supports: 'name', 'size', 'date' - but 'date' doesn't differentiate created vs modified
                    // We always apply custom sorting to: 1) ensure folders-first, 2) handle createdOn vs modifiedOn distinction
                    folders = ApplyCustomSorting(folders, sortByField, sortDirection).ToList();
                    files = ApplyCustomSorting(files, sortByField, sortDirection).ToList();
                }

                // Combine folders first, then files (maintaining Box's typical behavior)
                var sortedItems = folders.Concat(files).ToList();

                int itemIndex = 1; // Start index at 1

                foreach (var entry in sortedItems)
                {
                    Entity folderItemEntity = CreateBoxFolderItemEntity();
                    folderItemEntity.Attributes["id"] = entry.Id;
                    folderItemEntity.Attributes["name"] = entry.Name;
                    folderItemEntity.Attributes["type"] = entry.Type;
                    folderItemEntity.Attributes["size"] = (int)(entry.Size ?? 0);
                    folderItemEntity.Attributes["createdOn"] = entry.CreatedAt;
                    folderItemEntity.Attributes["modifiedOn"] = entry.ModifiedAt;
                    folderItemEntity.Attributes["contentType"] = entry.Type == "file" ? GetMimeTypeFromFileName(entry.Name) : null;
                    folderItemEntity.Attributes["itemIndex"] = itemIndex; // Add the new itemIndex field

                    // Get file and folder counts for folder items (similar to GetFolderTreePathForDynamics logic)
                    // if (entry.Type == "folder")
                    // {
                    //     var counts = GetFolderItemCounts(entry.Id);
                    //     folderItemEntity.Attributes["fileCount"] = counts.FileCount;
                    //     folderItemEntity.Attributes["folderCount"] = counts.FolderCount;
                    // }
                    // else
                    // {
                    //     // For files, counts remain 0 (already set by CreateBoxFolderItemEntity)
                    //     folderItemEntity.Attributes["fileCount"] = 0;
                    //     folderItemEntity.Attributes["folderCount"] = 0;
                    // }

                    folderContents.Entities.Add(folderItemEntity);
                    itemIndex++;
                }

                // Populate pagination metadata
                responseEntity.Attributes["totalCount"] = folderData.TotalCount;
                responseEntity.Attributes["currentOffset"] = folderData.Offset;
                responseEntity.Attributes["currentLimit"] = folderData.Limit;

                // Calculate next offset and determine if there are more items
                int currentOffset = folderData.Offset;
                int currentLimit = folderData.Limit;
                int totalCount = folderData.TotalCount;

                bool hasMoreItems = (currentOffset + currentLimit) < totalCount;
                int? nextOffset = hasMoreItems ? (currentOffset + currentLimit) : (int?)null;

                responseEntity.Attributes["nextOffset"] = nextOffset;
                responseEntity.Attributes["hasMoreItems"] = hasMoreItems;

                _tracingService.Trace($"Pagination info - Total: {totalCount}, Offset: {currentOffset}, Limit: {currentLimit}, HasMore: {hasMoreItems}, NextOffset: {nextOffset}");
                _tracingService.Trace($"Added {sortedItems.Count} items to folderContents with sorting: {sortByField} {sortDirection}");
            }
            else
            {
                throw new Exception($"Failed to get folder contents: {folderResponse.StatusCode} - {folderContent}");
            }

            // Get collaborators (handle root folder special case)
            GetFolderCollaboratorsForDynamics(responseEntity, folderId);

            // Get tags for the folder
            GetItemTagsForDynamics(responseEntity, folderId, "folder");

            // Get folder tree path
            GetFolderTreePathForDynamics(responseEntity, folderId);
        }

        private void GetFolderCollaboratorsForDynamics(Entity responseEntity, string folderId)
        {
            _tracingService.Trace($"Getting collaborators for Dynamics folder: {folderId}");

            EntityCollection collaboratorsCollection = (EntityCollection)responseEntity.Attributes["collaborators"];

            // Handle root folder special case
            if (folderId == "0")
            {
                Entity rootCollaboratorEntity = CreateBoxCollaboratorEntity();
                rootCollaboratorEntity.Attributes["id"] = "root";
                rootCollaboratorEntity.Attributes["name"] = "Root Folder";
                rootCollaboratorEntity.Attributes["type"] = "folder";
                rootCollaboratorEntity.Attributes["role"] = "owner";
                rootCollaboratorEntity.Attributes["status"] = "root_folder_no_collaborations";

                collaboratorsCollection.Entities.Add(rootCollaboratorEntity);
                return;
            }

            try
            {
                var collaboratorsUrl = $"{BoxApiUrl}/folders/{folderId}/collaborations?fields=id,type,role,status,accessible_by";
                var collaboratorsResponse = _httpClient.GetAsync(collaboratorsUrl).GetAwaiter().GetResult();
                var collaboratorsContent = collaboratorsResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                _tracingService.Trace($"Collaborators response: {collaboratorsResponse.StatusCode}");

                if (collaboratorsResponse.IsSuccessStatusCode)
                {
                    var collaboratorsData = JsonConvert.DeserializeObject<CollaboratorsResponse>(collaboratorsContent);

                    foreach (var collab in collaboratorsData.Entries ?? new List<BoxCollaboration>())
                    {
                        if (collab.AccessibleBy != null)
                        {
                            Entity collaboratorEntity = CreateBoxCollaboratorEntity();
                            collaboratorEntity.Attributes["id"] = collab.AccessibleBy.Id;
                            collaboratorEntity.Attributes["name"] = collab.AccessibleBy.Name;
                            collaboratorEntity.Attributes["login"] = collab.AccessibleBy.Login;
                            collaboratorEntity.Attributes["type"] = collab.AccessibleBy.Type;
                            collaboratorEntity.Attributes["role"] = collab.Role;
                            collaboratorEntity.Attributes["status"] = collab.Status;

                            collaboratorsCollection.Entities.Add(collaboratorEntity);
                        }
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to get collaborators: {collaboratorsResponse.StatusCode} - {collaboratorsContent}");

                    // Add a placeholder indicating no collaborators could be retrieved
                    Entity errorCollaboratorEntity = CreateBoxCollaboratorEntity();
                    errorCollaboratorEntity.Attributes["id"] = "unknown";
                    errorCollaboratorEntity.Attributes["name"] = "Unable to retrieve collaborators";
                    errorCollaboratorEntity.Attributes["role"] = "unknown";
                    errorCollaboratorEntity.Attributes["status"] = "error";

                    collaboratorsCollection.Entities.Add(errorCollaboratorEntity);
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error getting collaborators: {ex.Message}");

                Entity errorCollaboratorEntity = CreateBoxCollaboratorEntity();
                errorCollaboratorEntity.Attributes["id"] = "error";
                errorCollaboratorEntity.Attributes["name"] = $"Error retrieving collaborators: {ex.Message}";
                errorCollaboratorEntity.Attributes["role"] = "error";
                errorCollaboratorEntity.Attributes["status"] = "error";

                collaboratorsCollection.Entities.Add(errorCollaboratorEntity);
            }
        }

        private void GetItemTagsForDynamics(Entity responseEntity, string itemId, string itemType)
        {
            _tracingService.Trace($"Getting tags for Dynamics {itemType}: {itemId}");

            EntityCollection tagsCollection = (EntityCollection)responseEntity.Attributes["tags"];

            try
            {
                // Get tags by including them in the fields parameter
                string endpoint = itemType.ToLower() == "file"
                    ? $"{BoxApiUrl}/files/{itemId}?fields=tags"
                    : $"{BoxApiUrl}/folders/{itemId}?fields=tags";

                _tracingService.Trace($"Getting tags from: {endpoint}");

                var response = _httpClient.GetAsync(endpoint).GetAwaiter().GetResult();
                var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                _tracingService.Trace($"Tags response: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    dynamic responseData = JsonConvert.DeserializeObject(responseContent);

                    // Check if tags array exists and has items
                    if (responseData.tags != null)
                    {
                        foreach (var tag in responseData.tags)
                        {
                            Entity tagEntity = new Entity();
                            tagEntity.Attributes.Add("tag", tag.ToString());
                            tagsCollection.Entities.Add(tagEntity);
                        }

                        _tracingService.Trace($"Retrieved {tagsCollection.Entities.Count} tag(s) for {itemType} {itemId}");
                    }
                    else
                    {
                        _tracingService.Trace($"No tags found for {itemType} {itemId}");
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to get tags: {response.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error getting tags: {ex.Message}");
            }
        }

        private void GetFolderTreePathForDynamics(Entity responseEntity, string folderId)
        {
            _tracingService.Trace($"Getting folder tree path for Dynamics folder: {folderId}");

            EntityCollection folderTreeCollection = (EntityCollection)responseEntity.Attributes["folderTree"];

            // Check if folder tree is already populated to avoid duplication
            if (folderTreeCollection.Entities.Count > 0)
            {
                _tracingService.Trace("Folder tree already populated, skipping to avoid duplication");
                return;
            }

            try
            {
                // Get folder details to build the path
                var folderPath = new List<BoxFolderInfo>();
                string currentFolderId = folderId;
                int position = 0;

                // Traverse up the folder hierarchy (stop at _boxRootId)
                while (currentFolderId != null && currentFolderId != "0")
                {
                    var folderUrl = $"{BoxApiUrl}/folders/{currentFolderId}?fields=id,name,parent,item_collection";
                    var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
                    var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (folderResponse.IsSuccessStatusCode)
                    {
                        var folderDetails = JsonConvert.DeserializeObject<BoxFolderDetails>(folderContent);

                        // Count files and folders
                        int fileCount = 0, folderCount = 0;
                        if (folderDetails.ItemCollection != null)
                        {
                            foreach (var entry in folderDetails.ItemCollection.Entries ?? new List<BoxItem>())
                            {
                                if (entry.Type == "file") fileCount++;
                                else if (entry.Type == "folder") folderCount++;
                            }
                        }

                        folderPath.Insert(0, new BoxFolderInfo
                        {
                            Id = folderDetails.Id,
                            Name = folderDetails.Name,
                            FileCount = fileCount,
                            FolderCount = folderCount
                        });

                        // Stop traversal if we've reached the _boxRootId (don't go past it)
                        if (currentFolderId == _boxRootId)
                        {
                            _tracingService.Trace($"Reached _boxRootId ({_boxRootId}), stopping folder tree traversal");
                            break;
                        }

                        // Move to parent
                        currentFolderId = folderDetails.Parent?.Id;
                    }
                    else
                    {
                        _tracingService.Trace($"Failed to get folder details for {currentFolderId}: {folderResponse.StatusCode}");
                        break;
                    }

                    // Safety check to prevent infinite loops
                    if (position > 50) break;
                    position++;
                }

                // Add root folder (position 0)
                //Entity rootFolderTreeEntity = CreateBoxFolderTreeItemEntity();
                //rootFolderTreeEntity.Attributes["folderId"] = "0";
                //rootFolderTreeEntity.Attributes["folderName"] = "Root";
                //rootFolderTreeEntity.Attributes["position"] = 0;
                //rootFolderTreeEntity.Attributes["fileCount"] = 0; // Root file count is complex to calculate
                //rootFolderTreeEntity.Attributes["folderCount"] = 0; // Root folder count is complex to calculate
                //rootFolderTreeEntity.Attributes["isRoot"] = true;

                //folderTreeCollection.Entities.Add(rootFolderTreeEntity);

                // Add the path folders with correct positions
                for (int i = 0; i < folderPath.Count; i++)
                {
                    var folder = folderPath[i];
                    Entity folderTreeEntity = CreateBoxFolderTreeItemEntity();
                    folderTreeEntity.Attributes["folderId"] = folder.Id;
                    folderTreeEntity.Attributes["folderName"] = folder.Name;
                    folderTreeEntity.Attributes["position"] = i + 1; // Root is 0, so start from 1
                    folderTreeEntity.Attributes["fileCount"] = folder.FileCount;
                    folderTreeEntity.Attributes["folderCount"] = folder.FolderCount;
                    folderTreeEntity.Attributes["isRoot"] = false;

                    folderTreeCollection.Entities.Add(folderTreeEntity);
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error getting folder tree: {ex.Message}");

                // Add error placeholder
                Entity errorFolderTreeEntity = CreateBoxFolderTreeItemEntity();
                errorFolderTreeEntity.Attributes["folderId"] = folderId;
                errorFolderTreeEntity.Attributes["folderName"] = $"Error: {ex.Message}";
                errorFolderTreeEntity.Attributes["position"] = -1;
                errorFolderTreeEntity.Attributes["fileCount"] = 0;
                errorFolderTreeEntity.Attributes["folderCount"] = 0;
                errorFolderTreeEntity.Attributes["isRoot"] = false;

                folderTreeCollection.Entities.Add(errorFolderTreeEntity);
            }
        }

        /// <summary>
        /// Handles the allFilesInSubTree request - recursively traverses folder hierarchy up to 5 levels deep
        /// </summary>
        private void HandleAllFilesInSubTreeForDynamics(Entity responseEntity, BoxFolderRequest request)
        {
            _tracingService.Trace($"Starting allFilesInSubTree for folder: {request.FolderId}");

            try
            {
                const int maxDepth = 5;
                var allFiles = new List<BoxFileWithFolderInfo>();

                // Start recursive traversal from the root folder
                TraverseFolder(request.FolderId, request.FolderId, 1, maxDepth, allFiles);

                _tracingService.Trace($"Collected {allFiles.Count} files from folder subtree");

                // Add all files to the response entity
                EntityCollection folderContents = (EntityCollection)responseEntity.Attributes["folderContents"];

                foreach (var fileInfo in allFiles)
                {
                    Entity fileEntity = CreateBoxFolderItemEntity();
                    fileEntity.Attributes["id"] = fileInfo.Id;
                    fileEntity.Attributes["name"] = fileInfo.Name;
                    fileEntity.Attributes["size"] = fileInfo.Size;
                    fileEntity.Attributes["createdOn"] = fileInfo.CreatedOn;
                    fileEntity.Attributes["modifiedOn"] = fileInfo.ModifiedOn;
                    fileEntity.Attributes["contentType"] = fileInfo.ContentType;
                    fileEntity.Attributes["folderId"] = fileInfo.FolderId;
                    fileEntity.Attributes["folderName"] = fileInfo.FolderName;
                    fileEntity.Attributes["subFolderTree"] = fileInfo.SubFolderTree;
                    fileEntity.Attributes["type"] = "file";

                    // Add tags as an EntityCollection
                    EntityCollection tagsCollection = new EntityCollection();
                    if (fileInfo.Tags != null && fileInfo.Tags.Count > 0)
                    {
                        foreach (var tag in fileInfo.Tags)
                        {
                            Entity tagEntity = new Entity();
                            tagEntity.Attributes.Add("tag", tag);
                            tagsCollection.Entities.Add(tagEntity);
                        }
                    }
                    fileEntity.Attributes["tags"] = tagsCollection;

                    folderContents.Entities.Add(fileEntity);
                }

                // Set response message
                if (!responseEntity.Attributes.Contains("message"))
                {
                    responseEntity.Attributes.Add("message", $"Successfully retrieved {allFiles.Count} files from folder subtree (up to {maxDepth} levels deep)");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in HandleAllFilesInSubTreeForDynamics: {ex.Message}");
                throw new Exception($"Failed to retrieve files from subtree: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles the allFolderFilesInSubTree request - recursively traverses folder hierarchy up to 5 levels deep
        /// and organizes files by their parent folders
        /// </summary>
        private void HandleAllFolderFilesInSubTreeForDynamics(Entity responseEntity, BoxFolderRequest request)
        {
            _tracingService.Trace($"Starting allFolderFilesInSubTree for folder: {request.FolderId}");

            try
            {
                const int maxDepth = 5;
                var allFolders = new Dictionary<string, BoxFolderWithFiles>();

                // Start recursive traversal from the root folder
                TraverseFolderForFolderFiles(request.FolderId, request.FolderId, 1, maxDepth, allFolders);

                _tracingService.Trace($"Collected files from {allFolders.Count} folders in subtree");

                // Add all folders with their files to the response entity
                EntityCollection foldersInSubTree = (EntityCollection)responseEntity.Attributes["foldersInSubTree"];

                int totalFileCount = 0;
                foreach (var folderEntry in allFolders.Values)
                {
                    Entity folderEntity = new Entity();
                    folderEntity.Attributes.Add("folderId", folderEntry.FolderId);
                    folderEntity.Attributes.Add("folderName", folderEntry.FolderName);

                    // Create folderContents collection for this folder
                    EntityCollection folderContents = new EntityCollection();

                    foreach (var fileInfo in folderEntry.Files)
                    {
                        Entity fileEntity = CreateBoxFolderItemEntity();
                        fileEntity.Attributes["id"] = fileInfo.Id;
                        fileEntity.Attributes["name"] = fileInfo.Name;
                        fileEntity.Attributes["size"] = fileInfo.Size;
                        fileEntity.Attributes["createdOn"] = fileInfo.CreatedOn;
                        fileEntity.Attributes["modifiedOn"] = fileInfo.ModifiedOn;
                        fileEntity.Attributes["contentType"] = fileInfo.ContentType;
                        fileEntity.Attributes["folderId"] = fileInfo.FolderId;
                        fileEntity.Attributes["folderName"] = fileInfo.FolderName;
                        fileEntity.Attributes["subFolderTree"] = fileInfo.SubFolderTree;
                        fileEntity.Attributes["type"] = "file";

                        // Add tags as an EntityCollection
                        EntityCollection tagsCollection = new EntityCollection();
                        if (fileInfo.Tags != null && fileInfo.Tags.Count > 0)
                        {
                            foreach (var tag in fileInfo.Tags)
                            {
                                Entity tagEntity = new Entity();
                                tagEntity.Attributes.Add("tag", tag);
                                tagsCollection.Entities.Add(tagEntity);
                            }
                        }
                        fileEntity.Attributes["tags"] = tagsCollection;

                        folderContents.Entities.Add(fileEntity);
                        totalFileCount++;
                    }

                    folderEntity.Attributes.Add("folderContents", folderContents);
                    foldersInSubTree.Entities.Add(folderEntity);
                }

                // Set response message
                if (!responseEntity.Attributes.Contains("message"))
                {
                    responseEntity.Attributes.Add("message", $"Successfully retrieved {totalFileCount} files from {allFolders.Count} folders in subtree (up to {maxDepth} levels deep)");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in HandleAllFolderFilesInSubTreeForDynamics: {ex.Message}");
                throw new Exception($"Failed to retrieve folder files from subtree: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Recursively traverses folder hierarchy and collects folders with their files
        /// </summary>
        private void TraverseFolderForFolderFiles(string rootFolderId, string currentFolderId, int currentLevel, int maxDepth, Dictionary<string, BoxFolderWithFiles> allFolders)
        {
            if (currentLevel > maxDepth)
            {
                _tracingService.Trace($"Reached maximum depth {maxDepth}, stopping traversal");
                return;
            }

            _tracingService.Trace($"Traversing folder {currentFolderId} at level {currentLevel}");

            try
            {
                // Get folder contents - include tags field to retrieve tags for files
                var folderUrl = $"{BoxApiUrl}/folders/{currentFolderId}/items?fields=id,name,type,size,created_at,modified_at,tags&limit=1000";
                var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
                var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!folderResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"Failed to get folder contents for {currentFolderId}: {folderResponse.StatusCode}");
                    return;
                }

                var folderData = JsonConvert.DeserializeObject<FolderItemsResponse>(folderContent);
                var items = folderData.Entries ?? new List<BoxItem>();

                // Get folder name and build subfolder tree path
                string currentFolderName = GetFolderName(currentFolderId);
                string subFolderTree = BuildSubFolderTree(rootFolderId, currentFolderId);

                // Separate files and folders
                var files = items.Where(item => item.Type == "file").ToList();
                var folders = items.Where(item => item.Type == "folder").ToList();

                _tracingService.Trace($"Found {files.Count} files and {folders.Count} folders in {currentFolderId}");

                // Create folder entry (even if it has no files)
                var folderWithFiles = new BoxFolderWithFiles
                {
                    FolderId = currentFolderId,
                    FolderName = currentFolderName,
                    Files = new List<BoxFileWithFolderInfo>()
                };

                // Process all files in current folder
                foreach (var file in files)
                {
                    // Extract tags from the file object
                    var tags = new List<string>();
                    if (file.Tags != null && file.Tags.Count > 0)
                    {
                        tags = file.Tags.Select(t => t.ToString()).ToList();
                    }

                    var fileInfo = new BoxFileWithFolderInfo
                    {
                        Id = file.Id,
                        Name = file.Name,
                        Size = (int)(file.Size ?? 0),
                        CreatedOn = file.CreatedAt,
                        ModifiedOn = file.ModifiedAt,
                        ContentType = GetMimeTypeFromFileName(file.Name),
                        FolderId = currentFolderId,
                        FolderName = currentFolderName,
                        SubFolderTree = subFolderTree,
                        Tags = tags
                    };

                    folderWithFiles.Files.Add(fileInfo);
                }

                // Add this folder to the dictionary
                allFolders[currentFolderId] = folderWithFiles;

                // Recursively process subfolders (go one level deeper)
                foreach (var folder in folders)
                {
                    TraverseFolderForFolderFiles(rootFolderId, folder.Id, currentLevel + 1, maxDepth, allFolders);
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error traversing folder {currentFolderId}: {ex.Message}");
                // Continue with other folders even if one fails
            }
        }

        /// <summary>
        /// Recursively traverses folder hierarchy and collects all files
        /// </summary>
        private void TraverseFolder(string rootFolderId, string currentFolderId, int currentLevel, int maxDepth, List<BoxFileWithFolderInfo> allFiles)
        {
            if (currentLevel > maxDepth)
            {
                _tracingService.Trace($"Reached maximum depth {maxDepth}, stopping traversal");
                return;
            }

            _tracingService.Trace($"Traversing folder {currentFolderId} at level {currentLevel}");

            try
            {
                // Get folder contents - include tags field to retrieve tags for files
                var folderUrl = $"{BoxApiUrl}/folders/{currentFolderId}/items?fields=id,name,type,size,created_at,modified_at,tags&limit=1000";
                var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
                var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!folderResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"Failed to get folder contents for {currentFolderId}: {folderResponse.StatusCode}");
                    return;
                }

                var folderData = JsonConvert.DeserializeObject<FolderItemsResponse>(folderContent);
                var items = folderData.Entries ?? new List<BoxItem>();

                // Get folder name and build subfolder tree path
                string currentFolderName = GetFolderName(currentFolderId);
                string subFolderTree = BuildSubFolderTree(rootFolderId, currentFolderId);

                // Separate files and folders
                var files = items.Where(item => item.Type == "file").ToList();
                var folders = items.Where(item => item.Type == "folder").ToList();

                _tracingService.Trace($"Found {files.Count} files and {folders.Count} folders in {currentFolderId}");

                // Process all files in current folder
                foreach (var file in files)
                {
                    // Extract tags from the file object
                    var tags = new List<string>();
                    if (file.Tags != null && file.Tags.Count > 0)
                    {
                        tags = file.Tags.Select(t => t.ToString()).ToList();
                    }

                    var fileInfo = new BoxFileWithFolderInfo
                    {
                        Id = file.Id,
                        Name = file.Name,
                        Size = (int)(file.Size ?? 0),
                        CreatedOn = file.CreatedAt,
                        ModifiedOn = file.ModifiedAt,
                        ContentType = GetMimeTypeFromFileName(file.Name),
                        FolderId = currentFolderId,
                        FolderName = currentFolderName,
                        SubFolderTree = subFolderTree,
                        Tags = tags
                    };

                    allFiles.Add(fileInfo);
                }

                // Recursively process subfolders (go one level deeper)
                foreach (var folder in folders)
                {
                    TraverseFolder(rootFolderId, folder.Id, currentLevel + 1, maxDepth, allFiles);
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error traversing folder {currentFolderId}: {ex.Message}");
                // Continue with other folders even if one fails
            }
        }

        /// <summary>
        /// Gets the name of a folder by its ID
        /// </summary>
        private string GetFolderName(string folderId)
        {
            try
            {
                var folderUrl = $"{BoxApiUrl}/folders/{folderId}?fields=name";
                var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
                var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (folderResponse.IsSuccessStatusCode)
                {
                    var folderData = JsonConvert.DeserializeObject<BoxFolderDetails>(folderContent);
                    return folderData.Name ?? folderId;
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error getting folder name for {folderId}: {ex.Message}");
            }

            return folderId; // Return ID if name can't be retrieved
        }

        /// <summary>
        /// Builds the subfolder tree path from root folder to current folder
        /// </summary>
        private string BuildSubFolderTree(string rootFolderId, string currentFolderId)
        {
            if (rootFolderId == currentFolderId)
                return ""; // No subfolder tree for root folder itself

            try
            {
                var folderPath = new List<string>();
                string folderId = currentFolderId;
                int safetyCounter = 0;

                // Traverse up the folder hierarchy until we reach root or safety limit
                while (folderId != null && folderId != "0" && folderId != rootFolderId && safetyCounter < 20)
                {
                    var folderUrl = $"{BoxApiUrl}/folders/{folderId}?fields=id,name,parent";
                    var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
                    var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (folderResponse.IsSuccessStatusCode)
                    {
                        var folderData = JsonConvert.DeserializeObject<BoxFolderDetails>(folderContent);
                        folderPath.Insert(0, folderData.Name);

                        // Check if we've reached the root folder
                        if (folderData.Parent?.Id == rootFolderId)
                        {
                            break;
                        }

                        folderId = folderData.Parent?.Id;
                    }
                    else
                    {
                        break;
                    }

                    safetyCounter++;
                }

                // Build the path string (e.g., "Folder1/Folder2/Folder3")
                return string.Join("/", folderPath);
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error building subfolder tree: {ex.Message}");
                return "";
            }
        }

        // Entity factory methods for consistent Entity creation
        private Entity CreateBoxFolderItemEntity()
        {
            Entity entity = new Entity();
            // Initialize with default values to ensure all attributes exist
            entity.Attributes.Add("id", "");
            entity.Attributes.Add("name", "");
            entity.Attributes.Add("type", "");
            entity.Attributes.Add("size", 0);
            entity.Attributes.Add("createdOn", "");
            entity.Attributes.Add("modifiedOn", "");
            entity.Attributes.Add("contentType", "");
            entity.Attributes.Add("fileContent", ""); // Add fileContent attribute for base64 content
            entity.Attributes.Add("itemIndex", 0); // Add itemIndex attribute for display position
            entity.Attributes.Add("folderId", ""); // Add folderId for allFilesInSubTree
            entity.Attributes.Add("folderName", ""); // Add folderName for allFilesInSubTree
            entity.Attributes.Add("subFolderTree", ""); // Add subFolderTree for allFilesInSubTree
            // entity.Attributes.Add("fileCount", 0); // Add fileCount attribute for folders
            // entity.Attributes.Add("folderCount", 0); // Add folderCount attribute for folders
            return entity;
        }

        /// <summary>
        /// Converts sort field names from request format to Box API format
        /// </summary>
        private string ConvertSortFieldToBoxFormat(string sortByField)
        {
            if (string.IsNullOrWhiteSpace(sortByField))
                return "name"; // Default to name sorting

            switch (sortByField.ToLowerInvariant())
            {
                case "name":
                    return "name";
                case "size":
                    return "size";
                // case "createdon":
                //     return "date"; // Box API uses 'date' for both created and modified dates
                case "modifiedon":
                    return "date"; // Box API uses 'date' for both created and modified dates
                default:
                    _tracingService.Trace($"Unknown sort field '{sortByField}', defaulting to 'name'");
                    return "name";
            }
        }

        /// <summary>
        /// Converts sort direction from request format to Box API format
        /// </summary>
        private string ConvertSortDirectionToBoxFormat(string sortDirection)
        {
            if (string.IsNullOrWhiteSpace(sortDirection))
                return "ASC"; // Default to ascending

            switch (sortDirection.ToLowerInvariant())
            {
                case "asc":
                case "ascending":
                    return "ASC";
                case "desc":
                case "descending":
                    return "DESC";
                default:
                    _tracingService.Trace($"Unknown sort direction '{sortDirection}', defaulting to 'ASC'");
                    return "ASC";
            }
        }

        /// <summary>
        /// Applies custom sorting to items when Box API sorting needs to be supplemented
        /// </summary>
        private IEnumerable<BoxItem> ApplyCustomSorting(IEnumerable<BoxItem> items, string sortByField, string sortDirection)
        {
            if (items == null || !items.Any())
                return items;

            var isDescending = ConvertSortDirectionToBoxFormat(sortDirection) == "DESC";

            switch (sortByField.ToLowerInvariant())
            {
                case "name":
                    return isDescending ?
                        items.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase) :
                        items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

                case "size":
                    return isDescending ?
                        items.OrderByDescending(x => x.Size ?? 0) :
                        items.OrderBy(x => x.Size ?? 0);

                case "createdon":
                    return isDescending ?
                        items.OrderByDescending(x => ParseDateSafely(x.CreatedAt)) :
                        items.OrderBy(x => ParseDateSafely(x.CreatedAt));

                case "modifiedon":
                    return isDescending ?
                        items.OrderByDescending(x => ParseDateSafely(x.ModifiedAt)) :
                        items.OrderBy(x => ParseDateSafely(x.ModifiedAt));

                default:
                    // Default to name sorting
                    return isDescending ?
                        items.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase) :
                        items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Safely parses date strings to DateTime for sorting
        /// </summary>
        private DateTime ParseDateSafely(string dateString)
        {
            if (string.IsNullOrWhiteSpace(dateString))
                return DateTime.MinValue;

            if (DateTime.TryParse(dateString, out DateTime result))
                return result;

            return DateTime.MinValue;
        }

        /// <summary>
        /// Gets file and folder counts for a specific folder (similar to GetFolderTreePathForDynamics logic)
        /// </summary>
        private (int FileCount, int FolderCount) GetFolderItemCounts(string folderId)
        {
            try
            {
                _tracingService.Trace($"Getting item counts for folder: {folderId}");

                // Use the same API call as GetFolderTreePathForDynamics with item_collection field
                var folderUrl = $"{BoxApiUrl}/folders/{folderId}?fields=id,name,item_collection";
                var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
                var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (folderResponse.IsSuccessStatusCode)
                {
                    var folderDetails = JsonConvert.DeserializeObject<BoxFolderDetails>(folderContent);

                    // Count files and folders (same logic as GetFolderTreePathForDynamics)
                    int fileCount = 0, folderCount = 0;
                    if (folderDetails.ItemCollection != null)
                    {
                        foreach (var entry in folderDetails.ItemCollection.Entries ?? new List<BoxItem>())
                        {
                            if (entry.Type == "file") fileCount++;
                            else if (entry.Type == "folder") folderCount++;
                        }
                    }

                    _tracingService.Trace($"Folder {folderId} contains {fileCount} files and {folderCount} folders");
                    return (fileCount, folderCount);
                }
                else
                {
                    _tracingService.Trace($"Failed to get folder details for counts: {folderResponse.StatusCode} - {folderContent}");
                    return (0, 0);
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error getting folder item counts for {folderId}: {ex.Message}");
                return (0, 0);
            }
        }

        private Entity CreateBoxCollaboratorEntity()
        {
            Entity entity = new Entity();
            // Initialize with default values to ensure all attributes exist
            entity.Attributes.Add("id", "");
            entity.Attributes.Add("name", "");
            entity.Attributes.Add("login", "");
            entity.Attributes.Add("type", "");
            entity.Attributes.Add("role", "");
            entity.Attributes.Add("status", "");
            return entity;
        }

        private Entity CreateBoxFolderTreeItemEntity()
        {
            Entity entity = new Entity();
            // Initialize with default values to ensure all attributes exist
            entity.Attributes.Add("folderId", "");
            entity.Attributes.Add("folderName", "");
            entity.Attributes.Add("position", 0);
            entity.Attributes.Add("fileCount", 0);
            entity.Attributes.Add("folderCount", 0);
            entity.Attributes.Add("isRoot", false);
            return entity;
        }

        private Entity CreateBoxFileVersionEntity()
        {
            Entity entity = new Entity();
            // Initialize with default values to ensure all attributes exist
            entity.Attributes.Add("id", "");
            entity.Attributes.Add("versionNumber", "");
            entity.Attributes.Add("name", "");
            entity.Attributes.Add("size", 0);
            entity.Attributes.Add("createdOn", "");
            entity.Attributes.Add("modifiedOn", "");
            entity.Attributes.Add("sha1", "");
            entity.Attributes.Add("isCurrent", false);
            entity.Attributes.Add("modifiedById", "");
            entity.Attributes.Add("modifiedByName", "");
            entity.Attributes.Add("modifiedByLogin", "");
            return entity;
        }

        private string GetItemId(BoxFolderRequest request)
        {
            // For files, use simple ID resolution (preserve existing functionality)
            if (request.ItemType?.ToLower() == "file")
            {
                return !string.IsNullOrWhiteSpace(request.ItemId) ? request.ItemId : request.ItemBoxId;
            }

            // For folders, use advanced folder identification logic
            if (request.ItemType?.ToLower() == "folder")
            {
                return GetFolderIdWithAdvancedLookup(request);
            }

            // Default behavior for other item types
            return !string.IsNullOrWhiteSpace(request.ItemId) ? request.ItemId : request.ItemBoxId;
        }

        /// <summary>
        /// Advanced folder identification logic supporting itemId, ngoId, and itemName lookups
        /// </summary>
        private string GetFolderIdWithAdvancedLookup(BoxFolderRequest request)
        {
            // Priority 1: Use itemId if provided
            if (!string.IsNullOrWhiteSpace(request.ItemId))
            {
                _tracingService.Trace($"Using provided itemId: {request.ItemId}");
                return request.ItemId;
            }

            // Fallback to ItemBoxId if available
            if (!string.IsNullOrWhiteSpace(request.ItemBoxId))
            {
                _tracingService.Trace($"Using provided itemBoxId: {request.ItemBoxId}");
                return request.ItemBoxId;
            }

            // Priority 2: Look up folder by ngoId pattern
            if (!string.IsNullOrWhiteSpace(request.NgoId))
            {
                _tracingService.Trace($"Looking up folder by ngoId pattern: [{request.NgoId}]");
                string foundFolderId = SearchFolderByNgoIdPattern(request.NgoId, request.SearchUnderFolderId);
                if (!string.IsNullOrWhiteSpace(foundFolderId))
                {
                    _tracingService.Trace($"Found folder by ngoId: {foundFolderId}");
                    return foundFolderId;
                }
                else
                {
                    _tracingService.Trace($"No folder found matching ngoId pattern: [{request.NgoId}]");
                }
            }

            // Priority 3: Look up folder by itemName (case-insensitive exact match)
            if (!string.IsNullOrWhiteSpace(request.ItemName))
            {
                _tracingService.Trace($"Looking up folder by itemName: {request.ItemName}");
                string foundFolderId = SearchFolderByName(request.ItemName, request.SearchUnderFolderId);
                if (!string.IsNullOrWhiteSpace(foundFolderId))
                {
                    _tracingService.Trace($"Found folder by itemName: {foundFolderId}");
                    return foundFolderId;
                }
                else
                {
                    _tracingService.Trace($"No folder found matching itemName: {request.ItemName}");
                }
            }

            // No specific identifier provided or found, return null (caller will use _boxRootId)
            _tracingService.Trace("No folder identifier provided or found, will use root folder");
            return null;
        }

        /// <summary>
        /// Searches for folders matching the ngoId pattern [ngoId]* within the specified root folder and its subfolders
        /// </summary>
        private string SearchFolderByNgoIdPattern(string ngoId, string searchUnderFolderId = null)
        {
            try
            {
                // Use searchUnderFolderId if provided, otherwise default to _boxRootId
                string ancestorFolderId = !string.IsNullOrWhiteSpace(searchUnderFolderId) ? searchUnderFolderId : _boxRootId;

                string pattern = $"[{ngoId}]";
                _tracingService.Trace($"Searching for folders starting with pattern: {pattern}");
                _tracingService.Trace($"Search limited to ancestor folder: {ancestorFolderId}");

                // Use Box search API to search within the specified root folder and its subfolders
                // Search query looks for folders with names starting with the pattern
                var searchQuery = Uri.EscapeDataString(pattern);
                var searchUrl = $"{BoxApiUrl}/search?query={searchQuery}&type=folder&ancestor_folder_ids={ancestorFolderId}&fields=id,name,type&limit=200";

                var response = _httpClient.GetAsync(searchUrl).GetAwaiter().GetResult();
                var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                _tracingService.Trace($"Search response status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var searchData = JsonConvert.DeserializeObject<BoxFolderSearchResponse>(content);

                    // Look for folders that match the ngoId pattern exactly (starts with [ngoId])
                    var matchingFolder = searchData.Entries?
                        .Where(item => item.Type == "folder" &&
                               !string.IsNullOrEmpty(item.Name) &&
                               item.Name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();

                    if (matchingFolder != null)
                    {
                        _tracingService.Trace($"Found matching folder: {matchingFolder.Name} (ID: {matchingFolder.Id})");
                        return matchingFolder.Id;
                    }
                    else
                    {
                        _tracingService.Trace($"No folders found matching pattern: {pattern}");
                        // Log all found folders for debugging
                        if (searchData.Entries?.Any() == true)
                        {
                            _tracingService.Trace($"Search found {searchData.Entries.Count} folders, but none matched the exact pattern:");
                            foreach (var folder in searchData.Entries.Take(5)) // Log first 5 for debugging
                            {
                                _tracingService.Trace($"  - {folder.Name} (ID: {folder.Id})");
                            }
                        }
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to search folders: {response.StatusCode} - {content}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error searching for ngoId pattern: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Searches for folders with exact name match (case-insensitive) throughout the entire Box account
        /// </summary>
        private string SearchFolderByName(string folderName, string searchUnderFolderId = null)
        {
            try
            {
                // Use searchUnderFolderId if provided, otherwise default to _boxRootId
                string ancestorFolderId = !string.IsNullOrWhiteSpace(searchUnderFolderId) ? searchUnderFolderId : _boxRootId;

                _tracingService.Trace($"Searching for folder with exact name: {folderName}");
                _tracingService.Trace($"Search limited to ancestor folder: {ancestorFolderId}");

                // Use Box search API to search for folders
                var searchQuery = Uri.EscapeDataString(folderName);
                var searchUrl = $"{BoxApiUrl}/search?query={searchQuery}&type=folder&ancestor_folder_ids={ancestorFolderId}&fields=id,name,type&limit=200";

                var response = _httpClient.GetAsync(searchUrl).GetAwaiter().GetResult();
                var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                _tracingService.Trace($"Search response status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var searchData = JsonConvert.DeserializeObject<BoxFolderSearchResponse>(content);

                    // Look for folders with exact name match (case-insensitive)
                    var matchingFolder = searchData.Entries?
                        .Where(item => item.Type == "folder" &&
                               !string.IsNullOrEmpty(item.Name) &&
                               string.Equals(item.Name, folderName, StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();

                    if (matchingFolder != null)
                    {
                        _tracingService.Trace($"Found matching folder: {matchingFolder.Name} (ID: {matchingFolder.Id})");
                        return matchingFolder.Id;
                    }
                    else
                    {
                        _tracingService.Trace($"No folders found with exact name: {folderName}");
                        // Log all found folders for debugging
                        if (searchData.Entries?.Any() == true)
                        {
                            _tracingService.Trace($"Search found {searchData.Entries.Count} folders, but none matched exactly:");
                            foreach (var folder in searchData.Entries.Take(5)) // Log first 5 for debugging
                            {
                                _tracingService.Trace($"  - {folder.Name} (ID: {folder.Id})");
                            }
                        }
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to search folders: {response.StatusCode} - {content}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error searching for folder by name: {ex.Message}");
            }

            return null;
        }


        private string UploadFileToBox(string folderId, string fileName, byte[] fileBytes, string contentType)
        {
            _tracingService.Trace($"Uploading file to Box: {fileName} to folder {folderId}");

            try
            {
                var url = "https://upload.box.com/api/2.0/files/content";

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
                    client.Timeout = TimeSpan.FromMinutes(5);

                    using (var formData = new MultipartFormDataContent())
                    {
                        // Add the parent folder ID
                        formData.Add(new StringContent(folderId), "parent_id");

                        // Create file attributes JSON
                        var fileAttributes = new
                        {
                            name = fileName,
                            parent = new { id = folderId }
                        };

                        formData.Add(new StringContent(JsonConvert.SerializeObject(fileAttributes)), "attributes");

                        // Add the file content
                        var fileContent = new ByteArrayContent(fileBytes);
                        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType ?? "application/octet-stream");

                        // Add content disposition with filename
                        fileContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
                        {
                            Name = "file",
                            FileName = fileName
                        };

                        formData.Add(fileContent, "file", fileName);

                        var response = client.PostAsync(url, formData).GetAwaiter().GetResult();
                        var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                        _tracingService.Trace($"Upload response: {response.StatusCode} - {responseContent}");

                        if (response.IsSuccessStatusCode)
                        {
                            // Parse the response to get the file ID
                            var uploadResponse = JsonConvert.DeserializeObject<BoxUploadResponse>(responseContent);
                            if (uploadResponse?.Entries?.Count > 0)
                            {
                                return uploadResponse.Entries[0].Id;
                            }
                            else
                            {
                                throw new Exception("Upload successful but no file ID returned in response");
                            }
                        }
                        else
                        {
                            throw new Exception($"Upload failed: {response.StatusCode} - {responseContent}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error uploading file to Box: {ex.Message}");
                throw new Exception($"Error uploading file to Box: {ex.Message}", ex);
            }
        }

        private void HandleFileGetVersionsForDynamics(Entity responseEntity, string fileId)
        {
            _tracingService.Trace($"Starting file versions retrieval for Dynamics - File ID: {fileId}");

            try
            {
                // Get basic file info (without content)
                var fileUrl = $"{BoxApiUrl}/files/{fileId}?fields=id,name,type,size,created_at,modified_at,parent";
                var fileResponse = _httpClient.GetAsync(fileUrl).GetAwaiter().GetResult();
                var fileContent = fileResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!fileResponse.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to get file details: {fileResponse.StatusCode} - {fileContent}");
                }

                var fileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(fileContent);

                // Create file details entity (without file content)
                Entity fileEntity = CreateBoxFolderItemEntity();
                fileEntity.Attributes["id"] = fileData.Id;
                fileEntity.Attributes["name"] = fileData.Name;
                fileEntity.Attributes["type"] = fileData.Type;
                fileEntity.Attributes["size"] = (int)(fileData.Size ?? 0);
                fileEntity.Attributes["createdOn"] = fileData.CreatedAt;
                fileEntity.Attributes["modifiedOn"] = fileData.ModifiedAt;
                fileEntity.Attributes["contentType"] = GetMimeTypeFromFileName(fileData.Name);
                fileEntity.Attributes["fileContent"] = ""; // Empty as per requirement

                // Set fileDetails in response
                responseEntity.Attributes["fileDetails"] = fileEntity;

                // Get file versions
                var versionsUrl = $"{BoxApiUrl}/files/{fileId}/versions?fields=id,name,size,created_at,modified_at,modified_by,sha1,version_number";
                var versionsResponse = _httpClient.GetAsync(versionsUrl).GetAwaiter().GetResult();
                var versionsContent = versionsResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                _tracingService.Trace($"Versions response: {versionsResponse.StatusCode}");

                if (versionsResponse.IsSuccessStatusCode)
                {
                    var versionsData = JsonConvert.DeserializeObject<BoxFileVersionsResponse>(versionsContent);
                    EntityCollection versionsCollection = (EntityCollection)responseEntity.Attributes["versions"];

                    // Add current version (the file itself is always the latest version)
                    Entity currentVersionEntity = CreateBoxFileVersionEntity();
                    currentVersionEntity.Attributes["id"] = fileData.Id;
                    currentVersionEntity.Attributes["versionNumber"] = "current";
                    currentVersionEntity.Attributes["name"] = fileData.Name;
                    currentVersionEntity.Attributes["size"] = (int)(fileData.Size ?? 0);
                    currentVersionEntity.Attributes["createdOn"] = fileData.CreatedAt;
                    currentVersionEntity.Attributes["modifiedOn"] = fileData.ModifiedAt;
                    currentVersionEntity.Attributes["isCurrent"] = true;
                    versionsCollection.Entities.Add(currentVersionEntity);

                    // Add historical versions
                    if (versionsData?.Entries != null && versionsData.Entries.Count > 0)
                    {
                        foreach (var version in versionsData.Entries)
                        {
                            Entity versionEntity = CreateBoxFileVersionEntity();
                            versionEntity.Attributes["id"] = version.Id;
                            versionEntity.Attributes["versionNumber"] = version.VersionNumber?.ToString() ?? "unknown";
                            versionEntity.Attributes["name"] = version.Name ?? fileData.Name;
                            versionEntity.Attributes["size"] = (int)(version.Size ?? 0);
                            versionEntity.Attributes["createdOn"] = version.CreatedAt;
                            versionEntity.Attributes["modifiedOn"] = version.ModifiedAt;
                            versionEntity.Attributes["sha1"] = version.Sha1;
                            versionEntity.Attributes["isCurrent"] = false;

                            // Add modified by information if available
                            if (version.ModifiedBy != null)
                            {
                                versionEntity.Attributes["modifiedById"] = version.ModifiedBy.Id;
                                versionEntity.Attributes["modifiedByName"] = version.ModifiedBy.Name;
                                versionEntity.Attributes["modifiedByLogin"] = version.ModifiedBy.Login;
                            }

                            versionsCollection.Entities.Add(versionEntity);
                        }

                        _tracingService.Trace($"Retrieved {versionsData.Entries.Count} historical version(s) plus current version for file: {fileId}");
                    }
                    else
                    {
                        _tracingService.Trace($"No historical versions found for file: {fileId} (only current version exists)");
                    }

                    if (!responseEntity.Attributes.Contains("itemId"))
                    {
                        responseEntity.Attributes.Add("itemId", fileId);
                    }
                    if (!responseEntity.Attributes.Contains("totalVersions"))
                    {
                        responseEntity.Attributes.Add("totalVersions", versionsCollection.Entities.Count);
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        responseEntity.Attributes.Add("message", $"Retrieved {versionsCollection.Entities.Count} version(s) for file '{fileData.Name}'");
                    }
                }
                else
                {
                    throw new Exception($"Failed to get file versions: {versionsResponse.StatusCode} - {versionsContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error retrieving file versions: {ex.Message}");
                throw new Exception($"Error retrieving file versions: {ex.Message}", ex);
            }
        }

        private void HandleFileGetVersionFileContentForDynamics(Entity responseEntity, string fileId, string versionId)
        {
            _tracingService.Trace($"Starting file version content retrieval for Dynamics - File ID: {fileId}, Version ID: {versionId}");

            try
            {
                // Get basic file info (without content) to get file name and metadata
                var fileUrl = $"{BoxApiUrl}/files/{fileId}?fields=id,name,type,size,created_at,modified_at";
                var fileResponse = _httpClient.GetAsync(fileUrl).GetAwaiter().GetResult();
                var fileContent = fileResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!fileResponse.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to get file details: {fileResponse.StatusCode} - {fileContent}");
                }

                var fileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(fileContent);

                // Determine the content URL based on whether we're getting current version or a specific version
                string contentUrl;
                bool isCurrentVersion = false;

                // Check if versionId is "current" or matches the file ID (current version)
                if (versionId.Equals("current", StringComparison.OrdinalIgnoreCase) || versionId == fileId)
                {
                    // Get current version content
                    contentUrl = $"{BoxApiUrl}/files/{fileId}/content";
                    isCurrentVersion = true;
                    _tracingService.Trace($"Retrieving current version content for file: {fileId}");
                }
                else
                {
                    // Get specific version content using version ID
                    // Box API uses the version ID directly in the version parameter
                    contentUrl = $"{BoxApiUrl}/files/{fileId}/content?version={versionId}";
                    _tracingService.Trace($"Retrieving version {versionId} content for file: {fileId}");
                }

                var contentResponse = _httpClient.GetAsync(contentUrl).GetAwaiter().GetResult();

                string base64Content = "";
                string contentType = GetMimeTypeFromFileName(fileData.Name);
                long actualSize = 0;

                if (contentResponse.IsSuccessStatusCode)
                {
                    var fileBytes = contentResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    base64Content = Convert.ToBase64String(fileBytes);
                    contentType = contentResponse.Content.Headers.ContentType?.MediaType ?? contentType;
                    actualSize = fileBytes.Length;

                    _tracingService.Trace($"Successfully retrieved file version content: {actualSize} bytes");
                }
                else
                {
                    var errorContent = contentResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    throw new Exception($"Failed to get file version content: {contentResponse.StatusCode} - {errorContent}");
                }

                // Create file details entity with content
                Entity fileEntity = CreateBoxFolderItemEntity();
                fileEntity.Attributes["id"] = fileId;
                fileEntity.Attributes["versionId"] = versionId;
                fileEntity.Attributes["isCurrentVersion"] = isCurrentVersion;
                fileEntity.Attributes["name"] = fileData.Name;
                fileEntity.Attributes["type"] = fileData.Type;
                fileEntity.Attributes["size"] = (int)actualSize;
                fileEntity.Attributes["createdOn"] = fileData.CreatedAt;
                fileEntity.Attributes["modifiedOn"] = fileData.ModifiedAt;
                fileEntity.Attributes["contentType"] = contentType;
                fileEntity.Attributes["fileContent"] = base64Content;

                // Set fileDetails in response
                responseEntity.Attributes["fileDetails"] = fileEntity;

                if (!responseEntity.Attributes.Contains("itemId"))
                {
                    responseEntity.Attributes.Add("itemId", fileId);
                }
                if (!responseEntity.Attributes.Contains("versionId"))
                {
                    responseEntity.Attributes.Add("versionId", versionId);
                }
                if (!responseEntity.Attributes.Contains("isCurrentVersion"))
                {
                    responseEntity.Attributes.Add("isCurrentVersion", isCurrentVersion);
                }
                if (!responseEntity.Attributes.Contains("message"))
                {
                    string versionDesc = isCurrentVersion ? "current version" : $"version {versionId}";
                    responseEntity.Attributes.Add("message", $"Retrieved {versionDesc} content for file '{fileData.Name}' ({actualSize:N0} bytes)");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error retrieving file version content: {ex.Message}");
                throw new Exception($"Error retrieving file version content: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Checks if a file with the specified name exists in the given folder
        /// </summary>
        /// <param name="folderId">The folder ID to search in</param>
        /// <param name="fileName">The file name to search for (case-insensitive)</param>
        /// <returns>The file ID if found, null otherwise</returns>
        private string CheckFileExistsInFolder(string folderId, string fileName)
        {
            _tracingService.Trace($"Checking if file '{fileName}' exists in folder {folderId}");

            try
            {
                // Get folder contents
                var folderUrl = $"{BoxApiUrl}/folders/{folderId}/items?fields=id,name,type&limit=1000";
                var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
                var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (folderResponse.IsSuccessStatusCode)
                {
                    var folderData = JsonConvert.DeserializeObject<FolderItemsResponse>(folderContent);
                    var items = folderData.Entries ?? new List<BoxItem>();

                    // Look for file with matching name (case-insensitive)
                    var matchingFile = items
                        .Where(item => item.Type == "file" &&
                               !string.IsNullOrEmpty(item.Name) &&
                               string.Equals(item.Name, fileName, StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();

                    if (matchingFile != null)
                    {
                        _tracingService.Trace($"Found existing file: {matchingFile.Name} (ID: {matchingFile.Id})");
                        return matchingFile.Id;
                    }
                    else
                    {
                        _tracingService.Trace($"No file found with name: {fileName}");
                        return null;
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to get folder contents: {folderResponse.StatusCode} - {folderContent}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error checking if file exists: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Uploads a new version of an existing file
        /// </summary>
        /// <param name="fileId">The ID of the existing file</param>
        /// <param name="fileName">The file name</param>
        /// <param name="fileBytes">The file content as bytes</param>
        /// <param name="contentType">The MIME type of the file</param>
        /// <returns>The file ID (same as input)</returns>
        private string UploadFileVersion(string fileId, string fileName, byte[] fileBytes, string contentType)
        {
            _tracingService.Trace($"Uploading new version for file ID {fileId}: {fileName}");

            try
            {
                var url = $"https://upload.box.com/api/2.0/files/{fileId}/content";

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
                    client.Timeout = TimeSpan.FromMinutes(5);

                    using (var formData = new MultipartFormDataContent())
                    {
                        // Add the file content
                        var fileContent = new ByteArrayContent(fileBytes);
                        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType ?? "application/octet-stream");

                        // Add content disposition with filename
                        fileContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
                        {
                            Name = "file",
                            FileName = fileName
                        };

                        formData.Add(fileContent, "file", fileName);

                        var response = client.PostAsync(url, formData).GetAwaiter().GetResult();
                        var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                        _tracingService.Trace($"Version upload response: {response.StatusCode} - {responseContent}");

                        if (response.IsSuccessStatusCode)
                        {
                            // Parse the response to get the file ID (should be same as input)
                            var uploadResponse = JsonConvert.DeserializeObject<BoxUploadResponse>(responseContent);
                            if (uploadResponse?.Entries?.Count > 0)
                            {
                                _tracingService.Trace($"File version uploaded successfully. File ID: {uploadResponse.Entries[0].Id}");
                                return uploadResponse.Entries[0].Id;
                            }
                            else
                            {
                                // Return original file ID if response doesn't contain it
                                _tracingService.Trace($"Version upload successful, returning original file ID: {fileId}");
                                return fileId;
                            }
                        }
                        else
                        {
                            throw new Exception($"Version upload failed: {response.StatusCode} - {responseContent}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error uploading file version: {ex.Message}");
                throw new Exception($"Error uploading file version: {ex.Message}", ex);
            }
        }

        private string CreateFolderInBox(string parentFolderId, string folderName)
        {
            _tracingService.Trace($"Creating folder in Box: {folderName} in parent folder {parentFolderId}");

            try
            {
                var url = $"{BoxApiUrl}/folders";

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
                    client.Timeout = TimeSpan.FromMinutes(2);

                    // Create the request body for folder creation
                    var folderData = new
                    {
                        name = folderName,
                        parent = new
                        {
                            id = parentFolderId
                        }
                    };

                    var jsonContent = JsonConvert.SerializeObject(folderData);
                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    var response = client.PostAsync(url, content).GetAwaiter().GetResult();
                    var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    _tracingService.Trace($"Folder creation response: {response.StatusCode} - {responseContent}");

                    if (response.IsSuccessStatusCode)
                    {
                        // Parse the response to get the folder ID
                        var folderResponse = JsonConvert.DeserializeObject<BoxFolderCreationResponse>(responseContent);
                        if (!string.IsNullOrEmpty(folderResponse?.Id))
                        {
                            _tracingService.Trace($"Folder created successfully with ID: {folderResponse.Id}");
                            return folderResponse.Id;
                        }
                        else
                        {
                            throw new Exception("Folder creation successful but no folder ID returned in response");
                        }
                    }
                    else
                    {
                        throw new Exception($"Folder creation failed: {response.StatusCode} - {responseContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error creating folder in Box: {ex.Message}");
                throw new Exception($"Error creating folder in Box: {ex.Message}", ex);
            }
        }


        /// <summary>
        /// Handles file deletion for Dynamics Entity response (optimized method)
        /// </summary>
        private void HandleFileDeleteForDynamics(Entity responseEntity, string fileId)
        {
            _tracingService.Trace($"Starting file deletion for Dynamics: {fileId}");

            try
            {

                var fileInfoUrl = $"{BoxApiUrl}/files/{fileId}?fields=id,name,type,size,parent";
                var fileInfoResponse = _httpClient.GetAsync(fileInfoUrl).GetAwaiter().GetResult();

                string fileName = "Unknown";
                string parentFolderId = null;

                if (fileInfoResponse.IsSuccessStatusCode)
                {
                    var fileContent = fileInfoResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var fileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(fileContent);
                    fileName = fileData.Name;
                    parentFolderId = fileData.Parent?.Id;
                }

                // Delete the file from Box
                var deleteUrl = $"{BoxApiUrl}/files/{fileId}";
                var deleteResponse = _httpClient.DeleteAsync(deleteUrl).GetAwaiter().GetResult();

                if (deleteResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"File deleted successfully: {fileId}");

                    // Set response details
                    if (!responseEntity.Attributes.Contains("itemId"))
                    {
                        responseEntity.Attributes.Add("itemId", fileId);
                    }
                    if (!responseEntity.Attributes.Contains("fileName"))
                    {
                        responseEntity.Attributes.Add("fileName", fileName);
                    }
                    if (!responseEntity.Attributes.Contains("fileOperation"))
                    {
                        responseEntity.Attributes.Add("fileOperation", "delete");
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        responseEntity.Attributes.Add("message", $"File '{fileName}' (ID: {fileId}) deleted successfully from Box");
                    }
                    if (parentFolderId != null && !responseEntity.Attributes.Contains("folderId"))
                    {
                        responseEntity.Attributes.Add("folderId", parentFolderId);
                    }

                    // Create deleted file entity for reference
                    Entity deletedFileEntity = CreateBoxFolderItemEntity();
                    deletedFileEntity.Attributes["id"] = fileId;
                    deletedFileEntity.Attributes["name"] = fileName;
                    deletedFileEntity.Attributes["type"] = "file";
                    deletedFileEntity.Attributes["status"] = "deleted";
                    deletedFileEntity.Attributes["deletedOn"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                    // Add as fileDetails
                    responseEntity.Attributes["fileDetails"] = deletedFileEntity;

                    // Get folder tree path if we have parent folder
                    if (!string.IsNullOrEmpty(parentFolderId))
                    {
                        _tracingService.Trace($"Getting folder tree for deleted file's parent folder: {parentFolderId}");
                        GetFolderTreePathForDynamics(responseEntity, parentFolderId);
                    }
                }
                else
                {
                    var errorContent = deleteResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    _tracingService.Trace($"Failed to delete file: {deleteResponse.StatusCode} - {errorContent}");
                    throw new Exception($"Failed to delete file: {deleteResponse.StatusCode} - {errorContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error deleting file: {ex.Message}");
                throw new Exception($"Error deleting file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles folder deletion for Dynamics Entity response
        /// </summary>
        private void HandleFolderDeleteForDynamics(Entity responseEntity, string folderId)
        {
            _tracingService.Trace($"Starting folder deletion for Dynamics: {folderId}");

            try
            {
                // Get folder information before deletion
                var folderInfoUrl = $"{BoxApiUrl}/folders/{folderId}?fields=id,name,type,size,parent,item_collection";
                var folderInfoResponse = _httpClient.GetAsync(folderInfoUrl).GetAwaiter().GetResult();

                string folderName = "Unknown";
                string parentFolderId = null;
                int itemCount = 0;

                if (folderInfoResponse.IsSuccessStatusCode)
                {
                    var folderContent = folderInfoResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var folderData = JsonConvert.DeserializeObject<BoxFolderDetails>(folderContent);
                    folderName = folderData.Name;
                    parentFolderId = folderData.Parent?.Id;
                    itemCount = folderData.ItemCollection?.TotalCount ?? 0;

                    _tracingService.Trace($"Folder '{folderName}' contains {itemCount} items");
                }

                // Check if folder is empty - Box requires folders to be empty before deletion
                if (itemCount > 0)
                {
                    throw new Exception($"Cannot delete folder '{folderName}' (ID: {folderId}). Folder must be empty before deletion. Current item count: {itemCount}");
                }

                // Delete the folder from Box
                var deleteUrl = $"{BoxApiUrl}/folders/{folderId}?recursive=false";
                var deleteResponse = _httpClient.DeleteAsync(deleteUrl).GetAwaiter().GetResult();

                if (deleteResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"Folder deleted successfully: {folderId}");

                    // Set response details
                    if (!responseEntity.Attributes.Contains("itemId"))
                    {
                        responseEntity.Attributes.Add("itemId", folderId);
                    }
                    if (!responseEntity.Attributes.Contains("folderName"))
                    {
                        responseEntity.Attributes.Add("folderName", folderName);
                    }
                    if (!responseEntity.Attributes.Contains("itemOperation"))
                    {
                        responseEntity.Attributes.Add("itemOperation", "delete");
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        responseEntity.Attributes.Add("message", $"Folder '{folderName}' (ID: {folderId}) deleted successfully from Box");
                    }
                    if (parentFolderId != null && !responseEntity.Attributes.Contains("folderId"))
                    {
                        responseEntity.Attributes.Add("folderId", parentFolderId);
                    }

                    // Create deleted folder entity for reference
                    Entity deletedFolderEntity = CreateBoxFolderItemEntity();
                    deletedFolderEntity.Attributes["id"] = folderId;
                    deletedFolderEntity.Attributes["name"] = folderName;
                    deletedFolderEntity.Attributes["type"] = "folder";
                    deletedFolderEntity.Attributes["status"] = "deleted";
                    deletedFolderEntity.Attributes["deletedOn"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                    // Add as folderDetails
                    responseEntity.Attributes["folderDetails"] = deletedFolderEntity;

                    // Get folder tree path if we have parent folder
                    if (!string.IsNullOrEmpty(parentFolderId))
                    {
                        _tracingService.Trace($"Getting folder tree for deleted folder's parent folder: {parentFolderId}");
                        GetFolderTreePathForDynamics(responseEntity, parentFolderId);
                    }
                }
                else
                {
                    var errorContent = deleteResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    _tracingService.Trace($"Failed to delete folder: {deleteResponse.StatusCode} - {errorContent}");

                    // Parse Box API error for better error messages
                    string errorMessage = $"Failed to delete folder: {deleteResponse.StatusCode}";
                    try
                    {
                        var errorObj = JsonConvert.DeserializeObject<dynamic>(errorContent);
                        if (errorObj?.message != null)
                        {
                            errorMessage += $" - {errorObj.message}";
                        }
                    }
                    catch
                    {
                        errorMessage += $" - {errorContent}";
                    }

                    throw new Exception(errorMessage);
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error deleting folder: {ex.Message}");
                throw new Exception($"Error deleting folder: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles file move operation for Dynamics Entity response
        /// </summary>
        private void HandleFileMoveForDynamics(Entity responseEntity, string fileId, string destinationFolderId)
        {
            _tracingService.Trace($"Starting file move for Dynamics: {fileId} to folder {destinationFolderId}");

            try
            {
                // Get file information before move
                var fileInfoUrl = $"{BoxApiUrl}/files/{fileId}?fields=id,name,type,size,parent";
                var fileInfoResponse = _httpClient.GetAsync(fileInfoUrl).GetAwaiter().GetResult();

                string fileName = "Unknown";
                string sourceFolderId = null;

                if (fileInfoResponse.IsSuccessStatusCode)
                {
                    var fileContent = fileInfoResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var fileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(fileContent);
                    fileName = fileData.Name;
                    sourceFolderId = fileData.Parent?.Id;
                }

                // Move the file using Box API
                var moveUrl = $"{BoxApiUrl}/files/{fileId}";
                var moveData = new
                {
                    parent = new { id = destinationFolderId }
                };

                var jsonContent = JsonConvert.SerializeObject(moveData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var moveResponse = _httpClient.PutAsync(moveUrl, content).GetAwaiter().GetResult();
                var moveResponseContent = moveResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (moveResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"File moved successfully: {fileId}");

                    var movedFileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(moveResponseContent);

                    // Set response details
                    if (!responseEntity.Attributes.Contains("itemId"))
                    {
                        responseEntity.Attributes.Add("itemId", fileId);
                    }
                    if (!responseEntity.Attributes.Contains("fileName"))
                    {
                        responseEntity.Attributes.Add("fileName", fileName);
                    }
                    if (!responseEntity.Attributes.Contains("itemOperation"))
                    {
                        responseEntity.Attributes.Add("itemOperation", "move");
                    }
                    if (!responseEntity.Attributes.Contains("sourceFolderId"))
                    {
                        responseEntity.Attributes.Add("sourceFolderId", sourceFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("destinationFolderId"))
                    {
                        responseEntity.Attributes.Add("destinationFolderId", destinationFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        responseEntity.Attributes.Add("message", $"File '{fileName}' (ID: {fileId}) moved successfully from folder {sourceFolderId} to folder {destinationFolderId}");
                    }

                    // Create moved file entity
                    Entity movedFileEntity = CreateBoxFolderItemEntity();
                    movedFileEntity.Attributes["id"] = movedFileData.Id;
                    movedFileEntity.Attributes["name"] = movedFileData.Name;
                    movedFileEntity.Attributes["type"] = movedFileData.Type;
                    movedFileEntity.Attributes["size"] = (int)(movedFileData.Size ?? 0);
                    movedFileEntity.Attributes["createdOn"] = movedFileData.CreatedAt;
                    movedFileEntity.Attributes["modifiedOn"] = movedFileData.ModifiedAt;

                    // Add as fileDetails
                    responseEntity.Attributes["fileDetails"] = movedFileEntity;

                    // Get folder tree path for destination folder
                    if (!string.IsNullOrEmpty(destinationFolderId))
                    {
                        _tracingService.Trace($"Getting folder tree for destination folder: {destinationFolderId}");
                        GetFolderTreePathForDynamics(responseEntity, destinationFolderId);
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to move file: {moveResponse.StatusCode} - {moveResponseContent}");
                    throw new Exception($"Failed to move file: {moveResponse.StatusCode} - {moveResponseContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error moving file: {ex.Message}");
                throw new Exception($"Error moving file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles file copy operation for Dynamics Entity response
        /// </summary>
        private void HandleFileCopyForDynamics(Entity responseEntity, string fileId, string destinationFolderId, string newFileName = null)
        {
            _tracingService.Trace($"Starting file copy for Dynamics: {fileId} to folder {destinationFolderId}" +
                (!string.IsNullOrWhiteSpace(newFileName) ? $" with new name '{newFileName}'" : ""));

            try
            {
                // Get file information before copy
                var fileInfoUrl = $"{BoxApiUrl}/files/{fileId}?fields=id,name,type,size,parent";
                var fileInfoResponse = _httpClient.GetAsync(fileInfoUrl).GetAwaiter().GetResult();

                string fileName = "Unknown";
                string sourceFolderId = null;

                if (fileInfoResponse.IsSuccessStatusCode)
                {
                    var fileContent = fileInfoResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var fileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(fileContent);
                    fileName = fileData.Name;
                    sourceFolderId = fileData.Parent?.Id;
                }

                // Copy the file using Box API
                var copyUrl = $"{BoxApiUrl}/files/{fileId}/copy";

                // Build copy data with optional name
                dynamic copyData;
                if (!string.IsNullOrWhiteSpace(newFileName))
                {
                    copyData = new
                    {
                        parent = new { id = destinationFolderId },
                        name = newFileName
                    };
                }
                else
                {
                    copyData = new
                    {
                        parent = new { id = destinationFolderId }
                    };
                }

                var jsonContent = JsonConvert.SerializeObject(copyData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var copyResponse = _httpClient.PostAsync(copyUrl, content).GetAwaiter().GetResult();
                var copyResponseContent = copyResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (copyResponse.IsSuccessStatusCode)
                {
                    var copiedFileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(copyResponseContent);
                    _tracingService.Trace($"File copied successfully. New file ID: {copiedFileData.Id}");

                    // Set response details
                    if (!responseEntity.Attributes.Contains("itemId"))
                    {
                        responseEntity.Attributes.Add("itemId", copiedFileData.Id); // New copied file ID
                    }
                    if (!responseEntity.Attributes.Contains("sourceItemId"))
                    {
                        responseEntity.Attributes.Add("sourceItemId", fileId); // Original file ID
                    }
                    if (!responseEntity.Attributes.Contains("fileName"))
                    {
                        responseEntity.Attributes.Add("fileName", copiedFileData.Name); // Use the actual name from Box response
                    }
                    if (!responseEntity.Attributes.Contains("originalFileName"))
                    {
                        responseEntity.Attributes.Add("originalFileName", fileName); // Original source file name
                    }
                    if (!string.IsNullOrWhiteSpace(newFileName) && !responseEntity.Attributes.Contains("newFileName"))
                    {
                        responseEntity.Attributes.Add("newFileName", newFileName); // Requested new name
                    }
                    if (!responseEntity.Attributes.Contains("itemOperation"))
                    {
                        responseEntity.Attributes.Add("itemOperation", "copy");
                    }
                    if (!responseEntity.Attributes.Contains("sourceFolderId"))
                    {
                        responseEntity.Attributes.Add("sourceFolderId", sourceFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("destinationFolderId"))
                    {
                        responseEntity.Attributes.Add("destinationFolderId", destinationFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        string message = !string.IsNullOrWhiteSpace(newFileName)
                            ? $"File '{fileName}' (ID: {fileId}) copied successfully to folder {destinationFolderId} with new name '{copiedFileData.Name}'. New file ID: {copiedFileData.Id}"
                            : $"File '{fileName}' (ID: {fileId}) copied successfully to folder {destinationFolderId}. New file ID: {copiedFileData.Id}";
                        responseEntity.Attributes.Add("message", message);
                    }

                    // Create copied file entity
                    Entity copiedFileEntity = CreateBoxFolderItemEntity();
                    copiedFileEntity.Attributes["id"] = copiedFileData.Id;
                    copiedFileEntity.Attributes["name"] = copiedFileData.Name;
                    copiedFileEntity.Attributes["type"] = copiedFileData.Type;
                    copiedFileEntity.Attributes["size"] = (int)(copiedFileData.Size ?? 0);
                    copiedFileEntity.Attributes["createdOn"] = copiedFileData.CreatedAt;
                    copiedFileEntity.Attributes["modifiedOn"] = copiedFileData.ModifiedAt;

                    // Add as fileDetails
                    responseEntity.Attributes["fileDetails"] = copiedFileEntity;

                    // Get folder tree path for destination folder
                    if (!string.IsNullOrEmpty(destinationFolderId))
                    {
                        _tracingService.Trace($"Getting folder tree for destination folder: {destinationFolderId}");
                        GetFolderTreePathForDynamics(responseEntity, destinationFolderId);
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to copy file: {copyResponse.StatusCode} - {copyResponseContent}");
                    throw new Exception($"Failed to copy file: {copyResponse.StatusCode} - {copyResponseContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error copying file: {ex.Message}");
                throw new Exception($"Error copying file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles folder move operation for Dynamics Entity response
        /// </summary>
        private void HandleFolderMoveForDynamics(Entity responseEntity, string folderId, string destinationFolderId)
        {
            _tracingService.Trace($"Starting folder move for Dynamics: {folderId} to folder {destinationFolderId}");

            try
            {
                // Get folder information before move
                var folderInfoUrl = $"{BoxApiUrl}/folders/{folderId}?fields=id,name,type,size,parent,item_collection";
                var folderInfoResponse = _httpClient.GetAsync(folderInfoUrl).GetAwaiter().GetResult();

                string folderName = "Unknown";
                string sourceFolderId = null;

                if (folderInfoResponse.IsSuccessStatusCode)
                {
                    var folderContent = folderInfoResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var folderData = JsonConvert.DeserializeObject<BoxFolderDetails>(folderContent);
                    folderName = folderData.Name;
                    sourceFolderId = folderData.Parent?.Id;
                }

                // Move the folder using Box API
                var moveUrl = $"{BoxApiUrl}/folders/{folderId}";
                var moveData = new
                {
                    parent = new { id = destinationFolderId }
                };

                var jsonContent = JsonConvert.SerializeObject(moveData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var moveResponse = _httpClient.PutAsync(moveUrl, content).GetAwaiter().GetResult();
                var moveResponseContent = moveResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (moveResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"Folder moved successfully: {folderId}");

                    var movedFolderData = JsonConvert.DeserializeObject<BoxFolderDetails>(moveResponseContent);

                    // Set response details
                    if (!responseEntity.Attributes.Contains("itemId"))
                    {
                        responseEntity.Attributes.Add("itemId", folderId);
                    }
                    if (!responseEntity.Attributes.Contains("folderName"))
                    {
                        responseEntity.Attributes.Add("folderName", folderName);
                    }
                    if (!responseEntity.Attributes.Contains("itemOperation"))
                    {
                        responseEntity.Attributes.Add("itemOperation", "move");
                    }
                    if (!responseEntity.Attributes.Contains("sourceFolderId"))
                    {
                        responseEntity.Attributes.Add("sourceFolderId", sourceFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("destinationFolderId"))
                    {
                        responseEntity.Attributes.Add("destinationFolderId", destinationFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        responseEntity.Attributes.Add("message", $"Folder '{folderName}' (ID: {folderId}) moved successfully from folder {sourceFolderId} to folder {destinationFolderId}");
                    }

                    // Create moved folder entity
                    Entity movedFolderEntity = CreateBoxFolderItemEntity();
                    movedFolderEntity.Attributes["id"] = movedFolderData.Id;
                    movedFolderEntity.Attributes["name"] = movedFolderData.Name;
                    movedFolderEntity.Attributes["type"] = "folder";

                    // Add as folderDetails
                    responseEntity.Attributes["folderDetails"] = movedFolderEntity;

                    // Get folder tree path for destination folder
                    if (!string.IsNullOrEmpty(destinationFolderId))
                    {
                        _tracingService.Trace($"Getting folder tree for destination folder: {destinationFolderId}");
                        GetFolderTreePathForDynamics(responseEntity, destinationFolderId);
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to move folder: {moveResponse.StatusCode} - {moveResponseContent}");
                    throw new Exception($"Failed to move folder: {moveResponse.StatusCode} - {moveResponseContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error moving folder: {ex.Message}");
                throw new Exception($"Error moving folder: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles folder copy operation for Dynamics Entity response
        /// </summary>
        private void HandleFolderCopyForDynamics(Entity responseEntity, string folderId, string destinationFolderId, string newFolderName = null)
        {
            _tracingService.Trace($"Starting folder copy for Dynamics: {folderId} to folder {destinationFolderId}" +
                (!string.IsNullOrWhiteSpace(newFolderName) ? $" with new name '{newFolderName}'" : ""));

            try
            {
                // Get folder information before copy
                var folderInfoUrl = $"{BoxApiUrl}/folders/{folderId}?fields=id,name,type,size,parent,item_collection";
                var folderInfoResponse = _httpClient.GetAsync(folderInfoUrl).GetAwaiter().GetResult();

                string folderName = "Unknown";
                string sourceFolderId = null;

                if (folderInfoResponse.IsSuccessStatusCode)
                {
                    var folderContent = folderInfoResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var folderData = JsonConvert.DeserializeObject<BoxFolderDetails>(folderContent);
                    folderName = folderData.Name;
                    sourceFolderId = folderData.Parent?.Id;
                }

                // Copy the folder using Box API
                var copyUrl = $"{BoxApiUrl}/folders/{folderId}/copy";

                // Build copy data with optional name
                dynamic copyData;
                if (!string.IsNullOrWhiteSpace(newFolderName))
                {
                    copyData = new
                    {
                        parent = new { id = destinationFolderId },
                        name = newFolderName
                    };
                }
                else
                {
                    copyData = new
                    {
                        parent = new { id = destinationFolderId }
                    };
                }

                var jsonContent = JsonConvert.SerializeObject(copyData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var copyResponse = _httpClient.PostAsync(copyUrl, content).GetAwaiter().GetResult();
                var copyResponseContent = copyResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (copyResponse.IsSuccessStatusCode)
                {
                    var copiedFolderData = JsonConvert.DeserializeObject<BoxFolderDetails>(copyResponseContent);
                    _tracingService.Trace($"Folder copied successfully. New folder ID: {copiedFolderData.Id}");

                    // Set response details
                    if (!responseEntity.Attributes.Contains("itemId"))
                    {
                        responseEntity.Attributes.Add("itemId", copiedFolderData.Id); // New copied folder ID
                    }
                    if (!responseEntity.Attributes.Contains("sourceItemId"))
                    {
                        responseEntity.Attributes.Add("sourceItemId", folderId); // Original folder ID
                    }
                    if (!responseEntity.Attributes.Contains("folderName"))
                    {
                        responseEntity.Attributes.Add("folderName", copiedFolderData.Name); // Use the actual name from Box response
                    }
                    if (!responseEntity.Attributes.Contains("originalFolderName"))
                    {
                        responseEntity.Attributes.Add("originalFolderName", folderName); // Original source folder name
                    }
                    if (!string.IsNullOrWhiteSpace(newFolderName) && !responseEntity.Attributes.Contains("newFolderName"))
                    {
                        responseEntity.Attributes.Add("newFolderName", newFolderName); // Requested new name
                    }
                    if (!responseEntity.Attributes.Contains("itemOperation"))
                    {
                        responseEntity.Attributes.Add("itemOperation", "copy");
                    }
                    if (!responseEntity.Attributes.Contains("sourceFolderId"))
                    {
                        responseEntity.Attributes.Add("sourceFolderId", sourceFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("destinationFolderId"))
                    {
                        responseEntity.Attributes.Add("destinationFolderId", destinationFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        string message = !string.IsNullOrWhiteSpace(newFolderName)
                            ? $"Folder '{folderName}' (ID: {folderId}) copied successfully to folder {destinationFolderId} with new name '{copiedFolderData.Name}'. New folder ID: {copiedFolderData.Id}"
                            : $"Folder '{folderName}' (ID: {folderId}) copied successfully to folder {destinationFolderId}. New folder ID: {copiedFolderData.Id}";
                        responseEntity.Attributes.Add("message", message);
                    }

                    // Create copied folder entity
                    Entity copiedFolderEntity = CreateBoxFolderItemEntity();
                    copiedFolderEntity.Attributes["id"] = copiedFolderData.Id;
                    copiedFolderEntity.Attributes["name"] = copiedFolderData.Name;
                    copiedFolderEntity.Attributes["type"] = "folder";

                    // Add as folderDetails
                    responseEntity.Attributes["folderDetails"] = copiedFolderEntity;

                    // Get folder tree path for destination folder
                    if (!string.IsNullOrEmpty(destinationFolderId))
                    {
                        _tracingService.Trace($"Getting folder tree for destination folder: {destinationFolderId}");
                        GetFolderTreePathForDynamics(responseEntity, destinationFolderId);
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to copy folder: {copyResponse.StatusCode} - {copyResponseContent}");
                    throw new Exception($"Failed to copy folder: {copyResponse.StatusCode} - {copyResponseContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error copying folder: {ex.Message}");
                throw new Exception($"Error copying folder: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles file rename operation for Dynamics Entity response
        /// </summary>
        private void HandleFileRenameForDynamics(Entity responseEntity, string fileId, string newFileName)
        {
            _tracingService.Trace($"Starting file rename for Dynamics: {fileId} to '{newFileName}'");

            try
            {
                // Get file information before rename
                var fileInfoUrl = $"{BoxApiUrl}/files/{fileId}?fields=id,name,type,size,parent";
                var fileInfoResponse = _httpClient.GetAsync(fileInfoUrl).GetAwaiter().GetResult();

                string oldFileName = "Unknown";
                string parentFolderId = null;

                if (fileInfoResponse.IsSuccessStatusCode)
                {
                    var fileContent = fileInfoResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var fileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(fileContent);
                    oldFileName = fileData.Name;
                    parentFolderId = fileData.Parent?.Id;
                }

                // Rename the file using Box API
                var renameUrl = $"{BoxApiUrl}/files/{fileId}";
                var renameData = new
                {
                    name = newFileName
                };

                var jsonContent = JsonConvert.SerializeObject(renameData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var renameResponse = _httpClient.PutAsync(renameUrl, content).GetAwaiter().GetResult();
                var renameResponseContent = renameResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (renameResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"File renamed successfully: {fileId}");

                    var renamedFileData = JsonConvert.DeserializeObject<BoxFileDetailsWithParent>(renameResponseContent);

                    // Set response details
                    if (!responseEntity.Attributes.Contains("itemId"))
                    {
                        responseEntity.Attributes.Add("itemId", fileId);
                    }
                    if (!responseEntity.Attributes.Contains("oldFileName"))
                    {
                        responseEntity.Attributes.Add("oldFileName", oldFileName);
                    }
                    if (!responseEntity.Attributes.Contains("newFileName"))
                    {
                        responseEntity.Attributes.Add("newFileName", newFileName);
                    }
                    if (!responseEntity.Attributes.Contains("itemOperation"))
                    {
                        responseEntity.Attributes.Add("itemOperation", "rename");
                    }
                    if (!responseEntity.Attributes.Contains("folderId"))
                    {
                        responseEntity.Attributes.Add("folderId", parentFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        responseEntity.Attributes.Add("message", $"File renamed successfully from '{oldFileName}' to '{newFileName}' (ID: {fileId})");
                    }

                    // Create renamed file entity
                    Entity renamedFileEntity = CreateBoxFolderItemEntity();
                    renamedFileEntity.Attributes["id"] = renamedFileData.Id;
                    renamedFileEntity.Attributes["name"] = renamedFileData.Name;
                    renamedFileEntity.Attributes["type"] = renamedFileData.Type;
                    renamedFileEntity.Attributes["size"] = (int)(renamedFileData.Size ?? 0);
                    renamedFileEntity.Attributes["createdOn"] = renamedFileData.CreatedAt;
                    renamedFileEntity.Attributes["modifiedOn"] = renamedFileData.ModifiedAt;

                    // Add as fileDetails
                    responseEntity.Attributes["fileDetails"] = renamedFileEntity;

                    // Get folder tree path for parent folder
                    if (!string.IsNullOrEmpty(parentFolderId))
                    {
                        _tracingService.Trace($"Getting folder tree for parent folder: {parentFolderId}");
                        GetFolderTreePathForDynamics(responseEntity, parentFolderId);
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to rename file: {renameResponse.StatusCode} - {renameResponseContent}");
                    throw new Exception($"Failed to rename file: {renameResponse.StatusCode} - {renameResponseContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error renaming file: {ex.Message}");
                throw new Exception($"Error renaming file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles folder rename operation for Dynamics Entity response
        /// </summary>
        private void HandleFolderRenameForDynamics(Entity responseEntity, string folderId, string newFolderName)
        {
            _tracingService.Trace($"Starting folder rename for Dynamics: {folderId} to '{newFolderName}'");

            try
            {
                // Get folder information before rename
                var folderInfoUrl = $"{BoxApiUrl}/folders/{folderId}?fields=id,name,type,size,parent,item_collection";
                var folderInfoResponse = _httpClient.GetAsync(folderInfoUrl).GetAwaiter().GetResult();

                string oldFolderName = "Unknown";
                string parentFolderId = null;

                if (folderInfoResponse.IsSuccessStatusCode)
                {
                    var folderContent = folderInfoResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var folderData = JsonConvert.DeserializeObject<BoxFolderDetails>(folderContent);
                    oldFolderName = folderData.Name;
                    parentFolderId = folderData.Parent?.Id;
                }

                // Rename the folder using Box API
                var renameUrl = $"{BoxApiUrl}/folders/{folderId}";
                var renameData = new
                {
                    name = newFolderName
                };

                var jsonContent = JsonConvert.SerializeObject(renameData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var renameResponse = _httpClient.PutAsync(renameUrl, content).GetAwaiter().GetResult();
                var renameResponseContent = renameResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (renameResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"Folder renamed successfully: {folderId}");

                    var renamedFolderData = JsonConvert.DeserializeObject<BoxFolderDetails>(renameResponseContent);

                    // Set response details
                    if (!responseEntity.Attributes.Contains("itemId"))
                    {
                        responseEntity.Attributes.Add("itemId", folderId);
                    }
                    if (!responseEntity.Attributes.Contains("oldFolderName"))
                    {
                        responseEntity.Attributes.Add("oldFolderName", oldFolderName);
                    }
                    if (!responseEntity.Attributes.Contains("newFolderName"))
                    {
                        responseEntity.Attributes.Add("newFolderName", newFolderName);
                    }
                    if (!responseEntity.Attributes.Contains("itemOperation"))
                    {
                        responseEntity.Attributes.Add("itemOperation", "rename");
                    }
                    if (!responseEntity.Attributes.Contains("folderId"))
                    {
                        responseEntity.Attributes.Add("folderId", parentFolderId);
                    }
                    if (!responseEntity.Attributes.Contains("message"))
                    {
                        responseEntity.Attributes.Add("message", $"Folder renamed successfully from '{oldFolderName}' to '{newFolderName}' (ID: {folderId})");
                    }

                    // Create renamed folder entity
                    Entity renamedFolderEntity = CreateBoxFolderItemEntity();
                    renamedFolderEntity.Attributes["id"] = renamedFolderData.Id;
                    renamedFolderEntity.Attributes["name"] = renamedFolderData.Name;
                    renamedFolderEntity.Attributes["type"] = "folder";

                    // Add as folderDetails
                    responseEntity.Attributes["folderDetails"] = renamedFolderEntity;

                    // Get folder tree path for parent folder
                    if (!string.IsNullOrEmpty(parentFolderId))
                    {
                        _tracingService.Trace($"Getting folder tree for parent folder: {parentFolderId}");
                        GetFolderTreePathForDynamics(responseEntity, parentFolderId);
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to rename folder: {renameResponse.StatusCode} - {renameResponseContent}");
                    throw new Exception($"Failed to rename folder: {renameResponse.StatusCode} - {renameResponseContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error renaming folder: {ex.Message}");
                throw new Exception($"Error renaming folder: {ex.Message}", ex);
            }
        }



        /// <summary>
        /// Adds tags to a file in Box using PUT to update the file
        /// </summary>
        private void AddTagsToFile(string fileId, List<string> tags)
        {
            try
            {
                _tracingService.Trace($"Adding {tags.Count} tag(s) to file: {fileId}");

                // Update file with tags using PUT
                string updateUrl = $"{BoxApiUrl}/files/{fileId}";

                var updateData = new
                {
                    tags = tags.ToArray()
                };

                string updateJson = JsonConvert.SerializeObject(updateData);
                var updateContent = new StringContent(updateJson, Encoding.UTF8, "application/json");

                var updateResponse = _httpClient.PutAsync(updateUrl, updateContent).GetAwaiter().GetResult();
                var responseContent = updateResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (updateResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"Tags added successfully to file: {fileId}");
                }
                else
                {
                    _tracingService.Trace($"Failed to add tags to file {fileId}: {updateResponse.StatusCode} - {responseContent}");
                    throw new Exception($"Failed to add tags to file: {updateResponse.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error adding tags to file {fileId}: {ex.Message}");
                throw new Exception($"Error adding tags to file: {ex.Message}", ex);
            }
        }

        private void UpdateFileDescription(string fileId, string description)
        {
            try
            {
                _tracingService.Trace($"Updating description for file: {fileId}");

                // Update file description using PUT
                string updateUrl = $"{BoxApiUrl}/files/{fileId}";

                var updateData = new
                {
                    description = description
                };

                string updateJson = JsonConvert.SerializeObject(updateData);
                var updateContent = new StringContent(updateJson, Encoding.UTF8, "application/json");

                var updateResponse = _httpClient.PutAsync(updateUrl, updateContent).GetAwaiter().GetResult();
                var responseContent = updateResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (updateResponse.IsSuccessStatusCode)
                {
                    _tracingService.Trace($"Description updated successfully for file: {fileId}");
                }
                else
                {
                    _tracingService.Trace($"Failed to update description for file {fileId}: {updateResponse.StatusCode} - {responseContent}");
                    throw new Exception($"Failed to update file description: {updateResponse.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error updating description for file {fileId}: {ex.Message}");
                throw new Exception($"Error updating file description: {ex.Message}", ex);
            }
        }

        private string GetMimeTypeFromFileName(string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName))
                    return "application/octet-stream";

                var extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();

                var mimeTypes = new Dictionary<string, string>
                {
                    { ".pdf", "application/pdf" },
                    { ".doc", "application/msword" },
                    { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
                    { ".xls", "application/vnd.ms-excel" },
                    { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
                    { ".ppt", "application/vnd.ms-powerpoint" },
                    { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
                    { ".txt", "text/plain" },
                    { ".jpg", "image/jpeg" },
                    { ".jpeg", "image/jpeg" },
                    { ".png", "image/png" },
                    { ".gif", "image/gif" },
                    { ".bmp", "image/bmp" },
                    { ".zip", "application/zip" },
                    { ".rar", "application/x-rar-compressed" },
                    { ".json", "application/json" },
                    { ".xml", "application/xml" },
                    { ".csv", "text/csv" },
                    { ".html", "text/html" },
                    { ".htm", "text/html" },
                    { ".css", "text/css" },
                    { ".js", "application/javascript" }
                };


                return mimeTypes.TryGetValue(extension, out string mimeType) ? mimeType : "application/octet-stream";
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in GetMimeTypeFromFileName(string fileName): {ex.Message}");
                return "application/octet-stream";
            }
        }
        private void HandleBoxNativeSearchForDynamics(Entity responseEntity, BoxFolderRequest request)
        {
            try
            {
                // Use searchUnderFolderId if provided, otherwise default to _boxRootId
                string ancestorFolderId = !string.IsNullOrWhiteSpace(request.SearchUnderFolderId) ? request.SearchUnderFolderId : _boxRootId;

                _tracingService.Trace($"Performing Box native search for Dynamics: {request.SearchQuery}");
                _tracingService.Trace($"Search limited to ancestor folder: {ancestorFolderId}");

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");

                    // Box Search API endpoint with ancestor_folder_ids to limit search scope
                    var searchUrl = $"https://api.box.com/2.0/search?query={Uri.EscapeDataString(request.SearchQuery)}&ancestor_folder_ids={ancestorFolderId}&limit=200";

                    var searchResponse = httpClient.GetAsync(searchUrl).Result;
                    var searchContent = searchResponse.Content.ReadAsStringAsync().Result;

                    _tracingService.Trace($"Search response status: {searchResponse.StatusCode}");

                    if (searchResponse.IsSuccessStatusCode)
                    {
                        var searchResult = JsonConvert.DeserializeObject<BoxSearchResponse>(searchContent);

                        // Create search results collection
                        var searchResults = new EntityCollection();

                        if (searchResult?.Entries != null)
                        {
                            foreach (var item in searchResult.Entries)
                            {
                                Entity searchResultEntity = CreateBoxSearchResultEntity();
                                searchResultEntity.Attributes["id"] = item.Id;
                                searchResultEntity.Attributes["name"] = item.Name;
                                searchResultEntity.Attributes["type"] = item.Type;
                                searchResultEntity.Attributes["size"] = (int)(item.Size ?? 0);
                                searchResultEntity.Attributes["createdOn"] = item.CreatedAt;
                                searchResultEntity.Attributes["modifiedOn"] = item.ModifiedAt;
                                searchResultEntity.Attributes["contentType"] = item.Type == "file" ? GetMimeTypeFromFileName(item.Name) : null;
                                searchResultEntity.Attributes["parentFolder"] = item.Parent?.Name ?? "";
                                searchResultEntity.Attributes["path"] = GetItemPath(item.PathCollection?.Entries);
                                searchResultEntity.Attributes["relevanceScore"] = 0.0;
                                searchResultEntity.Attributes["aiInsights"] = "Standard Box search result";

                                searchResults.Entities.Add(searchResultEntity);
                            }
                        }

                        // Add search results to response entity
                        responseEntity.Attributes.Add("searchResults", searchResults);
                        responseEntity.Attributes.Add("searchQuery", request.SearchQuery);
                        responseEntity.Attributes.Add("searchType", "BoxNative");
                        responseEntity.Attributes.Add("totalSearchResults", searchResults.Entities.Count);
                        responseEntity.Attributes.Add("message", $"Box native search completed. Found {searchResults.Entities.Count} results.");
                        responseEntity.Attributes.Add("success", true);
                    }
                    else
                    {
                        throw new Exception($"Box search failed: {searchResponse.StatusCode} - {searchContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in Box native search for Dynamics: {ex.Message}");
                throw new Exception($"Box native search error: {ex.Message}", ex);
            }
        }

        private void HandleBoxAIEnhancedSearchForDynamics(Entity responseEntity, BoxFolderRequest request)
        {
            try
            {
                // Use searchUnderFolderId if provided, otherwise default to _boxRootId
                string ancestorFolderId = !string.IsNullOrWhiteSpace(request.SearchUnderFolderId) ? request.SearchUnderFolderId : _boxRootId;

                _tracingService.Trace($"Performing Box AI enhanced search for Dynamics: {request.SearchQuery}");
                _tracingService.Trace($"Search limited to ancestor folder: {ancestorFolderId}");

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");

                    // Enhanced search with content types and ancestor_folder_ids to limit search scope
                    var searchUrl = $"https://api.box.com/2.0/search?query={Uri.EscapeDataString(request.SearchQuery)}&ancestor_folder_ids={ancestorFolderId}&limit=200&content_types=name,description,file_content,comments,tags";

                    var searchResponse = httpClient.GetAsync(searchUrl).Result;
                    var searchContent = searchResponse.Content.ReadAsStringAsync().Result;

                    _tracingService.Trace($"AI search response status: {searchResponse.StatusCode}");

                    if (searchResponse.IsSuccessStatusCode)
                    {
                        var searchResult = JsonConvert.DeserializeObject<BoxSearchResponse>(searchContent);

                        // Create search results collection
                        var searchResults = new EntityCollection();

                        if (searchResult?.Entries != null)
                        {
                            var enhancedResults = new List<dynamic>();

                            foreach (var item in searchResult.Entries)
                            {
                                enhancedResults.Add(new
                                {
                                    Item = item,
                                    RelevanceScore = CalculateRelevanceScore(item, request.SearchQuery),
                                    AIInsights = GenerateAIInsights(item, request.SearchQuery)
                                });
                            }

                            // Sort by relevance score (descending)
                            enhancedResults = enhancedResults
                                .OrderByDescending(r => r.RelevanceScore)
                                .ToList();

                            foreach (var enhancedResult in enhancedResults)
                            {
                                var item = enhancedResult.Item;
                                Entity searchResultEntity = CreateBoxSearchResultEntity();
                                searchResultEntity.Attributes["id"] = item.Id;
                                searchResultEntity.Attributes["name"] = item.Name;
                                searchResultEntity.Attributes["type"] = item.Type;
                                searchResultEntity.Attributes["size"] = (int)(item.Size ?? 0);
                                searchResultEntity.Attributes["createdOn"] = item.CreatedAt;
                                searchResultEntity.Attributes["modifiedOn"] = item.ModifiedAt;
                                searchResultEntity.Attributes["contentType"] = item.Type == "file" ? GetMimeTypeFromFileName(item.Name) : null;
                                searchResultEntity.Attributes["parentFolder"] = item.Parent?.Name ?? "";
                                searchResultEntity.Attributes["path"] = GetItemPath(item.PathCollection?.Entries);
                                searchResultEntity.Attributes["relevanceScore"] = enhancedResult.RelevanceScore;
                                searchResultEntity.Attributes["aiInsights"] = enhancedResult.AIInsights;

                                searchResults.Entities.Add(searchResultEntity);
                            }
                        }

                        // Add search results to response entity
                        responseEntity.Attributes.Add("searchResults", searchResults);
                        responseEntity.Attributes.Add("searchQuery", request.SearchQuery);
                        responseEntity.Attributes.Add("searchType", "BoxAIEnhanced");
                        responseEntity.Attributes.Add("totalSearchResults", searchResults.Entities.Count);
                        responseEntity.Attributes.Add("message", $"Box AI enhanced search completed. Found {searchResults.Entities.Count} results with AI insights.");
                        responseEntity.Attributes.Add("success", true);
                    }
                    else
                    {
                        throw new Exception($"Box AI search failed: {searchResponse.StatusCode} - {searchContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in Box AI enhanced search for Dynamics: {ex.Message}");
                throw new Exception($"Box AI enhanced search error: {ex.Message}", ex);
            }
        }

        private Entity CreateBoxSearchResultEntity()
        {
            Entity entity = new Entity();
            // Initialize with default values to ensure all attributes exist
            entity.Attributes.Add("id", "");
            entity.Attributes.Add("name", "");
            entity.Attributes.Add("type", "");
            entity.Attributes.Add("size", 0);
            entity.Attributes.Add("createdOn", DateTime.MinValue);
            entity.Attributes.Add("modifiedOn", DateTime.MinValue);
            entity.Attributes.Add("contentType", "");
            entity.Attributes.Add("parentFolder", "");
            entity.Attributes.Add("path", "");
            entity.Attributes.Add("relevanceScore", 0.0);
            entity.Attributes.Add("aiInsights", "");
            return entity;
        }

        private string GetItemPath(List<BoxPathItem> pathEntries)
        {
            try
            {
                if (pathEntries == null || !pathEntries.Any())
                    return "/";

                return "/" + string.Join("/", pathEntries.Skip(1).Select(p => p.Name));
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in GetItemPath(List<BoxPathItem> pathEntries). Exception message: {ex.Message}");
                return "/";
            }
        }

        private double CalculateRelevanceScore(BoxSearchItem item, string searchQuery)
        {
            if (item?.Name == null || string.IsNullOrWhiteSpace(searchQuery))
                return 0.0;

            var score = 0.0;
            var query = searchQuery.ToLower();
            var name = item.Name.ToLower();

            // Exact match gets highest score
            if (name == query) score += 100.0;

            // Name starts with query
            if (name.StartsWith(query)) score += 50.0;

            // Name contains query
            if (name.Contains(query)) score += 25.0;

            // File type bonus for common document types
            if (item.Type == "file")
            {
                var extension = Path.GetExtension(name);
                if (new[] { ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx" }.Contains(extension))
                    score += 10.0;
            }

            // Recent modification bonus
            if (item.ModifiedAt.HasValue && item.ModifiedAt.Value > DateTime.Now.AddDays(-30))
                score += 5.0;

            return Math.Round(score, 2);
        }

        private string GenerateAIInsights(BoxSearchItem item, string searchQuery)
        {
            var insights = new List<string>();

            if (item?.Name != null)
            {
                var query = searchQuery.ToLower();
                var name = item.Name.ToLower();

                if (name.Contains(query))
                {
                    insights.Add($"Name matches search term '{searchQuery}'");
                }

                if (item.Type == "file")
                {
                    var extension = Path.GetExtension(name);
                    switch (extension.ToLower())
                    {
                        case ".pdf":
                            insights.Add("PDF document - may contain searchable text content");
                            break;
                        case ".doc":
                        case ".docx":
                            insights.Add("Word document - likely contains relevant text content");
                            break;
                        case ".ppt":
                        case ".pptx":
                            insights.Add("PowerPoint presentation - may contain relevant slides");
                            break;
                        case ".xls":
                        case ".xlsx":
                            insights.Add("Excel spreadsheet - may contain relevant data");
                            break;
                    }

                    // Size insights
                    if (item.Size.HasValue)
                    {
                        if (item.Size.Value > 10 * 1024 * 1024) // > 10MB
                            insights.Add("Large file - may contain comprehensive information");
                        else if (item.Size.Value < 1024) // < 1KB
                            insights.Add("Small file - may be a quick reference or note");
                    }
                }

                // Recent activity insights
                if (item.ModifiedAt.HasValue)
                {
                    var daysSinceModified = (DateTime.Now - item.ModifiedAt.Value).Days;
                    if (daysSinceModified <= 7)
                        insights.Add("Recently modified - likely contains current information");
                    else if (daysSinceModified <= 30)
                        insights.Add("Modified within the last month - fairly current");
                }
            }

            return insights.Any() ? string.Join("; ", insights) : "Standard search result";
        }
    }

    #region Data Models



    public class BoxCollaborator
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("login")]
        public string Login { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }
    }

    public class BoxFolderTreeItem
    {
        [JsonProperty("folderId")]
        public string FolderId { get; set; }

        [JsonProperty("folderName")]
        public string FolderName { get; set; }

        [JsonProperty("position")]
        public int Position { get; set; }

        [JsonProperty("fileCount")]
        public int FileCount { get; set; }

        [JsonProperty("folderCount")]
        public int FolderCount { get; set; }

        [JsonProperty("isRoot")]
        public bool IsRoot { get; set; }
    }

    public class FolderItemsResponse
    {
        [JsonProperty("entries")]
        public List<BoxItem> Entries { get; set; }

        [JsonProperty("total_count")]
        public int TotalCount { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }

        [JsonProperty("order")]
        public List<OrderBy> Order { get; set; }
    }

    public class BoxSearchResponse
    {
        [JsonProperty("entries")]
        public List<BoxSearchItem> Entries { get; set; }

        [JsonProperty("total_count")]
        public int TotalCount { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }
    }

    public class BoxSearchItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("size")]
        public long? Size { get; set; }

        [JsonProperty("created_at")]
        public DateTime? CreatedAt { get; set; }

        [JsonProperty("modified_at")]
        public DateTime? ModifiedAt { get; set; }

        [JsonProperty("parent")]
        public BoxParentItem Parent { get; set; }

        [JsonProperty("path_collection")]
        public BoxPathCollection PathCollection { get; set; }
    }

    public class BoxFolderSearchResponse
    {
        [JsonProperty("entries")]
        public List<BoxItem> Entries { get; set; }

        [JsonProperty("total_count")]
        public int TotalCount { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }
    }

    public class OrderBy
    {
        [JsonProperty("by")]
        public string By { get; set; }

        [JsonProperty("direction")]
        public string Direction { get; set; }
    }

    public class BoxItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("size")]
        public long? Size { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("modified_at")]
        public string ModifiedAt { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; }
    }

    public class BoxFileDetails
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("size")]
        public long? Size { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("modified_at")]
        public string ModifiedAt { get; set; }
    }

    public class BoxFileDetailsWithParent
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("size")]
        public long? Size { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("modified_at")]
        public string ModifiedAt { get; set; }

        [JsonProperty("parent")]
        public BoxParentFolder Parent { get; set; }
    }

    public class BoxUploadResponse
    {
        [JsonProperty("total_count")]
        public int TotalCount { get; set; }

        [JsonProperty("entries")]
        public List<BoxUploadedFile> Entries { get; set; } = new List<BoxUploadedFile>();
    }

    public class BoxUploadedFile
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }
    }

    public class BoxFolderCreationResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("modified_at")]
        public string ModifiedAt { get; set; }

        [JsonProperty("parent")]
        public BoxParentFolder Parent { get; set; }
    }

    public class BoxParentFolder
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class CollaboratorsResponse
    {
        [JsonProperty("entries")]
        public List<BoxCollaboration> Entries { get; set; } = new List<BoxCollaboration>();

        [JsonProperty("total_count")]
        public int TotalCount { get; set; }
    }

    public class BoxCollaboration
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("accessible_by")]
        public BoxUser AccessibleBy { get; set; }
    }

    public class BoxUser
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("login")]
        public string Login { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class BoxFolderDetails
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("parent")]
        public BoxParentFolder Parent { get; set; }

        [JsonProperty("item_collection")]
        public FolderItemsResponse ItemCollection { get; set; }
    }



    public class BoxFolderInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int FileCount { get; set; }
        public int FolderCount { get; set; }
    }

    public class BoxSearchResult
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("size")]
        public long? Size { get; set; }

        [JsonProperty("createdAt")]
        public string CreatedAt { get; set; }

        [JsonProperty("modifiedAt")]
        public string ModifiedAt { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("parentFolder")]
        public string ParentFolder { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("relevanceScore")]
        public double? RelevanceScore { get; set; }

        [JsonProperty("aiInsights")]
        public string AIInsights { get; set; }
    }





    public class BoxParentItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class BoxPathCollection
    {
        [JsonProperty("entries")]
        public List<BoxPathItem> Entries { get; set; }

        [JsonProperty("total_count")]
        public int TotalCount { get; set; }
    }

    public class BoxPathItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class BoxFileVersionsResponse
    {
        [JsonProperty("total_count")]
        public int TotalCount { get; set; }

        [JsonProperty("entries")]
        public List<BoxFileVersion> Entries { get; set; }
    }

    public class BoxFileVersion
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("size")]
        public long? Size { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("modified_at")]
        public string ModifiedAt { get; set; }

        [JsonProperty("modified_by")]
        public BoxUser ModifiedBy { get; set; }

        [JsonProperty("sha1")]
        public string Sha1 { get; set; }

        [JsonProperty("version_number")]
        public int? VersionNumber { get; set; }
    }

    public class BoxFolderRequest
    {
        [JsonProperty("itemType")]
        public string ItemType { get; set; }

        [JsonProperty("itemId")]
        public string ItemId { get; set; }

        [JsonProperty("itemOperation")]
        public string ItemOperation { get; set; }

        [JsonProperty("itemBoxId")]
        public string ItemBoxId { get; set; }

        [JsonProperty("folderId")]
        public string FolderId { get; set; }

        [JsonProperty("folderName")]
        public string FolderName { get; set; }

        [JsonProperty("fileName")]
        public string FileName { get; set; }

        [JsonProperty("fileFullPath")]
        public string FileFullPath { get; set; }

        [JsonProperty("fileOperation")]
        public string FileOperation { get; set; }

        [JsonProperty("fileContent")]
        public string FileContent { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("fileSize")]
        public long? FileSize { get; set; }

        [JsonProperty("dateCreated")]
        public DateTime? DateCreated { get; set; }

        [JsonProperty("lastModified")]
        public DateTime? LastModified { get; set; }

        [JsonProperty("requestCategory")]
        public string RequestCategory { get; set; }

        [JsonProperty("requestType")]
        public string RequestType { get; set; }

        [JsonProperty("searchQuery")]
        public string SearchQuery { get; set; }

        // Optional field to specify the ancestor folder for search scope
        [JsonProperty("searchUnderFolderId")]
        public string SearchUnderFolderId { get; set; }

        // New sorting properties
        [JsonProperty("sortByField")]
        public string SortByField { get; set; }

        [JsonProperty("sortDirection")]
        public string SortDirection { get; set; }

        // New pagination properties
        [JsonProperty("pageOffset")]
        public int? PageOffset { get; set; }

        [JsonProperty("pageLimit")]
        public int? PageLimit { get; set; }

        // New properties for folder identification
        [JsonProperty("ngoId")]
        public string NgoId { get; set; }

        [JsonProperty("itemName")]
        public string ItemName { get; set; }

        // Property for rename operations
        [JsonProperty("newItemName")]
        public string NewItemName { get; set; }

        // Property for uploader email (used to tag uploaded files)
        [JsonProperty("uploaderEmail")]
        public string UploaderEmail { get; set; }

        // Property for description tag (used to tag uploaded files with description)
        [JsonProperty("descriptionTag")]
        public string DescriptionTag { get; set; }

        // Property for file description (used to set description on uploaded files)
        [JsonProperty("description")]
        public string Description { get; set; }

        // Property for version upload on existing file (if true, check for existing file and upload version)
        [JsonProperty("versionUploadOnExistingFile")]
        public bool? VersionUploadOnExistingFile { get; set; }

        // Property for version ID (used when retrieving specific file version content)
        [JsonProperty("versionId")]
        public string VersionId { get; set; }

        // Property for custom root folder ID (used to limit folder tree traversal)
        [JsonProperty("rootId")]
        public string RootId { get; set; }
    }




    public class BoxTokenResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonProperty("token_type")]
        public string TokenType { get; set; }

        [JsonProperty("restricted_to")]
        public object RestrictedTo { get; set; }

        [JsonProperty("issued_token_type")]
        public string IssuedTokenType { get; set; }
    }

    /// <summary>
    /// Represents a folder with its contained files for allFolderFilesInSubTree operation
    /// </summary>
    public class BoxFolderWithFiles
    {
        public string FolderId { get; set; }
        public string FolderName { get; set; }
        public List<BoxFileWithFolderInfo> Files { get; set; }
    }


    public class BoxFileWithFolderInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Size { get; set; }
        public string CreatedOn { get; set; }
        public string ModifiedOn { get; set; }
        public string ContentType { get; set; }
        public string FolderId { get; set; }
        public string FolderName { get; set; }
        public string SubFolderTree { get; set; }
        public List<string> Tags { get; set; }
    }

    #endregion
}
