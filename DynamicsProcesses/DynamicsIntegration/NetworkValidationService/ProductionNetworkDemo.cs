using System;
using System.Threading.Tasks;

namespace NetworkValidationService
{
    /// <summary>
    /// Production demonstration of the Network Validation Service
    /// Shows real-world usage with comprehensive output
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Production Network Validation Service Demo ===");
            Console.WriteLine($"Started at: {DateTime.Now}");
            Console.WriteLine();

            using (var validator = new ProductionNetworkValidator())
            {
                // Domain validation tests
                Console.WriteLine("🌐 DOMAIN VALIDATION TESTS");
                Console.WriteLine("==========================================");
                await TestDomainValidation(validator, "microsoft.com");
                await TestDomainValidation(validator, "google.com");
                await TestDomainValidation(validator, "badactor-suspicious.ru");
                await TestDomainValidation(validator, "testdomain.us");
                await TestDomainValidation(validator, "invalid..domain");

                Console.WriteLine();

                // IP validation tests
                Console.WriteLine("🌍 IP GEOLOCATION TESTS");
                Console.WriteLine("==========================================");
                await TestIPValidation(validator, "8.8.8.8");        // Google DNS (US)
                await TestIPValidation(validator, "1.1.1.1");        // Cloudflare (US)
                await TestIPValidation(validator, "185.199.108.153"); // GitHub (US)
                await TestIPValidation(validator, "46.4.84.235");     // European IP
                await TestIPValidation(validator, "192.168.1.1");     // Private IP
                await TestIPValidation(validator, "invalid.ip");      // Invalid format

                Console.WriteLine();
                
                // Real-world scenarios
                Console.WriteLine("🔍 REAL-WORLD SCENARIOS");
                Console.WriteLine("==========================================");
                await DemonstrateRealWorldScenarios(validator);
            }

