using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DSPilot.TestConsole;

/// <summary>
/// Simple entry point for automated testing
/// </summary>
public class RunUnifiedTest
{
    public static async Task Main(string[] args)
    {
        try
        {
            await UnifiedDbTest.RunAsync();
            Console.WriteLine("\n✓ All tests passed! Press any key to exit...");
            Console.ReadKey();
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ Test failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            Environment.Exit(1);
        }
    }
}
