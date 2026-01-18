using System;
using System.Threading.Tasks;

namespace NetworkValidationService
{
    class QuickTest
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Quick Validation Test ===");
            Console.WriteLine($"Started at: {DateTime.Now}");
            Console.WriteLine();

            // Test websites - strategic selection
            var testWebsites = new[]
            {
                "https://www.danse-saint-andre.fr",     // First one (French)
                "www.instagram.com",                    // Highly reputable (US)
                "http://www.elthamcemetery.com",        // Last one (Australian)
                "https://www.brooklyn.cuny.edu/web/academics/centers/irpe.php#", // US Educational
                "https://www.mogu27.ru/"                // Russian domain
            };

            using (var validator = new ProductionNetworkValidator())
            {
                foreach (var website in testWebsites)
                {
                    Console.WriteLine($" Testing: {website}");
                    Console.WriteLine(new string('-', 60));
                    
                    try
                    {
                        var domain = CleanDomain(website);
                        Console.WriteLine($"Cleaned domain: {domain}");
                        
                        var result = await validator.ValidateDomainProductionAsync(domain);
                        
                        // Show key results
                        Console.WriteLine($" Valid: {result.FormatValidation?.IsValid == true && result.DNSValidation?.ResolvesSuccessfully == true}");
                        Console.WriteLine($" US Registered: {result.USRegistrationAnalysis?.IsLikelyUSRegistered} ({result.USRegistrationAnalysis?.ConfidenceScore}%)");
                        Console.WriteLine($" Country: {result.WHOISValidation?.Country ?? "Unknown"}");
                        Console.WriteLine($" Registrar: {result.WHOISValidation?.Registrar ?? "Unknown"}");
                        
                        if (result.DNSValidation?.ResolvedIPs?.Count > 0)
                        {
                            Console.WriteLine($" IPs: {string.Join(", ", result.DNSValidation.ResolvedIPs)}");
                            
                            // Test IP location for first IP
                            var firstIP = result.DNSValidation.ResolvedIPs[0];
                            var ipResult = await validator.ValidateIPLocationProductionAsync(firstIP);
                            
                            if (ipResult.LocationAnalysis != null)
                            {
                                Console.WriteLine($" IP Country: {ipResult.LocationAnalysis.ConsensusCountryCode} ({ipResult.LocationAnalysis.CountryConsensusPercentage:F1}% confidence)");
                                Console.WriteLine($" Outside US: {ipResult.LocationAnalysis.IsDefinitelyOutsideUS}");
                            }
                        }
                        
                        if (result.Errors?.Count > 0)
                        {
                            Console.WriteLine($" Errors: {string.Join("; ", result.Errors)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($" Error: {ex.Message}");
                    }
                    
                    Console.WriteLine();
                    await Task.Delay(2000); // 2 second delay between requests
                }
            }

            Console.WriteLine("=== Test Complete ===");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        static string CleanDomain(string url)
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
    }
}
