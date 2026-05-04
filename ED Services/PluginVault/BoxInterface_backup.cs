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

namespace EDServices
{
    public class BoxInterface : IPlugin
    {
        private const string BoxAccessToken = "REDACTED";
        private static readonly TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        public void Execute(IServiceProvider serviceProvider)
        {
            ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            tracingService.Trace($"{GetPSTTime()}: Starting - EDServices.BoxInterface");
            tracingService.Trace(new string('-', 100));

            try
            {
                // Get the input parameter (Generic Entity)
                //if (!context.InputParameters.Contains("input") || context.InputParameters["input"] == null)
                //{
                //    throw new InvalidPluginExecutionException("Input parameter 'input' is required.");
                //}

                Entity tsRequest = (Entity)context.InputParameters["ts_request"];
                tracingService.Trace($"{GetPSTTime()}: Input entity received");

                // Convert input entity to JSON (removing @odata.type elements)
                string cleanJson = ConvertExpandoToJson(tsRequest);
                tracingService.Trace($"{GetPSTTime()}: Converted input to clean JSON: {cleanJson}");

                // Call BoxApiService using direct Entity method for optimal performance
                Entity tsResponse = CallBoxApiServiceDirect(cleanJson, tracingService);
                tracingService.Trace($"{GetPSTTime()}: Received Entity response directly from BoxApiService");

                // Set the output parameter
                context.OutputParameters["ts_response"] = tsResponse;

                tracingService.Trace($"{GetPSTTime()}: BoxInterface completed successfully");
            }
            catch (Exception ex)
            {
                tracingService.Trace($"{GetPSTTime()}: Error in BoxInterface: {ex.Message}");
                tracingService.Trace($"{GetPSTTime()}: Stack trace: {ex.StackTrace}");
                //throw new InvalidPluginExecutionException($"BoxInterface error: {ex.Message}", ex);
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
                                IDictionary<string, Object>  expandoListItem = (JsonConvert.DeserializeObject<ExpandoObject>(JsonConvert.SerializeObject(listElement))) as IDictionary<string, Object>;
                               


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

        private string CallBoxApiService(string jsonRequest, ITracingService tracingService)
        {
            try
            {
                tracingService.Trace($"{GetPSTTime()}: Creating BoxApiService instance");

                // Create BoxApiService with hardcoded token
                var boxService = new EmbeddedBoxApiService(BoxAccessToken, tracingService);
                
                tracingService.Trace($"{GetPSTTime()}: Calling BoxApiService.ProcessBoxRequestForDynamics");
                
                // Call the Dynamics-optimized service that returns Entity directly
                var resultEntity = boxService.ProcessBoxRequestForDynamics(jsonRequest);
                
                // Convert the Entity result to JSON for the response
                string resultJson = ConvertEntityToJson(resultEntity, tracingService);

                tracingService.Trace($"{GetPSTTime()}: result: {resultJson}");
                tracingService.Trace($"{GetPSTTime()}: BoxApiService call completed");
                
                return resultJson;
            }
            catch (Exception ex)
            {
                tracingService.Trace($"{GetPSTTime()}: Error calling BoxApiService: {ex.Message}");
                return null;
                //throw new Exception($"BoxApiService call failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Converts an Entity (with EntityCollections) back to JSON format for response compatibility
        /// This ensures we can return properly structured data while using Entity objects internally
        /// </summary>
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

        /// <summary>
        /// Direct Entity-to-Entity method for optimal Dynamics 365 performance
        /// Bypasses JSON conversion entirely for maximum efficiency
        /// </summary>
        private Entity CallBoxApiServiceDirect(string jsonRequest, ITracingService tracingService)
        {
            try
            {
                tracingService.Trace($"{GetPSTTime()}: Creating BoxApiService instance for direct Entity response");

                // Create BoxApiService with hardcoded token
                var boxService = new EmbeddedBoxApiService(BoxAccessToken, tracingService);
                
                tracingService.Trace($"{GetPSTTime()}: Calling BoxApiService.ProcessBoxRequestForDynamics directly");
                
                // Call the Dynamics-optimized service that returns Entity directly
                var resultEntity = boxService.ProcessBoxRequestForDynamics(jsonRequest);

                tracingService.Trace($"{GetPSTTime()}: BoxApiService direct call completed");
                
                return resultEntity;
            }
            catch (Exception ex)
            {
                tracingService.Trace($"{GetPSTTime()}: Error calling BoxApiService direct: {ex.Message}");
                
                // Return error entity
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
        }

        /// <summary>
        /// Converts a Dynamics Entity (with @odata.type elements) to clean JSON
        /// Translation of Python convert_expando_to_json function
        /// </summary>
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

        /// <summary>
        /// Converts expando object to clean dictionary, removing @odata keys
        /// Based on the convertExpandooXml pattern but for JSON
        /// </summary>
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

        /// <summary>
        /// Converts JSON response from Box service to a clean Dynamics Entity
        /// Creates Entity objects without @odata annotations
        /// </summary>
        private Entity ConvertJsonToExpando(string json)
        {
            var jsonObject = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            var entity = new Entity();
            
            // No need to set @odata.type - just convert the data directly
            ConvertJsonObjectToEntity(jsonObject, entity.Attributes);
            
            return entity;
        }

        private void ConvertJsonObjectToEntity(object obj, AttributeCollection attributes)
        {
            if (obj is JObject jObject)
            {
                foreach (var property in jObject.Properties())
                {
                    string key = property.Name;
                    object value = ConvertJTokenToObject(property.Value);

                    if (value == null)
                        continue;

                    ProcessEntityValue(key, value, attributes);
                }
            }
            else if (obj is Dictionary<string, object> dict)
            {
                foreach (var kvp in dict)
                {
                    if (kvp.Value == null)
                        continue;

                    ProcessEntityValue(kvp.Key, kvp.Value, attributes);
                }
            }
        }

        private void ProcessEntityValue(string key, object value, AttributeCollection attributes)
        {
            if (value == null)
                return;

            if (value is JObject jObj)
            {
                // Create a nested Entity without @odata annotations
                var nestedEntity = new Entity();
                ConvertJsonObjectToEntity(jObj, nestedEntity.Attributes);
                attributes[key] = nestedEntity;
            }
            else if (value is JArray jArray)
            {
                var convertedArray = new List<object>();

                foreach (var item in jArray)
                {
                    if (item is JObject itemObj)
                    {
                        // Create nested Entity for each array item
                        var nestedEntity = new Entity();
                        ConvertJsonObjectToEntity(itemObj, nestedEntity.Attributes);
                        convertedArray.Add(nestedEntity);
                    }
                    else
                    {
                        convertedArray.Add(ConvertJTokenToObject(item));
                    }
                }
                
                // Store as EntityCollection if it contains Entity objects, otherwise as array
                if (convertedArray.Count > 0 && convertedArray[0] is Entity)
                {
                    var entityCollection = new EntityCollection();
                    foreach (var entity in convertedArray.Cast<Entity>())
                    {
                        entityCollection.Entities.Add(entity);
                    }
                    attributes[key] = entityCollection;
                }
                else
                {
                    attributes[key] = convertedArray.ToArray();
                }
            }
            else if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var convertedArray = new List<object>();
                bool hasEntities = false;
                
                foreach (var item in enumerable)
                {
                    if (item is Dictionary<string, object> itemDict)
                    {
                        var nestedEntity = new Entity();
                        ConvertJsonObjectToEntity(itemDict, nestedEntity.Attributes);
                        convertedArray.Add(nestedEntity);
                        hasEntities = true;
                    }
                    else
                    {
                        convertedArray.Add(item);
                    }
                }
                
                // Store as EntityCollection if it contains Entity objects
                if (hasEntities && convertedArray.Count > 0)
                {
                    var entityCollection = new EntityCollection();
                    foreach (var entity in convertedArray.Where(x => x is Entity).Cast<Entity>())
                    {
                        entityCollection.Entities.Add(entity);
                    }
                    attributes[key] = entityCollection;
                }
                else
                {
                    attributes[key] = convertedArray.ToArray();
                }
            }
            else
            {
                // Store simple values directly
                attributes[key] = value;
            }
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

    /// <summary>
    /// Embedded simplified BoxApiService for plugin use
    /// </summary>
    internal class EmbeddedBoxApiService
    {
        private readonly string _accessToken;
        private readonly ITracingService _tracingService;
        private readonly System.Net.Http.HttpClient _httpClient;
        private const string BoxApiUrl = "https://api.box.com/2.0";

        public EmbeddedBoxApiService(string accessToken, ITracingService tracingService)
        {
            _accessToken = accessToken;
            _tracingService = tracingService;
            _httpClient = new System.Net.Http.HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "BoxInterface/1.0");
        }

        public string AnalyzeFolderAsync(string jsonRequest)
        {
            try
            {
                _tracingService.Trace("Parsing JSON request");
                var request = JsonConvert.DeserializeObject<BoxFolderRequest>(jsonRequest);
                
                if (request == null)
                    throw new Exception("Invalid request format");

                var response = new BoxFolderAnalysisResponse
                {
                    ItemType = request.ItemType,
                    RequestedAt = DateTime.UtcNow
                };

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
                            HandleBoxNativeSearch(response, request);
                        }
                        else if (requestType == "boxaienhanced")
                        {
                            HandleBoxAIEnhancedSearch(response, request);
                        }
                        else
                        {
                            throw new Exception("RequestType must be either 'BoxNative' or 'BoxAIEnhanced' for search operations");
                        }
                    }
                    else
                    {
                        // Handle existing document/folder operations (backward compatibility)
                        var itemType = request.ItemType?.ToLower();
                        
                        if (itemType == "file")
                        {
                            var fileOperation = request.FileOperation?.ToLower();
                            
                            if (fileOperation == "upload")
                            {
                                // Handle file upload
                                if (string.IsNullOrWhiteSpace(request.FileFullPath))
                                    throw new Exception("FileFullPath is required for upload operations");
                                
                                if (string.IsNullOrWhiteSpace(request.FolderId))
                                    throw new Exception("FolderId is required for upload operations");
                                
                                HandleFileUpload(response, request);
                            }
                            else
                            {
                                // Handle file retrieval
                                var itemId = GetItemId(request);
                                if (string.IsNullOrWhiteSpace(itemId))
                                    throw new Exception("ItemId is required for file retrieval operations");
                                
                                response.FolderId = itemId;
                                AnalyzeFileItem(response, itemId);
                            }
                        }
                        else if (itemType == "folder")
                        {
                            // Handle folder analysis
                            var itemId = GetItemId(request);
                            if (string.IsNullOrWhiteSpace(itemId))
                                throw new Exception("ItemId is required for folder operations");
                            
                            response.FolderId = itemId;
                            AnalyzeFolderItem(response, itemId);
                        }
                        else
                        {
                            throw new Exception("ItemType must be either 'Folder' or 'File'");
                        }
                    }

                    response.Success = true;
                    if (string.IsNullOrWhiteSpace(response.Message))
                    {
                        response.Message = "Operation completed successfully";
                    }
                }
                catch (Exception ex)
                {
                    response.Success = false;
                    response.Message = $"Error processing {request.ItemType}: {ex.Message}";
                    response.Error = ex.ToString();
                }

                return JsonConvert.SerializeObject(response, Formatting.Indented);
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in AnalyzeFolderAsync: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Dynamics 365 optimized version that returns Entity objects directly instead of JSON
        /// This method builds Entity structures from the start for better Dynamics integration
        /// </summary>
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
                if (!responseEntity.Attributes.Contains("requestedAt"))
                {
                    responseEntity.Attributes.Add("requestedAt", DateTime.UtcNow);
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
                    else
                    {
                        // Handle existing document/folder operations (backward compatibility)
                        var itemType = request.ItemType?.ToLower();
                        
                        if (itemType == "file")
                        {
                            var fileOperation = request.FileOperation?.ToLower();
                            
                            if (fileOperation == "upload")
                            {
                                // Handle file upload
                                if (string.IsNullOrWhiteSpace(request.FileFullPath))
                                    throw new Exception("FileFullPath is required for upload operations");
                                
                                if (string.IsNullOrWhiteSpace(request.FolderId))
                                    throw new Exception("FolderId is required for upload operations");
                                
                                HandleFileUploadForDynamics(responseEntity, request);
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
                            // Handle folder analysis
                            var itemId = GetItemId(request);
                            if (string.IsNullOrWhiteSpace(itemId))
                                throw new Exception("ItemId is required for folder operations");
                            
                            if (!responseEntity.Attributes.Contains("folderId"))
                            {
                                responseEntity.Attributes.Add("folderId", itemId);
                            }
                            AnalyzeFolderItemForDynamics(responseEntity, itemId);
                        }
                        else
                        {
                            throw new Exception("ItemType must be either 'Folder' or 'File'");
                        }
                    }
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
                            // Handle folder analysis
                            var itemId = GetItemId(request);
                            if (string.IsNullOrWhiteSpace(itemId))
                                throw new Exception("ItemId is required for folder operations");
                            
                            if (!responseEntity.Attributes.Contains("folderId"))
                            {
                                responseEntity.Attributes.Add("folderId", itemId);
                            }
                            AnalyzeFolderItemForDynamics(responseEntity, itemId);
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

                // Upload the file to Box
                var uploadedFileId = UploadFileToBox(request.FolderId, request.FileName, fileBytes, request.ContentType);
                
                _tracingService.Trace($"File uploaded successfully with ID: {uploadedFileId}");

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
                    responseEntity.Attributes.Add("fileOperation", "upload");
                }
                if (!responseEntity.Attributes.Contains("message"))
                {
                    responseEntity.Attributes.Add("message", $"File '{request.FileName}' uploaded successfully to Box (Size: {request.FileSize:N0} bytes, Type: {request.ContentType ?? "unknown"})");
                }
                
                // Create uploaded file entity and add to collection
                Entity uploadedFileEntity = CreateBoxFolderItemEntity();
                uploadedFileEntity.Attributes["id"] = uploadedFileId;
                uploadedFileEntity.Attributes["name"] = request.FileName;
                uploadedFileEntity.Attributes["type"] = "file";
                uploadedFileEntity.Attributes["size"] = (int)request.FileSize.Value;
                uploadedFileEntity.Attributes["createdAt"] = request.DateCreated?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                uploadedFileEntity.Attributes["modifiedAt"] = request.LastModified?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
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
                
                // Create file entity
                Entity fileEntity = CreateBoxFolderItemEntity();
                fileEntity.Attributes["id"] = fileData.Id;
                fileEntity.Attributes["name"] = fileData.Name;
                fileEntity.Attributes["type"] = fileData.Type;
                fileEntity.Attributes["size"] = (int)(fileData.Size ?? 0);
                fileEntity.Attributes["createdAt"] = fileData.CreatedAt;
                fileEntity.Attributes["modifiedAt"] = fileData.ModifiedAt;
                fileEntity.Attributes["contentType"] = GetMimeTypeFromFileName(fileData.Name);

                EntityCollection folderContents = (EntityCollection)responseEntity.Attributes["folderContents"];
                folderContents.Entities.Add(fileEntity);

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

        private void AnalyzeFolderItemForDynamics(Entity responseEntity, string folderId)
        {
            _tracingService.Trace($"Analyzing folder for Dynamics: {folderId}");

            // Get folder contents
            var folderUrl = $"{BoxApiUrl}/folders/{folderId}/items?limit=100&fields=id,name,type,size,created_at,modified_at";
            var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
            var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            _tracingService.Trace($"folderContent: {folderContent}");

            if (folderResponse.IsSuccessStatusCode)
            {
                var folderData = JsonConvert.DeserializeObject<FolderItemsResponse>(folderContent);
                EntityCollection folderContents = (EntityCollection)responseEntity.Attributes["folderContents"];

                foreach (var entry in folderData.Entries ?? new List<BoxItem>())
                {
                    Entity folderItemEntity = CreateBoxFolderItemEntity();
                    folderItemEntity.Attributes["id"] = entry.Id;
                    folderItemEntity.Attributes["name"] = entry.Name;
                    folderItemEntity.Attributes["type"] = entry.Type;
                    folderItemEntity.Attributes["size"] = (int)(entry.Size ?? 0);
                    folderItemEntity.Attributes["createdAt"] = entry.CreatedAt;
                    folderItemEntity.Attributes["modifiedAt"] = entry.ModifiedAt;
                    folderItemEntity.Attributes["contentType"] = entry.Type == "file" ? GetMimeTypeFromFileName(entry.Name) : null;

                    folderContents.Entities.Add(folderItemEntity);
                }
            }
            else
            {
                throw new Exception($"Failed to get folder contents: {folderResponse.StatusCode} - {folderContent}");
            }

            // Get collaborators (handle root folder special case)
            GetFolderCollaboratorsForDynamics(responseEntity, folderId);

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
                
                // Traverse up the folder hierarchy
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
                        
                        // Move to parent
                        currentFolderId = folderDetails.Parent?.Id;
                    }
                    else
                    {
                        _tracingService.Trace($"Failed to get folder details for {currentFolderId}: {folderResponse.StatusCode}");
                        break;
                    }
                    
                    // Safety check to prevent infinite loops
                    if (position > 20) break;
                    position++;
                }
                
                // Add root folder (position 0)
                Entity rootFolderTreeEntity = CreateBoxFolderTreeItemEntity();
                rootFolderTreeEntity.Attributes["folderId"] = "0";
                rootFolderTreeEntity.Attributes["folderName"] = "Root";
                rootFolderTreeEntity.Attributes["position"] = 0;
                rootFolderTreeEntity.Attributes["fileCount"] = 0; // Root file count is complex to calculate
                rootFolderTreeEntity.Attributes["folderCount"] = 0; // Root folder count is complex to calculate
                rootFolderTreeEntity.Attributes["isRoot"] = true;
                
                folderTreeCollection.Entities.Add(rootFolderTreeEntity);
                
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

        // Entity factory methods for consistent Entity creation
        private Entity CreateBoxFolderItemEntity()
        {
            Entity entity = new Entity();
            // Initialize with default values to ensure all attributes exist
            entity.Attributes.Add("id", "");
            entity.Attributes.Add("name", "");
            entity.Attributes.Add("type", "");
            entity.Attributes.Add("size", 0);
            entity.Attributes.Add("createdAt", "");
            entity.Attributes.Add("modifiedAt", "");
            entity.Attributes.Add("contentType", "");
            return entity;
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

        private string GetItemId(BoxFolderRequest request)
        {
            return !string.IsNullOrWhiteSpace(request.ItemId) ? request.ItemId : request.ItemBoxId;
        }

        private void HandleFileUpload(BoxFolderAnalysisResponse response, BoxFolderRequest request)
        {
            _tracingService.Trace($"Starting file upload for: {request.FileName}");
            
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

                // Upload the file to Box
                var uploadedFileId = UploadFileToBox(request.FolderId, request.FileName, fileBytes, request.ContentType);
                
                _tracingService.Trace($"File uploaded successfully with ID: {uploadedFileId}");

                // Set response details
                response.ItemId = uploadedFileId;
                response.FolderId = request.FolderId;
                response.FileOperation = "upload";
                response.Message = $"File '{request.FileName}' uploaded successfully to Box (Size: {request.FileSize:N0} bytes, Type: {request.ContentType ?? "unknown"})";
                
                // Add uploaded file to response
                response.FolderContents = new List<BoxFolderItem>
                {
                    new BoxFolderItem
                    {
                        Id = uploadedFileId,
                        Name = request.FileName,
                        Type = "file",
                        Size = request.FileSize.Value,
                        CreatedAt = request.DateCreated?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        ModifiedAt = request.LastModified?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        ContentType = request.ContentType ?? "application/octet-stream"
                    }
                };

                _tracingService.Trace($"File upload completed successfully");
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in file upload: {ex.Message}");
                throw new Exception($"Failed to upload file: {ex.Message}", ex);
            }
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

        private void AnalyzeFileItem(BoxFolderAnalysisResponse response, string fileId)
        {
            _tracingService.Trace($"Analyzing file: {fileId}");
            
            // Simplified file analysis - get basic file info
            var fileUrl = $"{BoxApiUrl}/files/{fileId}?fields=id,name,type,size,created_at,modified_at";
            var fileResponse = _httpClient.GetAsync(fileUrl).GetAwaiter().GetResult();
            var fileContent = fileResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            
            if (fileResponse.IsSuccessStatusCode)
            {
                var fileData = JsonConvert.DeserializeObject<BoxFileDetails>(fileContent);
                response.FolderContents = new List<BoxFolderItem>
                {
                    new BoxFolderItem
                    {
                        Id = fileData.Id,
                        Name = fileData.Name,
                        Type = fileData.Type,
                        Size = fileData.Size ?? 0,
                        CreatedAt = fileData.CreatedAt,
                        ModifiedAt = fileData.ModifiedAt,
                        ContentType = "application/octet-stream" // Simplified
                    }
                };
            }
            else
            {
                throw new Exception($"Failed to get file details: {fileResponse.StatusCode} - {fileContent}");
            }
        }

        private void AnalyzeFolderItem(BoxFolderAnalysisResponse response, string folderId)
        {
            _tracingService.Trace($"Analyzing folder: {folderId}");

            // Get folder contents
            var folderUrl = $"{BoxApiUrl}/folders/{folderId}/items?limit=100&fields=id,name,type,size,created_at,modified_at";
            var folderResponse = _httpClient.GetAsync(folderUrl).GetAwaiter().GetResult();
            var folderContent = folderResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            _tracingService.Trace($"folderContent: {folderContent}");


            if (folderResponse.IsSuccessStatusCode)
            {
                var folderData = JsonConvert.DeserializeObject<FolderItemsResponse>(folderContent);
                response.FolderContents = new List<BoxFolderItem>();

                foreach (var entry in folderData.Entries ?? new List<BoxItem>())
                {
                    response.FolderContents.Add(new BoxFolderItem
                    {
                        Id = entry.Id,
                        Name = entry.Name,
                        Type = entry.Type,
                        Size = entry.Size ?? 0,
                        CreatedAt = entry.CreatedAt,
                        ModifiedAt = entry.ModifiedAt,
                        ContentType = entry.Type == "file" ? GetMimeTypeFromFileName(entry.Name) : null
                    });
                }
            }
            else
            {
                throw new Exception($"Failed to get folder contents: {folderResponse.StatusCode} - {folderContent}");
            }

            // Get collaborators (handle root folder special case)
            GetFolderCollaborators(response, folderId);

            // Get folder tree path
            GetFolderTreePath(response, folderId);
            
        }

        private void GetFolderCollaborators(BoxFolderAnalysisResponse response, string folderId)
        {
            _tracingService.Trace($"Getting collaborators for folder: {folderId}");
            
            response.Collaborators = new List<BoxCollaborator>();
            
            // Handle root folder special case
            if (folderId == "0")
            {
                response.Collaborators.Add(new BoxCollaborator
                {
                    Id = "root",
                    Name = "Root Folder",
                    Type = "folder",
                    Role = "owner",
                    Status = "root_folder_no_collaborations"
                });
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
                            response.Collaborators.Add(new BoxCollaborator
                            {
                                Id = collab.AccessibleBy.Id,
                                Name = collab.AccessibleBy.Name,
                                Login = collab.AccessibleBy.Login,
                                Type = collab.AccessibleBy.Type,
                                Role = collab.Role,
                                Status = collab.Status
                            });
                        }
                    }
                }
                else
                {
                    _tracingService.Trace($"Failed to get collaborators: {collaboratorsResponse.StatusCode} - {collaboratorsContent}");
                    // Add a placeholder indicating no collaborators could be retrieved
                    response.Collaborators.Add(new BoxCollaborator
                    {
                        Id = "unknown",
                        Name = "Unable to retrieve collaborators",
                        Role = "unknown",
                        Status = "error"
                    });
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error getting collaborators: {ex.Message}");
                response.Collaborators.Add(new BoxCollaborator
                {
                    Id = "error",
                    Name = $"Error retrieving collaborators: {ex.Message}",
                    Role = "error",
                    Status = "error"
                });
            }
        }

        private void GetFolderTreePath(BoxFolderAnalysisResponse response, string folderId)
        {
            _tracingService.Trace($"Getting folder tree path for folder: {folderId}");
            
            response.FolderTree = new List<BoxFolderTreeItem>();
            
            try
            {
                // Get folder details to build the path
                var folderPath = new List<BoxFolderInfo>();
                string currentFolderId = folderId;
                int position = 0;
                
                // Traverse up the folder hierarchy
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
                        
                        // Move to parent
                        currentFolderId = folderDetails.Parent?.Id;
                    }
                    else
                    {
                        _tracingService.Trace($"Failed to get folder details for {currentFolderId}: {folderResponse.StatusCode}");
                        break;
                    }
                    
                    // Safety check to prevent infinite loops
                    if (position > 20) break;
                    position++;
                }
                
                // Add root folder (position 0)
                response.FolderTree.Add(new BoxFolderTreeItem
                {
                    FolderId = "0",
                    FolderName = "Root",
                    Position = 0,
                    FileCount = 0, // Root file count is complex to calculate
                    FolderCount = 0, // Root folder count is complex to calculate
                    IsRoot = true
                });
                
                // Add the path folders with correct positions
                for (int i = 0; i < folderPath.Count; i++)
                {
                    var folder = folderPath[i];
                    response.FolderTree.Add(new BoxFolderTreeItem
                    {
                        FolderId = folder.Id,
                        FolderName = folder.Name,
                        Position = i + 1, // Root is 0, so start from 1
                        FileCount = folder.FileCount,
                        FolderCount = folder.FolderCount,
                        IsRoot = false
                    });
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error getting folder tree: {ex.Message}");
                // Add error placeholder
                response.FolderTree.Add(new BoxFolderTreeItem
                {
                    FolderId = folderId,
                    FolderName = $"Error: {ex.Message}",
                    Position = -1,
                    FileCount = 0,
                    FolderCount = 0,
                    IsRoot = false
                });
            }
        }

        private string GetMimeTypeFromFileName(string fileName)
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

        private void HandleBoxNativeSearch(BoxFolderAnalysisResponse response, BoxFolderRequest request)
        {
            try
            {
                _tracingService.Trace($"Performing Box native search for query: {request.SearchQuery}");

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {DEVELOPER_TOKEN}");

                    // Box Search API endpoint
                    var searchUrl = $"https://api.box.com/2.0/search?query={Uri.EscapeDataString(request.SearchQuery)}&type=file,folder&limit=200";

                    var searchResponse = httpClient.GetAsync(searchUrl).Result;
                    var searchContent = searchResponse.Content.ReadAsStringAsync().Result;

                    _tracingService.Trace($"Search response status: {searchResponse.StatusCode}");

                    if (searchResponse.IsSuccessStatusCode)
                    {
                        var searchResult = JsonConvert.DeserializeObject<BoxSearchResponse>(searchContent);

                        if (searchResult?.Entries != null)
                        {
                            foreach (var item in searchResult.Entries)
                            {
                                response.SearchResults.Add(new BoxSearchResult
                                {
                                    Id = item.Id,
                                    Name = item.Name,
                                    Type = item.Type,
                                    Size = item.Size,
                                    CreatedAt = item.CreatedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                    ModifiedAt = item.ModifiedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                    ContentType = item.Type == "file" ? GetMimeTypeFromFileName(item.Name) : null,
                                    ParentFolder = item.Parent?.Name,
                                    Path = GetItemPath(item.PathCollection?.Entries)
                                });
                            }
                        }

                        response.SearchQuery = request.SearchQuery;
                        response.SearchType = "BoxNative";
                        response.TotalSearchResults = response.SearchResults.Count;
                        response.Message = $"Box native search completed. Found {response.TotalSearchResults} results.";
                    }
                    else
                    {
                        throw new Exception($"Box search failed: {searchResponse.StatusCode} - {searchContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in Box native search: {ex.Message}");
                throw new Exception($"Box native search error: {ex.Message}", ex);
            }
        }

        private void HandleBoxAIEnhancedSearch(BoxFolderAnalysisResponse response, BoxFolderRequest request)
        {
            try
            {
                _tracingService.Trace($"Performing Box AI enhanced search for query: {request.SearchQuery}");

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {DEVELOPER_TOKEN}");

                    // First perform regular search
                    var searchUrl = $"https://api.box.com/2.0/search?query={Uri.EscapeDataString(request.SearchQuery)}&type=file,folder&limit=200&content_types=name,description,file_content,comments,tags";

                    var searchResponse = httpClient.GetAsync(searchUrl).Result;
                    var searchContent = searchResponse.Content.ReadAsStringAsync().Result;

                    _tracingService.Trace($"AI search response status: {searchResponse.StatusCode}");

                    if (searchResponse.IsSuccessStatusCode)
                    {
                        var searchResult = JsonConvert.DeserializeObject<BoxSearchResponse>(searchContent);

                        if (searchResult?.Entries != null)
                        {
                            foreach (var item in searchResult.Entries)
                            {
                                var searchResultItem = new BoxSearchResult
                                {
                                    Id = item.Id,
                                    Name = item.Name,
                                    Type = item.Type,
                                    Size = item.Size,
                                    CreatedAt = item.CreatedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                    ModifiedAt = item.ModifiedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                    ContentType = item.Type == "file" ? GetMimeTypeFromFileName(item.Name) : null,
                                    ParentFolder = item.Parent?.Name,
                                    Path = GetItemPath(item.PathCollection?.Entries),
                                    RelevanceScore = CalculateRelevanceScore(item, request.SearchQuery),
                                    AIInsights = GenerateAIInsights(item, request.SearchQuery)
                                };

                                response.SearchResults.Add(searchResultItem);
                            }

                            // Sort by relevance score (descending)
                            response.SearchResults = response.SearchResults
                                .OrderByDescending(r => r.RelevanceScore ?? 0)
                                .ToList();
                        }

                        response.SearchQuery = request.SearchQuery;
                        response.SearchType = "BoxAIEnhanced";
                        response.TotalSearchResults = response.SearchResults.Count;
                        response.Message = $"Box AI enhanced search completed. Found {response.TotalSearchResults} results with AI insights.";
                    }
                    else
                    {
                        throw new Exception($"Box AI search failed: {searchResponse.StatusCode} - {searchContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                _tracingService.Trace($"Error in Box AI enhanced search: {ex.Message}");
                throw new Exception($"Box AI enhanced search error: {ex.Message}", ex);
            }
        }

        private string GetItemPath(List<BoxPathItem> pathEntries)
        {
            if (pathEntries == null || !pathEntries.Any())
                return "/";

            return "/" + string.Join("/", pathEntries.Skip(1).Select(p => p.Name));
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

        private void HandleBoxNativeSearchForDynamics(Entity responseEntity, BoxFolderRequest request)
        {
            try
            {
                _tracingService.Trace($"Performing Box native search for Dynamics: {request.SearchQuery}");

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {DEVELOPER_TOKEN}");

                    // Box Search API endpoint
                    var searchUrl = $"https://api.box.com/2.0/search?query={Uri.EscapeDataString(request.SearchQuery)}&type=file,folder&limit=200";

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
                                searchResultEntity.Attributes["createdAt"] = item.CreatedAt;
                                searchResultEntity.Attributes["modifiedAt"] = item.ModifiedAt;
                                searchResultEntity.Attributes["contentType"] = item.Type == "file" ? GetMimeTypeFromFileName(item.Name) : null;
                                searchResultEntity.Attributes["parentFolder"] = item.Parent?.Name ?? "";
                                searchResultEntity.Attributes["path"] = GetItemPath(item.PathCollection?.Entries);
                                searchResultEntity.Attributes["relevanceScore"] = 0.0; // Basic search doesn't calculate relevance
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
                _tracingService.Trace($"Performing Box AI enhanced search for Dynamics: {request.SearchQuery}");

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {DEVELOPER_TOKEN}");

                    // Enhanced search with content types
                    var searchUrl = $"https://api.box.com/2.0/search?query={Uri.EscapeDataString(request.SearchQuery)}&type=file,folder&limit=200&content_types=name,description,file_content,comments,tags";

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
                                searchResultEntity.Attributes["createdAt"] = item.CreatedAt;
                                searchResultEntity.Attributes["modifiedAt"] = item.ModifiedAt;
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
            entity.Attributes.Add("createdAt", DateTime.MinValue);
            entity.Attributes.Add("modifiedAt", DateTime.MinValue);
            entity.Attributes.Add("contentType", "");
            entity.Attributes.Add("parentFolder", "");
            entity.Attributes.Add("path", "");
            entity.Attributes.Add("relevanceScore", 0.0);
            entity.Attributes.Add("aiInsights", "");
            return entity;
        }
    }

    #region Data Models
    public class BoxFolderRequest
    {
        [JsonProperty("itemType")]
        public string ItemType { get; set; }

        [JsonProperty("itemId")]
        public string ItemId { get; set; }

        [JsonProperty("itemBoxId")]
        public string ItemBoxId { get; set; }

        [JsonProperty("fileOperation")]
        public string FileOperation { get; set; }

        [JsonProperty("fileFullPath")]
        public string FileFullPath { get; set; }

        [JsonProperty("folderId")]
        public string FolderId { get; set; }

        // New request category and type for enhanced API structure
        [JsonProperty("requestCategory")]
        public string RequestCategory { get; set; } // "DocumentFolder" or "SearchService"

        [JsonProperty("requestType")]
        public string RequestType { get; set; } // "BoxNative", "BoxAIEnhanced", etc.

        // Search query parameter
        [JsonProperty("searchQuery")]
        public string SearchQuery { get; set; }

        // New file upload properties
        [JsonProperty("fileContent")]
        public string FileContent { get; set; } // Base64 encoded file content

        [JsonProperty("contentType")]
        public string ContentType { get; set; } // MIME type of the file

        [JsonProperty("fileName")]
        public string FileName { get; set; } // Name of the file

        [JsonProperty("dateCreated")]
        public DateTime? DateCreated { get; set; } // Original creation date of the file

        [JsonProperty("fileSize")]
        public long? FileSize { get; set; } // Size of the file in bytes

        [JsonProperty("lastModified")]
        public DateTime? LastModified { get; set; } // Last modification date
    }

    public class BoxFolderAnalysisResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("folderId")]
        public string FolderId { get; set; }

        [JsonProperty("itemType")]
        public string ItemType { get; set; }

        [JsonProperty("requestedAt")]
        public DateTime RequestedAt { get; set; }

        [JsonProperty("itemId")]
        public string ItemId { get; set; }

        [JsonProperty("fileOperation")]
        public string FileOperation { get; set; }

        [JsonProperty("folderContents")]
        public List<BoxFolderItem> FolderContents { get; set; } = new List<BoxFolderItem>();

        [JsonProperty("collaborators")]
        public List<BoxCollaborator> Collaborators { get; set; } = new List<BoxCollaborator>();

        [JsonProperty("folderTree")]
        public List<BoxFolderTreeItem> FolderTree { get; set; } = new List<BoxFolderTreeItem>();

        // Search-specific properties
        [JsonProperty("searchResults")]
        public List<BoxSearchResult> SearchResults { get; set; } = new List<BoxSearchResult>();

        [JsonProperty("searchQuery")]
        public string SearchQuery { get; set; }

        [JsonProperty("searchType")]
        public string SearchType { get; set; } // "BoxNative", "BoxAIEnhanced", etc.

        [JsonProperty("totalSearchResults")]
        public int TotalSearchResults { get; set; }
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

    public class BoxFolderItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("createdAt")]
        public string CreatedAt { get; set; }

        [JsonProperty("modifiedAt")]
        public string ModifiedAt { get; set; }

        [JsonProperty("fileType")]
        public string FileType { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("fileContent")]
        public string FileContent { get; set; }
    }

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

    public class BoxParentFolder
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class BoxFolderInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int FileCount { get; set; }
        public int FolderCount { get; set; }
    }

    // Box Search API Models
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
    #endregion
}
