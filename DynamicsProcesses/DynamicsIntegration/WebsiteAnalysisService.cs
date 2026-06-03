using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;

namespace DynamicsProcesses
{
    /// <summary>
    /// Sophisticated Website Analysis Service
    /// 
    /// This service provides comprehensive website analysis capabilities to determine
    /// organizational legitimacy. It employs multiple analysis strategies:
    /// 
    /// 1. STRUCTURAL ANALYSIS - Examines HTML structure, navigation, page organization
    /// 2. SEMANTIC CONTENT ANALYSIS - Extracts and understands meaning from content
    /// 3. IDENTITY VERIFICATION - Matches organization name, address, contact info
    /// 4. TRUST SIGNAL DETECTION - Identifies indicators of legitimacy
    /// 5. RED FLAG DETECTION - Identifies potential fraud indicators
    /// 6. CONTENT QUALITY ASSESSMENT - Evaluates depth and professionalism
    /// 7. TECHNICAL ANALYSIS - SSL, page load, mobile responsiveness indicators
    /// 
    /// The goal is to achieve LLM-level understanding of website content and context.
    /// </summary>
    public class WebsiteAnalysisService : IDisposable
    {
        #region Constants and Configuration

        private const int MaxRetries = 3;
        private const int InitialDelayMs = 1000;
        private const int MaxDelayMs = 10000;
        private const int RequestTimeoutSeconds = 30;
        private const int MaxContentLength = 50000;

        // Minimum thresholds for passing
        private const double OrgNameMatchThreshold = 0.40;
        private const double AddressMatchThreshold = 0.30;
        private const double TrustScoreThreshold = 0.35;
        private const double ContentQualityThreshold = 0.30;

        #endregion

        #region Fields

        private readonly HttpClient _httpClient;
        private readonly HttpClientHandler _httpClientHandler;
        private bool _disposed;

        #endregion

        #region Constructor and Disposal

        public WebsiteAnalysisService()
        {
            // CRITICAL: Enable TLS 1.2 and TLS 1.3 for compatibility with modern websites
            // Many government, healthcare, and UK-based sites require TLS 1.2+
            System.Net.ServicePointManager.SecurityProtocol = 
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            
            // Some sites may require TLS 1.3 which is included in Tls12 on newer .NET versions
            try
            {
                // Attempt to add TLS 1.3 if available (requires .NET 4.8+)
                System.Net.ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; // TLS 1.3
            }
            catch { /* TLS 1.3 not available on this runtime */ }

            _httpClientHandler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };

            _httpClient = new HttpClient(_httpClientHandler)
            {
                Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
            };

