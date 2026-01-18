using System;
using System.Threading.Tasks;

namespace NetworkValidationService
{
    /// <summary>
    /// Main program with options for single validation or batch processing
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Network Validation Service ===");
            Console.WriteLine($"Started at: {DateTime.Now}");
            Console.WriteLine();

            // Check if sample websites file exists
            var sampleFile = "SampleWebsites.json";
            if (System.IO.File.Exists(sampleFile))
            {
                Console.WriteLine($"Found sample websites file: {sampleFile}");
                Console.WriteLine("Choose an option:");
                Console.WriteLine("1. Run batch validation on sample websites");
                Console.WriteLine("2. Run interactive demo");
                Console.WriteLine("3. Exit");
                Console.Write("Enter choice (1-3): ");

                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        await RunBatchValidation(sampleFile);
                        break;
                    case "2":
                        await RunInteractiveDemo();
                        break;
                    case "3":
                        return;
                    default:
                        Console.WriteLine("Invalid choice. Running interactive demo...");
                        await RunInteractiveDemo();
                        break;
                }
            }
            else
            {
                Console.WriteLine("No sample websites file found. Running interactive demo...");
                await RunInteractiveDemo();
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        static async Task RunBatchValidation(string jsonFilePath)
        {
            Console.WriteLine("\n Starting batch validation process...");
            Console.WriteLine("This will validate all websites in the JSON file and generate comprehensive reports.");
            Console.WriteLine("Note: This may take several minutes depending on the number of websites.");
            Console.WriteLine();

            // FIX: Add parentheses to constructor and remove 'using' (BatchWebsiteProcessor does not implement IDisposable)
            var processor = new BatchWebsiteProcessor();
            await processor.ProcessWebsitesAsync(jsonFilePath);

            Console.WriteLine("\n Batch processing complete!");
            Console.WriteLine("Check the current directory for generated reports:");
            Console.WriteLine("  - ValidationSummary.txt (Overview and statistics)");
            Console.WriteLine("  - DetailedValidationReport.txt (Complete details for each domain)");
            Console.WriteLine("  - USAnalysisReport.txt (US registration analysis)");
            Console.WriteLine("  - ValidationResults.csv (Data for Excel/analysis)");
        }

        static async Task RunInteractiveDemo()
        {
            Console.WriteLine("\n Running interactive demo...");

            // FIX: Remove 'using' (ProductionNetworkValidator does not implement IDisposable)
            var validator = new ProductionNetworkValidator();

            // Domain validation tests
            Console.WriteLine("\n DOMAIN VALIDATION TESTS");
            Console.WriteLine("==========================================");
            await TestDomainValidation(validator, "microsoft.com");
            await TestDomainValidation(validator, "google.com");
            await TestDomainValidation(validator, "instagram.com");
            await TestDomainValidation(validator, "treasury.gov");

            Console.WriteLine();

            // IP validation tests
            Console.WriteLine(" IP GEOLOCATION TESTS");
            Console.WriteLine("==========================================");
            await TestIPValidation(validator, "8.8.8.8");        // Google DNS (US)
            await TestIPValidation(validator, "1.1.1.1");        // Cloudflare (US)
            await TestIPValidation(validator, "46.4.84.235");     // European IP

            Console.WriteLine();

            // Real-world scenarios
            Console.WriteLine(" REAL-WORLD SCENARIOS");
            Console.WriteLine("==========================================");
            await DemonstrateRealWorldScenarios(validator);
        }

       


    }
}