using Ds2.Core;
using Ds2.UI.Core;
using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.MX;
using Microsoft.FSharp.Core;
using static Ev2.PLC.Common.TagSpecModule;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;

namespace DSPilot.TestConsole;

/// <summary>
/// AASX 파일을 로딩하여 모든 Flow의 Call들을 병렬로 무한 시뮬레이션
/// Call 동작: OUT 신호 → 1초 대기 → 센서 ON → 0.5초 대기 → 센서 OFF
/// PLC 통신: Ev2.Backend.PLC 사용하여 실제 PLC에 신호 전송
/// ReplayMode와 동일한 구조로 TagSpec 먼저 등록 후 값 전송
/// </summary>
public static class FlowSimulationTest
{
    private class CallTagInfo
    {
        public required Flow Flow { get; init; }
        public required Call Call { get; init; }
        public required string OutTagName { get; init; }
        public required string OutTagAddress { get; init; }
        public required string SensorTagName { get; init; }
        public required string SensorTagAddress { get; init; }
    }

    public static async Task RunAsync()
    {
        Console.WriteLine("╔════════════════════════════════════════════╗");
        Console.WriteLine("║     AASX Flow Simulation - Infinite Loop   ║");
        Console.WriteLine("╚════════════════════════════════════════════╝");
        Console.WriteLine();

        // 1. AASX 파일 로딩
        var defaultPath = @"C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx";
        Console.Write($"Enter AASX file path (default: {defaultPath}): ");
        var aasxPath = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(aasxPath))
        {
            aasxPath = defaultPath;
        }

        if (!File.Exists(aasxPath))
        {
            Console.WriteLine($"❌ File not found: {aasxPath}");
            return;
        }

        Console.WriteLine($"1️⃣  Loading AASX file: {aasxPath}");

        var store = new DsStore();
        bool loaded = Ds2.Aasx.AasxImporter.importIntoStore(store, aasxPath);

        if (!loaded)
        {
            Console.WriteLine("   ❌ Failed to load AASX file");
            return;
        }

        Console.WriteLine("   ✅ AASX file loaded successfully");
        Console.WriteLine();

        // 2. 모든 Flow와 Call 수집
        Console.WriteLine("2️⃣  Collecting Flows and Calls...");
        var allFlows = new List<Flow>(DsQuery.allFlows(store));

        if (allFlows.Count == 0)
        {
            Console.WriteLine("   ⚠️  No flows found in the project");
            return;
        }

        var callTagInfos = new List<CallTagInfo>();

        foreach (var flow in allFlows)
        {
            var works = new List<Work>(DsQuery.worksOf(flow.Id, store));

            foreach (var work in works)
            {
                var workCalls = new List<Call>(DsQuery.callsOf(work.Id, store));

                foreach (var call in workCalls)
                {
                    // ApiCall에서 OutTag(출력)와 InTag(센서) 가져오기
                    string? outAddress = null;
                    string? inAddress = null;

                    if (call.ApiCalls.Count > 0)
                    {
                        var apiCall = call.ApiCalls[0];

                        // FSharpOption unwrap
                        if (FSharpOption<Ds2.Core.IOTag>.get_IsSome(apiCall.OutTag))
                        {
                            outAddress = apiCall.OutTag.Value.Address;
                        }

                        if (FSharpOption<Ds2.Core.IOTag>.get_IsSome(apiCall.InTag))
                        {
                            inAddress = apiCall.InTag.Value.Address;
                        }
                    }

                    // 주소가 없으면 스킵
                    if (string.IsNullOrEmpty(outAddress) || string.IsNullOrEmpty(inAddress))
                    {
                        Console.WriteLine($"   ⚠️  Skipping {flow.Name}/{call.Name}: Missing IOTag addresses");
                        continue;
                    }

                    callTagInfos.Add(new CallTagInfo
                    {
                        Flow = flow,
                        Call = call,
                        OutTagName = $"{flow.Name}_{call.Name}_OUT",
                        OutTagAddress = outAddress,
                        SensorTagName = $"{flow.Name}_{call.Name}_SENSOR",
                        SensorTagAddress = inAddress
                    });
                }
            }
        }

        Console.WriteLine($"   ✅ Total: {allFlows.Count} flows, {callTagInfos.Count} calls");
        Console.WriteLine();

        // 3. TagSpec 생성 (ApiCall의 IOTag 주소 사용)
        Console.WriteLine("3️⃣  Creating TagSpecs...");
        var tagSpecs = new List<TagSpec>();

        foreach (var info in callTagInfos)
        {
            // OUT 신호 태그 (ApiCall.OutTag 주소 사용)
            tagSpecs.Add(new TagSpec(
                name: info.OutTagName,
                address: info.OutTagAddress,
                dataType: PlcDataType.Bool,
                walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
                comment: FSharpOption<string>.None,
                everyNScan: FSharpOption<int>.None,
                directionHint: FSharpOption<DirectionHint>.None,
                plcValue: FSharpOption<PlcValue>.None
            ));

            // SENSOR 신호 태그 (ApiCall.InTag 주소 사용)
            tagSpecs.Add(new TagSpec(
                name: info.SensorTagName,
                address: info.SensorTagAddress,
                dataType: PlcDataType.Bool,
                walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
                comment: FSharpOption<string>.None,
                everyNScan: FSharpOption<int>.None,
                directionHint: FSharpOption<DirectionHint>.None,
                plcValue: FSharpOption<PlcValue>.None
            ));
        }

        Console.WriteLine($"   ✅ {tagSpecs.Count} tag specs created");
        Console.WriteLine();

