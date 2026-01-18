using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DynamicsProcesses
{
    public class EnhancedDomainValidator
    {
        private readonly WhoisJsonService _whoisService;

        public EnhancedDomainValidator()
        {
            _whoisService = new WhoisJsonService();
        }

        public async Task<EnhancedDomainValidationResult> ValidateDomainAsync(string domain)
        {
            var result = new EnhancedDomainValidationResult
            {
                Domain = domain,
                ValidationTimestamp = DateTime.UtcNow
            };

            try
            {
                DynamicsInterface.writeToLog($"Starting enhanced domain validation for: {domain}");

                // Get comprehensive WHOIS data
                var whoisData = await _whoisService.GetWhoisDataAsync(domain);
                if (whoisData == null)
                {
                    result.IsValid = false;
                    result.ValidationFlags.Add("Unable to retrieve WHOIS data");
                    DynamicsInterface.writeToLog($"Failed to retrieve WHOIS data for {domain}");
                    return result;
                }

                result.WhoisData = whoisData;
                result.IsValid = whoisData.IsRegistered;

                // Get DNS records for additional validation
                var dnsData = await _whoisService.GetDnsRecordsAsync(domain);
                result.DnsData = dnsData;

                if (dnsData != null)
                {
                    DynamicsInterface.writeToLog($"Retrieved DNS data for {domain}: A Records={dnsData.ARecords.Count}, NS Records={dnsData.NSRecords.Count}");
                }

                // Analyze domain for fraud indicators
                await AnalyzeDomainForFraud(result);

                DynamicsInterface.writeToLog($"Enhanced domain validation completed for {domain}: Valid={result.IsValid}, RiskScore={result.RiskScore}, RiskLevel={result.RiskLevel}");

                return result;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ValidationFlags.Add($"Error during validation: {ex.Message}");
                DynamicsInterface.writeToLog($"Error in ValidateDomainAsync for {domain}: {ex.Message}");
                return result;
            }
        }

        private async Task AnalyzeDomainForFraud(EnhancedDomainValidationResult result)
        {
            var whois = result.WhoisData;
            var dns = result.DnsData;
            var fraudFlags = new List<string>();
            var riskScore = 0;

            // 1. Check domain registration country
            if (whois.Contacts != null && whois.Contacts.HasNonUSContacts())
            {
                var nonUSCountries = whois.Contacts.GetAllCountries()
                    .Where(c => !string.Equals(c, "US", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                
                if (nonUSCountries.Any())
                {
                    fraudFlags.Add($"Domain contacts registered in non-US countries: {string.Join(", ", nonUSCountries)}");
                    riskScore += 30;
                }
            }

            // 2. Check registrar location via phone number
            if (whois.Registrar != null)
            {
                var registrarCountry = whois.Registrar.GetPhoneCountryCode();
                if (!string.IsNullOrEmpty(registrarCountry) && registrarCountry != "US")
                {
                    fraudFlags.Add($"Registrar located outside US (Phone country: {registrarCountry}, Registrar: {whois.Registrar.Name})");
                    riskScore += 25;
                }
            }

            // 3. Check domain age (recently registered domains are suspicious)
            if (whois.IsRecentlyRegistered)
            {
                fraudFlags.Add($"Domain recently registered ({whois.DomainAgeInDays} days ago)");
                riskScore += 20;
            }

            // 4. Check if domain is expiring soon (could indicate temporary usage)
            if (whois.IsExpiringSoon)
            {
                fraudFlags.Add($"Domain expiring soon (expires: {whois.ExpiresDate?.ToString("yyyy-MM-dd")})");
                riskScore += 15;
            }

            // 5. Enhanced IP validation using both WHOIS and DNS data
            var allIpAddresses = new List<string>();
            
            // Add IPs from WHOIS data
            if (whois.IpAddresses != null && whois.IpAddresses.Any())
            {
                allIpAddresses.AddRange(whois.IpAddresses);
            }

            // Add IPs from DNS A records
            if (dns?.ARecords != null && dns.ARecords.Any())
            {
                allIpAddresses.AddRange(dns.ARecords);
            }

            // Remove duplicates
            allIpAddresses = allIpAddresses.Distinct().ToList();

            if (allIpAddresses.Any())
            {
                var nonUSIPs = new List<string>();
                foreach (var ip in allIpAddresses)
                {
                    try
                    {
                        var ipValidation = await NetworkValidationService.ValidateIPAddressAsync(ip);
                        if (ipValidation != null && !ipValidation.IsInUS)
                        {
                            nonUSIPs.Add($"{ip} ({ipValidation.Country})");
                        }
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog($"Error validating IP {ip}: {ex.Message}");
                    }
                }

                if (nonUSIPs.Any())
                {
                    fraudFlags.Add($"Domain IP addresses not in US: {string.Join(", ", nonUSIPs)}");
                    riskScore += 35;
                }
            }

            // 6. Check for suspicious registrars (common fraud patterns)
            if (whois.Registrar != null && IsSuspiciousRegistrar(whois.Registrar.Name))
            {
                fraudFlags.Add($"Domain registered with potentially suspicious registrar: {whois.Registrar.Name}");
                riskScore += 15;
            }

            // 7. Check domain status for suspicious flags
            if (whois.Status != null && whois.Status.Any(s => s.Contains("clientHold") || s.Contains("serverHold")))
            {
                fraudFlags.Add("Domain has hold status which may indicate issues");
                riskScore += 10;
            }

            // 8. Check for privacy protection (not necessarily fraud, but worth noting)
            if (HasPrivacyProtection(whois))
            {
                fraudFlags.Add("Domain uses privacy protection services");
                riskScore += 5; // Low score as this is common practice
            }

            // 9. NEW: DNS-based validations
            if (dns != null)
            {
                // Check if domain has proper email setup (MX records)
                if (!dns.MXRecords.Any())
                {
                    fraudFlags.Add("Domain has no MX records configured (no email capability)");
                    riskScore += 10;
                }

                // Check for suspicious TXT records (sometimes used for verification bypassing)
                if (dns.TXTRecords.Any(txt => txt.Contains("v=spf1") && txt.Contains("~all")))
                {
                    // This is actually good - proper SPF configuration
                }
                else if (dns.TXTRecords.Any())
                {
                    // Has TXT records but no proper SPF - suspicious
                    fraudFlags.Add("Domain has TXT records but no proper SPF configuration");
                    riskScore += 5;
                }

                // Check nameserver consistency
                var whoisNS = whois.Nameservers ?? new List<string>();
                var dnsNS = dns.NSRecords ?? new List<string>();
                
                if (whoisNS.Any() && dnsNS.Any())
                {
                    var nsMatch = whoisNS.Intersect(dnsNS, StringComparer.OrdinalIgnoreCase).Any();
                    if (!nsMatch)
                    {
                        fraudFlags.Add("Nameserver mismatch between WHOIS and DNS records");
                        riskScore += 15;
                    }
                }
            }

            result.FraudFlags = fraudFlags;
            result.RiskScore = riskScore;
            result.RiskLevel = CalculateRiskLevel(riskScore);
        }

        private bool IsSuspiciousRegistrar(string registrarName)
        {
            if (string.IsNullOrEmpty(registrarName))
                return false;

            // List of registrars commonly associated with suspicious activities
            // This is a basic implementation - in production, you'd maintain a more comprehensive list
            var suspiciousRegistrars = new[]
            {
                "NAMECHEAP",
                "DYNADOT",
                "PORKBUN"
                // Add more as needed based on patterns observed
            };

            return suspiciousRegistrars.Any(sr => 
                registrarName.ToUpper().Contains(sr));
        }

        private bool HasPrivacyProtection(WhoisResult whois)
        {
            if (whois.Contacts?.Owner == null)
                return false;

            foreach (var contact in whois.Contacts.Owner)
            {
                if (!string.IsNullOrEmpty(contact.Name) && 
                    (contact.Name.ToUpper().Contains("PRIVACY") || 
                     contact.Name.ToUpper().Contains("PROTECTED") ||
                     contact.Name.ToUpper().Contains("REDACTED")))
                {
                    return true;
                }
            }

            return false;
        }

        private string CalculateRiskLevel(int riskScore)
        {
            if (riskScore >= 60)
                return "HIGH";
            else if (riskScore >= 30)
                return "MEDIUM";
            else if (riskScore >= 10)
                return "LOW";
            else
                return "MINIMAL";
        }

        public void Dispose()
        {
            _whoisService?.Dispose();
        }
    }

    public class EnhancedDomainValidationResult
    {
        public string Domain { get; set; }
        public bool IsValid { get; set; }
        public DateTime ValidationTimestamp { get; set; }
        public WhoisResult WhoisData { get; set; }
        public DnsLookupResult DnsData { get; set; }
        public List<string> ValidationFlags { get; set; } = new List<string>();
        public List<string> FraudFlags { get; set; } = new List<string>();
        public int RiskScore { get; set; }
        public string RiskLevel { get; set; }

        public bool IsFraudulent => RiskScore >= 30; // Medium risk or above
        public bool IsHighRisk => RiskLevel == "HIGH";
        public bool HasFraudIndicators => FraudFlags.Any();
    }
}