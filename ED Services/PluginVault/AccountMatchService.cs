using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
//using Microsoft.PowerPlatform.Dataverse.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;


namespace EDServices
{
    public class AccountMatchService
    {
        private readonly IOrganizationService _dataverseClient;
        private readonly MatchConfiguration _config;
        private bool? _isDataverseSearchEnabled;

        // Dynamics 365 credentials for Web API authentication
        private readonly string _dynamicsEnvironment;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _tenantId;
        private static readonly HttpClient _httpClient = new HttpClient();

        private readonly string _dynamicsEnvironmentCurrent;

        //public AccountMatchService(ServiceClient dataverseClient, MatchConfiguration config = null)
        //{
        //    _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        //    _config = config ?? MatchConfiguration.Default;

        //    // Load credentials from config for Web API authentication
        //    _dynamicsEnvironment = ConfigurationManager.AppSettings["DynamicsEnvironment"] ?? "https://techsoup.crm.dynamics.com";
        //    _clientId = ConfigurationManager.AppSettings["ClientId"] ?? "";
        //    _clientSecret = ConfigurationManager.AppSettings["ClientSecret"] ?? "";
        //    _tenantId = ConfigurationManager.AppSettings["TenantId"] ?? "d8ba2331-6b05-4303-9a60-36c58c3e272d";
        //}

        //public AccountMatchService(ServiceClient dataverseClient, Dictionary<string, string> dynamicsEnvironments, Dictionary<string, string> envVariables, MatchConfiguration config = null)
        //{
        //    _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        //    _config = config ?? MatchConfiguration.Default;

        //    _dynamicsEnvironmentCurrent = dynamicsEnvironments["DynamicsEnvironmentCurrent"];

        //    _dynamicsEnvironment = dynamicsEnvironments[_dynamicsEnvironmentCurrent];
        //    _clientId = envVariables["ts_TSDynamicsClientId"]; ;
        //    _clientSecret = envVariables["ts_TSDynamicsClientSecret"];
        //    _tenantId = ConfigurationManager.AppSettings["TenantId"] ?? "d8ba2331-6b05-4303-9a60-36c58c3e272d";


        //}

        public AccountMatchService(IOrganizationService dataverseClient, Dictionary<string, string> dynamicsEnvironments, Dictionary<string, string> envVariables, MatchConfiguration config = null)
        {
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _config = config ?? MatchConfiguration.Default;

            _dynamicsEnvironmentCurrent = dynamicsEnvironments["DynamicsEnvironmentCurrent"];

            _dynamicsEnvironment = dynamicsEnvironments[_dynamicsEnvironmentCurrent];
            _clientId = envVariables["ts_TSDynamicsClientId"]; ;
            _clientSecret = envVariables["ts_TSDynamicsClientSecret"];
            _tenantId = "d8ba2331-6b05-4303-9a60-36c58c3e272d";
        }
        public AccountMatchResponse FindMatches(string jsonRequest)
        {
            var request = JsonConvert.DeserializeObject<AccountMatchRequest>(jsonRequest);
            return FindMatches(request);
        }

        public AccountMatchResponse FindMatches(AccountMatchRequest request)
        {
            ValidateRequest(request);

            var response = new AccountMatchResponse
            {
                SearchCriteria = request,
                SearchTimestamp = DateTime.UtcNow
            };

            try
            {
                // Step 1: Retrieve candidate accounts from Dynamics 365
                var candidates = RetrieveCandidateAccounts(request);

                if (!candidates.Any())
                {
                    response.TotalCandidatesRetrieved = 0;
                    response.Matches = new List<AccountMatch>();
                    return response;
                }

                response.TotalCandidatesRetrieved = candidates.Count;

                // Step 2: Score each candidate using multiple algorithms
                var scoredMatches = new List<AccountMatch>();

                foreach (var candidate in candidates)
                {
                    var match = ScoreCandidate(request, candidate);

                    // Only include matches that meet minimum threshold
                    if (match.OverallScore >= _config.MinimumOverallScore)
                    {
                        scoredMatches.Add(match);
                    }
                }

                // Step 3: Sort by overall score (highest first) and apply result limit
                response.Matches = scoredMatches
                    .OrderByDescending(m => m.OverallScore)
                    .ThenByDescending(m => m.ConfidenceLevel)
                    .Take(_config.MaxResultsReturned)
                    .ToList();

                response.TotalMatchesFound = scoredMatches.Count;
                response.MatchesReturned = response.Matches.Count;

                return response;
            }
            catch (Exception ex)
            {
                response.Error = $"Error finding matches: {ex.Message}";
                response.Matches = new List<AccountMatch>();
                return response;
            }
        }

        private List<Entity> RetrieveCandidateAccounts(AccountMatchRequest request)
        {
            var candidates = new List<Entity>();
            var candidateIds = new HashSet<Guid>();

            // Primary: Try Dataverse Search API for broader, fuzzy matching
            if (_config.UseDataverseSearch && IsDataverseSearchEnabled())
            {
                try
                {
                    var searchCandidates = RetrieveCandidatesUsingDataverseSearch(request);
                    foreach (var entity in searchCandidates)
                    {
                        var id = entity.GetAttributeValue<Guid>("accountid");
                        if (candidateIds.Add(id))
                        {
                            candidates.Add(entity);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log the error but continue with QueryExpression fallback
                    System.Diagnostics.Debug.WriteLine($"Dataverse Search failed, falling back to QueryExpression: {ex.Message}");
                }
            }

            // Fallback or supplement: Use QueryExpression for additional candidates
            // This ensures we don't miss matches if Search API missed them or wasn't available
            var queryExpressionCandidates = RetrieveCandidatesUsingQueryExpression(request);
            foreach (var entity in queryExpressionCandidates)
            {
                var id = entity.GetAttributeValue<Guid>("accountid");
                if (candidateIds.Add(id))
                {
                    candidates.Add(entity);
                }
            }

            return candidates;
        }


        private List<Entity> RetrieveCandidatesUsingDataverseSearch(AccountMatchRequest request)
        {
            var searchTerms = BuildDataverseSearchQuery(request);
            if (string.IsNullOrWhiteSpace(searchTerms))
            {
                return new List<Entity>();
            }

            try
            {
                // Get authentication token for Web API
                string accessToken = GetDataverseAccessToken();
                if (string.IsNullOrEmpty(accessToken))
                {
                    System.Diagnostics.Debug.WriteLine("Failed to obtain access token for Dataverse Search");
                    return new List<Entity>();
                }

                // Build the entities array as a JSON string (v2.0 API requirement)
                var entitiesArray = new[]
                {
                    new
                    {
                        Name = "account",
                        selectColumns = new[] {
                            "name", "address1_city", "address1_stateorprovince",
                            "emailaddress1", "address1_line1", "address1_postalcode",
                            "telephone1", "websiteurl", "new_legalidentifier","accountnumber", "address1_country"
                        },
                        searchColumns = new[] {
                                                "name", "new_legalidentifier", "telephone1", "websiteurl", "address1_postalcode", "address1_line1"
                                            },
                        filter = "address1_country eq '" + request.CountryCode + "'"
                    }
                };
                string entitiesJson = JsonConvert.SerializeObject(entitiesArray);

                // Build the search request payload for v2.0 API
                var searchPayload = new Dictionary<string, object>
                {
                    { "search", searchTerms },
                    { "entities", entitiesJson },  // entities must be a JSON string
                    { "top", Math.Min(_config.MaxCandidatesToRetrieve, 100) }
                };

                var requestBody = JsonConvert.SerializeObject(searchPayload);

                System.Diagnostics.Debug.WriteLine($"Dataverse Search Query: {searchTerms}");
                System.Diagnostics.Debug.WriteLine($"Dataverse Search Request Body: {requestBody}");

                // Execute the Web API search request using v2.0 endpoint
                var searchUrl = $"{_dynamicsEnvironment}/api/search/v2.0/query";

                using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, searchUrl))
                {
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                    var response = _httpClient.SendAsync(httpRequest).GetAwaiter().GetResult();
                    var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine($"Dataverse Search API Error: {response.StatusCode} - {responseContent}");
                        throw new Exception($"Dataverse Search API returned {response.StatusCode}: {responseContent}");
                    }

                    System.Diagnostics.Debug.WriteLine($"Dataverse Search Response: {responseContent.Substring(0, Math.Min(500, responseContent.Length))}...");

                    // Parse the v2.0 search results and convert to Entity objects
                    return ParseSearchResultsV2(responseContent);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Dataverse Search Error: {ex.Message}");
                throw; // Re-throw to let caller handle
            }
        }


