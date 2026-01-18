using System;
using System.Threading.Tasks;

namespace NetworkValidationService
{
    /// <summary>
    /// Simple usage examples for the NetworkValidationService
    /// Copy these methods into your own application as needed
    /// </summary>
    public static class NetworkValidationExamples
    {
        /// <summary>
        /// Example: Quick website validation
        /// </summary>
        public static async Task<bool> IsWebsiteTrusted(string website)
        {
            using (var service = new NetworkValidationService())
            {
                var result = await service.ValidateWebsiteAsync(website);
                return result.IsValid && result.TrustScore >= 70;
            }
        }

        /// <summary>
        /// Example: Check if IP address is from the US
        /// </summary>
        public static async Task<bool> IsIPAddressFromUS(string ipAddress)
        {
            using (var service = new NetworkValidationService())
            {
                var result = await service.ValidateIPAddressAsync(ipAddress);
                return result.LocationAnalysis?.IsDefinitelyInUS == true || 
                       result.LocationAnalysis?.IsLikelyInUS == true;
            }
        }

        /// <summary>
        /// Example: Get comprehensive IP reputation with US location info
        /// </summary>
        public static async Task<IPReputationSummary> GetIPReputationSummary(string ipAddress)
        {
            using (var service = new NetworkValidationService())
            {
                var result = await service.ValidateIPAddressAsync(ipAddress);
                
                return new IPReputationSummary
                {
                    IPAddress = ipAddress,
                    IsValid = result.IsValid,
                    IsFromUS = result.LocationAnalysis?.IsDefinitelyInUS == true,
                    IsLikelyFromUS = result.LocationAnalysis?.IsLikelyInUS == true,
                    USLocationConfidence = result.LocationAnalysis?.USLocationConfidence ?? "UNKNOWN",
                    ReputationScore = result.ReputationScore,
                    RiskLevel = result.ReputationAnalysis?.RiskLevel ?? "UNKNOWN",
                    Country = result.LocationAnalysis?.ConsensusCountry,
                    City = result.LocationAnalysis?.ConsensusCity,
                    Summary = result.ValidationSummary
                };
            }
        }

        /// <summary>
        /// Example: Email validation with detailed scorecard
        /// </summary>
        public static async Task<EmailValidationSummary> GetEmailValidationSummary(string emailAddress)
        {
            using (var service = new NetworkValidationService())
            {
                var result = await service.ValidateEmailAsync(emailAddress);
                
                return new EmailValidationSummary
                {
                    EmailAddress = emailAddress,
                    IsValid = result.IsValid,
                    OverallScore = result.OverallScore,
                    RiskLevel = result.RiskLevel,
                    IsDeliverable = result.DeliverabilityAnalysis?.IsDeliverable == true,
                    IsDisposable = result.DomainAnalysis?.IsDisposableEmail == true,
                    EmailType = result.ReputationAnalysis?.EmailType,
                    ActivityScore = result.ReputationAnalysis?.ActivityScore ?? 0,
                    DomainScore = result.DomainAnalysis?.DomainScore ?? 0,
                    SpamLikelihood = result.ReputationAnalysis?.SpamLikelihood ?? 0,
                    Summary = result.ValidationSummary
                };
            }
        }

        /// <summary>
        /// Example: Batch validation of multiple websites
        /// </summary>
        public static async Task<WebsiteBatchResult> ValidateMultipleWebsites(string[] websites)
        {
            using (var service = new NetworkValidationService())
            {
                var results = new System.Collections.Generic.List<WebsiteValidationResult>();
                var validCount = 0;
                var trustedCount = 0;

                foreach (var website in websites)
                {
                    try
                    {
                        var result = await service.ValidateWebsiteAsync(website);
                        results.Add(result);

                        if (result.IsValid) validCount++;
                        if (result.IsValid && result.TrustScore >= 70) trustedCount++;
                    }
                    catch (Exception ex)
                    {
                        // Log error or add to results with error status
                        results.Add(new WebsiteValidationResult
                        {
                            OriginalInput = website,
                            IsValid = false,
                            ValidationSummary = $"Error: {ex.Message}",
                            Errors = new System.Collections.Generic.List<string> { ex.Message }
                        });
                    }
                }

                return new WebsiteBatchResult
                {
                    TotalWebsites = websites.Length,
                    ValidWebsites = validCount,
                    TrustedWebsites = trustedCount,
                    Results = results
                };
            }
        }

        // Demo method showing all features
        public static async Task RunQuickDemo()
        {
            Console.WriteLine("=== Network Validation Service - Quick Demo ===\n");

            // Website validation
            Console.WriteLine("1. Website Validation:");
            var websiteTrusted = await IsWebsiteTrusted("microsoft.com");
            Console.WriteLine($"   microsoft.com is trusted: {websiteTrusted}");

            // IP validation with US detection
            Console.WriteLine("\n2. IP Address Validation:");
            var ipSummary = await GetIPReputationSummary("8.8.8.8");
            Console.WriteLine($"   IP: {ipSummary.IPAddress}");
            Console.WriteLine($"   From US: {ipSummary.IsFromUS}");
            Console.WriteLine($"   Reputation Score: {ipSummary.ReputationScore}/100");

            // Email validation
            Console.WriteLine("\n3. Email Validation:");
            var emailSummary = await GetEmailValidationSummary("test@gmail.com");
            Console.WriteLine($"   Email: {emailSummary.EmailAddress}");
            Console.WriteLine($"   Valid: {emailSummary.IsValid}");
            Console.WriteLine($"   Score: {emailSummary.OverallScore}/100");
            Console.WriteLine($"   Type: {emailSummary.EmailType}");

            Console.WriteLine("\n=== Demo Complete ===");
        }
    }

    // Helper classes for simplified results
    public class IPReputationSummary
    {
        public string IPAddress { get; set; }
        public bool IsValid { get; set; }
        public bool IsFromUS { get; set; }
        public bool IsLikelyFromUS { get; set; }
        public string USLocationConfidence { get; set; }
        public int ReputationScore { get; set; }
        public string RiskLevel { get; set; }
        public string Country { get; set; }
        public string City { get; set; }
        public string Summary { get; set; }
    }

    public class EmailValidationSummary
    {
        public string EmailAddress { get; set; }
        public bool IsValid { get; set; }
        public int OverallScore { get; set; }
        public string RiskLevel { get; set; }
        public bool IsDeliverable { get; set; }
        public bool IsDisposable { get; set; }
        public string EmailType { get; set; }
        public int ActivityScore { get; set; }
        public int DomainScore { get; set; }
        public int SpamLikelihood { get; set; }
        public string Summary { get; set; }
    }

    public class WebsiteBatchResult
    {
        public int TotalWebsites { get; set; }
        public int ValidWebsites { get; set; }
        public int TrustedWebsites { get; set; }
        public System.Collections.Generic.List<WebsiteValidationResult> Results { get; set; }
    }
}