        // 4. PLC 연결 설정 (ReplayMode와 동일)
        Console.WriteLine("4️⃣  Configuring PLC connection...");
        var connectionConfig = new MxConnectionConfig
        {
            IpAddress = "192.168.9.120",
            Port = 5555,
            Name = "MitsubishiPLC",
            EnableScan = true,
            Timeout = TimeSpan.FromSeconds(5),
            ScanInterval = TimeSpan.FromMilliseconds(500),
            FrameType = FrameType.QnA_3E_Binary,
            Protocol = TransportProtocol.UDP,
            AccessRoute = new AccessRoute(0, 255, 1023, 0),
            MonitoringTimer = 16
        };

        var scanConfigs = new[] { new ScanConfiguration(connectionConfig, tagSpecs.ToArray()) };
        Console.WriteLine("   ✅ Config ready");
        Console.WriteLine();

        // 5. PLC 서비스 시작 (ReplayMode와 동일)
        Console.WriteLine("5️⃣  Starting PLC service...");
        IDisposable? disposable = null;

        try
        {
            var plcService = new PLCBackendService(
                scanConfigs: scanConfigs,
                tagHistoricWAL: FSharpOption<TagHistoricWAL>.None
            );

            disposable = plcService.Start();
            var connectionName = plcService.AllConnectionNames.FirstOrDefault();
            Console.WriteLine($"   ✅ Connected: {connectionName}");
            Console.WriteLine();

            await Task.Delay(2000); // PLC 스캔 루프 시작 대기

            // 6. 무한 루프 시작
            Console.WriteLine("6️⃣  Starting infinite simulation...");
            Console.WriteLine("   Press Ctrl+C to stop");
            Console.WriteLine();

            // CancellationToken 설정
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine();
                Console.WriteLine("🛑 Stopping simulation...");
            };

            // Flow별로 그룹화
            var flowGroups = callTagInfos.GroupBy(c => c.Flow.Name).ToList();

            int cycle = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                cycle++;
                Console.WriteLine($"━━━ Cycle #{cycle} ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

                // 모든 Flow를 병렬로 실행
                var flowTasks = flowGroups.Select(flowGroup =>
                    SimulateFlowAsync(plcService, connectionName!, flowGroup.ToList(), cts.Token)
                ).ToList();

                await Task.WhenAll(flowTasks);

                Console.WriteLine();
                await Task.Delay(100); // 사이클 간 짧은 대기
            }

            Console.WriteLine("✅ Simulation stopped");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("✅ Simulation stopped");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
        }
        finally
        {
            disposable?.Dispose();
            Console.WriteLine("🛑 PLC service stopped");
        }
    }

    private static async Task SimulateFlowAsync(
        PLCBackendService plcService,
        string connectionName,
        List<CallTagInfo> callInfos,
        CancellationToken cancellationToken)
    {
        if (callInfos.Count == 0) return;

        var flowName = callInfos[0].Flow.Name;
        var flowStartTime = DateTime.Now;

        foreach (var callInfo in callInfos)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await SimulateCallAsync(plcService, connectionName, callInfo);
        }

        var elapsed = (DateTime.Now - flowStartTime).TotalSeconds;
        Console.WriteLine($"  ✓ {flowName} completed in {elapsed:F2}s ({callInfos.Count} calls)");
    }

    private static async Task SimulateCallAsync(
        PLCBackendService plcService,
        string connectionName,
        CallTagInfo callInfo)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

        // 1. OUT 신호 전송
        SendSignalToPlc(plcService, connectionName, callInfo.OutTagName, "1");
        Console.WriteLine($"    [{timestamp}] {callInfo.Flow.Name} → {callInfo.Call.Name}: OUT=1");

        // 2. 1초 대기
        await Task.Delay(1000);

        // 3. 센서 ON 감지 → 즉시 OUT OFF
        SendSignalToPlc(plcService, connectionName, callInfo.SensorTagName, "1");
        timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        Console.WriteLine($"    [{timestamp}] {callInfo.Flow.Name} → {callInfo.Call.Name}: SENSOR=1");

        // 4. 센서 감지 즉시 OUT OFF
        SendSignalToPlc(plcService, connectionName, callInfo.OutTagName, "0");
        timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        Console.WriteLine($"    [{timestamp}] {callInfo.Flow.Name} → {callInfo.Call.Name}: OUT=0 (sensor detected)");

        // 5. 0.5초 후 센서 OFF
        await Task.Delay(500);
        SendSignalToPlc(plcService, connectionName, callInfo.SensorTagName, "0");
        timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        Console.WriteLine($"    [{timestamp}] {callInfo.Flow.Name} → {callInfo.Call.Name}: SENSOR=0");
    }

    private static void SendSignalToPlc(
        PLCBackendService plcService,
        string connectionName,
        string tagName,
        string value)
    {
        try
        {
            var tagSpecOpt = plcService.TryGetTagSpec(connectionName, tagName);

            if (FSharpOption<TagSpec>.get_IsSome(tagSpecOpt))
            {
                var tagSpec = tagSpecOpt.Value;
                var valueOpt = PlcValue.TryParse(value, tagSpec.DataType);

                if (FSharpOption<PlcValue>.get_IsSome(valueOpt))
                {
                    var commInfo = CommunicationInfo.Create(
                        connectorName: connectionName,
                        tagSpec: tagSpec,
                        value: valueOpt.Value,
                        origin: FSharpOption<ValueSource>.Some(ValueSource.FromWebClient)
                    );

                    GlobalCommunication.SubjectC2S.OnNext(commInfo);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠️  PLC write failed for {tagName}: {ex.Message}");
        }
    }
}