        private List<Entity> ParseSearchResultsV2(string responseJson)
        {
            List<Entity> entities = new List<Entity>();

            try
            {
                // The v2.0 response has a nested structure:
                // { "response": "{...JSON string with actual results...}" }
                dynamic outerResponse = JsonConvert.DeserializeObject(responseJson);

                // Get the inner response string and parse it
                string innerResponseJson = outerResponse?.response?.ToString();
                if (string.IsNullOrEmpty(innerResponseJson))
                {
                    System.Diagnostics.Debug.WriteLine("No inner response found in Dataverse Search result");
                    return entities;
                }

                dynamic searchResults = JsonConvert.DeserializeObject(innerResponseJson);

                if (searchResults?.Value == null)
                {
                    return entities;
                }

                string[] tsOrgIds = ((JArray)searchResults.Value).ToList<dynamic>().Select(result => (string)result.Attributes.accountnumber).ToArray();
                string[] names = ((JArray)searchResults.Value).ToList<dynamic>().Select(result => (string)result.Attributes.name).ToArray();

                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet(
                                                        "accountid",
                                                        "name",
                                                        "address1_line1",
                                                        "address1_postalcode",
                                                        "address1_stateorprovince",
                                                        "address1_country",
                                                        "websiteurl",
                                                        "new_legalidentifier",
                                                        "telephone1",
                                                        "accountnumber"
                                                         );
                queryAccount.Criteria.AddCondition("accountnumber", ConditionOperator.In, tsOrgIds);
                EntityCollection accountCollection = _dataverseClient.RetrieveMultiple(queryAccount);

                Dictionary<string, Entity> entitiesDictionary = accountCollection.Entities.ToDictionary(e => e.GetAttributeValue<string>("accountnumber"), e => e);


                var filteredResults = ((JArray)searchResults.Value).ToList<dynamic>().Where(result => entitiesDictionary.ContainsKey((string)result.Attributes.accountnumber));

                foreach (var result in filteredResults)
                {
                    string objectIdStr = result.Id?.ToString() ?? result.ObjectId?.ToString();
                    if (string.IsNullOrEmpty(objectIdStr) || !Guid.TryParse(objectIdStr, out Guid objectId))
                        continue;

                    string entityName = result.EntityName?.ToString() ?? "account";
                    Entity entity = new Entity(entityName, objectId);

                    // Map search result attributes to entity (v2.0 structure)
                    if (result.Attributes != null)
                    {
                        var attrs = result.Attributes;

                        // Extract known attributes
                        if (attrs.name != null) entity["name"] = attrs.name.ToString();
                        if (attrs.address1_city != null) entity["address1_city"] = attrs.address1_city.ToString();
                        if (attrs.address1_stateorprovince != null) entity["address1_stateorprovince"] = attrs.address1_stateorprovince.ToString();
                        if (attrs.address1_line1 != null) entity["address1_line1"] = attrs.address1_line1.ToString();
                        if (attrs.address1_postalcode != null) entity["address1_postalcode"] = attrs.address1_postalcode.ToString();
                        if (attrs.emailaddress1 != null) entity["emailaddress1"] = attrs.emailaddress1.ToString();
                        if (attrs.telephone1 != null) entity["telephone1"] = attrs.telephone1.ToString();
                        if (attrs.websiteurl != null) entity["websiteurl"] = attrs.websiteurl.ToString();
                        if (attrs.new_legalidentifier != null) entity["new_legalidentifier"] = attrs.new_legalidentifier.ToString();
                        if (attrs.accountnumber != null) entity["accountnumber"] = attrs.accountnumber.ToString();
                        if (attrs.address1_country != null) entity["address1_country"] = attrs.address1_country.ToString();

                    }

                    string tsOrgId = entity.GetAttributeValue<string>("accountnumber");

                    entity = entitiesDictionary[tsOrgId];
                    //if (
                    //    //DynamicsProcessesValidationServices
                    //    DynamicsInterface
                    //                    .DynamicsEnvironments["DynamicsEnvironmentCurrent"] != "prod"

                    //    )
                    //    entity = _dataverseClient.Retrieve(
                    //                                        entity.LogicalName
                    //                                        , entity.Id
                    //                                        , new ColumnSet(
                    //                                                        "accountid",
                    //                                                        "name",
                    //                                                        "address1_line1",
                    //                                                        "address1_postalcode",
                    //                                                        "address1_stateorprovince",
                    //                                                        "address1_country",
                    //                                                        "websiteurl",
                    //                                                        "new_legalidentifier",
                    //                                                        "telephone1",
                    //                                                        "accountnumber"
                    //                                                        )
                    //                                        );




                    // Ensure accountid is set
                    entity["accountid"] = objectId;
                    // Store search score for potential use in ranking
                    if (result.Score != null)
                    {
                        entity["search_score"] = (double)result.Score;
                    }

                    entities.Add(entity);
                }

                System.Diagnostics.Debug.WriteLine($"Dataverse Search returned {entities.Count} account candidates");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing v2.0 search results: {ex.Message}");
            }

            return entities;
        }

