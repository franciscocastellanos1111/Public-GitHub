using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DynamicsProcesses
{
    public class WhoisJsonService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string BaseUrl = "https://whoisjson.com/api/v1";

        public WhoisJsonService()
        {
            _httpClient = new HttpClient();
            _apiKey = System.Configuration.ConfigurationManager.AppSettings["WhoisJsonApiKey"];
        }

        public async Task<WhoisResult> GetWhoisDataAsync(string domain)
        {
            try
            {
                if (string.IsNullOrEmpty(domain))
                    return null;

                
                string cleanDomain = CleanDomain(domain);

                var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/whois?domain={cleanDomain}");
                
               
                bool headerAdded = request.Headers.TryAddWithoutValidation("Authorization", $"TOKEN={_apiKey}");
                if (!headerAdded)
                {
                    DynamicsInterface.writeToLog($"Failed to add Authorization header for domain {cleanDomain}");
                    return null;
                }

                DynamicsInterface.writeToLog($"Making WhoisJSON API request for domain: {cleanDomain}");

                HttpResponseMessage response = await _httpClient.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();

                DynamicsInterface.writeToLog($"WhoisJSON API response for {cleanDomain}: Status={response.StatusCode}, ContentLength={content?.Length ?? 0}");

                if (response.IsSuccessStatusCode)
                {
                    var whoisData = JsonConvert.DeserializeObject<JObject>(content);
                    
                    // Check for API-level errors in the response
                    if (whoisData["error"] != null)
                    {
                        DynamicsInterface.writeToLog($"WhoisJSON API returned error for {cleanDomain}: {whoisData["error"]}");
                        return null;
                    }
                    
                    return ParseWhoisResult(whoisData, content);
                }
                else
                {
                    DynamicsInterface.writeToLog($"WhoisJSON API error for domain {cleanDomain}: {response.StatusCode} - {content}");
                    
                    // Log response headers for debugging
                    foreach (var header in response.Headers)
                    {
                        DynamicsInterface.writeToLog($"Response header: {header.Key} = {string.Join(", ", header.Value)}");
                    }
                    
                    return null;
                }
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error in GetWhoisDataAsync for {domain}: {ex.Message}");
                return null;
            }
        }

        private WhoisResult ParseWhoisResult(JObject whoisData, string content)
        {
            try
            {
                var result = new WhoisResult
                {
                    content = content,
                    Domain = whoisData["name"]?.ToString(),
                    IsRegistered = whoisData["registered"]?.ToObject<bool>() ?? false,
                    CreatedDate = ParseDate(whoisData["created"]?.ToString()),
                    ExpiresDate = ParseDate(whoisData["expires"]?.ToString()),
                    UpdatedDate = ParseDate(whoisData["changed"]?.ToString()),
                    Nameservers = ParseNameservers(whoisData["nameserver"]),
                    IpAddresses = ParseIpAddresses(whoisData["ips"]?.ToString()),
                    Registrar = ParseRegistrar(whoisData["registrar"] as JObject),
                    Contacts = ParseContacts(whoisData["contacts"] as JObject),
                    Status = ParseStatus(whoisData["status"]),
                    DnssecEnabled = whoisData["dnssec"]?.ToString() == "signedDelegation"
                };

                DynamicsInterface.writeToLog($"Successfully parsed WHOIS data for {result.Domain}: " +
                    $"Registered={result.IsRegistered}, " +
                    $"Registrar={result.Registrar?.Name ?? "Unknown"}, " +
                    $"Created={result.CreatedDate?.ToString("yyyy-MM-dd") ?? "Unknown"}, " +
                    $"Status={string.Join(", ", result.Status)}");

                return result;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error parsing WHOIS result: {ex.Message}");
                throw new Exception($"Failed to parse WHOIS response: {ex.Message}", ex);
            }
        }

        private DateTime? ParseDate(string dateString)
        {
            if (string.IsNullOrEmpty(dateString))
                return null;

            if (DateTime.TryParse(dateString, out DateTime result))
                return result;

            return null;
        }

        private List<string> ParseIpAddresses(string ipsString)
        {
            if (string.IsNullOrEmpty(ipsString))
                return new List<string>();

            return ipsString.Split(',').Select(ip => ip.Trim()).ToList();
        }

        private List<string> ParseNameservers(JToken nameserverToken)
        {
            try
            {
                if (nameserverToken == null)
                    return new List<string>();

                if (nameserverToken.Type == JTokenType.Array)
                {
                    return nameserverToken.ToObject<List<string>>() ?? new List<string>();
                }
                else if (nameserverToken.Type == JTokenType.String)
                {
                    var nameserver = nameserverToken.ToString();
                    return !string.IsNullOrEmpty(nameserver) ? new List<string> { nameserver } : new List<string>();
                }

                return new List<string>();
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error parsing nameservers: {ex.Message}");
                return new List<string>();
            }
        }

        private List<string> ParseStatus(JToken statusToken)
        {
            try
            {
                if (statusToken == null)
                    return new List<string>();

                if (statusToken.Type == JTokenType.Array)
                {
                    return statusToken.ToObject<List<string>>() ?? new List<string>();
                }
                else if (statusToken.Type == JTokenType.String)
                {
                    var status = statusToken.ToString();
                    return !string.IsNullOrEmpty(status) ? new List<string> { status } : new List<string>();
                }

                return new List<string>();
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error parsing status: {ex.Message}");
                return new List<string>();
            }
        }

        private WhoisRegistrar ParseRegistrar(JObject registrarObj)
        {
            if (registrarObj == null)
                return null;

            return new WhoisRegistrar
            {
                Id = registrarObj["id"]?.ToString(),
                Name = registrarObj["name"]?.ToString(),
                Email = registrarObj["email"]?.ToString(),
                Url = registrarObj["url"]?.ToString(),
                Phone = registrarObj["phone"]?.ToString()
            };
        }

        private WhoisContacts ParseContacts(JObject contactsObj)
        {
            if (contactsObj == null)
                return new WhoisContacts();

            return new WhoisContacts
            {
                Owner = ParseContactList(contactsObj["owner"] as JArray),
                Admin = ParseContactList(contactsObj["admin"] as JArray),
                Tech = ParseContactList(contactsObj["tech"] as JArray)
            };
        }

        private List<WhoisContact> ParseContactList(JArray contactArray)
        {
            if (contactArray == null)
                return new List<WhoisContact>();

            var contacts = new List<WhoisContact>();
            foreach (var contactObj in contactArray)
            {
                var contact = new WhoisContact
                {
                    Name = contactObj["name"]?.ToString(),
                    Organization = contactObj["organization"]?.ToString(),
                    Email = contactObj["email"]?.ToString(),
                    Phone = contactObj["phone"]?.ToString(),
                    Address = contactObj["address"]?.ToString(),
                    City = contactObj["city"]?.ToString(),
                    State = contactObj["state"]?.ToString(),
                    Country = contactObj["country"]?.ToString(),
                    PostalCode = contactObj["zipcode"]?.ToString()
                };
                contacts.Add(contact);
            }
            return contacts;
        }

        private string CleanDomain(string domain)
        {
            if (string.IsNullOrEmpty(domain))
                return domain;

            // Remove protocol
            domain = domain.Replace("http://", "").Replace("https://", "");
            
            // Remove www
            if (domain.StartsWith("www."))
                domain = domain.Substring(4);

            // Remove trailing slash and path
            int slashIndex = domain.IndexOf('/');
            if (slashIndex > 0)
                domain = domain.Substring(0, slashIndex);

            return domain.ToLower();
        }

        public async Task<DnsLookupResult> GetDnsRecordsAsync(string domain)
        {
            try
            {
                if (string.IsNullOrEmpty(domain))
                    return null;

                string cleanDomain = CleanDomain(domain);

                var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/nslookup?domain={cleanDomain}");
                
                bool headerAdded = request.Headers.TryAddWithoutValidation("Authorization", $"TOKEN={_apiKey}");
                if (!headerAdded)
                {
                    DynamicsInterface.writeToLog($"Failed to add Authorization header for DNS lookup of {cleanDomain}");
                    return null;
                }

                DynamicsInterface.writeToLog($"Making WhoisJSON NSLookup API request for domain: {cleanDomain}");

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                DynamicsInterface.writeToLog($"WhoisJSON NSLookup API response for {cleanDomain}: Status={response.StatusCode}, ContentLength={content?.Length ?? 0}");

                if (response.IsSuccessStatusCode)
                {
                    var dnsData = JsonConvert.DeserializeObject<JObject>(content);
                    
                    if (dnsData["error"] != null)
                    {
                        DynamicsInterface.writeToLog($"WhoisJSON NSLookup API returned error for {cleanDomain}: {dnsData["error"]}");
                        return null;
                    }
                    
                    return ParseDnsLookupResult(dnsData, cleanDomain);
                }
                else
                {
                    DynamicsInterface.writeToLog($"WhoisJSON NSLookup API error for domain {cleanDomain}: {response.StatusCode} - {content}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error in GetDnsRecordsAsync for {domain}: {ex.Message}");
                return null;
            }
        }

        private DnsLookupResult ParseDnsLookupResult(JObject dnsData, string domain)
        {
            try
            {
                var result = new DnsLookupResult
                {
                    Domain = domain,
                    ARecords = ParseArrayProperty(dnsData["A"]),
                    AAAARecords = ParseArrayProperty(dnsData["AAAA"]),
                    NSRecords = ParseArrayProperty(dnsData["NS"]),
                    TXTRecords = ParseArrayProperty(dnsData["TXT"]),
                    MXRecords = ParseMXRecords(dnsData["MX"] as JArray),
                    SOARecord = ParseSOARecord(dnsData["SOA"] as JObject)
                };

                DynamicsInterface.writeToLog($"Successfully parsed DNS lookup for {domain}: " +
                    $"A Records={result.ARecords.Count}, " +
                    $"NS Records={result.NSRecords.Count}, " +
                    $"MX Records={result.MXRecords.Count}");

                return result;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error parsing DNS lookup result for {domain}: {ex.Message}");
                throw new Exception($"Failed to parse DNS lookup response: {ex.Message}", ex);
            }
        }

        private List<string> ParseArrayProperty(JToken token)
        {
            try
            {
                if (token == null || token.Type == JTokenType.Null)
                    return new List<string>();

                if (token.Type == JTokenType.Array)
                {
                    return token.ToObject<List<string>>() ?? new List<string>();
                }
                else if (token.Type == JTokenType.String)
                {
                    var value = token.ToString();
                    return !string.IsNullOrEmpty(value) ? new List<string> { value } : new List<string>();
                }

                return new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private List<MXRecord> ParseMXRecords(JArray mxArray)
        {
            var mxRecords = new List<MXRecord>();
            
            if (mxArray == null)
                return mxRecords;

            try
            {
                foreach (var mx in mxArray)
                {
                    var mxRecord = new MXRecord
                    {
                        Exchange = mx["exchange"]?.ToString(),
                        Priority = mx["priority"]?.ToObject<int>() ?? 0
                    };
                    mxRecords.Add(mxRecord);
                }
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error parsing MX records: {ex.Message}");
            }

            return mxRecords;
        }

        private SOARecord ParseSOARecord(JObject soaObject)
        {
            if (soaObject == null)
                return null;

            try
            {
                return new SOARecord
                {
                    NSName = soaObject["nsname"]?.ToString(),
                    Hostmaster = soaObject["hostmaster"]?.ToString(),
                    Serial = soaObject["serial"]?.ToObject<long>() ?? 0,
                    Refresh = soaObject["refresh"]?.ToObject<int>() ?? 0,
                    Retry = soaObject["retry"]?.ToObject<int>() ?? 0,
                    Expire = soaObject["expire"]?.ToObject<int>() ?? 0,
                    MinTTL = soaObject["minttl"]?.ToObject<int>() ?? 0
                };
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"Error parsing SOA record: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

       
        public static async Task<bool> TestWhoisApiAsync(string testDomain = "google.com")
        {
            try
            {
                DynamicsInterface.writeToLog($"Testing WhoisJSON API with domain: {testDomain}");
                
                var service = new WhoisJsonService();
                var result = await service.GetWhoisDataAsync(testDomain);
                
                bool isSuccess = result != null && result.IsRegistered;
                DynamicsInterface.writeToLog($"WhoisJSON API test result: {(isSuccess ? "SUCCESS" : "FAILED")}");
                
                if (result != null)
                {
                    DynamicsInterface.writeToLog($"Domain: {result.Domain}, Registered: {result.IsRegistered}, Registrar: {result.Registrar?.Name ?? "Unknown"}");
                }
                
                service.Dispose();
                return isSuccess;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"WhoisJSON API test failed: {ex.Message}");
                return false;
            }
        }

       
        public static async Task<bool> TestDnsLookupApiAsync(string testDomain = "google.com")
        {
            try
            {
                DynamicsInterface.writeToLog($"Testing WhoisJSON NSLookup API with domain: {testDomain}");
                
                var service = new WhoisJsonService();
                var result = await service.GetDnsRecordsAsync(testDomain);
                
                bool isSuccess = result != null && (result.ARecords.Any() || result.NSRecords.Any());
                DynamicsInterface.writeToLog($"WhoisJSON NSLookup API test result: {(isSuccess ? "SUCCESS" : "FAILED")}");
                
                if (result != null)
                {
                    DynamicsInterface.writeToLog($"Domain: {result.Domain}, A Records: {result.ARecords.Count}, NS Records: {result.NSRecords.Count}");
                }
                
                service.Dispose();
                return isSuccess;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"WhoisJSON NSLookup API test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Comprehensive test of all WhoisJSON APIs
        /// </summary>
        public static async Task<bool> TestAllApisAsync(string testDomain = "google.com")
        {
            try
            {
                DynamicsInterface.writeToLog($"Running comprehensive WhoisJSON API tests for domain: {testDomain}");
                
                bool whoisSuccess = await TestWhoisApiAsync(testDomain);
                bool dnsSuccess = await TestDnsLookupApiAsync(testDomain);
                
                bool overallSuccess = whoisSuccess && dnsSuccess;
                DynamicsInterface.writeToLog($"Overall WhoisJSON API test result: {(overallSuccess ? "SUCCESS" : "FAILED")} (WHOIS: {whoisSuccess}, DNS: {dnsSuccess})");
                
                return overallSuccess;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog($"WhoisJSON comprehensive API test failed: {ex.Message}");
                return false;
            }
        }
    }

    public class WhoisResult
    {
        public string content { get; set; }
        public string Domain { get; set; }
        public bool IsRegistered { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ExpiresDate { get; set; }
        public DateTime? UpdatedDate { get; set; }
        public List<string> Nameservers { get; set; } = new List<string>();
        public List<string> IpAddresses { get; set; } = new List<string>();
        public WhoisRegistrar Registrar { get; set; }
        public WhoisContacts Contacts { get; set; }
        public List<string> Status { get; set; } = new List<string>();
        public bool DnssecEnabled { get; set; }

        // Computed properties for fraud detection
        public int DomainAgeInDays => CreatedDate.HasValue ? (DateTime.Now - CreatedDate.Value).Days : 0;
        public bool IsRecentlyRegistered => DomainAgeInDays < 90; // Less than 3 months
        public bool IsExpiringSoon => ExpiresDate.HasValue && (ExpiresDate.Value - DateTime.Now).Days < 30;
    }

    public class WhoisRegistrar
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Url { get; set; }
        public string Phone { get; set; }

        // Helper method to extract country from phone number
        public string GetPhoneCountryCode()
        {
            if (string.IsNullOrEmpty(Phone))
                return null;

            // Extract country code from phone number (basic implementation)
            if (Phone.StartsWith("+1"))
                return "US"; // US/Canada
            if (Phone.StartsWith("+33"))
                return "FR"; // France
            if (Phone.StartsWith("+44"))
                return "GB"; // UK
            if (Phone.StartsWith("+49"))
                return "DE"; // Germany
            if (Phone.StartsWith("+86"))
                return "CN"; // China
            if (Phone.StartsWith("+91"))
                return "IN"; // India
            if (Phone.StartsWith("+7"))
                return "RU"; // Russia
            if (Phone.StartsWith("+81"))
                return "JP"; // Japan
            if (Phone.StartsWith("+82"))
                return "KR"; // South Korea

            return null; // Unknown
        }
    }

    public class WhoisContacts
    {
        public List<WhoisContact> Owner { get; set; } = new List<WhoisContact>();
        public List<WhoisContact> Admin { get; set; } = new List<WhoisContact>();
        public List<WhoisContact> Tech { get; set; } = new List<WhoisContact>();

        public List<string> GetAllCountries()
        {
            var countries = new List<string>();
            countries.AddRange(Owner.Where(c => !string.IsNullOrEmpty(c.Country)).Select(c => c.Country));
            countries.AddRange(Admin.Where(c => !string.IsNullOrEmpty(c.Country)).Select(c => c.Country));
            countries.AddRange(Tech.Where(c => !string.IsNullOrEmpty(c.Country)).Select(c => c.Country));
            return countries.Distinct().ToList();
        }

        public bool HasNonUSContacts()
        {
            var countries = GetAllCountries();
            return countries.Any(c => !string.Equals(c, "US", StringComparison.OrdinalIgnoreCase));
        }
    }

    public class WhoisContact
    {
        public string Name { get; set; }
        public string Organization { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string Address { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string Country { get; set; }
        public string PostalCode { get; set; }
    }

    public class DnsLookupResult
    {
        public string Domain { get; set; }
        public List<string> ARecords { get; set; } = new List<string>();
        public List<string> AAAARecords { get; set; } = new List<string>();
        public List<string> NSRecords { get; set; } = new List<string>();
        public List<string> TXTRecords { get; set; } = new List<string>();
        public List<MXRecord> MXRecords { get; set; } = new List<MXRecord>();
        public SOARecord SOARecord { get; set; }
    }

    public class MXRecord
    {
        public string Exchange { get; set; }
        public int Priority { get; set; }
    }

    public class SOARecord
    {
        public string NSName { get; set; }
        public string Hostmaster { get; set; }
        public long Serial { get; set; }
        public int Refresh { get; set; }
        public int Retry { get; set; }
        public int Expire { get; set; }
        public int MinTTL { get; set; }
    }
}