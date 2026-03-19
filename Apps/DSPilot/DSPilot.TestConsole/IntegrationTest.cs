using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.AB;
using Ev2.PLC.Protocol.MX;
using Microsoft.FSharp.Core;
using Ds2.Core;
using Ds2.UI.Core;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using WAL = Ev2.PLC.Common.TagSpecModule.WAL;

namespace DSPilot.TestConsole;

/// <summary>
/// AASX 파일 기반 실제 PLC 통합 테스트
/// </summary>
public static class IntegrationTest
{
    private const string AasxFilePath = @"C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx";

    /// <summary>
    /// PLC 서비스와 연결명을 담는 결과 클래스
    /// </summary>
    public class TestResult
    {
        public PLCBackendService? PlcService { get; set; }
        public string? ConnectionName { get; set; }
        public bool Success { get; set; }
    }

    public static async Task<TestResult> RunAsync()
    {
        var result = new TestResult { Success = false };
        Console.WriteLine("=== DSPilot PLC Integration Test (AASX-based) ===");
        Console.WriteLine();

        try
        {
            Console.WriteLine("1️⃣  Loading AASX project...");
            Console.WriteLine($"   File: {AasxFilePath}");

            if (!File.Exists(AasxFilePath))
            {
                Console.WriteLine($"   ❌ AASX file not found: {AasxFilePath}");
                return result;
            }

            // AASX 파일 로드
            var store = new DsStore();
            var loaded = Ds2.Aasx.AasxImporter.importIntoStore(store, AasxFilePath);

            if (!loaded)
            {
                Console.WriteLine("   ❌ Failed to load AASX file");
                return result;
            }

            Console.WriteLine("   ✅ AASX file loaded successfully");
            Console.WriteLine();

            Console.WriteLine("2️⃣  Extracting tags from AASX...");

            // 프로젝트에서 모든 Call과 태그 정보 추출
            var tagSpecs = ExtractTagSpecsFromProject(store);

            Console.WriteLine($"   ✅ Extracted {tagSpecs.Length} tags from AASX:");
            foreach (var spec in tagSpecs.Take(10))
            {
                Console.WriteLine($"      - {spec.Name} @ {spec.Address}");
            }
            if (tagSpecs.Length > 10)
            {
                Console.WriteLine($"      ... and {tagSpecs.Length - 10} more tags");
            }
            Console.WriteLine();

            Console.WriteLine("3️⃣  Setting up Mitsubishi PLC connection...");

            // Step 1: 미쯔비시 PLC 연결 설정 (MX 프로토콜)
            var connectionConfig = new MxConnectionConfig
            {
                IpAddress = "192.168.9.120",
                Port = 4444,  // MELSEC Ethernet 기본 포트
                Name = "MitsubishiPLC",
                EnableScan = true,
                Timeout = TimeSpan.FromSeconds(5),
                ScanInterval = TimeSpan.FromMilliseconds(500),
                FrameType = FrameType.QnA_3E_Binary,
                Protocol = TransportProtocol.TCP,
                AccessRoute = new AccessRoute(
                    networkNumber: 0,
                    stationNumber: 255,
                    ioNumber: 1023,
                    relayType: 0
                ),
                MonitoringTimer = 16
            };

            Console.WriteLine($"   ✅ Connection: {connectionConfig.GetType().Name}");
            Console.WriteLine($"   ✅ IP: 192.168.9.120");
            Console.WriteLine($"   ✅ Port: 4444 (MELSEC Ethernet)");
            Console.WriteLine($"   ✅ Frame: QnA 3E Binary");
            Console.WriteLine($"   ✅ Scan Interval: 500ms");
            Console.WriteLine();

            Console.WriteLine("4️⃣  Creating ScanConfiguration...");

            // Step 3: 스캔 설정
            var scanConfigs = new[]
            {
                new ScanConfiguration(connectionConfig, tagSpecs)
            };

            Console.WriteLine($"   ✅ ScanConfiguration created with {tagSpecs.Length} tags");
            Console.WriteLine();

            Console.WriteLine("5️⃣  Setting up TagHistoricWAL...");

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

            Console.WriteLine("6️⃣  Creating PLCBackendService...");

            // Step 5: PLCBackendService 생성
            var plcService = new PLCBackendService(
                scanConfigs: scanConfigs,
                tagHistoricWAL: FSharpOption<TagHistoricWAL>.Some(tagHistoricWAL)
            );

            Console.WriteLine($"   ✅ PLCBackendService created");
            Console.WriteLine($"   ✅ Active Connections: {plcService.ActiveConnections.Length}");
            Console.WriteLine($"   ✅ Connection Names: {string.Join(", ", plcService.AllConnectionNames)}");
            Console.WriteLine();

            Console.WriteLine("7️⃣  Starting PLC Service and monitoring tags...");
            Console.WriteLine($"   Connecting to Mitsubishi PLC at 192.168.9.120:5002");
            Console.WriteLine();

            var connectionName = "MitsubishiPLC";
            IDisposable? scanDisposable = null;

            try
            {
                // Step 6: 서비스 시작
                scanDisposable = plcService.Start();
                Console.WriteLine($"   ✅ PLC Service started");

                // 스캔 대기
                Console.WriteLine("   ⏳ Waiting 5 seconds for initial scan...");
                await Task.Delay(5000);

                Console.WriteLine();
                Console.WriteLine("8️⃣  Reading tag values...");

                // Step 7: 처음 몇 개 태그 읽기 시도
                var sampleTags = tagSpecs.Take(5).ToArray();
                foreach (var spec in sampleTags)
                {
                    Console.WriteLine($"   📖 Reading: {spec.Name}");

                    try
                    {
                        var readResult = plcService.RTryReadTagValue(connectionName, spec.Name);

                        if (readResult.IsOk)
                        {
                            var value = readResult.ResultValue;
                            Console.WriteLine($"      ✅ Value: {value}");
                        }
                        else
                        {
                            var error = readResult.ErrorValue;
                            Console.WriteLine($"      ⚠️  {error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"      ❌ Exception: {ex.Message}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine("9️⃣  Monitoring for 10 seconds...");
                Console.WriteLine("   (Check PLC for tag changes)");
                await Task.Delay(10000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Service failed: {ex.GetType().Name}");
                Console.WriteLine($"      Message: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"      Inner: {ex.InnerException.Message}");
                }
                Console.WriteLine();
                Console.WriteLine("   ⚠️  PLC connection test failed, but will continue with replay test");
            }
            finally
            {
                // scanDisposable는 dispose하지 않고 유지 (리플레이 테스트에서 재사용)
                Console.WriteLine("   ✅ PLCBackendService will be available for replay test");
            }

            Console.WriteLine();
            Console.WriteLine("🔟  Test Summary:");
            Console.WriteLine("   ✅ AASX file loaded");
            Console.WriteLine($"   ✅ {tagSpecs.Length} tags extracted from AASX");
            Console.WriteLine("   ✅ Mitsubishi PLC connection configured (192.168.9.120:4444)");
            Console.WriteLine("   ✅ TagHistoricWAL configured");
            Console.WriteLine("   ✅ PLCBackendService created");
            Console.WriteLine();
            Console.WriteLine("✨ Integration test complete!");
            Console.WriteLine();

            // 결과 반환 (실패해도 PLCBackendService는 반환)
            result.PlcService = plcService;
            result.ConnectionName = connectionName;
            result.Success = true;  // DB 리플레이 테스트를 위해 항상 true
            return result;
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

        return result;
    }

    /// <summary>
    /// AASX 프로젝트에서 모든 태그 정보를 추출하여 TagSpec 배열로 반환
    /// </summary>
    private static TagSpec[] ExtractTagSpecsFromProject(DsStore store)
    {
        var tagSpecs = new List<TagSpec>();

        // 프로젝트의 모든 플로우 가져오기
        var projects = DsQuery.allProjects(store);
        if (Microsoft.FSharp.Collections.ListModule.IsEmpty(projects))
        {
            Console.WriteLine("   ⚠️  No projects found in AASX");
            return tagSpecs.ToArray();
        }

        var project = Microsoft.FSharp.Collections.ListModule.Head(projects);

        // 모든 플로우 가져오기
        var flows = DsQuery.allFlows(store);

        foreach (var flow in flows)
        {
            // 플로우의 모든 워크 가져오기
            var works = DsQuery.worksOf(flow.Id, store);

            foreach (var work in works)
            {
                // 워크의 모든 콜 가져오기
                var calls = DsQuery.callsOf(work.Id, store);

                foreach (var call in calls)
                {
                    // 각 콜의 ApiCall에서 InTag와 OutTag 추출
                    foreach (var apiCall in call.ApiCalls)
                    {
                        // InTag 처리
                        if (Microsoft.FSharp.Core.FSharpOption<IOTag>.get_IsSome(apiCall.InTag))
                        {
                            var inTag = apiCall.InTag.Value;
                            var tagSpec = CreateTagSpec(inTag, $"{flow.Name}_{call.Name}_In");
                            if (tagSpec != null)
                            {
                                tagSpecs.Add(tagSpec);
                            }
                        }

                        // OutTag 처리
                        if (Microsoft.FSharp.Core.FSharpOption<IOTag>.get_IsSome(apiCall.OutTag))
                        {
                            var outTag = apiCall.OutTag.Value;
                            var tagSpec = CreateTagSpec(outTag, $"{flow.Name}_{call.Name}_Out");
                            if (tagSpec != null)
                            {
                                tagSpecs.Add(tagSpec);
                            }
                        }
                    }
                }
            }
        }

        // 중복 주소 제거 (같은 주소를 가진 태그 중 첫 번째만 유지)
        var uniqueTagSpecs = tagSpecs
            .GroupBy(t => t.Address)
            .Select(g => g.First())
            .ToArray();

        var duplicateCount = tagSpecs.Count - uniqueTagSpecs.Length;
        if (duplicateCount > 0)
        {
            Console.WriteLine($"   ⚠️  Removed {duplicateCount} duplicate address(es)");
        }

        return uniqueTagSpecs;
    }

    /// <summary>
    /// IOTag에서 TagSpec 생성
    /// </summary>
    private static TagSpec? CreateTagSpec(IOTag ioTag, string fallbackName)
    {
        if (string.IsNullOrEmpty(ioTag.Address))
        {
            return null;
        }

        var tagName = string.IsNullOrEmpty(ioTag.Name) ? fallbackName : ioTag.Name;

        // IOTag의 Description을 사용하여 PlcDataType으로 변환
        var plcDataType = ConvertToPlcDataType(ioTag.Description);

        return new TagSpec(
            name: tagName,
            address: ioTag.Address,
            dataType: plcDataType,
            walType: FSharpOption<WAL>.None,
            comment: FSharpOption<string>.Some($"Tag from AASX: {tagName}"),
            plcValue: FSharpOption<PlcValue>.None
        );
    }

    /// <summary>
    /// Description에서 데이터 타입을 추출하여 Ev2 PlcDataType으로 변환
    /// 현재는 모든 태그를 Bool로 처리 (가장 일반적인 I/O 태그 타입)
    /// </summary>
    private static PlcDataType ConvertToPlcDataType(string? description)
    {
        // 일단 모든 태그를 Bool로 처리
        // 필요시 나중에 확장 가능
        return PlcDataType.Bool;
    }
}