        /// <summary>
        /// [DEPRECATED - kept for reference] Parses legacy v1.0 Dataverse Search API response and converts results to Entity objects,
        /// filtering only for the specified entity type.
        /// </summary>
        private List<Entity> ParseSearchResultsFilterByEntity(string responseJson, string entityName)
        {
            var entities = new List<Entity>();

            try
            {
                dynamic searchResults = JsonConvert.DeserializeObject(responseJson);

                if (searchResults?.Value == null)
                {
                    return entities;
                }

                foreach (var result in searchResults.Value)
                {
                    // Filter by entity name - the legacy API returns all indexed entities
                    string resultEntityName = result.EntityName?.ToString();
                    if (!string.Equals(resultEntityName, entityName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string objectIdStr = result.ObjectId?.ToString() ?? result.Id?.ToString();
                    if (string.IsNullOrEmpty(objectIdStr) || !Guid.TryParse(objectIdStr, out Guid objectId))
                        continue;

                    var entity = new Entity(entityName, objectId);

                    // Map search result attributes to entity
                    if (result.Attributes != null)
                    {
                        foreach (var attr in result.Attributes)
                        {
                            string attrName = attr.Name?.ToString();
                            if (attrName != null && !attrName.StartsWith("@") && attr.Value != null)
                            {
                                entity[attrName] = ConvertDynamicAttribute(attrName, attr.Value);
                            }
                        }
                    }

                    // Ensure accountid is set
                    entity["accountid"] = objectId;

                    // Store search score for potential use in ranking
                    if (result.Score != null)
                    {
                        entity["search_score"] = (double)result.Score;
                    }

                    entities.Add(entity);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing search results: {ex.Message}");
            }

            return entities;
        }


        private object ConvertDynamicAttribute(string key, dynamic value)
        {
            if (value == null) return null;

            try
            {
                // Handle JToken values from JSON deserialization
                if (value is JToken jToken)
                {
                    switch (jToken.Type)
                    {
                        case JTokenType.String:
                            return jToken.Value<string>();
                        case JTokenType.Integer:
                            return jToken.Value<int>();
                        case JTokenType.Float:
                            return jToken.Value<double>();
                        case JTokenType.Boolean:
                            return jToken.Value<bool>();
                        case JTokenType.Date:
                            return jToken.Value<DateTime>();
                        case JTokenType.Guid:
                            return jToken.Value<Guid>();
                        default:
                            return jToken.ToString();
                    }
                }

                // Handle regular objects
                return value?.ToString();
            }
            catch
            {
                return value?.ToString();
            }
        }


        private string GetDataverseAccessToken()
        {
            try
            {
                string tokenUrl = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";

                var tokenParams = new Dictionary<string, string>
                {
                    { "client_id", _clientId },
                    { "scope", $"{_dynamicsEnvironment}/.default" },
                    { "client_secret", _clientSecret },
                    { "grant_type", "client_credentials" }
                };

                using (var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl))
                {
                    tokenRequest.Content = new FormUrlEncodedContent(tokenParams);

                    var response = _httpClient.SendAsync(tokenRequest).GetAwaiter().GetResult();
                    var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine($"Token request failed: {response.StatusCode} - {responseContent}");
                        return null;
                    }

                    dynamic tokenResponse = JsonConvert.DeserializeObject(responseContent);
                    return tokenResponse.access_token;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting access token: {ex.Message}");
                return null;
            }
        }

        private string BuildDataverseSearchQuery(AccountMatchRequest request)
        {
            var queryParts = new List<string>();

            // Name: Most important field - use fuzzy matching and boosting
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                var normalizedName = NormalizeOrgName(request.Name);

                // Add full phrase with proximity search and high boost
                if (normalizedName.Contains(" "))
                {
                    queryParts.Add($"\"{normalizedName}\"~3^4"); // Proximity search, very high boost
                }
                else
                {
                    queryParts.Add($"{normalizedName}~^4"); // Single word with fuzzy and boost
                }

                // Add individual significant words with fuzzy matching
                var words = normalizedName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2 && !IsCommonWord(w))
                    .ToList();

                foreach (var word in words)
                {
                    queryParts.Add($"{word}~^2"); // Fuzzy with moderate boost
                    queryParts.Add($"{word}*");   // Wildcard for prefix matching
                }
            }

            // Legal Identifier: Critical field - exact match with very high boost
            if (!string.IsNullOrWhiteSpace(request.LegalId))
            {
                var cleanLegalId = Regex.Replace(request.LegalId, @"[^a-zA-Z0-9]", "");
                if (!string.IsNullOrEmpty(cleanLegalId))
                {
                    queryParts.Add($"\"{cleanLegalId}\"^5"); // Exact match, highest boost
                    queryParts.Add($"{cleanLegalId}~^3");    // Fuzzy for typos
                }
            }

            // Phone: Use last significant digits
            if (!string.IsNullOrWhiteSpace(request.Phone))
            {
                var phoneDigits = Regex.Replace(request.Phone, @"[^\d]", "");
                if (phoneDigits.Length >= 7)
                {
                    var lastDigits = phoneDigits.Length >= 10
                        ? phoneDigits.Substring(phoneDigits.Length - 10)
                        : phoneDigits.Substring(phoneDigits.Length - 7);

                    queryParts.Add($"*{lastDigits}^3"); // Suffix match with boost
                }
            }

            // Website: Normalized domain search
            if (!string.IsNullOrWhiteSpace(request.Website))
            {
                var normalizedUrl = NormalizeUrl(request.Website);
                if (!string.IsNullOrEmpty(normalizedUrl))
                {
                    queryParts.Add($"*{normalizedUrl}*^2"); // Contains match with boost
                }
            }

            // Postal Code: Prefix matching
            if (!string.IsNullOrWhiteSpace(request.PostalCode))
            {
                var postalBase = request.PostalCode.Split('-')[0].Trim().Split(' ')[0].Trim();
                if (postalBase.Length >= 3)
                {
                    queryParts.Add($"{postalBase}*^2"); // Prefix match with boost
                }
            }

            // Address: Key words with fuzzy matching
            if (!string.IsNullOrWhiteSpace(request.Address1))
            {
                var normalizedAddress = NormalizeAddress(request.Address1);
                var addressWords = normalizedAddress.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2 && !IsAddressStopWord(w))
                    .Take(3) // Limit to avoid overly broad queries
                    .ToList();

                foreach (var word in addressWords)
                {
                    queryParts.Add($"{word}~"); // Fuzzy match
                }
            }

            return string.Join(" ", queryParts);
        }

        private List<Entity> ParseSearchResults(string responseJson)
        {
            var entities = new List<Entity>();

            try
            {
                var searchResults = JsonConvert.DeserializeObject<SearchQueryResults>(responseJson);

                if (searchResults?.Value == null)
                {
                    return entities;
                }

                foreach (var result in searchResults.Value)
                {
                    if (result.ObjectId == Guid.Empty)
                        continue;

                    var entity = new Entity("account", result.ObjectId);

                    // Map search result attributes to entity
                    if (result.Attributes != null)
                    {
                        foreach (var attr in result.Attributes)
                        {
                            if (attr.Value != null)
                            {
                                entity[attr.Key] = ConvertSearchAttribute(attr.Key, attr.Value);
                            }
                        }
                    }

                    // Ensure accountid is set
                    entity["accountid"] = result.ObjectId;

                    // Store search score for potential use in ranking
                    entity["search_score"] = result.Score;

                    entities.Add(entity);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing search results: {ex.Message}");
            }

            return entities;
        }

        private object ConvertSearchAttribute(string key, object value)
        {
            if (value == null) return null;

            // Handle JToken values from JSON deserialization
            if (value is JToken jToken)
            {
                switch (jToken.Type)
                {
                    case JTokenType.String:
                        return jToken.ToString();
                    case JTokenType.Integer:
                        return jToken.ToObject<int>();
                    case JTokenType.Float:
                        return jToken.ToObject<double>();
                    case JTokenType.Boolean:
                        return jToken.ToObject<bool>();
                    case JTokenType.Guid:
                        return jToken.ToObject<Guid>();
                    case JTokenType.Date:
                        return jToken.ToObject<DateTime>();
                    default:
                        return jToken.ToString();
                }
            }

            return value;
        }

        private bool IsDataverseSearchEnabled()
        {
            if (_isDataverseSearchEnabled.HasValue)
            {
                return _isDataverseSearchEnabled.Value;
            }

            try
            {
                var query = new QueryExpression("organization")
                {
                    ColumnSet = new ColumnSet("isexternalsearchindexenabled")
                };

                var result = _dataverseClient.RetrieveMultiple(query);
                var org = result.Entities.FirstOrDefault();

                _isDataverseSearchEnabled = org?.GetAttributeValue<bool>("isexternalsearchindexenabled") ?? false;
            }
            catch
            {
                _isDataverseSearchEnabled = false;
            }

            return _isDataverseSearchEnabled.Value;
        }

        private bool IsCommonWord(string word)
        {
            var commonWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "and", "or", "of", "for", "to", "in", "on", "at", "by",
                "inc", "llc", "ltd", "corp", "co", "company", "corporation",
                "group", "holdings", "international", "services", "solutions"
            };
            return commonWords.Contains(word);
        }


