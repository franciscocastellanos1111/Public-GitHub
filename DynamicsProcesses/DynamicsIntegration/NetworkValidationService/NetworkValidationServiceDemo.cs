using System;
using System.Threading.Tasks;

namespace NetworkValidationService
{
    /// <summary>
    /// Demo program showing how to use the standalone NetworkValidationService
    /// </summary>
    class NetworkValidationServiceDemo
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Standalone Network Validation Service Demo ===");
            Console.WriteLine($"Started at: {DateTime.Now}");
            Console.WriteLine();

            using (var networkService = new NetworkValidationService())
            {
                // Demo 1: Website Validation
                await DemoWebsiteValidation(networkService);

                Console.WriteLine("\n" + new string('=', 80) + "\n");

                // Demo 2: IP Address Validation with US Detection
                await DemoIPValidation(networkService);

                Console.WriteLine("\n" + new string('=', 80) + "\n");

                // Demo 3: Email Validation Scorecard
                await DemoEmailValidation(networkService);
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        static async Task DemoWebsiteValidation(NetworkValidationService service)
        {
            Console.WriteLine("🌐 WEBSITE VALIDATION DEMO");
            Console.WriteLine(new string('=', 50));

            var websites = new[] { "microsoft.com", "google.com", "badwebsite12345.com" };

            foreach (var website in websites)
            {
                Console.WriteLine($"\n🔍 Validating: {website}");
                Console.WriteLine(new string('-', 40));

                try
                {
                    var result = await service.ValidateWebsiteAsync(website);

                    Console.WriteLine($"✅ Valid: {result.IsValid}");
                    Console.WriteLine($"🎯 Trust Score: {result.TrustScore}/100");
                    Console.WriteLine($"⚠️ Risk Level: {result.RiskLevel}");
                    Console.WriteLine($"⏱️ Processing Time: {result.ProcessingTimeMs}ms");
                    Console.WriteLine($"📝 Summary: {result.ValidationSummary}");

                    if (result.TrustFactors.Count > 0)
                    {
                        Console.WriteLine("🎯 Trust Factors:");
                        foreach (var factor in result.TrustFactors)
                        {
                            Console.WriteLine($"   • {factor}");
                        }
                    }

                    if (result.Errors.Count > 0)
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
                    Console.WriteLine($"❌ Error validating {website}: {ex.Message}");
                }
            }
        }

        static async Task DemoIPValidation(NetworkValidationService service)
        {
            Console.WriteLine("🌍 IP ADDRESS VALIDATION DEMO (with US Detection)");
            Console.WriteLine(new string('=', 50));

            var ipAddresses = new[] 
            { 
                "8.8.8.8",        // Google DNS (US)
                "1.1.1.1",        // Cloudflare (US)
                "46.4.84.235",    // European IP
                "192.168.1.1",    // Private IP
                "invalid.ip"      // Invalid format
            };

            foreach (var ipAddress in ipAddresses)
            {
                Console.WriteLine($"\n🔍 Analyzing IP: {ipAddress}");
                Console.WriteLine(new string('-', 40));

                try
                {
                    var result = await service.ValidateIPAddressAsync(ipAddress);

                    Console.WriteLine($"✅ Valid: {result.IsValid}");
                    Console.WriteLine($"🏷️ Type: {result.Classification?.Type ?? "Unknown"}");
                    
                    if (result.LocationAnalysis?.USLocationSummary != null)
                    {
                        Console.WriteLine($"📍 US Location Analysis:");
                        Console.WriteLine($"   {result.LocationAnalysis.USLocationSummary}");
                    }

                    Console.WriteLine($"🛡️ Reputation Score: {result.ReputationScore}/100");
                    
                    if (!string.IsNullOrEmpty(result.ReputationAnalysis?.ReputationSummary))
                    {
                        Console.WriteLine($"🛡️ Reputation: {result.ReputationAnalysis.ReputationSummary}");
                    }

                    Console.WriteLine($"⏱️ Processing Time: {result.ProcessingTimeMs}ms");
                    
                    if (!string.IsNullOrEmpty(result.ValidationSummary))
                    {
                        Console.WriteLine($"📝 Summary:\n{result.ValidationSummary}");
                    }

                    if (result.Errors.Count > 0)
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
                    Console.WriteLine($"❌ Error analyzing {ipAddress}: {ex.Message}");
                }
            }
        }

        static async Task DemoEmailValidation(NetworkValidationService service)
        {
            Console.WriteLine("📧 EMAIL VALIDATION SCORECARD DEMO");
            Console.WriteLine(new string('=', 50));

            var emails = new[] 
            { 
                "john.doe@microsoft.com",
                "user@tempmail.org",
                "invalid-email",
                "test123@gmail.com",
                "admin@nonexistentdomain12345.com"
            };

            foreach (var email in emails)
            {
                Console.WriteLine($"\n🔍 Validating Email: {email}");
                Console.WriteLine(new string('-', 50));

                try
                {
                    var result = await service.ValidateEmailAsync(email);

                    Console.WriteLine($"✅ Valid: {result.IsValid}");
                    Console.WriteLine($"🎯 Overall Score: {result.OverallScore}/100");
                    Console.WriteLine($"⚠️ Risk Level: {result.RiskLevel}");

                    if (result.ScoreComponents.Count > 0)
                    {
                        Console.WriteLine("📊 Score Breakdown:");
                        foreach (var component in result.ScoreComponents)
                        {
                            Console.WriteLine($"   • {component.Category}: {component.Score}/{component.MaxScore} - {component.Details}");
                        }
                    }

                    Console.WriteLine($"⏱️ Processing Time: {result.ProcessingTimeMs}ms");
                    
                    if (!string.IsNullOrEmpty(result.ValidationSummary))
                    {
                        Console.WriteLine($"📝 Detailed Analysis:\n{result.ValidationSummary}");
                    }

                    if (result.DomainAnalysis?.IsDisposableEmail == true)
                    {
                        Console.WriteLine("⚠️ WARNING: This appears to be a disposable email address!");
                    }

                    if (result.Errors.Count > 0)
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
                    Console.WriteLine($"❌ Error validating {email}: {ex.Message}");
                }
            }
        }
    }
}
