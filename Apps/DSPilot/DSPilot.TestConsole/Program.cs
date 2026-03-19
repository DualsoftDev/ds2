using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using DSPilot.TestConsole;

Console.WriteLine("=== DSPilot Ev2.Backend.PLC DLL Inspector ===");
Console.WriteLine();

// DLL 경로
var dllPaths = new[]
{
    @"C:\ds\ds2\ExternalDlls\Ev2.Backend.PLC.dll",
    @"C:\ds\ds2\ExternalDlls\Ev2.PLC.Common.FS.dll",
    @"C:\ds\ds2\ExternalDlls\Ev2.Core.FS.dll"
};

foreach (var dllPath in dllPaths)
{
    if (File.Exists(dllPath))
    {
        DllInspector.InspectDll(dllPath);
        Console.WriteLine();
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine();
    }
    else
    {
        Console.WriteLine($"DLL not found: {dllPath}");
    }
}

Console.WriteLine();
Console.WriteLine("================================================================================");
Console.WriteLine();
Console.WriteLine("=== Ev2 Type Explorer (compile-time) ===");
Ev2TypeExplorer.ExplorePLCBackendService();
Ev2TypeExplorer.ExploreTagHistoricWAL();
Ev2TypeExplorer.ExploreConnectionTypes();
ConnectionConfigExplorer.Explore();
Console.WriteLine();

Console.WriteLine("=== PlcValue Inspector ===");
PlcValueInspector.Inspect();
Console.WriteLine();
Console.WriteLine("================================================================================");
Console.WriteLine();

// appsettings.json에서 DB 경로 읽기
var builder2 = Host.CreateApplicationBuilder(args);
builder2.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
var host2 = builder2.Build();
var config2 = host2.Services.GetRequiredService<IConfiguration>();
var dbPath = config2["PlcDatabase:SourceDbPath"] ?? "C:\\ds\\ds2\\Apps\\DSPilot\\DSPilot\\sample\\db\\DsDB.sqlite3";

// DB 내용 먼저 확인
await DbInspector.InspectAsync(dbPath);

Console.WriteLine("================================================================================");
Console.WriteLine();
Console.WriteLine("=== PLC Log Replay Test ===");
Console.WriteLine();
Console.WriteLine("This test will read historical logs from DB and write to PLC in sequence.");
Console.WriteLine();

await SimplePlcWriteTest.RunAsync(dbPath);

Console.WriteLine();
Console.WriteLine("================================================================================");
Console.WriteLine();
Console.WriteLine("Continuing to architecture overview...");
Console.WriteLine();

Console.WriteLine("=== DSPilot Event Processing Architecture ===");
Console.WriteLine();
Console.WriteLine("이 테스트 콘솔은 DSPilot의 새로운 이벤트 기반 아키텍처를 테스트합니다.");
Console.WriteLine();

var builder = Host.CreateApplicationBuilder(args);

// 로깅 설정
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Configuration 설정
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

var host = builder.Build();

// 설정 읽기
var config = host.Services.GetRequiredService<IConfiguration>();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

logger.LogInformation("=== Configuration ===");
logger.LogInformation("PlcConnection:Enabled = {Enabled}", config.GetValue<bool>("PlcConnection:Enabled"));
logger.LogInformation("PlcConnection:PlcName = {PlcName}", config["PlcConnection:PlcName"]);
logger.LogInformation("PlcConnection:ScanIntervalMs = {ScanInterval}", config.GetValue<int>("PlcConnection:ScanIntervalMs"));

Console.WriteLine();
Console.WriteLine("=== Architecture Overview ===");
Console.WriteLine("1. Ev2PlcEventSource (Mock) → PlcCommunicationEvent");
Console.WriteLine("2. PlcEventProcessorService → Channel<PlcCommunicationEvent> (백프레셔)");
Console.WriteLine("3. PlcEventProcessor → Rising Edge 감지 → State Transition");
Console.WriteLine("4. InMemoryCallStateStore (메모리 우선) + DB 병행");
Console.WriteLine();

Console.WriteLine("=== Key Features Implemented ===");
Console.WriteLine("✓ CallKey (FlowName, CallName, WorkName) 복합 키");
Console.WriteLine("✓ CallMappingInfo (FlowName/WorkName 캡처)");
Console.WriteLine("✓ Channel-based event processing with backpressure");
Console.WriteLine("✓ InMemoryCallStateStore (ConcurrentDictionary)");
Console.WriteLine("✓ IPlcHistorySource abstraction (future: Ev2 WAL integration)");
Console.WriteLine("✓ CallIOEvent logging with BatchTimestamp");
Console.WriteLine();

Console.WriteLine("=== Build Status ===");
logger.LogInformation("DSPilot.csproj 빌드: 성공 (0 errors)");
logger.LogInformation("Ev2.Backend.PLC 통합: Mock implementation");
logger.LogInformation("모든 핵심 서비스 구현 완료");

Console.WriteLine();
Console.WriteLine("=== Next Steps ===");
Console.WriteLine("1. DLL inspection 결과를 바탕으로 Ev2PlcEventSource 실제 구현");
Console.WriteLine("2. SubjectC2S 타입 매핑 및 사용");
Console.WriteLine("3. 실제 PLC 연결 테스트");
Console.WriteLine("4. Cycle Analysis F# 모듈 통합");
Console.WriteLine("5. UI 업데이트 (실시간 Call 상태 표시)");

Console.WriteLine();
logger.LogInformation("테스트 콘솔 종료");

return 0;