        private bool IsAddressStopWord(string word)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "street", "st", "avenue", "ave", "road", "rd", "drive", "dr",
                "lane", "ln", "boulevard", "blvd", "court", "ct", "place", "pl",
                "suite", "ste", "floor", "fl", "unit", "apt", "building", "bldg",
                "north", "south", "east", "west", "n", "s", "e", "w"
            };
            return stopWords.Contains(word);
        }


        private List<Entity> RetrieveCandidatesUsingQueryExpression(AccountMatchRequest request)
        {
            var query = new QueryExpression("account")
            {
                ColumnSet = new ColumnSet(
                    "accountid",
                    "name",
                    "address1_line1",
                    "address1_postalcode",
                    "address1_stateorprovince",
                    "address1_country",
                    "websiteurl",
                    "new_legalidentifier",
                    "telephone1",
                    "accountnumber"
                )
            };

            // Use broad filters to get potential matches
            var filterGroup = query.Criteria;
            filterGroup.FilterOperator = LogicalOperator.Or;

            // Filter by name (contains or starts with)
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                var nameFilter = new FilterExpression(LogicalOperator.Or);

                // Exact match
                nameFilter.AddCondition("name", ConditionOperator.Equal, request.Name);

                // Starts with (for partial matches)
                var nameWords = request.Name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (nameWords.Length > 0)
                {
                    nameFilter.AddCondition("name", ConditionOperator.BeginsWith, nameWords[0]);
                }

                filterGroup.AddFilter(nameFilter);
            }

            // Filter by legal identifier (exact match)
            if (!string.IsNullOrWhiteSpace(request.LegalId))
            {
                filterGroup.AddCondition("new_legalidentifier", ConditionOperator.Equal, request.LegalId);
            }

            // Filter by postal code (starts with for zip+4 variations)
            if (!string.IsNullOrWhiteSpace(request.PostalCode))
            {
                var postalBase = request.PostalCode.Split('-')[0].Trim();
                postalBase = postalBase.Split(' ')[0].Trim();
                postalBase = postalBase.Substring(0, Math.Min(4, postalBase.Length));
                filterGroup.AddCondition("address1_postalcode", ConditionOperator.BeginsWith, postalBase);
            }

            // Filter by phone (normalized - just digits)
            if (!string.IsNullOrWhiteSpace(request.Phone))
            {
                var phoneDigits = Regex.Replace(request.Phone, @"[^\d]", "");
                if (phoneDigits.Length >= 10)
                {
                    // Match on last 10 digits (local number)
                    var lastTenDigits = phoneDigits.Substring(phoneDigits.Length - 10);
                    filterGroup.AddCondition("telephone1", ConditionOperator.EndsWith, lastTenDigits);
                }
                else if (phoneDigits.Length >= 7)
                {
                    // Match on last 7 digits (local number)
                    var lastSevenDigits = phoneDigits.Substring(phoneDigits.Length - 7);
                    filterGroup.AddCondition("telephone1", ConditionOperator.EndsWith, lastSevenDigits);
                }
            }

            if (!string.IsNullOrWhiteSpace(request.Website))
            {
                string normalizedWebSite = NormalizeUrl(request.Website);
                filterGroup.AddCondition("websiteurl", ConditionOperator.Like, "%" + normalizedWebSite + "%");
            }

            // Filter by country
            if (!string.IsNullOrWhiteSpace(request.CountryCode))
            {
                var countryFilter = new FilterExpression(LogicalOperator.And);
                countryFilter.AddCondition("address1_country", ConditionOperator.Equal, request.CountryCode);

                // Add country filter as AND condition with the OR group
                var andGroup = new FilterExpression(LogicalOperator.And);
                andGroup.AddFilter(filterGroup);
                andGroup.AddFilter(countryFilter);
                query.Criteria = andGroup;
            }

            // Limit results to prevent timeout
            // query.TopCount = _config.MaxCandidatesToRetrieve;

            var allEntities = new List<Entity>();
            var pageNumber = 1;
            var maxRecords = 15000;

            // Initial query
            var results = _dataverseClient.RetrieveMultiple(query);
            allEntities.AddRange(results.Entities);

            // Handle pagination - retrieve up to 15,000 records
            while (results.MoreRecords && allEntities.Count < maxRecords)
            {
                pageNumber++;

                // Set the paging cookie and page number for next page
                query.PageInfo = new PagingInfo
                {
                    PageNumber = pageNumber,
                    PagingCookie = results.PagingCookie
                };

                results = _dataverseClient.RetrieveMultiple(query);

                // Add results, but ensure we don't exceed the max limit
                var remainingCapacity = maxRecords - allEntities.Count;
                var entitiesToAdd = results.Entities.Take(remainingCapacity).ToList();
                allEntities.AddRange(entitiesToAdd);

                // Break if we've reached the limit
                if (allEntities.Count >= maxRecords)
                    break;
            }

            return allEntities;
        }

        private AccountMatch ScoreCandidate(AccountMatchRequest request, Entity candidate)
        {
            var match = new AccountMatch
            {
                AccountId = candidate.Id,
                AccountName = candidate.GetAttributeValue<string>("name"),
                Address1Line1 = candidate.GetAttributeValue<string>("address1_line1"),
                PostalCode = candidate.GetAttributeValue<string>("address1_postalcode"),
                StateProvince = candidate.GetAttributeValue<string>("address1_stateorprovince"),
                CountryCode = candidate.GetAttributeValue<string>("address1_country"),
                WebsiteUrl = candidate.GetAttributeValue<string>("websiteurl"),
                LegalIdentifier = candidate.GetAttributeValue<string>("new_legalidentifier"),
                TSOrgId = candidate.GetAttributeValue<string>("accountnumber"),
                Phone = candidate.GetAttributeValue<string>("telephone1"),
                FieldScores = new Dictionary<string, FieldMatchScore>()
            };

            // Score each field using multiple algorithms
            ScoreNameField(request.Name, match.AccountName, match.FieldScores);
            ScoreAddressField(request.Address1, match.Address1Line1, match.FieldScores);
            ScorePostalCodeField(request.PostalCode, match.PostalCode, match.FieldScores);
            ScoreStateProvinceField(request.StateProvince, match.StateProvince, match.FieldScores);
            ScoreCountryField(request.CountryCode, match.CountryCode, match.FieldScores);
            ScoreWebsiteField(request.Website, match.WebsiteUrl, match.FieldScores);
            ScoreLegalIdField(request.LegalId, match.LegalIdentifier, match.FieldScores);
            //ScorePhoneField(request.Phone, match.Phone, match.FieldScores);

            // Calculate overall score using weighted average
            CalculateOverallScore(match);

            return match;
        }

        private void ScoreNameField(string searchName, string candidateName, Dictionary<string, FieldMatchScore> scores)
        {
            if (string.IsNullOrWhiteSpace(searchName) || string.IsNullOrWhiteSpace(candidateName))
            {
                scores["name"] = new FieldMatchScore { Weight = _config.NameWeight, FinalScore = 0.0 };
                return;
            }

            var fieldScore = new FieldMatchScore
            {
                Weight = _config.NameWeight,
                AlgorithmScores = new Dictionary<string, double>()
            };

            // Normalize names for comparison
            var normalizedSearch = NormalizeOrgName(searchName);
            var normalizedCandidate = NormalizeOrgName(candidateName);

            // Algorithm 1: Levenshtein Distance (edit distance)
            var levenshteinScore = CalculateLevenshteinSimilarity(normalizedSearch, normalizedCandidate);
            fieldScore.AlgorithmScores["Levenshtein"] = levenshteinScore;

            // Algorithm 2: Jaro-Winkler Distance (good for short strings and typos)
            var jaroWinklerScore = CalculateJaroWinklerSimilarity(normalizedSearch, normalizedCandidate);
            fieldScore.AlgorithmScores["JaroWinkler"] = jaroWinklerScore;

            // Algorithm 3: Sørensen-Dice Coefficient (bigram-based)
            var diceScore = CalculateDiceCoefficientSimilarity(normalizedSearch, normalizedCandidate);
            fieldScore.AlgorithmScores["DiceCoefficient"] = diceScore;

            // Algorithm 4: Token Sort Ratio (word order independent)
            var tokenSortScore = CalculateTokenSortRatio(normalizedSearch, normalizedCandidate);
            fieldScore.AlgorithmScores["TokenSort"] = tokenSortScore;

            // Algorithm 5: Cosine Similarity (character frequency based)
            var cosineScore = CalculateCosineSimilarity(normalizedSearch, normalizedCandidate);
            fieldScore.AlgorithmScores["Cosine"] = cosineScore;

            // Calculate final score as weighted average of algorithms
            // Give more weight to Jaro-Winkler and Token Sort for organization names
            fieldScore.FinalScore = (
                levenshteinScore * 0.15 +
                jaroWinklerScore * 0.30 +
                diceScore * 0.15 +
                tokenSortScore * 0.30 +
                cosineScore * 0.10
            );

            scores["name"] = fieldScore;
        }

        private void ScoreAddressField(string searchAddress, string candidateAddress, Dictionary<string, FieldMatchScore> scores)
        {
            if (string.IsNullOrWhiteSpace(searchAddress) || string.IsNullOrWhiteSpace(candidateAddress))
            {
                scores["address"] = new FieldMatchScore { Weight = _config.AddressWeight, FinalScore = 0.0 };
                return;
            }

            var fieldScore = new FieldMatchScore
            {
                Weight = _config.AddressWeight,
                AlgorithmScores = new Dictionary<string, double>()
            };

            var normalizedSearch = NormalizeAddress(searchAddress);
            var normalizedCandidate = NormalizeAddress(candidateAddress);

            // Use algorithms optimized for addresses
            var levenshteinScore = CalculateLevenshteinSimilarity(normalizedSearch, normalizedCandidate);
            fieldScore.AlgorithmScores["Levenshtein"] = levenshteinScore;

            var tokenSortScore = CalculateTokenSortRatio(normalizedSearch, normalizedCandidate);
            fieldScore.AlgorithmScores["TokenSort"] = tokenSortScore;

            var diceScore = CalculateDiceCoefficientSimilarity(normalizedSearch, normalizedCandidate);
            fieldScore.AlgorithmScores["DiceCoefficient"] = diceScore;

            // For addresses, token sort is most important (handles abbreviations)
            fieldScore.FinalScore = (
                levenshteinScore * 0.25 +
                tokenSortScore * 0.50 +
                diceScore * 0.25
            );

            scores["address"] = fieldScore;
        }

        private void ScorePostalCodeField(string searchPostal, string candidatePostal, Dictionary<string, FieldMatchScore> scores)
        {
            if (string.IsNullOrWhiteSpace(searchPostal) || string.IsNullOrWhiteSpace(candidatePostal))
            {
                scores["postalCode"] = new FieldMatchScore { Weight = _config.PostalCodeWeight, FinalScore = 0.0 };
                return;
            }

            var fieldScore = new FieldMatchScore
            {
                Weight = _config.PostalCodeWeight,
                AlgorithmScores = new Dictionary<string, double>()
            };

            // Normalize postal codes (remove spaces, hyphens)
            var normalizedSearch = Regex.Replace(searchPostal, @"[\s\-]", "").ToUpperInvariant();
            var normalizedCandidate = Regex.Replace(candidatePostal, @"[\s\-]", "").ToUpperInvariant();

            // Exact match
            if (normalizedSearch == normalizedCandidate)
            {
                fieldScore.FinalScore = 1.0;
                fieldScore.AlgorithmScores["Exact"] = 1.0;
            }
            // ZIP+4 vs ZIP-5 (US) or partial match
            else if (normalizedSearch.StartsWith(normalizedCandidate) || normalizedCandidate.StartsWith(normalizedSearch))
            {
                var minLength = Math.Min(normalizedSearch.Length, normalizedCandidate.Length);
                var commonPrefix = 0;
                for (int i = 0; i < minLength; i++)
                {
                    if (normalizedSearch[i] == normalizedCandidate[i])
                        commonPrefix++;
                    else
                        break;
                }

                fieldScore.FinalScore = (double)commonPrefix / Math.Max(normalizedSearch.Length, normalizedCandidate.Length);
                fieldScore.AlgorithmScores["PrefixMatch"] = fieldScore.FinalScore;
            }
            else
            {
                // Use Levenshtein for typos
                var levenshteinScore = CalculateLevenshteinSimilarity(normalizedSearch, normalizedCandidate);
                fieldScore.FinalScore = levenshteinScore;
                fieldScore.AlgorithmScores["Levenshtein"] = levenshteinScore;
            }

            scores["postalCode"] = fieldScore;
        }

        private void ScoreStateProvinceField(string searchState, string candidateState, Dictionary<string, FieldMatchScore> scores)
        {
            searchState.Replace("---", "").Replace("--", "");
            if (string.IsNullOrWhiteSpace(searchState) && string.IsNullOrWhiteSpace(candidateState))
                return;

            if (string.IsNullOrWhiteSpace(searchState) || string.IsNullOrWhiteSpace(candidateState))
            {
                scores["stateProvince"] = new FieldMatchScore { Weight = _config.StateProvinceWeight, FinalScore = 0.0 };
                return;
            }

            var normalizedSearch = searchState.Trim().ToUpperInvariant();
            var normalizedCandidate = candidateState.Trim().ToUpperInvariant();

            var score = normalizedSearch == normalizedCandidate ? 1.0 : 0.0;

            scores["stateProvince"] = new FieldMatchScore
            {
                Weight = _config.StateProvinceWeight,
                FinalScore = score,
                AlgorithmScores = new Dictionary<string, double> { { "Exact", score } }
            };
        }

        private void ScoreCountryField(string searchCountry, string candidateCountry, Dictionary<string, FieldMatchScore> scores)
        {
            if (string.IsNullOrWhiteSpace(searchCountry) || string.IsNullOrWhiteSpace(candidateCountry))
            {
                scores["country"] = new FieldMatchScore { Weight = _config.CountryWeight, FinalScore = 0.0 };
                return;
            }

            var normalizedSearch = searchCountry.Trim().ToUpperInvariant();
            var normalizedCandidate = candidateCountry.Trim().ToUpperInvariant();

            var score = normalizedSearch == normalizedCandidate ? 1.0 : 0.0;

            scores["country"] = new FieldMatchScore
            {
                Weight = _config.CountryWeight,
                FinalScore = score,
                AlgorithmScores = new Dictionary<string, double> { { "Exact", score } }
            };
        }

        private void ScoreWebsiteField(string searchWebsite, string candidateWebsite, Dictionary<string, FieldMatchScore> scores)
        {
            if (string.IsNullOrWhiteSpace(searchWebsite) || string.IsNullOrWhiteSpace(candidateWebsite))
            {
                //scores["website"] = new FieldMatchScore { Weight = _config.WebsiteWeight, FinalScore = 0.0 };
                return;
            }

            var fieldScore = new FieldMatchScore
            {
                Weight = _config.WebsiteWeight,
                AlgorithmScores = new Dictionary<string, double>()
            };

            // Normalize URLs (remove protocol, www, trailing slash)
            var normalizedSearch = NormalizeUrl(searchWebsite);
            var normalizedCandidate = NormalizeUrl(candidateWebsite);

            // Exact domain match
            if (normalizedSearch == normalizedCandidate)
            {
                fieldScore.FinalScore = 1.0;
                fieldScore.AlgorithmScores["Exact"] = 1.0;
            }
            else
            {
                // Use Levenshtein for similar domains
                var levenshteinScore = CalculateLevenshteinSimilarity(normalizedSearch, normalizedCandidate);
                fieldScore.FinalScore = levenshteinScore;
                fieldScore.AlgorithmScores["Levenshtein"] = levenshteinScore;
            }

            scores["website"] = fieldScore;
        }

        private void ScoreLegalIdField(string searchLegalId, string candidateLegalId, Dictionary<string, FieldMatchScore> scores)
        {
            if (string.IsNullOrWhiteSpace(searchLegalId) || string.IsNullOrWhiteSpace(candidateLegalId))
            {
                scores["legalId"] = new FieldMatchScore { Weight = _config.LegalIdWeight, FinalScore = 0.0 };
                return;
            }

            // Normalize legal IDs (remove hyphens, spaces)
            //string normalizedSearch = Regex.Replace(searchLegalId, @"[\s\-]", "").ToUpperInvariant();
            //string normalizedCandidate = Regex.Replace(candidateLegalId, @"[\s\-]", "").ToUpperInvariant();
            string normalizedSearch = Regex.Replace(searchLegalId, @"\W", "").Replace("_", "").ToUpperInvariant();
            string normalizedCandidate = Regex.Replace(candidateLegalId, @"\W", "").Replace("_", "").ToUpperInvariant();
            // Legal ID should be exact match or very close (accounting for typos)
            double score = 0.0;

            if (normalizedSearch == normalizedCandidate)
            {
                score = 1.0;
            }
            else
            {
                // Allow for minor typos but penalize heavily
                double levenshteinScore = CalculateLevenshteinSimilarity(normalizedSearch, normalizedCandidate);


                //double jaroWinklerScore = CalculateJaroWinklerSimilarity(normalizedSearch, normalizedCandidate);
                //fieldScore.AlgorithmScores["JaroWinkler"] = jaroWinklerScore;

                double preliminaryScore = levenshteinScore;

                score = preliminaryScore >= 0.90 ? preliminaryScore
                    :
                    (
                        preliminaryScore >= 0.85 ? preliminaryScore * 0.70
                        :
                        (
                            preliminaryScore >= 0.80 ? preliminaryScore * 0.5
                            : 0.00
                        )
                    );

                //Console.WriteLine($"Legal ID Scoring: Search='{normalizedSearch}', Candidate='{normalizedCandidate}', LevenshteinScore={levenshteinScore}, FinalScore={score}");
            }

            scores["legalId"] = new FieldMatchScore
            {
                Weight = _config.LegalIdWeight,
                FinalScore = score,
                AlgorithmScores = new Dictionary<string, double> { { "Exact", score } }
            };
        }

        private void ScorePhoneField(string searchPhone, string candidatePhone, Dictionary<string, FieldMatchScore> scores)
        {
            if (string.IsNullOrWhiteSpace(searchPhone) || string.IsNullOrWhiteSpace(candidatePhone))
            {
                scores["phone"] = new FieldMatchScore { Weight = _config.PhoneWeight, FinalScore = 0.0 };
                return;
            }

            var fieldScore = new FieldMatchScore
            {
                Weight = _config.PhoneWeight,
                AlgorithmScores = new Dictionary<string, double>()
            };

            // Normalize phone numbers (keep only digits)
            var normalizedSearch = Regex.Replace(searchPhone, @"[^\d]", "");
            var normalizedCandidate = Regex.Replace(candidatePhone, @"[^\d]", "");

            // Exact match on all digits
            if (normalizedSearch == normalizedCandidate)
            {
                fieldScore.FinalScore = 1.0;
                fieldScore.AlgorithmScores["Exact"] = 1.0;
            }
            // Match on last 10 digits
            else if (normalizedSearch.Length >= 10 && normalizedCandidate.Length >= 10)
            {
                var searchLast10 = normalizedSearch.Substring(normalizedSearch.Length - 10);
                var candidateLast10 = normalizedCandidate.Substring(normalizedCandidate.Length - 10);

                if (searchLast10 == candidateLast10)
                {
                    fieldScore.FinalScore = 0.85; // High score but not perfect
                    fieldScore.AlgorithmScores["Last10Digits"] = 0.85;
                }
                else
                {
                    // Check for transposition errors in the last 10 digits
                    var levenshteinScore = CalculateLevenshteinSimilarity(searchLast10, candidateLast10);
                    if (levenshteinScore >= 0.85)
                    {
                        fieldScore.FinalScore = levenshteinScore * 0.70; // Penalize for not being exact
                        fieldScore.AlgorithmScores["Levenshtein"] = levenshteinScore;
                    }
                    else
                    {
                        fieldScore.FinalScore = 0.0;
                        fieldScore.AlgorithmScores["NoMatch"] = 0.0;
                    }
                }
            }
            else
            {
                // Phone numbers too short or don't match
                fieldScore.FinalScore = 0.0;
                fieldScore.AlgorithmScores["TooShort"] = 0.0;
            }

            scores["phone"] = fieldScore;
        }

        private void CalculateOverallScore(AccountMatch match)
        {
            double totalWeightedScore = 0.0;
            double totalWeight = 0.0;

            foreach (var fieldScore in match.FieldScores.Values)
            {
                if (fieldScore.Weight > 0)
                {
                    totalWeightedScore += fieldScore.FinalScore * fieldScore.Weight;
                    totalWeight += fieldScore.Weight;
                }
            }

            match.OverallScore = totalWeight > 0 ? totalWeightedScore / totalWeight : 0.0;

            // Determine confidence level based on overall score
            if (match.OverallScore >= 0.95)
            {
                match.ConfidenceLevel = MatchConfidence.VeryHigh;
                match.MatchQuality = "Exact or near-exact match";
            }
            else if (match.OverallScore >= 0.85)
            {
                match.ConfidenceLevel = MatchConfidence.High;
                match.MatchQuality = "Strong match with minor variations";
            }
            else if (match.OverallScore >= 0.70)
            {
                match.ConfidenceLevel = MatchConfidence.Medium;
                match.MatchQuality = "Probable match with some differences";
            }
            else if (match.OverallScore >= 0.50)
            {
                match.ConfidenceLevel = MatchConfidence.Low;
                match.MatchQuality = "Possible match with significant differences";
            }
            else
            {
                match.ConfidenceLevel = MatchConfidence.VeryLow;
                match.MatchQuality = "Weak match - manual review recommended";
            }

            // Additional quality indicators
            var criticalFieldsMatched = 0;
            if (match.FieldScores.ContainsKey("legalId") && match.FieldScores["legalId"].FinalScore >= 0.95)
                criticalFieldsMatched++;
            if (match.FieldScores.ContainsKey("name") && match.FieldScores["name"].FinalScore >= 0.85)
                criticalFieldsMatched++;
            if (match.FieldScores.ContainsKey("postalCode") && match.FieldScores["postalCode"].FinalScore >= 0.90)
                criticalFieldsMatched++;
            if (match.FieldScores.ContainsKey("phone") && match.FieldScores["phone"].FinalScore >= 0.85)
                criticalFieldsMatched++;

            if (criticalFieldsMatched >= 2)
            {
                match.HasCriticalFieldMatch = true;
            }
        }

        #region String Similarity Algorithms

        private double CalculateLevenshteinSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
                return 1.0;
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0.0;

            var distance = Fastenshtein.Levenshtein.Distance(s1, s2);
            var maxLength = Math.Max(s1.Length, s2.Length);

            return 1.0 - ((double)distance / maxLength);
        }

        private double CalculateJaroWinklerSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
                return 1.0;
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0.0;

            // Calculate Jaro similarity first
            var jaroScore = CalculateJaroSimilarity(s1, s2);

            // Calculate common prefix length (up to 4 characters)
            var prefixLength = 0;
            var maxPrefix = Math.Min(Math.Min(s1.Length, s2.Length), 4);

            for (int i = 0; i < maxPrefix; i++)
            {
                if (s1[i] == s2[i])
                    prefixLength++;
                else
                    break;
            }

            // Jaro-Winkler = Jaro + (prefix_length * 0.1 * (1 - Jaro))
            var jaroWinklerScore = jaroScore + (prefixLength * 0.1 * (1.0 - jaroScore));

            return Math.Min(jaroWinklerScore, 1.0);
        }

        private double CalculateJaroSimilarity(string s1, string s2)
        {
            if (s1.Length == 0 && s2.Length == 0)
                return 1.0;
            if (s1.Length == 0 || s2.Length == 0)
                return 0.0;

            var matchDistance = Math.Max(s1.Length, s2.Length) / 2 - 1;
            var s1Matches = new bool[s1.Length];
            var s2Matches = new bool[s2.Length];

            var matches = 0;
            var transpositions = 0;

            // Find matches
            for (int i = 0; i < s1.Length; i++)
            {
                var start = Math.Max(0, i - matchDistance);
                var end = Math.Min(i + matchDistance + 1, s2.Length);

                for (int j = start; j < end; j++)
                {
                    if (s2Matches[j] || s1[i] != s2[j])
                        continue;

                    s1Matches[i] = true;
                    s2Matches[j] = true;
                    matches++;
                    break;
                }
            }

            if (matches == 0)
                return 0.0;

            // Count transpositions
            var k = 0;
            for (int i = 0; i < s1.Length; i++)
            {
                if (!s1Matches[i])
                    continue;

                while (!s2Matches[k])
                    k++;

                if (s1[i] != s2[k])
                    transpositions++;

                k++;
            }

            return ((double)matches / s1.Length +
                    (double)matches / s2.Length +
                    (double)(matches - transpositions / 2) / matches) / 3.0;
        }

        private double CalculateDiceCoefficientSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
                return 1.0;
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0.0;

            // Generate bigrams (pairs of consecutive characters)
            var s1Bigrams = GetBigrams(s1);
            var s2Bigrams = GetBigrams(s2);

            if (s1Bigrams.Count == 0 && s2Bigrams.Count == 0)
                return 1.0;
            if (s1Bigrams.Count == 0 || s2Bigrams.Count == 0)
                return 0.0;

            // Count common bigrams
            var intersection = s1Bigrams.Intersect(s2Bigrams).Count();

            // Dice coefficient = 2 * |intersection| / (|set1| + |set2|)
            return (2.0 * intersection) / (s1Bigrams.Count + s2Bigrams.Count);
        }

        private List<string> GetBigrams(string s)
        {
            var bigrams = new List<string>();

            for (int i = 0; i < s.Length - 1; i++)
            {
                bigrams.Add(s.Substring(i, 2));
            }

            return bigrams;
        }

        private double CalculateTokenSortRatio(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
                return 1.0;
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0.0;

            // Tokenize and sort
            var tokens1 = s1.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .OrderBy(t => t)
                            .ToArray();
            var tokens2 = s2.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .OrderBy(t => t)
                            .ToArray();

            var sorted1 = string.Join(" ", tokens1);
            var sorted2 = string.Join(" ", tokens2);

            // Use Levenshtein on sorted strings
            return CalculateLevenshteinSimilarity(sorted1, sorted2);
        }

        private double CalculateCosineSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
                return 1.0;
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0.0;

            // Get character frequency vectors
            var vector1 = GetCharacterFrequencyVector(s1);
            var vector2 = GetCharacterFrequencyVector(s2);

            // Get all unique characters
            var allChars = new HashSet<char>(vector1.Keys.Union(vector2.Keys));

            // Calculate dot product and magnitudes
            double dotProduct = 0.0;
            double magnitude1 = 0.0;
            double magnitude2 = 0.0;

            foreach (var c in allChars)
            {
                var freq1 = vector1.ContainsKey(c) ? vector1[c] : 0;
                var freq2 = vector2.ContainsKey(c) ? vector2[c] : 0;

                dotProduct += freq1 * freq2;
                magnitude1 += freq1 * freq1;
                magnitude2 += freq2 * freq2;
            }

            magnitude1 = Math.Sqrt(magnitude1);
            magnitude2 = Math.Sqrt(magnitude2);

            if (magnitude1 == 0 || magnitude2 == 0)
                return 0.0;

            return dotProduct / (magnitude1 * magnitude2);
        }

        private Dictionary<char, int> GetCharacterFrequencyVector(string s)
        {
            var vector = new Dictionary<char, int>();

            foreach (var c in s)
            {
                if (vector.ContainsKey(c))
                    vector[c]++;
                else
                    vector[c] = 1;
            }

            return vector;
        }

        #endregion

        #region Normalization Helpers

        private string NormalizeOrgName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            // Convert to lowercase
            var normalized = name.ToLowerInvariant();

            // Remove common legal entity suffixes
            var suffixes = new[] { "inc", "incorporated", "corp", "corporation", "llc", "ltd", "limited",
                                   "co", "company", "plc", "lp", "llp", "pc", "pllc" };

            foreach (var suffix in suffixes)
            {
                // Remove suffix with period
                normalized = Regex.Replace(normalized, $@"\b{suffix}\.$", "", RegexOptions.IgnoreCase);
                // Remove suffix without period at end
                normalized = Regex.Replace(normalized, $@"\b{suffix}\b$", "", RegexOptions.IgnoreCase);
            }

            // Remove extra punctuation and whitespace
            normalized = Regex.Replace(normalized, @"[,\.\-&]", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ");

            return normalized.Trim();
        }

        private string NormalizeAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return string.Empty;

            var normalized = address.ToLowerInvariant();

            // Expand common abbreviations
            var abbreviations = new Dictionary<string, string>
            {
                { @"\bst\b", "street" },
                { @"\bave\b", "avenue" },
                { @"\brd\b", "road" },
                { @"\bdr\b", "drive" },
                { @"\bln\b", "lane" },
                { @"\bblvd\b", "boulevard" },
                { @"\bct\b", "court" },
                { @"\bpl\b", "place" },
                { @"\bsq\b", "square" },
                { @"\bapt\b", "apartment" },
                { @"\bste\b", "suite" },
                { @"\bfl\b", "floor" },
                { @"\bn\b", "north" },
                { @"\bs\b", "south" },
                { @"\be\b", "east" },
                { @"\bw\b", "west" }
            };

            foreach (var abbrev in abbreviations)
            {
                normalized = Regex.Replace(normalized, abbrev.Key, abbrev.Value);
            }

            // Remove punctuation
            normalized = Regex.Replace(normalized, @"[,\.\-#]", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ");

            return normalized.Trim();
        }

        private string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            var normalized = url.ToLowerInvariant();

            // Remove protocol
            normalized = Regex.Replace(normalized, @"^https?://", "");

            // Remove www
            normalized = Regex.Replace(normalized, @"^www\.", "");

            // Remove trailing slash and path
            normalized = Regex.Replace(normalized, @"/.*$", "");

            return normalized.Trim();
        }

        #endregion

        private void ValidateRequest(AccountMatchRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            // At least one field must be provided
            if (string.IsNullOrWhiteSpace(request.Name) &&
                string.IsNullOrWhiteSpace(request.Address1) &&
                string.IsNullOrWhiteSpace(request.PostalCode) &&
                string.IsNullOrWhiteSpace(request.LegalId) &&
                string.IsNullOrWhiteSpace(request.Website) &&
                string.IsNullOrWhiteSpace(request.Phone))
            {
                throw new ArgumentException("At least one search criterion must be provided (Name, Address, PostalCode, LegalId, Website, or Phone)");
            }
        }
    }

    #region Request/Response Models

    public class AccountMatchRequest
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("address1")]
        public string Address1 { get; set; }

        [JsonProperty("postalCode")]
        public string PostalCode { get; set; }

        [JsonProperty("website")]
        public string Website { get; set; }

        [JsonProperty("legalId")]
        public string LegalId { get; set; }

        [JsonProperty("stateProvince")]
        public string StateProvince { get; set; }

        [JsonProperty("countryCode")]
        public string CountryCode { get; set; }

        [JsonProperty("phone")]
        public string Phone { get; set; }
    }

    public class AccountMatchResponse
    {
        [JsonProperty("searchCriteria")]
        public AccountMatchRequest SearchCriteria { get; set; }

        [JsonProperty("searchTimestamp")]
        public DateTime SearchTimestamp { get; set; }

        [JsonProperty("totalCandidatesRetrieved")]
        public int TotalCandidatesRetrieved { get; set; }

        [JsonProperty("totalMatchesFound")]
        public int TotalMatchesFound { get; set; }

        [JsonProperty("matchesReturned")]
        public int MatchesReturned { get; set; }

        [JsonProperty("matches")]
        public List<AccountMatch> Matches { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }
    }

    public class AccountMatch
    {
        [JsonProperty("accountId")]
        public Guid AccountId { get; set; }

        [JsonProperty("accountName")]
        public string AccountName { get; set; }

        [JsonProperty("address1Line1")]
        public string Address1Line1 { get; set; }

        [JsonProperty("postalCode")]
        public string PostalCode { get; set; }

        [JsonProperty("stateProvince")]
        public string StateProvince { get; set; }

        [JsonProperty("countryCode")]
        public string CountryCode { get; set; }

        [JsonProperty("websiteUrl")]
        public string WebsiteUrl { get; set; }

        [JsonProperty("legalIdentifier")]
        public string LegalIdentifier { get; set; }

        [JsonProperty("tsOrgId")]
        public string TSOrgId { get; set; }

        [JsonProperty("phone")]
        public string Phone { get; set; }

        [JsonProperty("overallScore")]
        public double OverallScore { get; set; }

        [JsonProperty("confidenceLevel")]
        public MatchConfidence ConfidenceLevel { get; set; }

        [JsonProperty("matchQuality")]
        public string MatchQuality { get; set; }

        [JsonProperty("hasCriticalFieldMatch")]
        public bool HasCriticalFieldMatch { get; set; }

        [JsonProperty("fieldScores")]
        public Dictionary<string, FieldMatchScore> FieldScores { get; set; }
    }

    public class FieldMatchScore
    {
        [JsonProperty("weight")]
        public double Weight { get; set; }

        [JsonProperty("finalScore")]
        public double FinalScore { get; set; }

        [JsonProperty("algorithmScores")]
        public Dictionary<string, double> AlgorithmScores { get; set; }
    }

    public enum MatchConfidence
    {
        VeryLow = 1,
        Low = 2,
        Medium = 3,
        High = 4,
        VeryHigh = 5
    }

    #endregion

    #region Configuration

    public class MatchConfiguration
    {
        public double NameWeight { get; set; } = 0.30;
        public double AddressWeight { get; set; } = 0.15;
        public double PostalCodeWeight { get; set; } = 0.10;
        public double StateProvinceWeight { get; set; } = 0.00;
        public double CountryWeight { get; set; } = 0.00;
        public double WebsiteWeight { get; set; } = 0.10;
        public double LegalIdWeight { get; set; } = 0.35;
        public double PhoneWeight { get; set; } = 0.00;
        public double MinimumOverallScore { get; set; } = 0.50;
        public int MaxCandidatesToRetrieve { get; set; } = 15000;
        public int MaxResultsReturned { get; set; } = 100;

        /// <summary>
        /// When true, uses Dataverse Search API for primary candidate retrieval
        /// with fuzzy matching and term boosting. Falls back to QueryExpression
        /// if Search is not enabled or fails. Default is true.
        /// </summary>
        public bool UseDataverseSearch { get; set; } = true;

        public static MatchConfiguration Default => new MatchConfiguration();

        public static MatchConfiguration Strict => new MatchConfiguration
        {
            NameWeight = 0.25,
            AddressWeight = 0.15,
            PostalCodeWeight = 0.10,
            LegalIdWeight = 0.35,
            PhoneWeight = 0.15,
            MinimumOverallScore = 0.75,
            MaxResultsReturned = 5,
            UseDataverseSearch = true
        };

        public static MatchConfiguration Broad => new MatchConfiguration
        {
            NameWeight = 0.35,
            AddressWeight = 0.20,
            PostalCodeWeight = 0.10,
            LegalIdWeight = 0.20,
            PhoneWeight = 0.15,
            MinimumOverallScore = 0.40,
            MaxResultsReturned = 20,
            UseDataverseSearch = true
        };

        /// <summary>
        /// Configuration that only uses QueryExpression (original behavior).
        /// Use this if Dataverse Search is not available or desired.
        /// </summary>
        public static MatchConfiguration QueryExpressionOnly => new MatchConfiguration
        {
            UseDataverseSearch = false
        };
    }

    #endregion

    #region Dataverse Search Models

    /// <summary>
    /// Represents an entity configuration for Dataverse Search query.
    /// </summary>
    internal class SearchEntity
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("selectcolumns")]
        public List<string> SelectColumns { get; set; }

        [JsonProperty("searchcolumns")]
        public List<string> SearchColumns { get; set; }

        [JsonProperty("filter")]
        public string Filter { get; set; }
    }

    /// <summary>
    /// Results from a Dataverse Search query.
    /// </summary>
    internal class SearchQueryResults
    {
        [JsonProperty("Error")]
        public SearchErrorDetail Error { get; set; }

        [JsonProperty("Value")]
        public List<QueryResult> Value { get; set; }

        [JsonProperty("Count")]
        public long? Count { get; set; }
    }

    /// <summary>
    /// Individual search result from Dataverse Search.
    /// </summary>
    internal class QueryResult
    {
        [JsonProperty("@search.score")]
        public double Score { get; set; }

        [JsonProperty("@search.objectid")]
        public Guid ObjectId { get; set; }

        [JsonProperty("@search.entityname")]
        public string EntityName { get; set; }

        [JsonProperty("@search.objecttypecode")]
        public int ObjectTypeCode { get; set; }

        /// <summary>
        /// Dynamic attributes returned from the search.
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, object> Attributes { get; set; }
    }

    /// <summary>
    /// Error detail from Dataverse Search.
    /// </summary>
    internal class SearchErrorDetail
    {
        [JsonProperty("Code")]
        public string Code { get; set; }

        [JsonProperty("Message")]
        public string Message { get; set; }
    }

    #endregion
}