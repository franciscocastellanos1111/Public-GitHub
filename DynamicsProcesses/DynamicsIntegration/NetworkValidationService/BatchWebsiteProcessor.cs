using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetworkValidationService
{
    /// <summary>
    /// Batch processor for validating multiple websites from JSON file
    /// Generates comprehensive reports with US registration and location analysis
    /// </summary>
    public class BatchWebsiteProcessor
    {
        private readonly ProductionNetworkValidator _validator;
        private readonly List<BatchValidationResult> _results;

        public BatchWebsiteProcessor()
        {
            _validator = new ProductionNetworkValidator();
            _results = new List<BatchValidationResult>();


        }

        public void Dispose()
        {
            System.GC.Collect();
            Console.WriteLine("Resources disposed.");
        }

        /// <summary>
        /// Process all websites from the JSON file
        /// </summary>
        public async Task ProcessWebsitesAsync(string jsonFilePath)
        {
            Console.WriteLine("=== Network Validation Batch Processor ===");
            Console.WriteLine($"Started at: {DateTime.Now}");
            Console.WriteLine();

            try
            {
                // Load websites from JSON
                var websites = LoadWebsitesFromJson(jsonFilePath);
                Console.WriteLine($"Loaded {websites.Count} websites for validation");
                Console.WriteLine();

                // Process each website
                int processed = 0;
                foreach (var website in websites)
                {
                    processed++;
                    Console.WriteLine($"[{processed}/{websites.Count}] Processing: {website}");
                    
                    try
                    {
                        var result = await ProcessSingleWebsite(website);
                        _results.Add(result);
                        
                        // Show quick summary
                        Console.WriteLine($"   Valid: {result.IsValidDomain}");
                        Console.WriteLine($"   US Registered: {result.IsUSRegistered} ({result.USConfidence}%)");
                        Console.WriteLine($"   Country: {result.PrimaryCountry ?? "Unknown"}");
                        Console.WriteLine();
                        
                        // Small delay to be respectful to APIs
                        await Task.Delay(1000);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   Error: {ex.Message}");
                        Console.WriteLine();
                    }
                }

                // Generate reports
                await GenerateReports();
                
                Console.WriteLine("=== Processing Complete ===");
                Console.WriteLine($"Total processed: {_results.Count}");
                Console.WriteLine($"Reports generated in current directory");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
            }
        }

        private List<string> LoadWebsitesFromJson(string filePath)
        {
            var jsonContent = File.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<List<string>>(jsonContent) ?? new List<string>();

        }

        private async Task<BatchValidationResult> ProcessSingleWebsite(string website)
        {
            var result = new BatchValidationResult
            {
                OriginalURL = website,
                ProcessedAt = DateTime.UtcNow
            };

            // Clean the domain
            var domain = CleanDomain(website);
            result.CleanedDomain = domain;

            if (string.IsNullOrEmpty(domain))
            {
                result.Errors.Add("Invalid domain format");
                return result;
            }

            // Validate the domain
            var domainResult = await _validator.ValidateDomainProductionAsync(domain);
            
            // Extract key information
            result.IsValidDomain = domainResult.FormatValidation?.IsValid == true && 
                                 domainResult.DNSValidation?.ResolvesSuccessfully == true;
            
            result.IsUSRegistered = domainResult.USRegistrationAnalysis?.IsLikelyUSRegistered ?? false;
            result.USConfidence = domainResult.USRegistrationAnalysis?.ConfidenceScore ?? 0;
            result.USConfidenceLevel = domainResult.USRegistrationAnalysis?.ConfidenceLevel ?? "Unknown";
            
            result.Registrar = domainResult.WHOISValidation?.Registrar;
            result.RegistrationDate = domainResult.WHOISValidation?.RegistrationDate;
            result.PrimaryCountry = domainResult.WHOISValidation?.Country;
            
            if (domainResult.DNSValidation?.ResolvedIPs?.Any() == true)
            {
                result.IPAddresses = domainResult.DNSValidation.ResolvedIPs;
                
                // Validate the first IP for location
                var firstIP = domainResult.DNSValidation.ResolvedIPs.First();
                var ipResult = await _validator.ValidateIPLocationProductionAsync(firstIP);
                
                if (ipResult.LocationAnalysis != null)
                {
                    result.IPCountry = ipResult.LocationAnalysis.ConsensusCountryCode;
                    result.IPLocationConfidence = ipResult.LocationAnalysis.CountryConsensusPercentage;
                    result.IsIPOutsideUS = ipResult.LocationAnalysis.IsDefinitelyOutsideUS;
                }
            }

            // Collect any errors
            if (domainResult.Errors?.Any() == true)
            {
                result.Errors.AddRange(domainResult.Errors);
            }

            return result;
        }

        private string CleanDomain(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            try
            {
                var cleaned = url.Trim().ToLower()
                    .Replace("https://", "")
                    .Replace("http://", "")
                    .Replace("www.", "");

                // Remove path and parameters
                if (cleaned.Contains("/"))
                    cleaned = cleaned.Split('/')[0];
                
                if (cleaned.Contains("?"))
                    cleaned = cleaned.Split('?')[0];
                
                if (cleaned.Contains("#"))
                    cleaned = cleaned.Split('#')[0];

                return cleaned;
            }
            catch
            {
                return null;
            }
        }

        private async Task GenerateReports()
        {
            await GenerateSummaryReport();
            await GenerateDetailedReport();
            await GenerateUSAnalysisReport();
            await GenerateCSVReport();
        }

        private async Task GenerateSummaryReport()
        {
            var report = new System.Text.StringBuilder();

            report.AppendLine("=== NETWORK VALIDATION SUMMARY REPORT ===");
            report.AppendLine($"Generated: {DateTime.Now}");
            report.AppendLine($"Total Websites Processed: {_results.Count}");
            report.AppendLine();

            var validDomains = _results.Count(r => r.IsValidDomain);
            var usRegistered = _results.Count(r => r.IsUSRegistered);
            var outsideUS = _results.Count(r => r.IsIPOutsideUS);

            report.AppendLine("OVERVIEW:");
            report.AppendLine($" Valid Domains: {validDomains}/{_results.Count} ({(double)validDomains / _results.Count * 100:F1}%)");
            report.AppendLine($" US Registered: {usRegistered}/{_results.Count} ({(double)usRegistered / _results.Count * 100:F1}%)");
            report.AppendLine($" IPs Outside US: {outsideUS}/{_results.Count} ({(double)outsideUS / _results.Count * 100:F1}%)");
            report.AppendLine();

            // Country breakdown
            report.AppendLine("TOP COUNTRIES (by domain registration):");
            var countryStats = _results.Where(r => !string.IsNullOrEmpty(r.PrimaryCountry))
                                        .GroupBy(r => r.PrimaryCountry)
                                        .OrderByDescending(g => g.Count())
                                        .Take(10);

            foreach (var country in countryStats)
            {
                report.AppendLine($"  {country.Key}: {country.Count()} domains");
            }

            // Replace the problematic line with the following:
             File.WriteAllText("ValidationSummary.txt", report.ToString());
            Console.WriteLine(" Summary report saved: ValidationSummary.txt");
        }

        private async Task GenerateDetailedReport()
        {
            var report = new System.Text.StringBuilder();
            
            report.AppendLine("=== DETAILED VALIDATION REPORT ===");
            report.AppendLine($"Generated: {DateTime.Now}");

            // Replace the problematic line with the following:
             System.IO.File.WriteAllText("ValidationSummary.txt", report.ToString());
            report.AppendLine();

            foreach (var result in _results.OrderBy(r => r.CleanedDomain))
            {
                report.AppendLine($"Domain: {result.CleanedDomain}");
                report.AppendLine($"Original URL: {result.OriginalURL}");
                report.AppendLine($"Valid: {result.IsValidDomain}");
                report.AppendLine($"US Registered: {result.IsUSRegistered} (Confidence: {result.USConfidence}%)");
                report.AppendLine($"Registrar: {result.Registrar ?? "Unknown"}");
                report.AppendLine($"Registration Date: {result.RegistrationDate?.ToString("yyyy-MM-dd") ?? "Unknown"}");
                report.AppendLine($"Country: {result.PrimaryCountry ?? "Unknown"}");
                report.AppendLine($"IP Country: {result.IPCountry ?? "Unknown"} (Confidence: {result.IPLocationConfidence:F1}%)");
                
                if (result.IPAddresses?.Any() == true)
                {
                    report.AppendLine($"IP Addresses: {string.Join(", ", result.IPAddresses)}");
                }
                
                if (result.Errors?.Any() == true)
                {
                    report.AppendLine($"Errors: {string.Join("; ", result.Errors)}");
                }
                
                report.AppendLine("---");
                report.AppendLine();
            }

             File.WriteAllText("DetailedValidationReport.txt", report.ToString());
            Console.WriteLine(" Detailed report saved: DetailedValidationReport.txt");
        }

        private async Task GenerateUSAnalysisReport()
        {
            var report = new System.Text.StringBuilder();
            
            report.AppendLine("=== US REGISTRATION ANALYSIS ===");
            report.AppendLine($"Generated: {DateTime.Now}");
            report.AppendLine();

            report.AppendLine("DEFINITELY US REGISTERED (High Confidence 80%+):");
            var definitelyUS = _results.Where(r => r.IsUSRegistered && r.USConfidence >= 80)
                                     .OrderByDescending(r => r.USConfidence);
            
            foreach (var result in definitelyUS)
            {
                report.AppendLine($"  {result.CleanedDomain} - {result.USConfidence}% confidence");
            }
            report.AppendLine();

            report.AppendLine("LIKELY US REGISTERED (Medium Confidence 60-79%):");
            var likelyUS = _results.Where(r => r.IsUSRegistered && r.USConfidence >= 60 && r.USConfidence < 80)
                                 .OrderByDescending(r => r.USConfidence);
            
            foreach (var result in likelyUS)
            {
                report.AppendLine($"  {result.CleanedDomain} - {result.USConfidence}% confidence");
            }
            report.AppendLine();

            report.AppendLine("DEFINITELY NON-US (IPs confirmed outside US):");
            var definitelyNonUS = _results.Where(r => r.IsIPOutsideUS && !string.IsNullOrEmpty(r.IPCountry))
                                         .GroupBy(r => r.IPCountry)
                                         .OrderByDescending(g => g.Count());
            
            foreach (var countryGroup in definitelyNonUS)
            {
                report.AppendLine($"  {countryGroup.Key}:");
                foreach (var result in countryGroup.OrderBy(r => r.CleanedDomain))
                {
                    report.AppendLine($"    - {result.CleanedDomain}");
                }
            }

             File.WriteAllText("USAnalysisReport.txt", report.ToString());
            Console.WriteLine(" US analysis report saved: USAnalysisReport.txt");
        }

        private async Task GenerateCSVReport()
        {
            var csv = new System.Text.StringBuilder();
            
            // CSV Header
            csv.AppendLine("Domain,Original_URL,Valid,US_Registered,US_Confidence,Registrar,Registration_Date,Domain_Country,IP_Country,IP_Confidence,Outside_US,Primary_IP,Errors");

            // CSV Data
            foreach (var result in _results.OrderBy(r => r.CleanedDomain))
            {
                var line = $"{result.CleanedDomain}," +
                          $"\"{result.OriginalURL}\"," +
                          $"{result.IsValidDomain}," +
                          $"{result.IsUSRegistered}," +
                          $"{result.USConfidence}," +
                          $"\"{result.Registrar ?? string.Empty}\"," +
                          $"\"{result.RegistrationDate?.ToString("yyyy-MM-dd") ?? string.Empty}\"," +
                          $"\"{result.PrimaryCountry ?? string.Empty}\"," +
                          $"\"{result.IPCountry ?? string.Empty}\"," +
                          $"{result.IPLocationConfidence:F1}," +
                          $"{result.IsIPOutsideUS}," +
                          $"\"{result.IPAddresses?.FirstOrDefault() ?? string.Empty}\"," +
                          $"\"{string.Join("; ", result.Errors ?? new List<string>())}\"";
                
                csv.AppendLine(line);
            }

             File.WriteAllText("ValidationResults.csv", csv.ToString());
            Console.WriteLine(" CSV report saved: ValidationResults.csv");
        }

      
    }

    /// <summary>
    /// Result class for batch validation
    /// </summary>
    public class BatchValidationResult
    {
        public string OriginalURL { get; set; }
        public string CleanedDomain { get; set; }
        public DateTime ProcessedAt { get; set; }
        public bool IsValidDomain { get; set; }
        public bool IsUSRegistered { get; set; }
        public int USConfidence { get; set; }
        public string USConfidenceLevel { get; set; }
        public string Registrar { get; set; }
        public DateTime? RegistrationDate { get; set; }
        public string PrimaryCountry { get; set; }
        public List<string> IPAddresses { get; set; } = new List<string>();
        public string IPCountry { get; set; }
        public double IPLocationConfidence { get; set; }
        public bool IsIPOutsideUS { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }
}
