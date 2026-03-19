using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.AB;
using Microsoft.FSharp.Core;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using WAL = Ev2.PLC.Common.TagSpecModule.WAL;

namespace DSPilot.TestConsole;

/// <summary>
/// 실제 PLC 통합 테스트 (시뮬레이션)
/// </summary>
public static class IntegrationTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== DSPilot PLC Integration Test ===");
        Console.WriteLine();

        try
        {
            Console.WriteLine("1️⃣  Setting up PLC connection configuration...");

            // Step 1: 연결 설정 (Mock PLC - 실제로는 연결 안됨)
            var connectionConfig = AbConnectionConfig.Create(
                ipAddress: "127.0.0.1",  // Localhost - 실제 PLC 없어도 테스트 가능
                port: FSharpOption<int>.Some(44818),
                name: FSharpOption<string>.Some("TestPLC"),
                plcType: FSharpOption<Ev2.PLC.Protocol.AB.PlcType>.None,
                slot: FSharpOption<byte>.Some((byte)0),
                scanInterval: FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(1000)),
                timeout: FSharpOption<TimeSpan>.Some(TimeSpan.FromSeconds(2)),
                maxRetries: FSharpOption<int>.Some(1),
                retryDelay: FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(100))
            );

            Console.WriteLine($"   ✅ Connection: {connectionConfig.GetType().Name}");
            Console.WriteLine($"   ✅ IP: 127.0.0.1");
            Console.WriteLine($"   ✅ Scan Interval: 1000ms");
            Console.WriteLine();

            Console.WriteLine("2️⃣  Creating TagSpecs...");

            // Step 2: 태그 스펙 생성 (샘플 데이터)
            var tagSpecs = new[]
            {
                new TagSpec(
                    name: "Flow1_Call1_In",
                    address: "Program:MainProgram.Flow1_Call1_In",
                    dataType: PlcDataType.Bool,
                    walType: FSharpOption<WAL>.None,
                    comment: FSharpOption<string>.Some("Flow1 Call1 Input Signal"),
                    plcValue: FSharpOption<PlcValue>.None
                ),
                new TagSpec(
                    name: "Flow1_Call1_Out",
                    address: "Program:MainProgram.Flow1_Call1_Out",
                    dataType: PlcDataType.Bool,
                    walType: FSharpOption<WAL>.None,
                    comment: FSharpOption<string>.Some("Flow1 Call1 Output Signal"),
                    plcValue: FSharpOption<PlcValue>.None
                ),
                new TagSpec(
                    name: "Flow1_Call2_In",
                    address: "Program:MainProgram.Flow1_Call2_In",
                    dataType: PlcDataType.Bool,
                    walType: FSharpOption<WAL>.None,
                    comment: FSharpOption<string>.Some("Flow1 Call2 Input Signal"),
                    plcValue: FSharpOption<PlcValue>.None
                ),
                new TagSpec(
                    name: "Flow2_Call1_In",
                    address: "Program:MainProgram.Flow2_Call1_In",
                    dataType: PlcDataType.Bool,
                    walType: FSharpOption<WAL>.None,
                    comment: FSharpOption<string>.Some("Flow2 Call1 Input Signal"),
                    plcValue: FSharpOption<PlcValue>.None
                )
            };

            Console.WriteLine($"   ✅ Created {tagSpecs.Length} TagSpecs:");
            foreach (var spec in tagSpecs)
            {
                Console.WriteLine($"      - {spec.Name} @ {spec.Address}");
            }
            Console.WriteLine();

            Console.WriteLine("3️⃣  Creating ScanConfiguration...");

            // Step 3: 스캔 설정
            var scanConfigs = new[]
            {
                new ScanConfiguration(connectionConfig, tagSpecs)
            };

            Console.WriteLine($"   ✅ ScanConfiguration created");
            Console.WriteLine();

            Console.WriteLine("4️⃣  Setting up TagHistoricWAL...");

            // Step 4: WAL 설정
            var memoryBuffer = new MemoryWalBuffer();
            var walFilePath = Path.Combine(Path.GetTempPath(), "dspilot_integration_test.db");
            var fileBuffer = new FileWalBuffer(walFilePath);

            var tagHistoricWAL = new TagHistoricWAL(
                walSize: 10000,
                flushInterval: TimeSpan.FromSeconds(5),
                memoryBuffer: memoryBuffer,
                diskBuffer: fileBuffer
            );

            Console.WriteLine($"   ✅ WAL Database: {walFilePath}");
            Console.WriteLine($"   ✅ WAL Size: 10,000 entries");
            Console.WriteLine($"   ✅ Flush Interval: 5 seconds");
            Console.WriteLine();

            Console.WriteLine("5️⃣  Creating PLCBackendService...");

            // Step 5: PLCBackendService 생성
            var plcService = new PLCBackendService(
                scanConfigs: scanConfigs,
                tagHistoricWAL: FSharpOption<TagHistoricWAL>.Some(tagHistoricWAL)
            );

            Console.WriteLine($"   ✅ PLCBackendService created");
            Console.WriteLine($"   ✅ Active Connections: {plcService.ActiveConnections.Length}");
            Console.WriteLine($"   ✅ Connection Names: {string.Join(", ", plcService.AllConnectionNames)}");
            Console.WriteLine();

            Console.WriteLine("6️⃣  Starting PLC Service (will fail - no real PLC)...");
            Console.WriteLine("   ⚠️  Expected: Connection timeout (no real PLC available)");
            Console.WriteLine();

            IDisposable? scanDisposable = null;

            try
            {
                // Step 6: 서비스 시작 시도 (실제 PLC가 없으므로 실패할 것)
                scanDisposable = plcService.Start();
                Console.WriteLine($"   ✅ Service started (surprising - maybe simulator?)");

                // 잠시 대기하여 스캔 시도
                await Task.Delay(3000);

                Console.WriteLine();
                Console.WriteLine("7️⃣  Attempting to read tags...");

                // Step 7: 태그 읽기 시도
                foreach (var spec in tagSpecs.Take(2))
                {
                    Console.WriteLine($"   📖 Reading: {spec.Name}...");

                    try
                    {
                        var result = plcService.RTryReadTagValue("TestPLC", spec.Name);

                        if (result.IsOk)
                        {
                            var value = result.ResultValue;
                            Console.WriteLine($"      ✅ Success: {value}");
                        }
                        else
                        {
                            var error = result.ErrorValue;
                            Console.WriteLine($"      ❌ Failed: {error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"      ❌ Exception: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Service start failed (expected): {ex.GetType().Name}");
                Console.WriteLine($"      Message: {ex.Message}");
            }
            finally
            {
                scanDisposable?.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine("8️⃣  Test Summary:");
            Console.WriteLine("   ✅ Connection Configuration: Success");
            Console.WriteLine("   ✅ TagSpec Creation: Success");
            Console.WriteLine("   ✅ ScanConfiguration: Success");
            Console.WriteLine("   ✅ TagHistoricWAL Setup: Success");
            Console.WriteLine("   ✅ PLCBackendService Creation: Success");
            Console.WriteLine("   ⚠️  PLC Connection: Expected Failure (no physical PLC)");
            Console.WriteLine();
            Console.WriteLine("✨ Integration test structure validated successfully!");
            Console.WriteLine();
            Console.WriteLine("📋 Next Steps:");
            Console.WriteLine("   1. Connect to real PLC or simulator");
            Console.WriteLine("   2. Update connection config with real PLC IP");
            Console.WriteLine("   3. Map actual tag addresses from AASX");
            Console.WriteLine("   4. Test rising edge detection");
            Console.WriteLine("   5. Integrate with DSPilot event processing");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"❌ Test failed with exception:");
            Console.WriteLine($"   Type: {ex.GetType().Name}");
            Console.WriteLine($"   Message: {ex.Message}");
            Console.WriteLine($"   Stack: {ex.StackTrace}");
            Console.WriteLine();
        }
    }
}