            Console.WriteLine("\n=== Demo Complete ===");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        static async Task TestDomainValidation(ProductionNetworkValidator validator, string domain)
        {
            Console.WriteLine($"\n🔍 Validating Domain: {domain}");
            Console.WriteLine(new string('-', 50));

            try
            {
                var result = await validator.ValidateDomainProductionAsync(domain);

                // Format validation
                Console.WriteLine($"📝 Format Valid: {result.FormatValidation?.IsValid ?? false}");
                if (result.FormatValidation?.Issues?.Count > 0)
                {
                    foreach (var issue in result.FormatValidation.Issues)
                    {
                        Console.WriteLine($"   ⚠️  {issue}");
                    }
                }

                // DNS validation
                Console.WriteLine($"🌐 DNS Resolves: {result.DNSValidation?.ResolvesSuccessfully ?? false}");
                if (result.DNSValidation?.ResolvedIPs?.Count > 0)
                {
                    Console.WriteLine($"   📍 IP Addresses: {string.Join(", ", result.DNSValidation.ResolvedIPs)}");
                }

                // WHOIS information
                if (result.WHOISValidation?.IsSuccessful == true)
                {
                    Console.WriteLine("📋 WHOIS Information:");
                    Console.WriteLine($"   🏢 Registrar: {result.WHOISValidation.Registrar ?? "Unknown"}");
                    Console.WriteLine($"   🌍 Country: {result.WHOISValidation.Country ?? "Unknown"}");
                    Console.WriteLine($"   📅 Registered: {result.WHOISValidation.RegistrationDate?.ToString("yyyy-MM-dd") ?? "Unknown"}");
                }

                // US registration analysis
                if (result.USRegistrationAnalysis != null)
                {
                    Console.WriteLine("🇺🇸 US Registration Analysis:");
                    Console.WriteLine($"   📊 Confidence: {result.USRegistrationAnalysis.ConfidenceScore}% ({result.USRegistrationAnalysis.ConfidenceLevel})");
                    Console.WriteLine($"   ✅ Likely US Registered: {result.USRegistrationAnalysis.IsLikelyUSRegistered}");
                    
                    if (result.USRegistrationAnalysis.Indicators?.Count > 0)
                    {
                        Console.WriteLine("   🔍 Indicators:");
                        foreach (var indicator in result.USRegistrationAnalysis.Indicators)
                        {
                            Console.WriteLine($"      • {indicator}");
                        }
                    }
                }

                // Overall assessment
                if (result.OverallAssessment != null)
                {
                    Console.WriteLine($"📊 Overall Assessment: {result.OverallAssessment.Recommendation}");
                    Console.WriteLine($"   🛡️ Trust Score: {result.OverallAssessment.TrustScore}/100");
                }

                // Errors
                if (result.Errors?.Count > 0)
                {
                    Console.WriteLine("❌ Errors:");
                    foreach (var error in result.Errors)
                    {
                        Console.WriteLine($"   • {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Validation failed: {ex.Message}");
            }
        }

        static async Task TestIPValidation(ProductionNetworkValidator validator, string ipAddress)
        {
            Console.WriteLine($"\n🔍 Validating IP: {ipAddress}");
            Console.WriteLine(new string('-', 50));

            try
            {
                var result = await validator.ValidateIPLocationProductionAsync(ipAddress);

                Console.WriteLine($"✅ Valid Format: {result.IsValid}");

                if (result.IsValid)
                {
                    Console.WriteLine($"📡 Address Family: {result.AddressFamily}");

                    // Classification
                    if (result.Classification != null)
                    {
                        Console.WriteLine("🏷️  IP Classification:");
                        if (result.Classification.IsPrivate) Console.WriteLine("   • Private Address (RFC 1918)");
                        if (result.Classification.IsLoopback) Console.WriteLine("   • Loopback Address");
                        if (result.Classification.IsLinkLocal) Console.WriteLine("   • Link-Local Address");
                        if (result.Classification.IsMulticast) Console.WriteLine("   • Multicast Address");
                        if (result.Classification.IsReserved) Console.WriteLine("   • Reserved Address");
                        if (result.Classification.IsCGNAT) Console.WriteLine("   • CGNAT Address");
                        
                        if (!result.Classification.IsPrivate && !result.Classification.IsReserved)
                        {
                            Console.WriteLine("   • Public Routable Address");
                        }
                    }

                    // Geolocation results
                    if (result.GeolocationResults?.Count > 0)
                    {
                        Console.WriteLine($"🌍 Geolocation Results ({result.GeolocationResults.Count} sources):");
                        foreach (var geo in result.GeolocationResults)
                        {
                            Console.WriteLine($"   📍 {geo.Source}:");
                            Console.WriteLine($"      🏁 Country: {geo.CountryCode ?? "Unknown"} ({geo.Country ?? "Unknown"})");
                            Console.WriteLine($"      🏙️  City: {geo.City ?? "Unknown"}");
                            Console.WriteLine($"      🏢 ISP: {geo.ISP ?? "Unknown"}");
                            if (geo.Latitude.HasValue && geo.Longitude.HasValue)
                            {
                                Console.WriteLine($"      📐 Coordinates: {geo.Latitude:F4}, {geo.Longitude:F4}");
                            }
                        }
                    }

                    // Consensus analysis
                    if (result.LocationAnalysis != null)
                    {
                        Console.WriteLine("🎯 Location Consensus:");
                        Console.WriteLine($"   🏁 Country: {result.LocationAnalysis.ConsensusCountryCode ?? "Unknown"}");
                        Console.WriteLine($"   📊 Confidence: {result.LocationAnalysis.CountryConsensusPercentage:F1}% ({result.LocationAnalysis.ConfidenceLevel})");
                        Console.WriteLine($"   🇺🇸 Definitely US: {result.LocationAnalysis.IsDefinitelyInUS}");
                        Console.WriteLine($"   🌍 Definitely Outside US: {result.LocationAnalysis.IsDefinitelyOutsideUS}");
                        Console.WriteLine($"   📡 Sources Used: {result.LocationAnalysis.SourcesUsed}");
                    }
                }

                // Errors
                if (result.Errors?.Count > 0)
                {
                    Console.WriteLine("❌ Errors:");
                    foreach (var error in result.Errors)
                    {
                        Console.WriteLine($"   • {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Validation failed: {ex.Message}");
            }
        }

        static async Task DemonstrateRealWorldScenarios(ProductionNetworkValidator validator)
        {
            Console.WriteLine("💼 SCENARIO 1: Suspicious Email Domain Check");
            Console.WriteLine("Checking if 'tempmail.org' is US-registered and legitimate...");
            var suspiciousDomain = await validator.ValidateDomainProductionAsync("tempmail.org");
            
            Console.WriteLine($"Result: {(suspiciousDomain.USRegistrationAnalysis?.IsLikelyUSRegistered == true ? "US-based" : "Non-US or uncertain")}");
            Console.WriteLine($"Confidence: {suspiciousDomain.USRegistrationAnalysis?.ConfidenceScore ?? 0}%");
            Console.WriteLine();

            Console.WriteLine("💼 SCENARIO 2: Foreign IP Address Detection");
            Console.WriteLine("Checking if '46.4.84.235' is outside the US...");
            var foreignIP = await validator.ValidateIPLocationProductionAsync("46.4.84.235");
            
            Console.WriteLine($"Result: {(foreignIP.LocationAnalysis?.IsDefinitelyOutsideUS == true ? "Confirmed outside US" : "US or uncertain")}");
            Console.WriteLine($"Country: {foreignIP.LocationAnalysis?.ConsensusCountryCode ?? "Unknown"}");
            Console.WriteLine($"Confidence: {foreignIP.LocationAnalysis?.CountryConsensusPercentage:F1}%");
            Console.WriteLine();

            Console.WriteLine("💼 SCENARIO 3: Government Domain Verification");
            Console.WriteLine("Checking if 'treasury.gov' is US-registered...");
            var govDomain = await validator.ValidateDomainProductionAsync("treasury.gov");
            
            Console.WriteLine($"Result: {(govDomain.USRegistrationAnalysis?.IsLikelyUSRegistered == true ? "Confirmed US" : "Uncertain")}");
            Console.WriteLine($"Confidence: {govDomain.USRegistrationAnalysis?.ConfidenceScore ?? 0}%");
            if (govDomain.USRegistrationAnalysis?.Indicators?.Count > 0)
            {
                Console.WriteLine("Key indicators:");
                foreach (var indicator in govDomain.USRegistrationAnalysis.Indicators)
                {
                    Console.WriteLine($"  • {indicator}");
                }
            }
            Console.WriteLine();

            Console.WriteLine("💼 SCENARIO 4: Performance Benchmarking");
            Console.WriteLine("Testing validation speed for batch processing...");
            
            var startTime = DateTime.UtcNow;
            var testDomains = new[] { "microsoft.com", "google.com", "github.com" };
            
            foreach (var domain in testDomains)
            {
                var domainStart = DateTime.UtcNow;
                var result = await validator.ValidateDomainProductionAsync(domain);
                var elapsed = DateTime.UtcNow - domainStart;
                
                Console.WriteLine($"  {domain}: {elapsed.TotalMilliseconds:F0}ms " +
                                $"(US: {result.USRegistrationAnalysis?.IsLikelyUSRegistered}, " +
                                $"Valid: {result.FormatValidation?.IsValid})");
            }
            
            var totalElapsed = DateTime.UtcNow - startTime;
            Console.WriteLine($"Total batch time: {totalElapsed.TotalMilliseconds:F0}ms");
            Console.WriteLine($"Average per domain: {totalElapsed.TotalMilliseconds / testDomains.Length:F0}ms");
        }
    }
}
