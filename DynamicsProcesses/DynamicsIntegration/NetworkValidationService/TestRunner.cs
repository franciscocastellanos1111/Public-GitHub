using System;
using System.Threading.Tasks;

namespace NetworkValidationService
{
    /// <summary>
    /// Simple test runner for batch processing
    /// </summary>
    class TestRunner
    {
        static async Task TestBatchProcess()
        {
            Console.WriteLine("=== Testing Batch Website Processor ===");
            Console.WriteLine($"Started at: {DateTime.Now}");
            Console.WriteLine();

            try
            {
                using (var processor = new BatchWebsiteProcessor())
                {
                    // Test with first 5 websites to verify functionality
                    Console.WriteLine("Testing with first 5 websites from the sample file...");
                    
                    // Read and process a subset for testing
                    var allWebsites = System.Text.Json.JsonSerializer.Deserialize<string[]>(
                        System.IO.File.ReadAllText("SampleWebsites.json"));
                    
                    var testWebsites = new string[] 
                    {
                        allWebsites[0],
                        allWebsites[1], 
                        allWebsites[2],
                        allWebsites[3],
                        allWebsites[4]
                    };

                    // Create temporary test file
                    var testJson = System.Text.Json.JsonSerializer.Serialize(testWebsites, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await System.IO.File.WriteAllTextAsync("TestWebsites.json", testJson);

                    // Process the test websites
                    await processor.ProcessWebsitesAsync("TestWebsites.json");
                    
                    Console.WriteLine("\n=== Test Complete ===");
                    Console.WriteLine("Check the generated reports to verify functionality.");
                    Console.WriteLine("If everything looks good, you can run the full batch on all websites.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during testing: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        static async Task Main(string[] args)
        {
            await TestBatchProcess();
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }
}
