using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ev2.Backend.Common;
using Ev2.Backend.PLC;
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
        public required Work Work { get; init; }
        public required Call Call { get; init; }
        public required string OutTagName { get; init; }
        public required string OutTagAddress { get; init; }
        public required string SensorTagName { get; init; }
        public required string SensorTagAddress { get; init; }
    }

    internal static async Task RunAsync(PlcConnectionSettings plcSettings, string defaultAasxPath = @"C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx")
    {
        Console.WriteLine("╔════════════════════════════════════════════╗");
        Console.WriteLine("║     AASX Flow Simulation - Infinite Loop   ║");
        Console.WriteLine("╚════════════════════════════════════════════╝");
        Console.WriteLine();

        // 0. PLC 연결 설정 (콘솔에서 오버라이드 가능)
        Console.WriteLine($"PLC 설정 (현재: {plcSettings.DisplayName})");
        Console.WriteLine("  PLC 타입: 1=LS  2=Mitsubishi");
        Console.Write($"  선택 (기본: {plcSettings.PlcType}, Enter=유지): ");
        var plcTypeInput = Console.ReadLine()?.Trim();
        if (plcTypeInput == "1") plcSettings.PlcType = "LS";
        else if (plcTypeInput == "2") plcSettings.PlcType = "Mitsubishi";

        bool isLsConn = plcSettings.PlcType.Equals("LS", StringComparison.OrdinalIgnoreCase);
        var currentIp = isLsConn ? plcSettings.LS.IpAddress : plcSettings.Mitsubishi.IpAddress;
        var currentPort = isLsConn ? plcSettings.LS.Port : plcSettings.Mitsubishi.Port;

        Console.Write($"  IP 주소 (기본: {currentIp}, Enter=유지): ");
        var ipInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(ipInput))
        {
            if (isLsConn) plcSettings.LS.IpAddress = ipInput;
            else plcSettings.Mitsubishi.IpAddress = ipInput;
        }

        Console.Write($"  Port (기본: {currentPort}, Enter=유지): ");
        var portInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(portInput) && int.TryParse(portInput, out int port))
        {
            if (isLsConn) plcSettings.LS.Port = port;
            else plcSettings.Mitsubishi.Port = port;
        }

        Console.WriteLine($"  → 연결: {plcSettings.DisplayName}");
        Console.WriteLine();

        // 1. AASX 파일 로딩
        Console.Write($"Enter AASX file path (default: {defaultAasxPath}): ");
        var aasxPath = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(aasxPath))
        {
            aasxPath = defaultAasxPath;
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
        var allFlows = new List<Flow>(Queries.allFlows(store));

        if (allFlows.Count == 0)
        {
            Console.WriteLine("   ⚠️  No flows found in the project");
            return;
        }

        var callTagInfos = new List<CallTagInfo>();
        var arrowsByWork = new Dictionary<Guid, List<ArrowBetweenCalls>>();

        foreach (var flow in allFlows)
        {
            var works = new List<Work>(Queries.worksOf(flow.Id, store));

            foreach (var work in works)
            {
                var workCalls = new List<Call>(Queries.callsOf(work.Id, store));
                var arrows = new List<ArrowBetweenCalls>(Queries.arrowCallsOf(work.Id, store));
                arrowsByWork[work.Id] = arrows;

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
                        Work = work,
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
        Console.WriteLine($"   🔌 {plcSettings.DisplayName}");
        var scanConfigs = new[] { plcSettings.CreateScanConfig(tagSpecs.ToArray()) };
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
                    SimulateFlowAsync(plcService, connectionName!, flowGroup.ToList(), arrowsByWork, cts.Token)
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
        Dictionary<Guid, List<ArrowBetweenCalls>> arrowsByWork,
        CancellationToken cancellationToken)
    {
        if (callInfos.Count == 0) return;

        var flowName = callInfos[0].Flow.Name;
        var flowStartTime = DateTime.Now;

        // Work 순서대로 그룹화하여 순차 실행
        // (Work 내부에서는 Arrow 그래프로 병렬 분기 처리)
        var workGroups = callInfos.GroupBy(c => c.Work.Id).ToList();
        foreach (var workGroup in workGroups)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var arrows = arrowsByWork.GetValueOrDefault(workGroup.Key, new List<ArrowBetweenCalls>());
            await ExecuteWorkGraphAsync(plcService, connectionName, workGroup.ToList(), arrows, cancellationToken);
        }

        var elapsed = (DateTime.Now - flowStartTime).TotalSeconds;
        Console.WriteLine($"  ✓ {flowName} completed in {elapsed:F2}s ({callInfos.Count} calls)");
    }

    /// <summary>
    /// Work 내 Call을 Arrow 그래프 기반으로 실행합니다.
    /// - 단일 Target: 순차 실행
    /// - 복수 Target: Task.WhenAll로 동시 실행
    /// - 수렴 노드: 모든 선행 노드 완료 후 실행
    /// </summary>
    private static async Task ExecuteWorkGraphAsync(
        PLCBackendService plcService,
        string connectionName,
        List<CallTagInfo> callInfos,
        List<ArrowBetweenCalls> arrows,
        CancellationToken cancellationToken)
    {
        if (arrows.Count == 0)
        {
            // Arrow 없으면 순차 실행
            foreach (var info in callInfos)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await SimulateCallAsync(plcService, connectionName, info);
            }
            return;
        }

        var callInfoById = callInfos.ToDictionary(c => c.Call.Id);

        // 그래프 구성: outgoing 엣지 목록, incoming 카운트
        var outgoing = callInfos.ToDictionary(c => c.Call.Id, _ => new List<Guid>());
        var remaining = callInfos.ToDictionary(c => c.Call.Id, _ => 0);

        foreach (var arrow in arrows)
        {
            if (outgoing.ContainsKey(arrow.SourceId))
                outgoing[arrow.SourceId].Add(arrow.TargetId);
            if (remaining.ContainsKey(arrow.TargetId))
                remaining[arrow.TargetId]++;
        }

        // Wave 기반 실행: 현재 Wave의 모든 Call을 병렬 실행 후 다음 Wave 계산
        var currentWave = callInfos
            .Where(c => remaining[c.Call.Id] == 0)
            .Select(c => c.Call.Id)
            .ToList();

        while (currentWave.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            // 이번 Wave의 Call을 동시 실행
            var tasks = currentWave
                .Where(id => callInfoById.ContainsKey(id))
                .Select(id => SimulateCallAsync(plcService, connectionName, callInfoById[id]));
            await Task.WhenAll(tasks);

            // 다음 Wave 계산: 완료된 Call의 Target 중 incoming이 0이 된 것
            var nextWave = new List<Guid>();
            foreach (var completedId in currentWave)
            {
                foreach (var targetId in outgoing[completedId])
                {
                    remaining[targetId]--;
                    if (remaining[targetId] == 0)
                        nextWave.Add(targetId);
                }
            }
            currentWave = nextWave;
        }
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