            // MINIMAL headers that work reliably - DO NOT add extra headers
            // Previous issue: Additional headers like Accept-Encoding, Cache-Control, Sec-Fetch-* 
            // caused some sites to return incomplete/different content
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _httpClientHandler?.Dispose();
                _disposed = true;
            }
        }

        #endregion

        #region Main Analysis Entry Point

        /// <summary>
        /// Performs comprehensive website analysis to determine organizational legitimacy.
        /// </summary>
        public async Task<WebsiteAnalysisResult> AnalyzeAsync(WebsiteAnalysisRequest request)
        {
            var result = new WebsiteAnalysisResult();
            var analysisContext = new AnalysisContext();

            try
            {
                LogInfo($"Starting comprehensive website analysis for {request.Website}");

                // Phase 1: Pre-fetch Analysis (Domain-level checks)
                await PerformDomainAnalysisAsync(request, result, analysisContext);

                // Phase 2: Fetch Website Content
                var fetchResult = await FetchWebsiteContentAsync(request.Website);
                if (!fetchResult.Success)
                {
                    result.IsAccessible = false;
                    result.AccessError = fetchResult.Error;
                    result.AnalysisFlags.Add($"Website is not accessible: {fetchResult.Error}");
                    CalculateFinalScores(result, analysisContext);
                    return result;
                }

                result.IsAccessible = true;
                result.RetryAttempts = fetchResult.Attempts;
                analysisContext.RawHtml = fetchResult.Content;
                analysisContext.FinalUrl = fetchResult.FinalUrl;

                // Phase 3: Parse and Extract Content
                await ParseAndExtractContentAsync(analysisContext);

                // Phase 4: Structural Analysis
                PerformStructuralAnalysis(analysisContext, result);

                // Phase 5: Identity Verification (Org Name & Address)
                PerformIdentityVerification(request, analysisContext, result);

                // Phase 6: Trust Signal Analysis
                PerformTrustSignalAnalysis(analysisContext, result);

                // Phase 7: Red Flag Detection
                PerformRedFlagDetection(analysisContext, result);

                // Phase 8: Content Quality Assessment
                PerformContentQualityAssessment(analysisContext, result);

                // Phase 9: Calculate Final Scores
                CalculateFinalScores(result, analysisContext);

                // Store extracted content for reference
                result.ExtractedContent = analysisContext.GetExtractedContentSummary(MaxContentLength);

                LogInfo($"Website analysis completed for {request.Website}: " +
                    $"OrgScore={result.OrganizationNameMatchScore:P0}, " +
                    $"AddrScore={result.AddressMatchScore:P0}, " +
                    $"TrustScore={result.TrustScore:P0}, " +
                    $"QualityScore={result.ContentQualityScore:P0}, " +
                    $"OverallScore={result.OverallContentScore:P0}");
            }
            catch (Exception ex)
            {
                result.IsAccessible = false;
                result.AccessError = ex.Message;
                result.AnalysisFlags.Add($"Error analyzing website: {ex.Message}");
                LogError($"Error in AnalyzeAsync for {request.Website}: {ex.Message}\n{ex.StackTrace}");
            }

            return result;
        }

        #endregion

        #region Phase 1: Domain Analysis

        private async Task PerformDomainAnalysisAsync(WebsiteAnalysisRequest request, 
            WebsiteAnalysisResult result, AnalysisContext context)
        {
            // Check for generic/social media domains
            var genericCheck = CheckIfGenericOrSocialMediaDomain(request.Website);
            if (genericCheck.IsGeneric)
            {
                result.IsGenericOrSocialMediaDomain = true;
                result.DomainType = genericCheck.DomainType;
                result.AnalysisFlags.Add(genericCheck.Message);
                context.DomainFlags.Add("GENERIC_PLATFORM");
                LogInfo($"Website {request.Website} identified as generic/social media domain: {genericCheck.DomainType}");
            }

            // Extract and analyze domain
            context.Domain = ExtractDomain(request.Website);
            context.IsSecure = request.Website.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            await Task.CompletedTask; // Placeholder for additional async domain analysis
        }

        #endregion

        #region Phase 2: Content Fetching

        private async Task<FetchResult> FetchWebsiteContentAsync(string website)
        {
            var result = new FetchResult();
            string normalizedUrl = NormalizeUrl(website);

            // Try primary URL with HttpClient
            var primaryResult = await TryFetchWithRetriesAsync(normalizedUrl);
            if (primaryResult.Success)
                return primaryResult;

            // Try alternate URLs with HttpClient
            var alternateUrls = GenerateAlternateUrls(normalizedUrl);
            foreach (var altUrl in alternateUrls)
            {
                LogInfo($"Trying alternate URL: {altUrl}");
                var altResult = await TryFetchWithRetriesAsync(altUrl);
                if (altResult.Success)
                    return altResult;
            }

            // FALLBACK: Try using WebClient which sometimes handles edge cases better
            LogInfo($"HttpClient failed for all URLs, trying WebClient fallback");
            var webClientResult = await TryFetchWithWebClientAsync(normalizedUrl);
            if (webClientResult.Success)
                return webClientResult;

            // Try alternates with WebClient
            foreach (var altUrl in alternateUrls)
            {
                var altResult = await TryFetchWithWebClientAsync(altUrl);
                if (altResult.Success)
                    return altResult;
            }

            return new FetchResult
            {
                Success = false,
                Error = primaryResult.Error ?? "Unable to retrieve website content after multiple attempts"
            };
        }

        /// <summary>
        /// Fallback fetch using WebClient which handles some edge cases better than HttpClient
        /// </summary>
        private async Task<FetchResult> TryFetchWithWebClientAsync(string url)
        {
            var result = new FetchResult { FinalUrl = url, Attempts = 1 };

            try
            {
                LogInfo($"WebClient fallback attempt for {url}");

                using (var webClient = new WebClient())
                {
                    // MINIMAL headers only - same as HttpClient
                    webClient.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    webClient.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
                    webClient.Encoding = Encoding.UTF8;

                    result.Content = await webClient.DownloadStringTaskAsync(new Uri(url));
                    result.Success = true;
                    LogInfo($"WebClient successfully fetched {url} (Length: {result.Content?.Length ?? 0})");
                    return result;
                }
            }
            catch (WebException wex)
            {
                string innerMsg = wex.InnerException?.Message ?? "";
                result.Error = string.IsNullOrEmpty(innerMsg) ? wex.Message : $"{wex.Message} -> {innerMsg}";
                LogInfo($"WebClient error for {url}: {result.Error}");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                LogInfo($"WebClient exception for {url}: {ex.Message}");
            }

            return result;
        }

        private async Task<FetchResult> TryFetchWithRetriesAsync(string url)
        {
            var result = new FetchResult { FinalUrl = url };
            Exception lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                result.Attempts = attempt;

                try
                {
                    LogInfo($"Fetch attempt {attempt}/{MaxRetries} for {url}");

                    // Use simple GetAsync - headers are already set on _httpClient
                    // DO NOT modify headers per-request as this caused issues with content fetching
                    var response = await _httpClient.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        result.Content = await response.Content.ReadAsStringAsync();
                        result.Success = true;
                        result.FinalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                        LogInfo($"Successfully fetched {url} (Length: {result.Content?.Length ?? 0})");
                        return result;
                    }

                    lastException = new HttpRequestException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
                    LogInfo($"HTTP {(int)response.StatusCode} on attempt {attempt} for {url}");
                }
                catch (TaskCanceledException) when (!_disposed)
                {
                    lastException = new TimeoutException($"Request timed out after {RequestTimeoutSeconds} seconds");
                    LogInfo($"Timeout on attempt {attempt} for {url}");
                }
                catch (HttpRequestException hex)
                {
                    // Extract inner exception for more details
                    string innerMsg = hex.InnerException?.Message ?? "";
                    string fullError = string.IsNullOrEmpty(innerMsg) ? hex.Message : $"{hex.Message} -> {innerMsg}";
                    lastException = new HttpRequestException(fullError);
                    LogInfo($"HTTP error on attempt {attempt} for {url}: {fullError}");
                    
                    // Check for SSL/TLS issues
                    if (innerMsg.Contains("SSL") || innerMsg.Contains("TLS") || 
                        innerMsg.Contains("secure channel") || innerMsg.Contains("authentication"))
                    {
                        LogInfo($"SSL/TLS issue detected for {url} - may need different protocol");
                    }
                }
                catch (Exception ex)
                {
                    string innerMsg = ex.InnerException?.Message ?? "";
                    string fullError = string.IsNullOrEmpty(innerMsg) ? ex.Message : $"{ex.Message} -> {innerMsg}";
                    lastException = new Exception(fullError);
                    LogInfo($"Error on attempt {attempt} for {url}: {fullError}");
                }

                if (attempt < MaxRetries)
                {
                    int delay = Math.Min(InitialDelayMs * (int)Math.Pow(2, attempt - 1), MaxDelayMs);
                    delay += new Random().Next(0, 500);
                    await Task.Delay(delay);
                }
            }

            result.Error = lastException?.Message;
            return result;
        }

        #endregion

        #region Phase 3: Content Parsing and Extraction

        private async Task ParseAndExtractContentAsync(AnalysisContext context)
        {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(context.RawHtml);
            context.Document = htmlDoc;

            // Remove noise elements
            RemoveNoiseElements(htmlDoc);

            // Extract structured content
            ExtractMetadata(htmlDoc, context);
            ExtractHeadings(htmlDoc, context);
            ExtractStructuredData(htmlDoc, context);
            ExtractNavigation(htmlDoc, context);
            ExtractMainContent(htmlDoc, context);
            ExtractContactInformation(htmlDoc, context);
            ExtractFooterContent(htmlDoc, context);
            ExtractImages(htmlDoc, context);
            ExtractLinks(htmlDoc, context);

            // Build searchable content
            BuildSearchableContent(context);

            await Task.CompletedTask;
        }

        private void RemoveNoiseElements(HtmlDocument doc)
        {
            var noiseSelectors = new[] { "//script", "//style", "//noscript", "//svg", "//iframe", 
                "//comment()", "//*[contains(@class, 'cookie')]", "//*[contains(@class, 'popup')]",
                "//*[contains(@class, 'modal')]", "//*[contains(@class, 'advertisement')]",
                "//*[contains(@class, 'ad-')]", "//*[contains(@id, 'ad-')]" };

            foreach (var selector in noiseSelectors)
            {
                try
                {
                    var nodes = doc.DocumentNode.SelectNodes(selector);
                    if (nodes != null)
                    {
                        foreach (var node in nodes.ToList())
                            node.Remove();
                    }
                }
                catch { /* Ignore XPath errors */ }
            }
        }

        private void ExtractMetadata(HtmlDocument doc, AnalysisContext context)
        {
            // Title
            var titleNode = doc.DocumentNode.SelectSingleNode("//title");
            context.PageTitle = CleanText(titleNode?.InnerText);

            // Meta description
            var metaDesc = doc.DocumentNode.SelectSingleNode("//meta[@name='description']");
            context.MetaDescription = metaDesc?.GetAttributeValue("content", "");

            // Meta keywords
            var metaKeywords = doc.DocumentNode.SelectSingleNode("//meta[@name='keywords']");
            context.MetaKeywords = metaKeywords?.GetAttributeValue("content", "");

            // Open Graph
            context.OgTitle = GetMetaContent(doc, "og:title");
            context.OgDescription = GetMetaContent(doc, "og:description");
            context.OgSiteName = GetMetaContent(doc, "og:site_name");
            context.OgType = GetMetaContent(doc, "og:type");
            context.OgImage = GetMetaContent(doc, "og:image");

            // Twitter Card
            context.TwitterTitle = GetMetaContent(doc, "twitter:title", "name");
            context.TwitterDescription = GetMetaContent(doc, "twitter:description", "name");

            // Canonical URL
            var canonical = doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']");
            context.CanonicalUrl = canonical?.GetAttributeValue("href", "");

            // Language
            var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
            context.Language = htmlNode?.GetAttributeValue("lang", "");
        }

        private string GetMetaContent(HtmlDocument doc, string property, string attribute = "property")
        {
            var node = doc.DocumentNode.SelectSingleNode($"//meta[@{attribute}='{property}']");
            return node?.GetAttributeValue("content", "");
        }

        private void ExtractHeadings(HtmlDocument doc, AnalysisContext context)
        {
            for (int level = 1; level <= 6; level++)
            {
                var headings = doc.DocumentNode.SelectNodes($"//h{level}");
                if (headings != null)
                {
                    foreach (var heading in headings)
                    {
                        string text = CleanText(heading.InnerText);
                        if (!string.IsNullOrWhiteSpace(text) && text.Length > 2)
                        {
                            context.Headings.Add(new HeadingInfo { Level = level, Text = text });
                        }
                    }
                }
            }
        }

        private void ExtractStructuredData(HtmlDocument doc, AnalysisContext context)
        {
            var schemaNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
            if (schemaNodes != null)
            {
                foreach (var node in schemaNodes)
                {
                    try
                    {
                        var json = JToken.Parse(node.InnerText);
                        context.StructuredData.Add(json);
                        ExtractFromJsonLd(json, context);
                    }
                    catch { /* Ignore malformed JSON */ }
                }
            }
        }

        private void ExtractFromJsonLd(JToken json, AnalysisContext context)
        {
            if (json == null) return;

            try
            {
                // Handle arrays
                if (json is JArray array)
                {
                    foreach (var item in array)
                        ExtractFromJsonLd(item, context);
                    return;
                }

                // Handle objects
                if (json is JObject obj)
                {
                    string type = obj["@type"]?.ToString();

                    // Extract organization info
                    if (type == "Organization" || type == "NGO" || type == "NonProfit" || 
                        type == "Corporation" || type == "LocalBusiness")
                    {
                        context.SchemaOrgName = obj["name"]?.ToString();
                        context.SchemaOrgDescription = obj["description"]?.ToString();
                        
                        var address = obj["address"];
                        if (address != null)
                        {
                            context.SchemaAddress = new AddressInfo
                            {
                                Street = address["streetAddress"]?.ToString(),
                                City = address["addressLocality"]?.ToString(),
                                Region = address["addressRegion"]?.ToString(),
                                PostalCode = address["postalCode"]?.ToString(),
                                Country = address["addressCountry"]?.ToString()
                            };
                        }

                        context.SchemaPhone = obj["telephone"]?.ToString();
                        context.SchemaEmail = obj["email"]?.ToString();
                    }

                    // Extract contact info
                    if (type == "ContactPoint" || type == "PostalAddress")
                    {
                        context.ExtractedPhones.Add(obj["telephone"]?.ToString());
                        context.ExtractedEmails.Add(obj["email"]?.ToString());
                    }

                    // Recursively process nested objects
                    foreach (var prop in obj.Properties())
                    {
                        if (prop.Value is JObject || prop.Value is JArray)
                            ExtractFromJsonLd(prop.Value, context);
                    }
                }
            }
            catch { /* Ignore extraction errors */ }
        }

        private void ExtractNavigation(HtmlDocument doc, AnalysisContext context)
        {
            var navSelectors = new[] { "//nav", "//header//ul", "//*[contains(@class, 'nav')]", 
                "//*[contains(@class, 'menu')]", "//*[@role='navigation']" };

            foreach (var selector in navSelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes != null)
                {
                    foreach (var node in nodes.Take(3))
                    {
                        var links = node.SelectNodes(".//a[@href]");
                        if (links != null)
                        {
                            foreach (var link in links)
                            {
                                string href = link.GetAttributeValue("href", "").ToLowerInvariant();
                                string text = CleanText(link.InnerText);

                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    context.NavigationLinks.Add(new LinkInfo { Href = href, Text = text });

                                    // Detect important pages
                                    if (IsAboutPage(href, text)) context.HasAboutPage = true;
                                    if (IsContactPage(href, text)) context.HasContactPage = true;
                                    if (IsMissionPage(href, text)) context.HasMissionPage = true;
                                    if (IsTeamPage(href, text)) context.HasTeamPage = true;
                                    if (IsProgramsPage(href, text)) context.HasProgramsPage = true;
                                    if (IsDonatePage(href, text)) context.HasDonatePage = true;
                                    if (IsPrivacyPage(href, text)) context.HasPrivacyPage = true;
                                    if (IsTermsPage(href, text)) context.HasTermsPage = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        private void ExtractMainContent(HtmlDocument doc, AnalysisContext context)
        {
            // Priority content areas
            var prioritySelectors = new[] {
                "//main", "//article", "//*[@role='main']", 
                "//*[contains(@class, 'content')]", "//*[contains(@class, 'main')]",
                "//*[contains(@id, 'content')]", "//*[contains(@id, 'main')]"
            };

            foreach (var selector in prioritySelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes != null)
                {
                    foreach (var node in nodes.Take(5))
                    {
                        string text = CleanText(node.InnerText);
                        if (!string.IsNullOrWhiteSpace(text) && text.Length > 50)
                        {
                            context.MainContent.Add(text);
                        }
                    }
                }
            }

            // Extract paragraphs
            var paragraphs = doc.DocumentNode.SelectNodes("//p");
            if (paragraphs != null)
            {
                foreach (var p in paragraphs.Take(100))
                {
                    string text = CleanText(p.InnerText);
                    if (!string.IsNullOrWhiteSpace(text) && text.Length > 20)
                    {
                        context.Paragraphs.Add(text);
                    }
                }
            }

            // About/Mission sections
            var aboutSelectors = new[] { 
                "//*[contains(@class, 'about')]", "//*[contains(@id, 'about')]",
                "//*[contains(@class, 'mission')]", "//*[contains(@id, 'mission')]",
                "//*[contains(@class, 'who-we-are')]", "//*[contains(@class, 'our-story')]"
            };

            foreach (var selector in aboutSelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes != null)
                {
                    foreach (var node in nodes.Take(3))
                    {
                        string text = CleanText(node.InnerText);
                        if (!string.IsNullOrWhiteSpace(text) && text.Length > 50)
                        {
                            context.AboutContent.Add(text);
                            context.HasAboutSection = true;
                        }
                    }
                }
            }
        }

        private void ExtractContactInformation(HtmlDocument doc, AnalysisContext context)
        {
            // Contact sections
            var contactSelectors = new[] { 
                "//address", "//*[contains(@class, 'contact')]", "//*[contains(@id, 'contact')]",
                "//*[contains(@class, 'address')]", "//*[contains(@itemprop, 'address')]"
            };

            var contactText = new StringBuilder();
            foreach (var selector in contactSelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        string text = CleanText(node.InnerText);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            contactText.AppendLine(text);
                            context.ContactSections.Add(text);
                        }
                    }
                }
            }

            string allContactText = contactText.ToString();

            // Extract emails
            var emailMatches = Regex.Matches(allContactText + " " + context.RawHtml, 
                @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
            foreach (Match match in emailMatches)
            {
                string email = match.Value.ToLowerInvariant();
                if (!IsGenericEmail(email))
                {
                    context.ExtractedEmails.Add(email);
                }
            }

            // Extract phone numbers
            var phonePatterns = new[] {
                @"\+?1?[-.\s]?\(?[0-9]{3}\)?[-.\s]?[0-9]{3}[-.\s]?[0-9]{4}",
                @"\+44\s?\d{4}\s?\d{6}",
                @"0\d{4}\s?\d{6}",
                @"\+\d{1,3}[-.\s]?\d{1,4}[-.\s]?\d{1,4}[-.\s]?\d{1,9}"
            };

            foreach (var pattern in phonePatterns)
            {
                var matches = Regex.Matches(allContactText, pattern);
                foreach (Match match in matches)
                {
                    context.ExtractedPhones.Add(match.Value);
                }
            }

            // Extract physical address patterns
            ExtractAddressPatterns(allContactText, context);
        }

        private void ExtractAddressPatterns(string text, AnalysisContext context)
        {
            // Look for address patterns in text
            // This is a simplified version - a more sophisticated implementation 
            // would use NLP or address parsing libraries

            // UK postcode pattern
            var ukPostcode = Regex.Match(text, @"[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}", RegexOptions.IgnoreCase);
            if (ukPostcode.Success)
            {
                context.ExtractedPostalCodes.Add(ukPostcode.Value.ToUpperInvariant());
            }

            // US ZIP code pattern
            var usZip = Regex.Match(text, @"\b\d{5}(-\d{4})?\b");
            if (usZip.Success)
            {
                context.ExtractedPostalCodes.Add(usZip.Value);
            }
        }

        private void ExtractFooterContent(HtmlDocument doc, AnalysisContext context)
        {
            var footerSelectors = new[] { "//footer", "//*[contains(@class, 'footer')]", 
                "//*[contains(@id, 'footer')]" };

            foreach (var selector in footerSelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes != null)
                {
                    foreach (var node in nodes.Take(2))
                    {
                        string text = CleanText(node.InnerText);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            context.FooterContent.Add(text);
                            
                            // Check for copyright with org name
                            if (Regex.IsMatch(text, @"©|copyright|\(c\)", RegexOptions.IgnoreCase))
                            {
                                context.HasCopyright = true;
                                context.CopyrightText = text;
                            }

                            // Check for registration numbers
                            if (Regex.IsMatch(text, @"(charity|registered|reg\.?\s*no\.?|ein|501\s*\(c\)\s*\(3\)|sc\d{6})", RegexOptions.IgnoreCase))
                            {
                                context.HasRegistrationNumber = true;
                            }
                        }
                    }
                }
            }
        }

        private void ExtractImages(HtmlDocument doc, AnalysisContext context)
        {
            // Logo images
            var logoSelectors = new[] { 
                "//img[contains(@class, 'logo')]", "//img[contains(@id, 'logo')]",
                "//*[contains(@class, 'logo')]//img", "//header//img",
                "//a[contains(@class, 'logo')]//img", "//*[contains(@class, 'brand')]//img"
            };

            foreach (var selector in logoSelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes != null)
                {
                    foreach (var img in nodes)
                    {
                        string alt = img.GetAttributeValue("alt", "");
                        string title = img.GetAttributeValue("title", "");
                        string src = img.GetAttributeValue("src", "");

                        if (!string.IsNullOrWhiteSpace(alt))
                        {
                            context.LogoAlts.Add(alt);
                        }
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            context.LogoTitles.Add(title);
                        }
                        if (!string.IsNullOrWhiteSpace(src))
                        {
                            context.HasLogo = true;
                        }
                    }
                }
            }

            // Team/staff images
            var teamSelectors = new[] { 
                "//*[contains(@class, 'team')]//img", "//*[contains(@class, 'staff')]//img",
                "//*[contains(@class, 'people')]//img", "//*[contains(@class, 'leadership')]//img"
            };

            foreach (var selector in teamSelectors)
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes != null && nodes.Count > 0)
                {
                    context.HasTeamImages = true;
                    context.TeamImageCount = nodes.Count;
                    break;
                }
            }
        }

        private void ExtractLinks(HtmlDocument doc, AnalysisContext context)
        {
            var links = doc.DocumentNode.SelectNodes("//a[@href]");
            if (links == null) return;

            foreach (var link in links)
            {
                string href = link.GetAttributeValue("href", "").ToLowerInvariant();

                // Social media links
                if (IsSocialMediaLink(href))
                {
                    context.SocialMediaLinks.Add(href);
                }

                // External links (potential partnerships/press)
                if (href.StartsWith("http") && !href.Contains(context.Domain ?? ""))
                {
                    context.ExternalLinks.Add(href);
                }
            }
        }

        private void BuildSearchableContent(AnalysisContext context)
        {
            var builder = new StringBuilder();

            // Priority content (for org name matching)
            builder.AppendLine("TITLE: " + context.PageTitle);
            builder.AppendLine("OG_TITLE: " + context.OgTitle);
            builder.AppendLine("OG_SITE_NAME: " + context.OgSiteName);
            builder.AppendLine("SCHEMA_ORG_NAME: " + context.SchemaOrgName);

            foreach (var alt in context.LogoAlts)
                builder.AppendLine("LOGO_ALT: " + alt);

            foreach (var heading in context.Headings.Where(h => h.Level <= 2))
                builder.AppendLine($"H{heading.Level}: " + heading.Text);

            context.PriorityContent = builder.ToString();

            // Full searchable content
            builder.Clear();
            builder.AppendLine(context.PriorityContent);
            builder.AppendLine("META_DESC: " + context.MetaDescription);

            foreach (var heading in context.Headings)
                builder.AppendLine($"HEADING: " + heading.Text);

            foreach (var content in context.MainContent)
                builder.AppendLine("MAIN: " + content);

            foreach (var about in context.AboutContent)
                builder.AppendLine("ABOUT: " + about);

            foreach (var para in context.Paragraphs)
                builder.AppendLine(para);

            foreach (var contact in context.ContactSections)
                builder.AppendLine("CONTACT: " + contact);

            foreach (var footer in context.FooterContent)
                builder.AppendLine("FOOTER: " + footer);

            context.FullContent = builder.ToString();
            context.NormalizedContent = NormalizeForMatching(context.FullContent);
            context.NormalizedPriorityContent = NormalizeForMatching(context.PriorityContent);
        }

        #endregion

        #region Phase 4: Structural Analysis

        private void PerformStructuralAnalysis(AnalysisContext context, WebsiteAnalysisResult result)
        {
            // Analyze page structure
            result.StructuralMetrics = new StructuralMetrics
            {
                HasProperHtmlStructure = context.Document?.DocumentNode.SelectSingleNode("//html") != null,
                HasHead = context.Document?.DocumentNode.SelectSingleNode("//head") != null,
                HasBody = context.Document?.DocumentNode.SelectSingleNode("//body") != null,
                HasTitle = !string.IsNullOrWhiteSpace(context.PageTitle),
                HasMetaDescription = !string.IsNullOrWhiteSpace(context.MetaDescription),
                HasOpenGraph = !string.IsNullOrWhiteSpace(context.OgTitle) || !string.IsNullOrWhiteSpace(context.OgSiteName),
                HasStructuredData = context.StructuredData.Count > 0,
                HeadingCount = context.Headings.Count,
                H1Count = context.Headings.Count(h => h.Level == 1),
                NavigationLinkCount = context.NavigationLinks.Count,
                HasNavigation = context.NavigationLinks.Count >= 3,
                HasFooter = context.FooterContent.Count > 0,
                HasLogo = context.HasLogo,
                HasAboutPage = context.HasAboutPage,
                HasContactPage = context.HasContactPage,
                HasMissionPage = context.HasMissionPage,
                HasTeamPage = context.HasTeamPage,
                HasProgramsPage = context.HasProgramsPage,
                HasDonatePage = context.HasDonatePage,
                HasPrivacyPolicy = context.HasPrivacyPage,
                HasTermsOfService = context.HasTermsPage
            };

            // Calculate structural score
            int structuralPoints = 0;
            if (result.StructuralMetrics.HasProperHtmlStructure) structuralPoints += 5;
            if (result.StructuralMetrics.HasTitle) structuralPoints += 10;
            if (result.StructuralMetrics.HasMetaDescription) structuralPoints += 5;
            if (result.StructuralMetrics.HasOpenGraph) structuralPoints += 5;
            if (result.StructuralMetrics.HasStructuredData) structuralPoints += 10;
            if (result.StructuralMetrics.HasNavigation) structuralPoints += 10;
            if (result.StructuralMetrics.HasFooter) structuralPoints += 5;
            if (result.StructuralMetrics.HasLogo) structuralPoints += 5;
            if (result.StructuralMetrics.H1Count >= 1) structuralPoints += 5;
            if (result.StructuralMetrics.HasAboutPage) structuralPoints += 10;
            if (result.StructuralMetrics.HasContactPage) structuralPoints += 10;
            if (result.StructuralMetrics.HasPrivacyPolicy) structuralPoints += 5;
            if (result.StructuralMetrics.HasTermsOfService) structuralPoints += 5;

            result.StructuralScore = Math.Min(1.0, structuralPoints / 100.0);
        }

        #endregion

        #region Phase 5: Identity Verification

        private void PerformIdentityVerification(WebsiteAnalysisRequest request, 
            AnalysisContext context, WebsiteAnalysisResult result)
        {
            // Organization Name Matching
            if (!string.IsNullOrWhiteSpace(request.OrganizationName))
            {
                var orgMatchResult = CalculateOrganizationNameMatch(
                    request.OrganizationName, context);

                result.OrganizationNameMatchScore = orgMatchResult.Score;
                result.OrganizationNameMatched = orgMatchResult.Score >= OrgNameMatchThreshold;
                result.OrganizationNameMatchDetails = orgMatchResult.Details;

                if (result.OrganizationNameMatched)
                {
                    result.MatchedElements.Add($"Organization name match: {result.OrganizationNameMatchScore:P0} ({orgMatchResult.MatchType})");
                }
                else
                {
                    result.AnalysisFlags.Add($"Organization name not clearly found on website (score: {result.OrganizationNameMatchScore:P0})");
                }

                LogInfo($"Org name matching: '{request.OrganizationName}' -> score={result.OrganizationNameMatchScore:P0}, type={orgMatchResult.MatchType}");
            }

            // Address Matching
            var addressMatchResult = CalculateAddressMatch(request, context);
            result.AddressMatchScore = addressMatchResult.Score;
            result.AddressMatched = addressMatchResult.Score >= AddressMatchThreshold;
            result.AddressMatchDetails = addressMatchResult.Details;

            if (result.AddressMatched)
            {
                result.MatchedElements.Add($"Address match: {result.AddressMatchScore:P0}");
            }
            else if (result.AddressMatchScore > 0)
            {
                result.AnalysisFlags.Add($"Partial address match (score: {result.AddressMatchScore:P0})");
            }
            else
            {
                result.AnalysisFlags.Add("Organization address not found on website");
            }

            LogInfo($"Address matching: city='{request.City}', score={result.AddressMatchScore:P0}");
        }

        private OrgNameMatchResult CalculateOrganizationNameMatch(string orgName, AnalysisContext context)
        {
            var result = new OrgNameMatchResult();
            string normalizedOrgName = NormalizeOrgName(orgName);

            LogInfo($"[MATCH] Org name: '{orgName}' -> normalized: '{normalizedOrgName}'");

            // Strategy 1: Check Schema.org data first (highest confidence)
            if (!string.IsNullOrWhiteSpace(context.SchemaOrgName))
            {
                string normalizedSchemaName = NormalizeOrgName(context.SchemaOrgName);
                if (normalizedSchemaName.Contains(normalizedOrgName) || normalizedOrgName.Contains(normalizedSchemaName))
                {
                    result.Score = 1.0;
                    result.MatchType = "Schema.org Exact Match";
                    result.Details = $"Found in structured data: '{context.SchemaOrgName}'";
                    return result;
                }
            }

            // Strategy 2: Direct containment in priority content
            if (context.NormalizedPriorityContent.Contains(normalizedOrgName))
            {
                result.Score = 1.0;
                result.MatchType = "Priority Content Exact Match";
                result.Details = "Organization name found exactly in title/headings/logo";
                return result;
            }

            // Strategy 3: Check logo alt text specifically (high confidence)
            foreach (var alt in context.LogoAlts)
            {
                string normalizedAlt = NormalizeOrgName(alt);
                if (normalizedAlt.Contains(normalizedOrgName) || normalizedOrgName.Contains(normalizedAlt))
                {
                    result.Score = 0.95;
                    result.MatchType = "Logo Alt Text Match";
                    result.Details = $"Found in logo alt text: '{alt}'";
                    return result;
                }
            }

            // Strategy 4: Check OG site name
            if (!string.IsNullOrWhiteSpace(context.OgSiteName))
            {
                string normalizedSiteName = NormalizeOrgName(context.OgSiteName);
                if (normalizedSiteName.Contains(normalizedOrgName) || normalizedOrgName.Contains(normalizedSiteName))
                {
                    result.Score = 0.95;
                    result.MatchType = "OG Site Name Match";
                    result.Details = $"Found in Open Graph site name: '{context.OgSiteName}'";
                    return result;
                }
            }

            // Strategy 5: Word-by-word matching
            var orgWords = ExtractSignificantWords(normalizedOrgName, 2);
            if (orgWords.Count == 0)
            {
                result.Score = 0;
                result.MatchType = "No Significant Words";
                result.Details = "Could not extract significant words from organization name";
                return result;
            }

            int exactMatches = 0;
            int fuzzyMatches = 0;
            var matchedWords = new List<string>();
            var fuzzyMatchedWords = new List<string>();

            foreach (var word in orgWords)
            {
                if (context.NormalizedContent.Contains(word))
                {
                    exactMatches++;
                    matchedWords.Add(word);
                }
                else
                {
                    // Try fuzzy matching
                    var contentWords = context.NormalizedContent.Split(' ')
                        .Where(w => w.Length >= word.Length - 2 && w.Length <= word.Length + 2);

                    foreach (var contentWord in contentWords)
                    {
                        int distance = LevenshteinDistance(word, contentWord);
                        double similarity = 1.0 - ((double)distance / Math.Max(word.Length, contentWord.Length));
                        if (similarity >= 0.75)
                        {
                            fuzzyMatches++;
                            fuzzyMatchedWords.Add($"{word}~{contentWord}");
                            break;
                        }
                    }
                }
            }

            double exactScore = (double)exactMatches / orgWords.Count;
            double fuzzyScore = (double)fuzzyMatches / orgWords.Count * 0.7;
            result.Score = exactScore + fuzzyScore;

            // Strategy 6: Phrase matching bonus
            if (orgWords.Count >= 2)
            {
                for (int i = 0; i < orgWords.Count - 1; i++)
                {
                    string phrase = orgWords[i] + " " + orgWords[i + 1];
                    if (context.NormalizedContent.Contains(phrase))
                    {
                        result.Score = Math.Max(result.Score, 0.8);
                        result.MatchType = "Phrase Match";
                    }
                }
            }

            result.MatchType = result.MatchType ?? (exactMatches > 0 ? "Word Match" : (fuzzyMatches > 0 ? "Fuzzy Match" : "No Match"));
            result.Details = $"Words matched: [{string.Join(", ", matchedWords)}], Fuzzy: [{string.Join(", ", fuzzyMatchedWords)}]";

            return result;
        }

        private AddressMatchResult CalculateAddressMatch(WebsiteAnalysisRequest request, AnalysisContext context)
        {
            var result = new AddressMatchResult();
            var components = new List<string>();
            double totalScore = 0;
            int componentsChecked = 0;

            // Check Schema.org address first (highest confidence)
            if (context.SchemaAddress != null)
            {
                if (!string.IsNullOrWhiteSpace(request.City) && !string.IsNullOrWhiteSpace(context.SchemaAddress.City))
                {
                    if (NormalizeForMatching(context.SchemaAddress.City).Contains(NormalizeForMatching(request.City)))
                    {
                        totalScore += 1.0;
                        components.Add($"City (Schema): {context.SchemaAddress.City}");
                    }
                    componentsChecked++;
                }
            }

            // City matching
            if (!string.IsNullOrWhiteSpace(request.City) && componentsChecked == 0)
            {
                componentsChecked++;
                string normalizedCity = NormalizeForMatching(request.City);
                if (context.NormalizedContent.Contains(normalizedCity))
                {
                    totalScore += 1.0;
                    components.Add($"City: {request.City}");
                }
                else
                {
                    // Try partial city match
                    var cityWords = normalizedCity.Split(' ').Where(w => w.Length > 2).ToList();
                    int matchedCityWords = cityWords.Count(w => context.NormalizedContent.Contains(w));
                    if (matchedCityWords > 0)
                    {
                        double partialScore = (double)matchedCityWords / cityWords.Count * 0.7;
                        totalScore += partialScore;
                        components.Add($"City partial: {matchedCityWords}/{cityWords.Count}");
                    }
                }
            }

            // Street address matching (combined address1 + address2)
            string combinedAddress = $"{request.Address1} {request.Address2}".Trim();
            if (!string.IsNullOrWhiteSpace(combinedAddress))
            {
                componentsChecked++;
                var addressParts = ExtractAddressParts(combinedAddress);

                if (addressParts.Count > 0)
                {
                    int matchedParts = 0;
                    var matchedPartsList = new List<string>();

                    foreach (var part in addressParts)
                    {
                        if (context.NormalizedContent.Contains(part))
                        {
                            matchedParts++;
                            matchedPartsList.Add(part);
                        }
                    }

                    double partScore = (double)matchedParts / addressParts.Count;
                    totalScore += partScore;
                    if (matchedParts > 0)
                    {
                        components.Add($"Address: {matchedParts}/{addressParts.Count} ({string.Join(", ", matchedPartsList)})");
                    }
                }
            }

            // Postal code matching
            if (!string.IsNullOrWhiteSpace(request.PostalCode))
            {
                componentsChecked++;
                string normalizedPostal = request.PostalCode.ToLowerInvariant().Replace(" ", "");
                if (context.NormalizedContent.Contains(normalizedPostal) ||
                    context.ExtractedPostalCodes.Any(p => p.Replace(" ", "").ToLowerInvariant() == normalizedPostal))
                {
                    totalScore += 1.0;
                    components.Add($"Postal code: {request.PostalCode}");
                }
            }

            result.Score = componentsChecked > 0 ? totalScore / componentsChecked : 0;
            result.Details = components.Count > 0 ? string.Join("; ", components) : "No address components matched";

            return result;
        }

        private List<string> ExtractAddressParts(string address)
        {
            string normalized = NormalizeForMatching(address);

            // Remove common prefixes and suffixes
            var removals = new[] { "street", "st", "avenue", "ave", "road", "rd", "drive", "dr",
                "lane", "ln", "boulevard", "blvd", "way", "court", "ct", "circle", "cir",
                "place", "pl", "terrace", "close", "crescent", "unit", "suite", "ste",
                "apt", "apartment", "floor", "fl" };

            foreach (var removal in removals)
            {
                normalized = Regex.Replace(normalized, $@"\b{removal}\b", " ");
            }

            // Remove unit/apartment numbers
            normalized = Regex.Replace(normalized, @"\b\d+[a-z]?\b", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

            return normalized.Split(' ')
                .Where(p => p.Length > 2 && !Regex.IsMatch(p, @"^\d+$"))
                .Distinct()
                .ToList();
        }

        #endregion

        #region Phase 6: Trust Signal Analysis

        private void PerformTrustSignalAnalysis(AnalysisContext context, WebsiteAnalysisResult result)
        {
            var trustSignals = new List<string>();
            double trustPoints = 0;
            double maxPoints = 100;

            // Essential trust signals
            if (context.HasAboutSection || context.HasAboutPage)
            {
                trustPoints += 10;
                trustSignals.Add("Has About section/page");
            }

            if (context.HasContactPage || context.ContactSections.Count > 0)
            {
                trustPoints += 10;
                trustSignals.Add("Has Contact information");
            }

            if (context.HasMissionPage || context.AboutContent.Any(c => 
                c.ToLowerInvariant().Contains("mission") || c.ToLowerInvariant().Contains("purpose")))
            {
                trustPoints += 8;
                trustSignals.Add("Has Mission/Purpose statement");
            }

            if (context.HasTeamPage || context.HasTeamImages)
            {
                trustPoints += 8;
                trustSignals.Add("Has Team/Staff information");
            }

            if (context.HasProgramsPage || context.MainContent.Any(c => 
                c.ToLowerInvariant().Contains("program") || c.ToLowerInvariant().Contains("service")))
            {
                trustPoints += 8;
                trustSignals.Add("Has Programs/Services information");
            }

            // Contact details
            if (context.ExtractedEmails.Count > 0)
            {
                trustPoints += 5;
                trustSignals.Add($"Has email contact ({context.ExtractedEmails.Count} found)");
            }

            if (context.ExtractedPhones.Count > 0)
            {
                trustPoints += 5;
                trustSignals.Add($"Has phone contact ({context.ExtractedPhones.Count} found)");
            }

            // Schema.org data
            if (!string.IsNullOrWhiteSpace(context.SchemaOrgName))
            {
                trustPoints += 8;
                trustSignals.Add("Has Schema.org organization data");
            }

            // Social media presence
            if (context.SocialMediaLinks.Count >= 2)
            {
                trustPoints += 5;
                trustSignals.Add($"Has social media presence ({context.SocialMediaLinks.Count} links)");
            }

            // Legal pages
            if (context.HasPrivacyPage)
            {
                trustPoints += 5;
                trustSignals.Add("Has Privacy Policy");
            }

            if (context.HasTermsPage)
            {
                trustPoints += 3;
                trustSignals.Add("Has Terms of Service");
            }

            // Registration/Charity number
            if (context.HasRegistrationNumber)
            {
                trustPoints += 10;
                trustSignals.Add("Has registration/charity number");
            }

            // Copyright notice
            if (context.HasCopyright)
            {
                trustPoints += 3;
                trustSignals.Add("Has copyright notice");
            }

            // Donation capability (for nonprofits)
            if (context.HasDonatePage)
            {
                trustPoints += 5;
                trustSignals.Add("Has donation capability");
            }

            // Professional logo
            if (context.HasLogo)
            {
                trustPoints += 5;
                trustSignals.Add("Has logo");
            }

            // HTTPS
            if (context.IsSecure)
            {
                trustPoints += 7;
                trustSignals.Add("Uses HTTPS");
            }

            result.TrustScore = Math.Min(1.0, trustPoints / maxPoints);
            result.TrustSignals = trustSignals;

            if (result.TrustScore >= TrustScoreThreshold)
            {
                result.MatchedElements.Add($"Trust score: {result.TrustScore:P0} ({trustSignals.Count} signals)");
            }
            else
            {
                result.AnalysisFlags.Add($"Low trust score: {result.TrustScore:P0} (below {TrustScoreThreshold:P0} threshold)");
            }
        }

        #endregion

        #region Phase 7: Red Flag Detection

        private void PerformRedFlagDetection(AnalysisContext context, WebsiteAnalysisResult result)
        {
            var redFlags = new List<string>();

            // Generic domain flag (already set in domain analysis)
            if (result.IsGenericOrSocialMediaDomain)
            {
                redFlags.Add($"Website uses generic/free platform: {result.DomainType}");
            }

            // Placeholder/Lorem Ipsum content
            if (ContainsPlaceholderContent(context.FullContent))
            {
                redFlags.Add("Website contains placeholder/Lorem Ipsum content");
            }

            // Very thin content
            if (context.Paragraphs.Count < 3)
            {
                redFlags.Add($"Very thin content (only {context.Paragraphs.Count} paragraphs)");
            }

            // No contact information
            if (context.ExtractedEmails.Count == 0 && context.ExtractedPhones.Count == 0 && 
                context.ContactSections.Count == 0)
            {
                redFlags.Add("No contact information found");
            }

            // Generic email addresses only
            if (context.ExtractedEmails.All(e => IsGenericEmail(e)))
            {
                if (context.ExtractedEmails.Count > 0)
                    redFlags.Add("Only generic email addresses found (gmail, yahoo, etc.)");
            }

            // No about/mission content
            if (!context.HasAboutSection && !context.HasAboutPage && context.AboutContent.Count == 0)
            {
                redFlags.Add("No About/Mission section found");
            }

            // No logo
            if (!context.HasLogo)
            {
                redFlags.Add("No logo found");
            }

            // No navigation structure
            if (context.NavigationLinks.Count < 3)
            {
                redFlags.Add("Minimal or no navigation structure");
            }

            // Single page with little content
            if (context.MainContent.Count <= 1 && context.Paragraphs.Count < 5)
            {
                redFlags.Add("Appears to be a single page with minimal content");
            }

            // Check for suspicious patterns
            if (ContainsSuspiciousPatterns(context.FullContent))
            {
                redFlags.Add("Contains suspicious content patterns");
            }

            result.RedFlags = redFlags;
            result.RedFlagCount = redFlags.Count;

            foreach (var flag in redFlags)
            {
                if (!result.AnalysisFlags.Contains(flag))
                {
                    result.AnalysisFlags.Add($"Red flag: {flag}");
                }
            }
        }

        private bool ContainsPlaceholderContent(string content)
        {
            var placeholderPatterns = new[] {
                "lorem ipsum", "dolor sit amet", "consectetur adipiscing",
                "placeholder text", "[insert", "[add", "[your", "[company name]",
                "sample text", "dummy text", "example.com"
            };

            string lower = content.ToLowerInvariant();
            return placeholderPatterns.Any(p => lower.Contains(p));
        }

        private bool ContainsSuspiciousPatterns(string content)
        {
            var suspiciousPatterns = new[] {
                "send money", "wire transfer", "western union",
                "nigerian prince", "inheritance claim", "lottery winner",
                "urgent response required", "confidential business proposal"
            };

            string lower = content.ToLowerInvariant();
            return suspiciousPatterns.Any(p => lower.Contains(p));
        }

        #endregion

        #region Phase 8: Content Quality Assessment

        private void PerformContentQualityAssessment(AnalysisContext context, WebsiteAnalysisResult result)
        {
            double qualityPoints = 0;
            double maxPoints = 100;
            var qualityIndicators = new List<string>();

            // Content volume
            int totalContentLength = context.Paragraphs.Sum(p => p.Length);
            if (totalContentLength > 5000)
            {
                qualityPoints += 15;
                qualityIndicators.Add($"Substantial content volume ({totalContentLength} chars)");
            }
            else if (totalContentLength > 1000)
            {
                qualityPoints += 8;
                qualityIndicators.Add($"Moderate content volume ({totalContentLength} chars)");
            }
            else if (totalContentLength > 200)
            {
                qualityPoints += 3;
                qualityIndicators.Add($"Minimal content volume ({totalContentLength} chars)");
            }

            // Content diversity (multiple sections)
            int sectionCount = 0;
            if (context.AboutContent.Count > 0) sectionCount++;
            if (context.ContactSections.Count > 0) sectionCount++;
            if (context.MainContent.Count > 0) sectionCount++;
            if (context.FooterContent.Count > 0) sectionCount++;

            if (sectionCount >= 3)
            {
                qualityPoints += 10;
                qualityIndicators.Add($"Well-structured ({sectionCount} distinct sections)");
            }

            // Heading structure
            if (context.Headings.Count >= 5)
            {
                qualityPoints += 10;
                qualityIndicators.Add($"Good heading structure ({context.Headings.Count} headings)");
            }
            else if (context.Headings.Count >= 2)
            {
                qualityPoints += 5;
                qualityIndicators.Add($"Basic heading structure ({context.Headings.Count} headings)");
            }

            // H1 present
            if (context.Headings.Any(h => h.Level == 1))
            {
                qualityPoints += 5;
                qualityIndicators.Add("Has H1 heading");
            }

            // Meta description
            if (!string.IsNullOrWhiteSpace(context.MetaDescription) && context.MetaDescription.Length >= 50)
            {
                qualityPoints += 5;
                qualityIndicators.Add("Has quality meta description");
            }

            // Open Graph data
            if (!string.IsNullOrWhiteSpace(context.OgTitle) && !string.IsNullOrWhiteSpace(context.OgDescription))
            {
                qualityPoints += 5;
                qualityIndicators.Add("Has Open Graph social media data");
            }

            // Structured data
            if (context.StructuredData.Count > 0)
            {
                qualityPoints += 10;
                qualityIndicators.Add($"Has structured data ({context.StructuredData.Count} schemas)");
            }

            // Images with alt text
            if (context.LogoAlts.Count > 0)
            {
                qualityPoints += 5;
                qualityIndicators.Add("Logo has alt text (accessibility)");
            }

            // Nonprofit-specific content indicators
            var nonprofitTerms = new[] { "nonprofit", "non-profit", "charity", "foundation", "501(c)(3)",
                "donate", "volunteer", "mission", "impact", "community", "support", "cause" };
            int nonprofitTermCount = nonprofitTerms.Count(term => 
                context.FullContent.ToLowerInvariant().Contains(term));

            if (nonprofitTermCount >= 5)
            {
                qualityPoints += 15;
                qualityIndicators.Add($"Strong nonprofit language ({nonprofitTermCount} terms)");
            }
            else if (nonprofitTermCount >= 2)
            {
                qualityPoints += 8;
                qualityIndicators.Add($"Some nonprofit language ({nonprofitTermCount} terms)");
            }

            // External credibility indicators
            if (context.ExternalLinks.Count >= 3)
            {
                qualityPoints += 5;
                qualityIndicators.Add($"References external sources ({context.ExternalLinks.Count} links)");
            }

            // Social proof
            if (context.SocialMediaLinks.Count >= 2)
            {
                qualityPoints += 5;
                qualityIndicators.Add($"Social media presence ({context.SocialMediaLinks.Count} platforms)");
            }

            result.ContentQualityScore = Math.Min(1.0, qualityPoints / maxPoints);
            result.ContentQualityIndicators = qualityIndicators;

            // Determine if content is meaningful
            result.HasMeaningfulContent = result.ContentQualityScore >= ContentQualityThreshold;

            if (!result.HasMeaningfulContent)
            {
                result.AnalysisFlags.Add($"Website lacks meaningful organizational content (quality score: {result.ContentQualityScore:P0})");
            }
        }

        #endregion

        #region Phase 9: Final Score Calculation

        private void CalculateFinalScores(WebsiteAnalysisResult result, AnalysisContext context)
        {
            // Weighted composite score
            double orgNameWeight = 0.30;
            double addressWeight = 0.15;
            double trustWeight = 0.25;
            double qualityWeight = 0.20;
            double structuralWeight = 0.10;

            // Apply penalties for generic domains
            double genericPenalty = result.IsGenericOrSocialMediaDomain ? 0.85 : 1.0;

            // Apply penalties for red flags
            double redFlagPenalty = 1.0 - (result.RedFlagCount * 0.05);
            redFlagPenalty = Math.Max(0.5, redFlagPenalty);

            result.OverallContentScore = (
                (result.OrganizationNameMatchScore * orgNameWeight) +
                (result.AddressMatchScore * addressWeight) +
                (result.TrustScore * trustWeight) +
                (result.ContentQualityScore * qualityWeight) +
                (result.StructuralScore * structuralWeight)
            ) * genericPenalty * redFlagPenalty;

            result.OverallContentScore = Math.Min(1.0, Math.Max(0.0, result.OverallContentScore));

            // Generate overall assessment
            result.OverallAssessment = GenerateOverallAssessment(result);

            LogInfo($"Final scores - Org: {result.OrganizationNameMatchScore:P0}, " +
                $"Addr: {result.AddressMatchScore:P0}, Trust: {result.TrustScore:P0}, " +
                $"Quality: {result.ContentQualityScore:P0}, Structural: {result.StructuralScore:P0}, " +
                $"Overall: {result.OverallContentScore:P0}");
        }

        private string GenerateOverallAssessment(WebsiteAnalysisResult result)
        {
            var sb = new StringBuilder();

            if (result.OverallContentScore >= 0.7)
            {
                sb.AppendLine("ASSESSMENT: HIGH CONFIDENCE - Website appears legitimate");
            }
            else if (result.OverallContentScore >= 0.5)
            {
                sb.AppendLine("ASSESSMENT: MODERATE CONFIDENCE - Website shows some legitimacy indicators");
            }
            else if (result.OverallContentScore >= 0.3)
            {
                sb.AppendLine("ASSESSMENT: LOW CONFIDENCE - Website has limited legitimacy indicators");
            }
            else
            {
                sb.AppendLine("ASSESSMENT: VERY LOW CONFIDENCE - Website lacks legitimacy indicators");
            }

            sb.AppendLine();
            sb.AppendLine($"Organization Name Match: {result.OrganizationNameMatchScore:P0} ({(result.OrganizationNameMatched ? "PASS" : "FAIL")})");
            sb.AppendLine($"Address Match: {result.AddressMatchScore:P0} ({(result.AddressMatched ? "PASS" : "FAIL")})");
            sb.AppendLine($"Trust Score: {result.TrustScore:P0}");
            sb.AppendLine($"Content Quality: {result.ContentQualityScore:P0}");

            if (result.RedFlagCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"RED FLAGS ({result.RedFlagCount}):");
                foreach (var flag in result.RedFlags)
                {
                    sb.AppendLine($"  • {flag}");
                }
            }

            return sb.ToString();
        }

        #endregion

        #region Helper Methods

        private string NormalizeUrl(string url)
        {
            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }
            return url;
        }

        private List<string> GenerateAlternateUrls(string url)
        {
            var alternates = new List<string>();

            if (url.StartsWith("https://"))
                alternates.Add(url.Replace("https://", "http://"));

            if (!url.Contains("www."))
                alternates.Add(url.Replace("://", "://www."));
            else
                alternates.Add(url.Replace("://www.", "://"));

            return alternates;
        }

        private string ExtractDomain(string url)
        {
            try
            {
                string domain = url.Trim().ToLowerInvariant();
                domain = Regex.Replace(domain, @"^https?://", "");
                domain = Regex.Replace(domain, @"^www\.", "");
                int pathIndex = domain.IndexOf('/');
                if (pathIndex > 0) domain = domain.Substring(0, pathIndex);
                int queryIndex = domain.IndexOf('?');
                if (queryIndex > 0) domain = domain.Substring(0, queryIndex);
                return domain;
            }
            catch { return ""; }
        }

        private string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"<[^>]+>", " ");
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        private string NormalizeForMatching(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = text.ToLowerInvariant();
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"[^\w\s\-]", " ");
            text = Regex.Replace(text, @"[\-–—]", " ");
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        private string NormalizeOrgName(string orgName)
        {
            if (string.IsNullOrWhiteSpace(orgName)) return "";

            string text = NormalizeForMatching(orgName);

            var suffixes = new[] {
                "inc", "incorporated", "llc", "corp", "corporation", "company", "co",
                "ltd", "limited", "foundation", "org", "organization", "nonprofit",
                "non profit", "ngo", "association", "assoc", "society", "institute",
                "group", "trust", "charity", "the", "a", "an",
                "ev", "e v", "gmbh", "ag", "stiftung", "verein",
                "sarl", "sas", "sa", "fondation", "ong",
                "sl", "fundacion", "asociacion",
                "srl", "spa", "fondazione", "associazione", "onlus",
                "bv", "nv", "stichting", "vereniging",
                "ltda", "fundacao", "associacao"
            };

            foreach (var suffix in suffixes)
            {
                text = Regex.Replace(text, $@"\s+{Regex.Escape(suffix)}$", "", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, $@"^{Regex.Escape(suffix)}\s+", "", RegexOptions.IgnoreCase);
            }

            return text.Trim();
        }

        private List<string> ExtractSignificantWords(string text, int minLength = 3)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();

            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
                "by", "from", "as", "is", "was", "are", "were", "been", "be", "have", "has", "had",
                "inc", "llc", "ltd", "org", "der", "die", "das", "ein", "eine", "und",
                "le", "la", "les", "un", "une", "et", "el", "los", "las"
            };

            return NormalizeForMatching(text)
                .Split(' ')
                .Where(w => w.Length >= minLength && !stopWords.Contains(w))
                .Distinct()
                .ToList();
        }

        private int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
            if (string.IsNullOrEmpty(target)) return source.Length;

            int[,] d = new int[source.Length + 1, target.Length + 1];
            for (int i = 0; i <= source.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= target.Length; j++) d[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[source.Length, target.Length];
        }

        private bool IsAboutPage(string href, string text)
        {
            var patterns = new[] { "about", "who-we-are", "our-story", "about-us" };
            return patterns.Any(p => href.Contains(p) || text.ToLowerInvariant().Contains(p));
        }

        private bool IsContactPage(string href, string text)
        {
            var patterns = new[] { "contact", "get-in-touch", "reach-us" };
            return patterns.Any(p => href.Contains(p) || text.ToLowerInvariant().Contains(p));
        }

        private bool IsMissionPage(string href, string text)
        {
            var patterns = new[] { "mission", "purpose", "vision", "values" };
            return patterns.Any(p => href.Contains(p) || text.ToLowerInvariant().Contains(p));
        }

        private bool IsTeamPage(string href, string text)
        {
            var patterns = new[] { "team", "staff", "people", "leadership", "board" };
            return patterns.Any(p => href.Contains(p) || text.ToLowerInvariant().Contains(p));
        }

        private bool IsProgramsPage(string href, string text)
        {
            var patterns = new[] { "program", "service", "what-we-do", "our-work" };
            return patterns.Any(p => href.Contains(p) || text.ToLowerInvariant().Contains(p));
        }

        private bool IsDonatePage(string href, string text)
        {
            var patterns = new[] { "donate", "support", "give", "contribute" };
            return patterns.Any(p => href.Contains(p) || text.ToLowerInvariant().Contains(p));
        }

        private bool IsPrivacyPage(string href, string text)
        {
            var patterns = new[] { "privacy", "privacy-policy" };
            return patterns.Any(p => href.Contains(p) || text.ToLowerInvariant().Contains(p));
        }

        private bool IsTermsPage(string href, string text)
        {
            var patterns = new[] { "terms", "terms-of-service", "terms-of-use", "tos" };
            return patterns.Any(p => href.Contains(p) || text.ToLowerInvariant().Contains(p));
        }

        private bool IsSocialMediaLink(string href)
        {
            var socialDomains = new[] {
                "facebook.com", "twitter.com", "x.com", "instagram.com", "linkedin.com",
                "youtube.com", "pinterest.com", "tiktok.com"
            };
            return socialDomains.Any(d => href.Contains(d));
        }

        private bool IsGenericEmail(string email)
        {
            var genericDomains = new[] {
                "gmail.com", "yahoo.com", "hotmail.com", "outlook.com", "live.com",
                "aol.com", "mail.com", "protonmail.com", "icloud.com"
            };
            return genericDomains.Any(d => email.EndsWith("@" + d));
        }

        private void LogInfo(string message)
        {
            try { DynamicsInterface.writeToLog(message); } catch { }
        }

        private void LogError(string message)
        {
            try { DynamicsInterface.writeToLog($"[ERROR] {message}"); } catch { }
        }

        #endregion

        #region Generic Domain Detection

        private static readonly HashSet<string> GenericDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Social Media
            "facebook.com", "fb.com", "instagram.com", "twitter.com", "x.com", "linkedin.com",
            "pinterest.com", "tiktok.com", "snapchat.com", "reddit.com", "tumblr.com",
            "youtube.com", "vimeo.com", "flickr.com", "medium.com", "quora.com",
            
            // Free Website Builders
            "wix.com", "wixsite.com", "weebly.com", "squarespace.com", "wordpress.com",
            "blogger.com", "blogspot.com", "sites.google.com", "github.io", "netlify.app",
            "vercel.app", "herokuapp.com", "000webhostapp.com", "jimdo.com", "strikingly.com",
            "carrd.co", "webnode.com", "site123.com", "tilda.cc", "webflow.io",
            
            // Email Providers
            "gmail.com", "yahoo.com", "hotmail.com", "outlook.com", "live.com", "aol.com",
            
            // URL Shorteners
            "bit.ly", "goo.gl", "tinyurl.com", "t.co",
            
            // Crowdfunding
            "gofundme.com", "kickstarter.com", "indiegogo.com", "patreon.com",
            
            // E-commerce
            "shopify.com", "myshopify.com", "etsy.com", "ebay.com",
            
            // Link Aggregators
            "linktr.ee", "linktree.com", "beacons.ai", "bio.link"
        };

        public static (bool IsGeneric, string DomainType, string Message) CheckIfGenericOrSocialMediaDomain(string website)
        {
            if (string.IsNullOrWhiteSpace(website))
                return (false, null, null);

            try
            {
                string domain = website.Trim().ToLowerInvariant();
                domain = Regex.Replace(domain, @"^https?://", "");
                domain = Regex.Replace(domain, @"^www\.", "");
                int pathIndex = domain.IndexOf('/');
                if (pathIndex > 0) domain = domain.Substring(0, pathIndex);

                if (GenericDomains.Contains(domain))
                {
                    string type = GetDomainType(domain);
                    return (true, type, $"Website uses {type}: {domain}");
                }

                foreach (var genericDomain in GenericDomains)
                {
                    if (domain.EndsWith("." + genericDomain))
                    {
                        string type = GetDomainType(genericDomain);
                        return (true, type, $"Website is hosted on {type}: {genericDomain}");
                    }
                }

                return (false, null, null);
            }
            catch
            {
                return (false, null, null);
            }
        }

        private static string GetDomainType(string domain)
        {
            domain = domain.ToLowerInvariant();

            if (new[] { "facebook.com", "instagram.com", "twitter.com", "x.com", "linkedin.com" }.Contains(domain))
                return "Social Media Platform";
            if (new[] { "wix.com", "wixsite.com", "weebly.com", "squarespace.com", "wordpress.com" }.Contains(domain))
                return "Free Website Builder";
            if (new[] { "github.io", "netlify.app", "vercel.app", "herokuapp.com" }.Contains(domain))
                return "Developer Hosting";
            if (new[] { "gofundme.com", "kickstarter.com", "patreon.com" }.Contains(domain))
                return "Crowdfunding Platform";
            if (new[] { "gmail.com", "yahoo.com", "hotmail.com" }.Contains(domain))
                return "Email Provider";

            return "Generic/Free Platform";
        }

        #endregion
    }

    #region Supporting Classes

    /// <summary>
    /// Request object for website analysis
    /// </summary>
    public class WebsiteAnalysisRequest
    {
        public string Website { get; set; }
        public string OrganizationName { get; set; }
        public string Address1 { get; set; }
        public string Address2 { get; set; }
        public string City { get; set; }
        public string RegionCode { get; set; }
        public string CountryCode { get; set; }
        public string PostalCode { get; set; }
        public string TransactionId { get; set; }
    }

    /// <summary>
    /// Internal context for analysis processing
    /// </summary>
    internal class AnalysisContext
    {
        // Raw data
        public string RawHtml { get; set; }
        public string FinalUrl { get; set; }
        public string Domain { get; set; }
        public bool IsSecure { get; set; }
        public HtmlDocument Document { get; set; }

        // Metadata
        public string PageTitle { get; set; }
        public string MetaDescription { get; set; }
        public string MetaKeywords { get; set; }
        public string OgTitle { get; set; }
        public string OgDescription { get; set; }
        public string OgSiteName { get; set; }
        public string OgType { get; set; }
        public string OgImage { get; set; }
        public string TwitterTitle { get; set; }
        public string TwitterDescription { get; set; }
        public string CanonicalUrl { get; set; }
        public string Language { get; set; }

        // Structured data
        public List<JToken> StructuredData { get; set; } = new List<JToken>();
        public string SchemaOrgName { get; set; }
        public string SchemaOrgDescription { get; set; }
        public AddressInfo SchemaAddress { get; set; }
        public string SchemaPhone { get; set; }
        public string SchemaEmail { get; set; }

        // Content
        public List<HeadingInfo> Headings { get; set; } = new List<HeadingInfo>();
        public List<LinkInfo> NavigationLinks { get; set; } = new List<LinkInfo>();
        public List<string> MainContent { get; set; } = new List<string>();
        public List<string> AboutContent { get; set; } = new List<string>();
        public List<string> Paragraphs { get; set; } = new List<string>();
        public List<string> ContactSections { get; set; } = new List<string>();
        public List<string> FooterContent { get; set; } = new List<string>();

        // Images
        public List<string> LogoAlts { get; set; } = new List<string>();
        public List<string> LogoTitles { get; set; } = new List<string>();
        public bool HasLogo { get; set; }
        public bool HasTeamImages { get; set; }
        public int TeamImageCount { get; set; }

        // Links
        public List<string> SocialMediaLinks { get; set; } = new List<string>();
        public List<string> ExternalLinks { get; set; } = new List<string>();

        // Extracted contact info
        public HashSet<string> ExtractedEmails { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExtractedPhones { get; set; } = new HashSet<string>();
        public HashSet<string> ExtractedPostalCodes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Page structure flags
        public bool HasAboutPage { get; set; }
        public bool HasContactPage { get; set; }
        public bool HasMissionPage { get; set; }
        public bool HasTeamPage { get; set; }
        public bool HasProgramsPage { get; set; }
        public bool HasDonatePage { get; set; }
        public bool HasPrivacyPage { get; set; }
        public bool HasTermsPage { get; set; }
        public bool HasAboutSection { get; set; }
        public bool HasCopyright { get; set; }
        public string CopyrightText { get; set; }
        public bool HasRegistrationNumber { get; set; }

        // Searchable content
        public string PriorityContent { get; set; }
        public string FullContent { get; set; }
        public string NormalizedContent { get; set; }
        public string NormalizedPriorityContent { get; set; }

        // Domain flags
        public List<string> DomainFlags { get; set; } = new List<string>();

        public string GetExtractedContentSummary(int maxLength)
        {
            string summary = $"TITLE: {PageTitle}\n" +
                $"META_DESC: {MetaDescription}\n" +
                $"OG_SITE_NAME: {OgSiteName}\n\n" +
                $"HEADINGS:\n{string.Join("\n", Headings.Take(10).Select(h => $"  H{h.Level}: {h.Text}"))}\n\n" +
                $"LOGO_ALTS:\n{string.Join("\n", LogoAlts.Take(5).Select(a => $"  {a}"))}\n\n" +
                $"CONTENT SAMPLE:\n{(FullContent?.Length > 1000 ? FullContent.Substring(0, 1000) + "..." : FullContent)}";

            return summary.Length > maxLength ? summary.Substring(0, maxLength) : summary;
        }
    }

    internal class HeadingInfo
    {
        public int Level { get; set; }
        public string Text { get; set; }
    }

    internal class LinkInfo
    {
        public string Href { get; set; }
        public string Text { get; set; }
    }

    internal class AddressInfo
    {
        public string Street { get; set; }
        public string City { get; set; }
        public string Region { get; set; }
        public string PostalCode { get; set; }
        public string Country { get; set; }
    }

    internal class FetchResult
    {
        public bool Success { get; set; }
        public string Content { get; set; }
        public string FinalUrl { get; set; }
        public string Error { get; set; }
        public int Attempts { get; set; }
    }

    internal class OrgNameMatchResult
    {
        public double Score { get; set; }
        public string MatchType { get; set; }
        public string Details { get; set; }
    }

    internal class AddressMatchResult
    {
        public double Score { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Structural metrics for the analyzed website
    /// </summary>
    public class StructuralMetrics
    {
        public bool HasProperHtmlStructure { get; set; }
        public bool HasHead { get; set; }
        public bool HasBody { get; set; }
        public bool HasTitle { get; set; }
        public bool HasMetaDescription { get; set; }
        public bool HasOpenGraph { get; set; }
        public bool HasStructuredData { get; set; }
        public int HeadingCount { get; set; }
        public int H1Count { get; set; }
        public int NavigationLinkCount { get; set; }
        public bool HasNavigation { get; set; }
        public bool HasFooter { get; set; }
        public bool HasLogo { get; set; }
        public bool HasAboutPage { get; set; }
        public bool HasContactPage { get; set; }
        public bool HasMissionPage { get; set; }
        public bool HasTeamPage { get; set; }
        public bool HasProgramsPage { get; set; }
        public bool HasDonatePage { get; set; }
        public bool HasPrivacyPolicy { get; set; }
        public bool HasTermsOfService { get; set; }
    }

    #endregion
}
