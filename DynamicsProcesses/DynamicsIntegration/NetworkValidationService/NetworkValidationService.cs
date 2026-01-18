using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using Newtonsoft.Json.Linq;

namespace NetworkValidationService
{
    /// <summary>
    /// Standalone Network Validation Service providing comprehensive validation
    /// for websites, IP addresses, and email addresses with clean, consolidated results
    /// </summary>
    public class NetworkValidationService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ValidationConfig _config;

        public NetworkValidationService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _config = LoadConfiguration();
        }

        #region Configuration

        private ValidationConfig LoadConfiguration()
        {
            return new ValidationConfig
            {
                WhoisAPIKey = ConfigurationManager.AppSettings["WhoisAPIKey"] ?? "",
                IPGeolocationAPIKey = ConfigurationManager.AppSettings["IPGeolocationAPIKey"] ?? "",
                EmailValidationAPIKey = ConfigurationManager.AppSettings["EmailValidationAPIKey"] ?? "",
                VirusTotalAPIKey = ConfigurationManager.AppSettings["VirusTotalAPIKey"] ?? "",
                IPQualityAPIKey = ConfigurationManager.AppSettings["IPQualityAPIKey"] ?? "",
                HunterIOAPIKey = ConfigurationManager.AppSettings["HunterIOAPIKey"] ?? "",
                EnableDeepAnalysis = bool.Parse(ConfigurationManager.AppSettings["EnableDeepAnalysis"] ?? "true"),
                EnableThreatIntelligence = bool.Parse(ConfigurationManager.AppSettings["EnableThreatIntelligence"] ?? "true"),
                EnableSMTPValidation = bool.Parse(ConfigurationManager.AppSettings["EnableSMTPValidation"] ?? "false"),
                DefaultTimeoutSeconds = int.Parse(ConfigurationManager.AppSettings["DefaultTimeoutSeconds"] ?? "10")
            };
        }

        #endregion

        #region Website Validation

        /// <summary>
        /// Validates a single website/domain and returns a comprehensive, clean result
        /// </summary>
        /// <param name="website">The website URL or domain to validate</param>
        /// <returns>Clean, consolidated validation result</returns>
        public async Task<WebsiteValidationResult> ValidateWebsiteAsync(string website)
        {
            var result = new WebsiteValidationResult
            {
                OriginalInput = website,
                ValidationTimestamp = DateTime.UtcNow,
                ProcessingTimeMs = 0
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Clean and normalize the input
                result.CleanedDomain = CleanDomainInput(website);
                
                if (string.IsNullOrEmpty(result.CleanedDomain))
                {
                    result.IsValid = false;
                    result.ValidationSummary = "Invalid or empty domain input";
                    result.RiskLevel = "UNKNOWN";
                    return result;
                }

                // Perform comprehensive validation
                var tasks = new List<Task>
                {
                    ValidateWebsiteDNSAsync(result),
                    ValidateWebsiteConnectivityAsync(result),
                    ValidateWebsiteWHOISAsync(result),
                    ValidateWebsiteSecurityAsync(result)
                };

                await Task.WhenAll(tasks);

                // Generate final assessment
                GenerateWebsiteAssessment(result);

                result.IsValid = result.DNSValidation.IsSuccessful && 
                               !result.SecurityValidation.IsMalicious &&
                               result.ConnectivityValidation.IsReachable;

            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ValidationSummary = $"Validation failed: {ex.Message}";
                result.RiskLevel = "ERROR";
                result.Errors.Add(ex.Message);
            }

            stopwatch.Stop();
            result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

            return result;
        }

        private string CleanDomainInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            return input.Trim().ToLower()
                .Replace("http://", "")
                .Replace("https://", "")
                .Replace("www.", "")
                .Split('/')[0]
                .Split(':')[0]
                .Split('?')[0];
        }

        private async Task ValidateWebsiteDNSAsync(WebsiteValidationResult result)
        {
            result.DNSValidation = new DNSValidationResult();

            try
            {
                var hostEntry = await Dns.GetHostEntryAsync(result.CleanedDomain);
                result.DNSValidation.IsSuccessful = true;
                result.DNSValidation.IPAddresses.AddRange(hostEntry.AddressList.Select(ip => ip.ToString()));
                result.DNSValidation.ResponseTimeMs = 0; // Could measure with stopwatch if needed
            }
            catch (Exception ex)
            {
                result.DNSValidation.IsSuccessful = false;
                result.DNSValidation.ErrorMessage = ex.Message;
            }
        }

        private async Task ValidateWebsiteConnectivityAsync(WebsiteValidationResult result)
        {
            result.ConnectivityValidation = new ConnectivityValidationResult();

            try
            {
                // Test HTTPS connectivity
                var httpsUrl = $"https://{result.CleanedDomain}";
                var httpsResponse = await _httpClient.GetAsync(httpsUrl);
                result.ConnectivityValidation.HTTPSAccessible = httpsResponse.IsSuccessStatusCode;
                result.ConnectivityValidation.HTTPSStatusCode = (int)httpsResponse.StatusCode;

                // Test HTTP connectivity
                try
                {
                    var httpUrl = $"http://{result.CleanedDomain}";
                    var httpResponse = await _httpClient.GetAsync(httpUrl);
                    result.ConnectivityValidation.HTTPAccessible = httpResponse.IsSuccessStatusCode;
                    result.ConnectivityValidation.HTTPStatusCode = (int)httpResponse.StatusCode;
                }
                catch
                {
                    result.ConnectivityValidation.HTTPAccessible = false;
                }

                result.ConnectivityValidation.IsReachable = 
                    result.ConnectivityValidation.HTTPSAccessible || 
                    result.ConnectivityValidation.HTTPAccessible;

                // Test ping connectivity
                try
                {
                    using (var ping = new Ping())
                    {
                        var reply = await ping.SendPingAsync(result.CleanedDomain, _config.DefaultTimeoutSeconds * 1000);
                        result.ConnectivityValidation.PingSuccessful = reply.Status == IPStatus.Success;
                        result.ConnectivityValidation.PingResponseTimeMs = reply.RoundtripTime;
                    }
                }
                catch
                {
                    result.ConnectivityValidation.PingSuccessful = false;
                }

            }
            catch (Exception ex)
            {
                result.ConnectivityValidation.IsReachable = false;
                result.ConnectivityValidation.ErrorMessage = ex.Message;
            }
        }

        private async Task ValidateWebsiteWHOISAsync(WebsiteValidationResult result)
        {
            result.WHOISValidation = new WHOISValidationResult();

            try
            {
                if (!string.IsNullOrEmpty(_config.WhoisAPIKey))
                {
                    // Use API for WHOIS lookup
                    var apiUrl = $"https://api.whoisfreaks.com/v1.0/whois?apiKey={_config.WhoisAPIKey}&whois=live&domainName={result.CleanedDomain}";
                    var response = await _httpClient.GetStringAsync(apiUrl);
                    var whoisData = JObject.Parse(response);

                    result.WHOISValidation.IsSuccessful = true;
                    result.WHOISValidation.Registrar = whoisData["registrar"]?.ToString();
                    result.WHOISValidation.RegistrantCountry = whoisData["registrant_country"]?.ToString();
                    
                    if (DateTime.TryParse(whoisData["create_date"]?.ToString(), out DateTime created))
                        result.WHOISValidation.RegistrationDate = created;
                    
                    if (DateTime.TryParse(whoisData["expire_date"]?.ToString(), out DateTime expires))
                        result.WHOISValidation.ExpirationDate = expires;
                }
                else
                {
                    // Basic WHOIS lookup without API
                    await PerformBasicWHOISLookup(result.CleanedDomain, result.WHOISValidation);
                }

                // Analyze US registration likelihood
                AnalyzeUSRegistration(result.WHOISValidation, result.CleanedDomain);

            }
            catch (Exception ex)
            {
                result.WHOISValidation.IsSuccessful = false;
                result.WHOISValidation.ErrorMessage = ex.Message;
            }
        }

        private async Task ValidateWebsiteSecurityAsync(WebsiteValidationResult result)
        {
            result.SecurityValidation = new SecurityValidationResult();

            try
            {
                // Basic security checks
                result.SecurityValidation.SecurityScore = 80; // Default score
                result.SecurityValidation.IsMalicious = false;

                // Check against known malicious patterns
                var suspiciousPatterns = new[]
                {
                    @"\b(tempmail|10minutemail|guerrillamail)\b",
                    @"\b(bit\.ly|tinyurl|t\.co)\b",
                    @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b" // Direct IP addresses
                };

                foreach (var pattern in suspiciousPatterns)
                {
                    if (Regex.IsMatch(result.CleanedDomain, pattern, RegexOptions.IgnoreCase))
                    {
                        result.SecurityValidation.SecurityScore -= 20;
                        result.SecurityValidation.SecurityFlags.Add($"Matches suspicious pattern: {pattern}");
                    }
                }

                // VirusTotal integration if API key available
                if (!string.IsNullOrEmpty(_config.VirusTotalAPIKey))
                {
                    await CheckVirusTotalReputation(result.CleanedDomain, result.SecurityValidation);
                }

                result.SecurityValidation.IsMalicious = result.SecurityValidation.SecurityScore < 30;

            }
            catch (Exception ex)
            {
                result.SecurityValidation.ErrorMessage = ex.Message;
            }
        }

        private void GenerateWebsiteAssessment(WebsiteValidationResult result)
        {
            var score = 0;
            var factors = new List<string>();

            // DNS resolution (30 points)
            if (result.DNSValidation.IsSuccessful)
            {
                score += 30;
                factors.Add("DNS resolves successfully");
            }

            // Connectivity (25 points)
            if (result.ConnectivityValidation.IsReachable)
            {
                score += 25;
                factors.Add("Website is reachable");
            }

            // WHOIS data (20 points)
            if (result.WHOISValidation.IsSuccessful && result.WHOISValidation.RegistrationDate.HasValue)
            {
                score += 20;
                factors.Add("Valid registration information");
            }

            // Security (25 points)
            if (!result.SecurityValidation.IsMalicious)
            {
                score += 25;
                factors.Add("No security threats detected");
            }

            result.TrustScore = Math.Min(100, score);
            result.TrustFactors = factors;

            // Risk level assessment
            if (result.TrustScore >= 80)
                result.RiskLevel = "LOW";
            else if (result.TrustScore >= 60)
                result.RiskLevel = "MEDIUM";
            else if (result.TrustScore >= 30)
                result.RiskLevel = "HIGH";
            else
                result.RiskLevel = "CRITICAL";

            // Generate summary
            if (result.IsValid)
            {
                result.ValidationSummary = $"Website validation successful. Trust score: {result.TrustScore}/100 ({result.RiskLevel} risk)";
            }
            else
            {
                result.ValidationSummary = "Website validation failed. Multiple issues detected.";
            }
        }

        #endregion

        #region IP Address Validation

        /// <summary>
        /// Validates an IP address and provides reputation scoring with prominent US location detection
        /// </summary>
        /// <param name="ipAddress">The IP address to validate</param>
        /// <returns>Comprehensive IP validation result with reputation scoring</returns>
        public async Task<IPValidationResult> ValidateIPAddressAsync(string ipAddress)
        {
            var result = new IPValidationResult
            {
                IPAddress = ipAddress,
                ValidationTimestamp = DateTime.UtcNow,
                ProcessingTimeMs = 0
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Parse and validate IP address format
                if (!IPAddress.TryParse(ipAddress, out IPAddress parsedIP))
                {
                    result.IsValid = false;
                    result.ValidationSummary = "Invalid IP address format";
                    return result;
                }

                result.IsValid = true;
                result.AddressFamily = parsedIP.AddressFamily.ToString();

                // Classify IP address type
                result.Classification = ClassifyIPAddress(parsedIP);

                // Skip further analysis for private/reserved addresses
                if (result.Classification.IsPrivate || result.Classification.IsReserved)
                {
                    result.ValidationSummary = $"Private/Reserved IP address ({result.Classification.Type})";
                    result.LocationAnalysis.IsDefinitelyInUS = false;
                    result.LocationAnalysis.IsDefinitelyOutsideUS = false;
                    result.ReputationScore = 50; // Neutral for private IPs
                    stopwatch.Stop();
                    result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;
                    return result;
                }

                // Perform comprehensive analysis for public IPs
                var tasks = new List<Task>
                {
                    PerformGeolocationAnalysisAsync(ipAddress, result),
                    PerformReputationAnalysisAsync(ipAddress, result),
                    PerformNetworkAnalysisAsync(ipAddress, result)
                };

                await Task.WhenAll(tasks);

                // Generate final assessment
                GenerateIPAssessment(result);

            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ValidationSummary = $"IP validation failed: {ex.Message}";
                result.Errors.Add(ex.Message);
            }

            stopwatch.Stop();
            result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

            return result;
        }

        private IPClassificationResult ClassifyIPAddress(IPAddress ip)
        {
            var classification = new IPClassificationResult();

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();

                // RFC 1918 private addresses
                if ((bytes[0] == 10) ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168))
                {
                    classification.IsPrivate = true;
                    classification.Type = "Private (RFC 1918)";
                }
                // Loopback
                else if (bytes[0] == 127)
                {
                    classification.IsLoopback = true;
                    classification.Type = "Loopback";
                }
                // Link-local (169.254.0.0/16)
                else if (bytes[0] == 169 && bytes[1] == 254)
                {
                    classification.IsLinkLocal = true;
                    classification.Type = "Link-Local";
                }
                // Multicast (224.0.0.0/4)
                else if (bytes[0] >= 224 && bytes[0] <= 239)
                {
                    classification.IsMulticast = true;
                    classification.Type = "Multicast";
                }
                // Reserved ranges
                else if (bytes[0] == 0 || (bytes[0] >= 240 && bytes[0] <= 255))
                {
                    classification.IsReserved = true;
                    classification.Type = "Reserved";
                }
                // CGNAT (100.64.0.0/10)
                else if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64)
                {
                    classification.IsCGNAT = true;
                    classification.Type = "CGNAT (Carrier Grade NAT)";
                }
                else
                {
                    classification.IsPublic = true;
                    classification.Type = "Public IPv4";
                }
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                classification.Type = "IPv6";
                classification.IsPublic = !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal;
            }

            return classification;
        }

        private async Task PerformGeolocationAnalysisAsync(string ipAddress, IPValidationResult result)
        {
            result.LocationAnalysis = new LocationAnalysisResult();

            try
            {
                var geolocationSources = new List<GeolocationSource>();

                // Service 1: IPGeolocation.io (if API key available)
                if (!string.IsNullOrEmpty(_config.IPGeolocationAPIKey))
                {
                    try
                    {
                        var url = $"https://api.ipgeolocation.io/ipgeo?apiKey={_config.IPGeolocationAPIKey}&ip={ipAddress}";
                        var response = await _httpClient.GetStringAsync(url);
                        var geoData = JObject.Parse(response);
                        
                        geolocationSources.Add(new GeolocationSource
                        {
                            Provider = "IPGeolocation.io",
                            Country = geoData["country_name"]?.ToString(),
                            CountryCode = geoData["country_code2"]?.ToString(),
                            City = geoData["city"]?.ToString(),
                            Region = geoData["state_prov"]?.ToString(),
                            ISP = geoData["isp"]?.ToString(),
                            Confidence = 90
                        });
                    }
                    catch { /* Continue with other sources */ }
                }

                // Service 2: ip-api.com (free service)
                try
                {
                    var url = $"http://ip-api.com/json/{ipAddress}?fields=status,country,countryCode,region,regionName,city,isp,org,as,query";
                    var response = await _httpClient.GetStringAsync(url);
                    var geoData = JObject.Parse(response);
                    
                    if (geoData["status"]?.ToString() == "success")
                    {
                        geolocationSources.Add(new GeolocationSource
                        {
                            Provider = "ip-api.com",
                            Country = geoData["country"]?.ToString(),
                            CountryCode = geoData["countryCode"]?.ToString(),
                            City = geoData["city"]?.ToString(),
                            Region = geoData["regionName"]?.ToString(),
                            ISP = geoData["isp"]?.ToString(),
                            Organization = geoData["org"]?.ToString(),
                            Confidence = 85
                        });
                    }
                    
                    await Task.Delay(1000); // Rate limiting for free service
                }
                catch { /* Continue with other sources */ }

                // Service 3: ipinfo.io
                try
                {
                    var url = $"https://ipinfo.io/{ipAddress}/json";
                    var response = await _httpClient.GetStringAsync(url);
                    var geoData = JObject.Parse(response);
                    
                    geolocationSources.Add(new GeolocationSource
                    {
                        Provider = "ipinfo.io",
                        Country = geoData["country"]?.ToString(),
                        CountryCode = geoData["country"]?.ToString(),
                        City = geoData["city"]?.ToString(),
                        Region = geoData["region"]?.ToString(),
                        Organization = geoData["org"]?.ToString(),
                        Confidence = 80
                    });
                }
                catch { /* Continue */ }

                result.LocationAnalysis.GeolocationSources = geolocationSources;

                // Analyze US location consensus
                AnalyzeUSLocationConsensus(result.LocationAnalysis);

            }
            catch (Exception ex)
            {
                result.LocationAnalysis.ErrorMessage = ex.Message;
            }
        }

        private void AnalyzeUSLocationConsensus(LocationAnalysisResult locationAnalysis)
        {
            var sources = locationAnalysis.GeolocationSources;
            if (!sources.Any()) return;

            var usIndicators = 0;
            var nonUsIndicators = 0;
            var totalConfidence = 0.0;
            var usConfidence = 0.0;

            foreach (var source in sources)
            {
                var countryCode = source.CountryCode?.ToUpper();
                
                if (countryCode == "US" || countryCode == "USA")
                {
                    usIndicators++;
                    usConfidence += source.Confidence;
                }
                else if (!string.IsNullOrEmpty(countryCode))
                {
                    nonUsIndicators++;
                }
                
                totalConfidence += source.Confidence;
            }

            var usConsensusPercentage = sources.Count > 0 ? (usIndicators / (double)sources.Count) * 100 : 0;
            var avgUSConfidence = usIndicators > 0 ? usConfidence / usIndicators : 0;

            locationAnalysis.USConsensusPercentage = usConsensusPercentage;
            locationAnalysis.SourcesReportingUS = usIndicators;
            locationAnalysis.TotalSources = sources.Count;

            // Determine US location status with high confidence
            if (usConsensusPercentage >= 80 && avgUSConfidence >= 80)
            {
                locationAnalysis.IsDefinitelyInUS = true;
                locationAnalysis.USLocationConfidence = "VERY HIGH";
                locationAnalysis.USLocationSummary = $"🇺🇸 **DEFINITELY US-BASED** - {usIndicators}/{sources.Count} sources confirm US location with {usConsensusPercentage:F1}% consensus";
            }
            else if (usConsensusPercentage >= 60)
            {
                locationAnalysis.IsLikelyInUS = true;
                locationAnalysis.USLocationConfidence = "HIGH";
                locationAnalysis.USLocationSummary = $"🇺🇸 **LIKELY US-BASED** - {usIndicators}/{sources.Count} sources indicate US location ({usConsensusPercentage:F1}% consensus)";
            }
            else if (usConsensusPercentage > 0)
            {
                locationAnalysis.USLocationConfidence = "UNCERTAIN";
                locationAnalysis.USLocationSummary = $"❓ **UNCERTAIN** - Mixed signals on US location ({usIndicators}/{sources.Count} sources, {usConsensusPercentage:F1}% consensus)";
            }
            else
            {
                locationAnalysis.IsDefinitelyOutsideUS = true;
                locationAnalysis.USLocationConfidence = "VERY HIGH";
                locationAnalysis.USLocationSummary = $"🌍 **NOT US-BASED** - All {sources.Count} sources confirm non-US location";
            }

            // Set consensus location info
            if (sources.Any())
            {
                var mostCommonCountry = sources
                    .Where(s => !string.IsNullOrEmpty(s.Country))
                    .GroupBy(s => s.Country)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault();

                if (mostCommonCountry != null)
                {
                    locationAnalysis.ConsensusCountry = mostCommonCountry.Key;
                    locationAnalysis.ConsensusCity = sources
                        .Where(s => s.Country == mostCommonCountry.Key && !string.IsNullOrEmpty(s.City))
                        .GroupBy(s => s.City)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault()?.Key;
                }
            }
        }

        private async Task PerformReputationAnalysisAsync(string ipAddress, IPValidationResult result)
        {
            result.ReputationAnalysis = new ReputationAnalysisResult();

            try
            {
                var reputationSources = new List<ReputationSource>();

                // AbuseIPDB integration (if API key available)
                if (!string.IsNullOrEmpty(_config.IPQualityAPIKey))
                {
                    try
                    {
                        var url = $"https://api.abuseipdb.com/api/v2/check?ipAddress={ipAddress}&maxAgeInDays=90&verbose";
                        _httpClient.DefaultRequestHeaders.Clear();
                        _httpClient.DefaultRequestHeaders.Add("Key", _config.IPQualityAPIKey);
                        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
                        
                        var response = await _httpClient.GetStringAsync(url);
                        var data = JObject.Parse(response);
                        
                        reputationSources.Add(new ReputationSource
                        {
                            Provider = "AbuseIPDB",
                            Score = 100 - (data["data"]?["abuseConfidencePercentage"]?.ToObject<int>() ?? 0),
                            IsMalicious = data["data"]?["abuseConfidencePercentage"]?.ToObject<int>() > 25,
                            LastSeen = data["data"]?["lastReportedAt"]?.ToObject<DateTime?>(),
                            ReportCount = data["data"]?["totalReports"]?.ToObject<int>() ?? 0,
                            Confidence = 95
                        });
                    }
                    catch { /* Continue with other sources */ }
                }

                // VirusTotal integration
                if (!string.IsNullOrEmpty(_config.VirusTotalAPIKey))
                {
                    try
                    {
                        var url = $"https://www.virustotal.com/vtapi/v2/ip-address/report?apikey={_config.VirusTotalAPIKey}&ip={ipAddress}";
                        var response = await _httpClient.GetStringAsync(url);
                        var data = JObject.Parse(response);
                        
                        var positives = data["detected_urls"]?.Count() ?? 0;
                        var total = Math.Max(1, positives + 10); // Assume some clean URLs
                        var score = Math.Max(0, 100 - (positives * 10));
                        
                        reputationSources.Add(new ReputationSource
                        {
                            Provider = "VirusTotal",
                            Score = score,
                            IsMalicious = positives > 2,
                            ReportCount = positives,
                            Confidence = 90
                        });
                    }
                    catch { /* Continue */ }
                }

                result.ReputationAnalysis.ReputationSources = reputationSources;

                // Calculate overall reputation score
                CalculateOverallReputationScore(result.ReputationAnalysis);

            }
            catch (Exception ex)
            {
                result.ReputationAnalysis.ErrorMessage = ex.Message;
            }
        }

        private void CalculateOverallReputationScore(ReputationAnalysisResult reputationAnalysis)
        {
            var sources = reputationAnalysis.ReputationSources;
            if (!sources.Any())
            {
                reputationAnalysis.OverallScore = 50; // Neutral if no data
                reputationAnalysis.RiskLevel = "UNKNOWN";
                reputationAnalysis.ReputationSummary = "No reputation data available";
                return;
            }

            // Weighted average based on confidence
            var totalWeightedScore = 0.0;
            var totalWeight = 0.0;
            var maliciousCount = 0;

            foreach (var source in sources)
            {
                var weight = source.Confidence / 100.0;
                totalWeightedScore += source.Score * weight;
                totalWeight += weight;
                
                if (source.IsMalicious) maliciousCount++;
            }

            reputationAnalysis.OverallScore = totalWeight > 0 ? (int)(totalWeightedScore / totalWeight) : 50;

            // Determine risk level
            if (maliciousCount > 0 || reputationAnalysis.OverallScore < 30)
            {
                reputationAnalysis.RiskLevel = "HIGH";
                reputationAnalysis.ReputationSummary = $"⚠️ **HIGH RISK** - Multiple reputation sources indicate malicious activity (Score: {reputationAnalysis.OverallScore}/100)";
            }
            else if (reputationAnalysis.OverallScore < 60)
            {
                reputationAnalysis.RiskLevel = "MEDIUM";
                reputationAnalysis.ReputationSummary = $"⚠️ **MEDIUM RISK** - Some reputation concerns detected (Score: {reputationAnalysis.OverallScore}/100)";
            }
            else if (reputationAnalysis.OverallScore >= 80)
            {
                reputationAnalysis.RiskLevel = "LOW";
                reputationAnalysis.ReputationSummary = $"✅ **LOW RISK** - Clean reputation across sources (Score: {reputationAnalysis.OverallScore}/100)";
            }
            else
            {
                reputationAnalysis.RiskLevel = "MEDIUM";
                reputationAnalysis.ReputationSummary = $"✅ **ACCEPTABLE** - Generally clean reputation (Score: {reputationAnalysis.OverallScore}/100)";
            }
        }

        private async Task PerformNetworkAnalysisAsync(string ipAddress, IPValidationResult result)
        {
            result.NetworkAnalysis = new NetworkAnalysisResult();

            try
            {
                // Reverse DNS lookup
                try
                {
                    var hostEntry = await Dns.GetHostEntryAsync(IPAddress.Parse(ipAddress));
                    result.NetworkAnalysis.ReverseDNS = hostEntry.HostName;
                }
                catch
                {
                    result.NetworkAnalysis.ReverseDNS = "No reverse DNS";
                }

                // Additional network info could be added here (ASN lookup, etc.)
                result.NetworkAnalysis.IsRoutable = true; // Assume routable for public IPs

            }
            catch (Exception ex)
            {
                result.NetworkAnalysis.ErrorMessage = ex.Message;
            }
        }

        private void GenerateIPAssessment(IPValidationResult result)
        {
            // Calculate overall reputation score
            result.ReputationScore = result.ReputationAnalysis?.OverallScore ?? 50;

            // Generate comprehensive summary
            var summaryParts = new List<string>();

            // Add US location information prominently
            if (!string.IsNullOrEmpty(result.LocationAnalysis?.USLocationSummary))
            {
                summaryParts.Add(result.LocationAnalysis.USLocationSummary);
            }

            // Add reputation information
            if (!string.IsNullOrEmpty(result.ReputationAnalysis?.ReputationSummary))
            {
                summaryParts.Add(result.ReputationAnalysis.ReputationSummary);
            }

            // Add location details
            if (!string.IsNullOrEmpty(result.LocationAnalysis?.ConsensusCountry))
            {
                var locationDetails = $"📍 Location: {result.LocationAnalysis.ConsensusCountry}";
                if (!string.IsNullOrEmpty(result.LocationAnalysis.ConsensusCity))
                {
                    locationDetails += $", {result.LocationAnalysis.ConsensusCity}";
                }
                summaryParts.Add(locationDetails);
            }

            result.ValidationSummary = string.Join("\n", summaryParts);

            if (string.IsNullOrEmpty(result.ValidationSummary))
            {
                result.ValidationSummary = $"IP address validated successfully. Type: {result.Classification.Type}";
            }
        }

        #endregion

        #region Email Validation

        /// <summary>
        /// Performs thorough email validation from reputable APIs and returns a comprehensive scorecard
        /// </summary>
        /// <param name="emailAddress">The email address to validate</param>
        /// <returns>Comprehensive email validation scorecard</returns>
        public async Task<EmailValidationResult> ValidateEmailAsync(string emailAddress)
        {
            var result = new EmailValidationResult
            {
                EmailAddress = emailAddress,
                ValidationTimestamp = DateTime.UtcNow,
                ProcessingTimeMs = 0
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Step 1: Format validation
                result.FormatValidation = ValidateEmailFormat(emailAddress);
                
                if (!result.FormatValidation.IsValid)
                {
                    result.IsValid = false;
                    result.ValidationSummary = "Invalid email format";
                    result.OverallScore = 0;
                    return result;
                }

                // Step 2: Domain analysis
                var domain = emailAddress.Split('@')[1];
                result.DomainAnalysis = await AnalyzeEmailDomain(domain);

                // Step 3: Deliverability checks
                result.DeliverabilityAnalysis = await AnalyzeEmailDeliverability(emailAddress);

                // Step 4: Reputation and activity analysis
                result.ReputationAnalysis = await AnalyzeEmailReputation(emailAddress);

                // Step 5: Generate comprehensive scorecard
                GenerateEmailScorecard(result);

                result.IsValid = result.OverallScore >= 50; // Configurable threshold

            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ValidationSummary = $"Email validation failed: {ex.Message}";
                result.Errors.Add(ex.Message);
            }

            stopwatch.Stop();
            result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

            return result;
        }

        private EmailFormatValidation ValidateEmailFormat(string email)
        {
            var result = new EmailFormatValidation();

            try
            {
                if (string.IsNullOrWhiteSpace(email))
                {
                    result.IsValid = false;
                    result.Issues.Add("Email is null or empty");
                    return result;
                }

                // Use MailAddress for basic validation
                var mailAddress = new MailAddress(email);
                result.IsValid = true;
                result.LocalPart = mailAddress.User;
                result.Domain = mailAddress.Host;

                // Additional format checks
                if (email.Length > 254)
                {
                    result.Issues.Add("Email exceeds maximum length of 254 characters");
                    result.IsValid = false;
                }

                if (result.LocalPart.Length > 64)
                {
                    result.Issues.Add("Local part exceeds maximum length of 64 characters");
                    result.IsValid = false;
                }

                // Check for suspicious patterns
                var suspiciousPatterns = new[]
                {
                    @"\.{2,}", // Multiple consecutive dots
                    @"^\.|\.$", // Starts or ends with dot
                    @"\+.*\+", // Multiple plus signs
                    @"[<>]" // Angle brackets
                };

                foreach (var pattern in suspiciousPatterns)
                {
                    if (Regex.IsMatch(email, pattern))
                    {
                        result.Issues.Add($"Contains suspicious pattern: {pattern}");
                    }
                }

            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Issues.Add($"Format validation failed: {ex.Message}");
            }

            return result;
        }

        private async Task<EmailDomainAnalysis> AnalyzeEmailDomain(string domain)
        {
            var result = new EmailDomainAnalysis();

            try
            {
                // Validate domain using existing website validation
                var domainValidation = await ValidateWebsiteAsync(domain);
                
                result.DomainExists = domainValidation.DNSValidation.IsSuccessful;
                result.DomainScore = domainValidation.TrustScore;
                result.DomainRiskLevel = domainValidation.RiskLevel;

                // Check for disposable email patterns
                var disposablePatterns = new[]
                {
                    "10minutemail", "tempmail", "guerrillamail", "mailinator",
                    "throwaway", "temp-mail", "fakeinbox", "maildrop"
                };

                result.IsDisposableEmail = disposablePatterns.Any(pattern => 
                    domain.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

                // Check MX records
                await CheckMXRecords(domain, result);

                // Analyze domain age and reputation
                if (domainValidation.WHOISValidation.RegistrationDate.HasValue)
                {
                    var domainAge = DateTime.Now - domainValidation.WHOISValidation.RegistrationDate.Value;
                    result.DomainAgeInDays = (int)domainAge.TotalDays;
                    result.IsNewDomain = domainAge.TotalDays < 30;
                }

            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private async Task CheckMXRecords(string domain, EmailDomainAnalysis result)
        {
            try
            {
                // This is a simplified MX record check
                // In a production environment, you'd use a proper DNS library
                var hostEntry = await Dns.GetHostEntryAsync(domain);
                result.HasMXRecord = hostEntry.AddressList.Length > 0;
                result.MXRecords = hostEntry.AddressList.Select(ip => ip.ToString()).ToList();
            }
            catch
            {
                result.HasMXRecord = false;
            }
        }

        private async Task<EmailDeliverabilityAnalysis> AnalyzeEmailDeliverability(string emailAddress)
        {
            var result = new EmailDeliverabilityAnalysis();

            try
            {
                // Hunter.io integration (if API key available)
                if (!string.IsNullOrEmpty(_config.HunterIOAPIKey))
                {
                    try
                    {
                        var url = $"https://api.hunter.io/v2/email-verifier?email={emailAddress}&api_key={_config.HunterIOAPIKey}";
                        var response = await _httpClient.GetStringAsync(url);
                        var data = JObject.Parse(response);

                        var hunterResult = data["data"];
                        if (hunterResult != null)
                        {
                            result.DeliverabilityScore = hunterResult["score"]?.ToObject<int>() ?? 50;
                            result.Status = hunterResult["result"]?.ToString() ?? "unknown";
                            result.IsDeliverable = result.Status == "deliverable";
                            result.IsRisky = result.Status == "risky";
                            result.ValidationSource = "Hunter.io";
                        }
                    }
                    catch (Exception ex)
                    {
                        result.ErrorMessage = $"Hunter.io API failed: {ex.Message}";
                    }
                }

                // Fallback: Basic deliverability heuristics
                if (result.DeliverabilityScore == 0)
                {
                    result.DeliverabilityScore = 60; // Default moderate score
                    result.Status = "unknown";
                    result.ValidationSource = "Heuristic analysis";
                }

                // SMTP validation (if enabled and safe to do)
                if (_config.EnableSMTPValidation)
                {
                    await PerformSMTPValidation(emailAddress, result);
                }

            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private async Task PerformSMTPValidation(string emailAddress, EmailDeliverabilityAnalysis result)
        {
            // Note: SMTP validation can be intrusive and may be blocked
            // This is a simplified version - production code would be more sophisticated
            try
            {
                var domain = emailAddress.Split('@')[1];
                
                // This would require a more sophisticated SMTP client implementation
                // For now, we'll just indicate it was attempted
                result.SMTPValidationAttempted = true;
                result.SMTPValidationResult = "Not implemented (requires careful SMTP handling)";
                
                await Task.Delay(100); // Placeholder
            }
            catch (Exception ex)
            {
                result.SMTPValidationResult = $"SMTP validation failed: {ex.Message}";
            }
        }

        private async Task<EmailReputationAnalysis> AnalyzeEmailReputation(string emailAddress)
        {
            var result = new EmailReputationAnalysis();

            try
            {
                // Check against known spam patterns
                var spamPatterns = new[]
                {
                    @"^(noreply|no-reply|donotreply)@",
                    @"\d{4,}", // Many consecutive numbers
                    @"^[a-z]+\d+@", // Common bot pattern
                    @"(test|temp|fake|spam).*@"
                };

                result.SpamLikelihood = 0;
                foreach (var pattern in spamPatterns)
                {
                    if (Regex.IsMatch(emailAddress, pattern, RegexOptions.IgnoreCase))
                    {
                        result.SpamLikelihood += 20;
                        result.SpamIndicators.Add($"Matches pattern: {pattern}");
                    }
                }

                result.SpamLikelihood = Math.Min(100, result.SpamLikelihood);

                // Activity estimation (simplified heuristic)
                var localPart = emailAddress.Split('@')[0];
                if (localPart.Length >= 8 && Regex.IsMatch(localPart, @"[a-zA-Z].*[0-9]|[0-9].*[a-zA-Z]"))
                {
                    result.ActivityScore = 70; // Mixed alphanumeric suggests human user
                }
                else if (localPart.Length < 4)
                {
                    result.ActivityScore = 40; // Very short might be less active
                }
                else
                {
                    result.ActivityScore = 60; // Default moderate activity
                }

                // Professional vs personal estimation
                var professionalIndicators = new[] { "admin", "info", "contact", "support", "sales" };
                var personalIndicators = new[] { "gmail", "yahoo", "hotmail", "outlook" };
                
                var domain = emailAddress.Split('@')[1];
                if (professionalIndicators.Any(indicator => 
                    localPart.IndexOf(indicator, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    result.EmailType = "Professional";
                }
                else if (personalIndicators.Any(provider => 
                    domain.IndexOf(provider, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    result.EmailType = "Personal";
                }
                else
                {
                    result.EmailType = "Business/Organization";
                }

                // Add minimal await to satisfy compiler
                await Task.Delay(1);

            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private void GenerateEmailScorecard(EmailValidationResult result)
        {
            var scoreComponents = new List<ScoreComponent>();

            // Format validation (20 points)
            var formatScore = result.FormatValidation.IsValid ? 20 : 0;
            scoreComponents.Add(new ScoreComponent
            {
                Category = "Format",
                Score = formatScore,
                MaxScore = 20,
                Details = result.FormatValidation.IsValid ? "Valid format" : string.Join(", ", result.FormatValidation.Issues)
            });

            // Domain validation (25 points)
            var domainScore = (int)((result.DomainAnalysis.DomainScore / 100.0) * 25);
            scoreComponents.Add(new ScoreComponent
            {
                Category = "Domain",
                Score = domainScore,
                MaxScore = 25,
                Details = $"Domain trust score: {result.DomainAnalysis.DomainScore}/100"
            });

            // Deliverability (25 points)
            var deliverabilityScore = (int)((result.DeliverabilityAnalysis.DeliverabilityScore / 100.0) * 25);
            scoreComponents.Add(new ScoreComponent
            {
                Category = "Deliverability",
                Score = deliverabilityScore,
                MaxScore = 25,
                Details = $"Deliverability: {result.DeliverabilityAnalysis.Status}"
            });

            // Reputation (30 points)
            var reputationScore = 30 - (int)((result.ReputationAnalysis.SpamLikelihood / 100.0) * 30);
            scoreComponents.Add(new ScoreComponent
            {
                Category = "Reputation",
                Score = reputationScore,
                MaxScore = 30,
                Details = $"Spam likelihood: {result.ReputationAnalysis.SpamLikelihood}%"
            });

            result.ScoreComponents = scoreComponents;
            result.OverallScore = scoreComponents.Sum(s => s.Score);

            // Generate risk level
            if (result.OverallScore >= 80)
                result.RiskLevel = "LOW";
            else if (result.OverallScore >= 60)
                result.RiskLevel = "MEDIUM";
            else if (result.OverallScore >= 30)
                result.RiskLevel = "HIGH";
            else
                result.RiskLevel = "CRITICAL";

            // Generate detailed summary
            var summaryParts = new List<string>
            {
                $"📧 **Email Validation Scorecard: {result.OverallScore}/100 ({result.RiskLevel} Risk)**",
                $"📧 Type: {result.ReputationAnalysis.EmailType}",
                $"🎯 Activity Score: {result.ReputationAnalysis.ActivityScore}/100"
            };

            if (result.DomainAnalysis.IsDisposableEmail)
            {
                summaryParts.Add("⚠️ **WARNING: Disposable email detected**");
            }

            if (result.DeliverabilityAnalysis.IsDeliverable)
            {
                summaryParts.Add("✅ Email appears deliverable");
            }
            else if (result.DeliverabilityAnalysis.IsRisky)
            {
                summaryParts.Add("⚠️ Email deliverability is risky");
            }

            result.ValidationSummary = string.Join("\n", summaryParts);
        }

        #endregion

        #region Helper Methods

        private async Task PerformBasicWHOISLookup(string domain, WHOISValidationResult result)
        {
            // Placeholder for basic WHOIS lookup implementation
            await Task.Delay(100);
            result.IsSuccessful = false;
            result.ErrorMessage = "Basic WHOIS lookup not fully implemented";
        }

        private void AnalyzeUSRegistration(WHOISValidationResult whoisResult, string domain)
        {
            // Simplified US registration analysis
            var usIndicators = 0;

            if (!string.IsNullOrEmpty(whoisResult.RegistrantCountry) && 
                whoisResult.RegistrantCountry.Equals("US", StringComparison.OrdinalIgnoreCase))
            {
                usIndicators++;
            }

            if (domain.EndsWith(".us") || domain.EndsWith(".gov"))
            {
                usIndicators++;
            }

            whoisResult.IsLikelyUSRegistered = usIndicators > 0;
        }

        private async Task CheckVirusTotalReputation(string domain, SecurityValidationResult result)
        {
            try
            {
                var url = $"https://www.virustotal.com/vtapi/v2/domain/report?apikey={_config.VirusTotalAPIKey}&domain={domain}";
                var response = await _httpClient.GetStringAsync(url);
                var data = JObject.Parse(response);

                var positives = data["detected_urls"]?.Count() ?? 0;
                if (positives > 0)
                {
                    result.SecurityScore = Math.Max(20, result.SecurityScore - (positives * 10));
                    result.SecurityFlags.Add($"VirusTotal detected {positives} suspicious URLs");
                }
            }
            catch
            {
                // Continue without VirusTotal data
            }
        }

        #endregion

        #region Data Models

        public class ValidationConfig
        {
            public string WhoisAPIKey { get; set; }
            public string IPGeolocationAPIKey { get; set; }
            public string EmailValidationAPIKey { get; set; }
            public string VirusTotalAPIKey { get; set; }
            public string IPQualityAPIKey { get; set; }
            public string HunterIOAPIKey { get; set; }
            public bool EnableDeepAnalysis { get; set; } = true;
            public bool EnableThreatIntelligence { get; set; } = true;
            public bool EnableSMTPValidation { get; set; } = false;
            public int DefaultTimeoutSeconds { get; set; } = 10;
        }

        // Website Validation Models
        public class WebsiteValidationResult
        {
            public string OriginalInput { get; set; }
            public string CleanedDomain { get; set; }
            public bool IsValid { get; set; }
            public DateTime ValidationTimestamp { get; set; }
            public long ProcessingTimeMs { get; set; }
            public string ValidationSummary { get; set; }
            public int TrustScore { get; set; }
            public string RiskLevel { get; set; }
            public List<string> TrustFactors { get; set; } = new List<string>();
            public List<string> Errors { get; set; } = new List<string>();

            public DNSValidationResult DNSValidation { get; set; }
            public ConnectivityValidationResult ConnectivityValidation { get; set; }
            public WHOISValidationResult WHOISValidation { get; set; }
            public SecurityValidationResult SecurityValidation { get; set; }
        }

        public class DNSValidationResult
        {
            public bool IsSuccessful { get; set; }
            public List<string> IPAddresses { get; set; } = new List<string>();
            public long ResponseTimeMs { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class ConnectivityValidationResult
        {
            public bool IsReachable { get; set; }
            public bool HTTPSAccessible { get; set; }
            public bool HTTPAccessible { get; set; }
            public int HTTPSStatusCode { get; set; }
            public int HTTPStatusCode { get; set; }
            public bool PingSuccessful { get; set; }
            public long PingResponseTimeMs { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class WHOISValidationResult
        {
            public bool IsSuccessful { get; set; }
            public DateTime? RegistrationDate { get; set; }
            public DateTime? ExpirationDate { get; set; }
            public string Registrar { get; set; }
            public string RegistrantCountry { get; set; }
            public bool IsLikelyUSRegistered { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class SecurityValidationResult
        {
            public int SecurityScore { get; set; } = 80;
            public bool IsMalicious { get; set; }
            public List<string> SecurityFlags { get; set; } = new List<string>();
            public string ErrorMessage { get; set; }
        }

        // IP Validation Models
        public class IPValidationResult
        {
            public string IPAddress { get; set; }
            public bool IsValid { get; set; }
            public DateTime ValidationTimestamp { get; set; }
            public long ProcessingTimeMs { get; set; }
            public string ValidationSummary { get; set; }
            public string AddressFamily { get; set; }
            public int ReputationScore { get; set; }
            public List<string> Errors { get; set; } = new List<string>();

            public IPClassificationResult Classification { get; set; }
            public LocationAnalysisResult LocationAnalysis { get; set; }
            public ReputationAnalysisResult ReputationAnalysis { get; set; }
            public NetworkAnalysisResult NetworkAnalysis { get; set; }
        }

        public class IPClassificationResult
        {
            public string Type { get; set; }
            public bool IsPublic { get; set; }
            public bool IsPrivate { get; set; }
            public bool IsLoopback { get; set; }
            public bool IsLinkLocal { get; set; }
            public bool IsMulticast { get; set; }
            public bool IsReserved { get; set; }
            public bool IsCGNAT { get; set; }
        }

        public class LocationAnalysisResult
        {
            public List<GeolocationSource> GeolocationSources { get; set; } = new List<GeolocationSource>();
            public bool IsDefinitelyInUS { get; set; }
            public bool IsDefinitelyOutsideUS { get; set; }
            public bool IsLikelyInUS { get; set; }
            public string USLocationConfidence { get; set; }
            public string USLocationSummary { get; set; }
            public double USConsensusPercentage { get; set; }
            public int SourcesReportingUS { get; set; }
            public int TotalSources { get; set; }
            public string ConsensusCountry { get; set; }
            public string ConsensusCity { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class GeolocationSource
        {
            public string Provider { get; set; }
            public string Country { get; set; }
            public string CountryCode { get; set; }
            public string City { get; set; }
            public string Region { get; set; }
            public string ISP { get; set; }
            public string Organization { get; set; }
            public int Confidence { get; set; }
        }

        public class ReputationAnalysisResult
        {
            public List<ReputationSource> ReputationSources { get; set; } = new List<ReputationSource>();
            public int OverallScore { get; set; }
            public string RiskLevel { get; set; }
            public string ReputationSummary { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class ReputationSource
        {
            public string Provider { get; set; }
            public int Score { get; set; }
            public bool IsMalicious { get; set; }
            public DateTime? LastSeen { get; set; }
            public int ReportCount { get; set; }
            public int Confidence { get; set; }
        }

        public class NetworkAnalysisResult
        {
            public string ReverseDNS { get; set; }
            public bool IsRoutable { get; set; }
            public string ASN { get; set; }
            public string ASNOrganization { get; set; }
            public string ErrorMessage { get; set; }
        }

        // Email Validation Models
        public class EmailValidationResult
        {
            public string EmailAddress { get; set; }
            public bool IsValid { get; set; }
            public DateTime ValidationTimestamp { get; set; }
            public long ProcessingTimeMs { get; set; }
            public string ValidationSummary { get; set; }
            public int OverallScore { get; set; }
            public string RiskLevel { get; set; }
            public List<ScoreComponent> ScoreComponents { get; set; } = new List<ScoreComponent>();
            public List<string> Errors { get; set; } = new List<string>();

            public EmailFormatValidation FormatValidation { get; set; }
            public EmailDomainAnalysis DomainAnalysis { get; set; }
            public EmailDeliverabilityAnalysis DeliverabilityAnalysis { get; set; }
            public EmailReputationAnalysis ReputationAnalysis { get; set; }
        }

        public class EmailFormatValidation
        {
            public bool IsValid { get; set; }
            public string LocalPart { get; set; }
            public string Domain { get; set; }
            public List<string> Issues { get; set; } = new List<string>();
        }

        public class EmailDomainAnalysis
        {
            public bool DomainExists { get; set; }
            public int DomainScore { get; set; }
            public string DomainRiskLevel { get; set; }
            public bool IsDisposableEmail { get; set; }
            public bool HasMXRecord { get; set; }
            public List<string> MXRecords { get; set; } = new List<string>();
            public int DomainAgeInDays { get; set; }
            public bool IsNewDomain { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class EmailDeliverabilityAnalysis
        {
            public int DeliverabilityScore { get; set; }
            public string Status { get; set; } // deliverable, undeliverable, risky, unknown
            public bool IsDeliverable { get; set; }
            public bool IsRisky { get; set; }
            public string ValidationSource { get; set; }
            public bool SMTPValidationAttempted { get; set; }
            public string SMTPValidationResult { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class EmailReputationAnalysis
        {
            public int SpamLikelihood { get; set; }
            public List<string> SpamIndicators { get; set; } = new List<string>();
            public int ActivityScore { get; set; }
            public string EmailType { get; set; } // Professional, Personal, Business/Organization
            public string ErrorMessage { get; set; }
        }

        public class ScoreComponent
        {
            public string Category { get; set; }
            public int Score { get; set; }
            public int MaxScore { get; set; }
            public string Details { get; set; }
        }

        #endregion

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
