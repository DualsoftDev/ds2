using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.AB;
using Ev2.PLC.Common;
using Microsoft.FSharp.Core;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using WAL = Ev2.PLC.Common.TagSpecModule.WAL;
using AbPlcType = Ev2.PLC.Protocol.AB.PlcType;

namespace DSPilot.TestConsole;

/// <summary>
/// PLCBackendService 사용 예제
/// Ev2.Backend.PLC의 실제 API를 사용하는 방법을 보여줍니다
/// </summary>
public static class PLCBackendServiceExample
{
    public static void DemonstrateUsage()
    {
        Console.WriteLine("=== PLCBackendService Usage Example ===");
        Console.WriteLine();

        // Step 1: IConnectionConfiguration 생성 (Mitsubishi 예제)
        var connectionConfig = AbConnectionConfig.Create(
            ipAddress: "192.168.9.20",
            port: FSharpOption<int>.Some(5555),
            name: FSharpOption<string>.Some("TestPLC"),
            plcType: FSharpOption<AbPlcType>.None,
            slot: FSharpOption<byte>.Some((byte)0),
            scanInterval: FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(500)),
            timeout: FSharpOption<TimeSpan>.Some(TimeSpan.FromSeconds(5)),
            maxRetries: FSharpOption<int>.Some(3),
            retryDelay: FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(100))
        );

        Console.WriteLine($"Created connection config:");
        Console.WriteLine($"  Type: {connectionConfig.GetType().Name}");
        Console.WriteLine();

        // Step 2: TagSpec 배열 생성
        // TagSpec을 생성하려면 Ev2.PLC.Common.TagSpecModule의 static 메서드를 사용해야 함
        // 예제: AASX 파일에서 파싱한 태그 정보 기반
        var tagSpecs = CreateSampleTagSpecs();

        Console.WriteLine($"Created {tagSpecs.Length} TagSpec(s)");
        foreach (var spec in tagSpecs)
        {
            Console.WriteLine($"  - {spec.Name} @ {spec.Address}");
        }
        Console.WriteLine();

        // Step 3: ScanConfiguration 생성
        var scanConfigs = new[]
        {
            new ScanConfiguration(connectionConfig, tagSpecs)
        };

        Console.WriteLine($"Created {scanConfigs.Length} ScanConfiguration(s)");
        Console.WriteLine();

        // Step 4: TagHistoricWAL 생성 (선택사항)
        // MemoryWalBuffer와 FileWalBuffer의 생성자 파라미터는 DLL inspection 필요
        // 여기서는 간단한 예제로 대체
        var memoryBuffer = new MemoryWalBuffer();
        var fileBuffer = new FileWalBuffer(
            Path.Combine(Path.GetTempPath(), "dspilot_wal.db")
        );

        var tagHistoricWAL = new TagHistoricWAL(
            walSize: 10000,
            flushInterval: TimeSpan.FromSeconds(10),
            memoryBuffer: memoryBuffer,
            diskBuffer: fileBuffer
        );

        Console.WriteLine("Created TagHistoricWAL");
        Console.WriteLine($"  WAL file: {Path.Combine(Path.GetTempPath(), "dspilot_wal.db")}");
        Console.WriteLine();

        // Step 5: PLCBackendService 생성
        var plcService = new PLCBackendService(
            scanConfigs: scanConfigs,
            tagHistoricWAL: FSharpOption<TagHistoricWAL>.Some(tagHistoricWAL)
        );

        Console.WriteLine("Created PLCBackendService");
        Console.WriteLine($"  Active connections: {string.Join(", ", plcService.ActiveConnections)}");
        Console.WriteLine($"  All connection names: {string.Join(", ", plcService.AllConnectionNames)}");
        Console.WriteLine();

        // Step 6: 서비스 시작 (실제로는 시작하지 않음 - PLC가 연결되지 않았으므로)
        Console.WriteLine("PLCBackendService.Start() would be called here in real scenario");
        Console.WriteLine("This would return IDisposable for cleanup");
        Console.WriteLine();

        // Step 7: 태그 읽기 예제 (실제로는 실행하지 않음)
        Console.WriteLine("Example usage:");
        Console.WriteLine("  var result = plcService.RTryReadTagValue(\"TestPLC\", \"Flow1_Call1_In\");");
        Console.WriteLine("  if (FSharpResult<PlcValue, string>.IsOk(result)) {");
        Console.WriteLine("    var value = result.ResultValue;");
        Console.WriteLine("    // process value...");
        Console.WriteLine("  }");
        Console.WriteLine();

        Console.WriteLine("=== Example Complete ===");
        Console.WriteLine();
    }

    private static TagSpec[] CreateSampleTagSpecs()
    {
        // AASX나 설정 파일에서 파싱해서 생성해야 하는 부분
        // 여기서는 샘플로 직접 생성

        // TagSpec 생성자:
        // new TagSpec(name, address, dataType, walType, comment, plcValue)

        var tagSpecs = new List<TagSpec>();

        // 예제: Flow1의 Call1 입력 태그
        tagSpecs.Add(new TagSpec(
            name: "Flow1_Call1_In",
            address: "DB1.DBX0.0",  // Siemens S7 주소 예제
            dataType: PlcDataType.Bool,
            walType: FSharpOption<WAL>.None,
            comment: FSharpOption<string>.Some("Flow1 Call1 input signal"),
            plcValue: FSharpOption<PlcValue>.None
        ));

        tagSpecs.Add(new TagSpec(
            name: "Flow1_Call1_Out",
            address: "DB1.DBX0.1",
            dataType: PlcDataType.Bool,
            walType: FSharpOption<WAL>.None,
            comment: FSharpOption<string>.Some("Flow1 Call1 output signal"),
            plcValue: FSharpOption<PlcValue>.None
        ));

        // Allen-Bradley 주소 예제
        tagSpecs.Add(new TagSpec(
            name: "Flow2_Call1_In",
            address: "Program:MainProgram.Flow2_Call1_In",
            dataType: PlcDataType.Bool,
            walType: FSharpOption<WAL>.None,
            comment: FSharpOption<string>.Some("Flow2 Call1 input signal (AB)"),
            plcValue: FSharpOption<PlcValue>.None
        ));

        return tagSpecs.ToArray();
    }
}
