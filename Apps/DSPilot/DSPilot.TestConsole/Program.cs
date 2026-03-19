using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DSPilot.TestConsole;

class Program
{
    static async Task Main(string[] args)
    {
        // 설정 로드
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        // 로거 생성
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger<Program>();

        // 메뉴 루프
        while (true)
        {
            Console.Clear();
            Console.WriteLine("╔════════════════════════════════════════════╗");
            Console.WriteLine("║     DSPilot Test Console - 2 Modes         ║");
            Console.WriteLine("╚════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.WriteLine("Select mode:");
            Console.WriteLine();
            Console.WriteLine("  1. Replay Mode  (DB → PLC)");
            Console.WriteLine("     - Read logs from DB");
            Console.WriteLine("     - Replay to PLC with timestamp intervals");
            Console.WriteLine();
            Console.WriteLine("  2. Capture Mode (PLC → DB + Events)");
            Console.WriteLine("     - Receive PLC events via SubjectC2S");
            Console.WriteLine("     - Auto-save to DB via TagHistoricWAL");
            Console.WriteLine();
            Console.WriteLine("  3. DB Verifier");
            Console.WriteLine("     - Verify DB schema and data");
            Console.WriteLine();
            Console.WriteLine("  0. Exit");
            Console.WriteLine();
            Console.Write("Enter selection (0-3): ");

            var choice = Console.ReadLine()?.Trim();
            Console.WriteLine();

            try
            {
                switch (choice)
                {
                    case "1":
                        Console.WriteLine("=== Replay Mode ===");
                        Console.Write("Enter DB path (default: C:/ds/ds2/Apps/DSPilot/DSPilot/sample/db/dsdb_capture.sqlite3): ");
                        var dbPath = Console.ReadLine()?.Trim();
                        if (string.IsNullOrEmpty(dbPath))
                        {
                            dbPath = "C:/ds/ds2/Apps/DSPilot/DSPilot/sample/db/dsdb_capture.sqlite3";
                        }
                        Console.WriteLine($"   📖 Reading from: {dbPath}");
                        await ReplayMode.RunAsync(dbPath);
                        break;

                    case "2":
                        Console.WriteLine("=== Capture Mode ===");
                        Console.WriteLine($"   💾 Writing to: C:/ds/ds2/Apps/DSPilot/DSPilot/sample/db/dsdb_capture.sqlite3");
                        await CaptureMode.RunAsync();
                        break;

                    case "3":
                        Console.WriteLine("=== DB Verifier ===");
                        Console.Write("Enter DB path (default: C:/ds/ds2/Apps/DSPilot/DSPilot/sample/db/dsdb_capture.sqlite3): ");
                        var dbVerifyPath = Console.ReadLine()?.Trim();
                        if (string.IsNullOrEmpty(dbVerifyPath))
                        {
                            dbVerifyPath = "C:/ds/ds2/Apps/DSPilot/DSPilot/sample/db/dsdb_capture.sqlite3";
                        }
                        await DbVerifier.RunAsync(dbVerifyPath);
                        break;

                    case "0":
                        Console.WriteLine("Exiting...");
                        return;

                    default:
                        Console.WriteLine("Invalid selection. Press any key to continue...");
                        Console.ReadKey();
                        continue;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in menu");
                Console.WriteLine();
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("Press any key to return to menu...");
            Console.ReadKey();
        }
    }
}
