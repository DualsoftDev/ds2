using System.IO;
using Ds2.Core;
using Ds2.Core.Store;
using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using static Ev2.PLC.Common.TagSpecModule;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;

namespace DSPilot.AasxSimulator;

/// <summary>
/// AASX 로드 → 모든 Flow/Call 수집 → PLC 신호 시뮬레이션.
/// Promaker 와 동일한 v10 경로(importIntoStoreWithError + V10ValidationBatch)를 사용.
/// UI 비종속 — log/status 콜백으로 호스트(WPF/Console)에 출력 전달.
/// </summary>
public sealed class FlowSimulationService
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

    public Action<string>? Log { get; init; }
    public Action<int>? CycleChanged { get; init; }

    /// <summary>시뮬레이션 속도 배수 (1.0 = 기본). 모든 내부 지연을 이 값으로 나눠 적용.</summary>
    public double SpeedMultiplier { get; init; } = 1.0;

    private int Scaled(int ms)
    {
        var mult = SpeedMultiplier <= 0 ? 1.0 : SpeedMultiplier;
        return Math.Max(1, (int)(ms / mult));
    }

    // 여러 Call 이 같은 물리 주소를 공유 (예: C/V 컨베이어의 정회전/역회전/저속/리셋 4 동작이 동일 run sensor X1020 공유).
    // PLCBackendService 의 ValidateTags 가 같은 주소를 가진 TagSpec 중복을 거부하므로
    // BuildTagSpecs 가 주소별 1개 TagSpec 만 등록하고, 같은 주소의 다른 logical 이름은 canonical 이름으로 alias.
    // SendSignalToPlc 가 logical name → canonical name 해석 후 PLC 에 위임.
    private readonly Dictionary<string, string> _tagNameAliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// AASX 를 store 에 import 한 뒤 v10 검증 결과 반환. 호출자가 시뮬레이션 진행 전 확인 가능.
    /// import 실패 시 ErrorMessage 가 set, store 는 partial 상태일 수 있음.
    /// </summary>
    public LoadResult LoadAndValidate(string aasxPath, out DsStore store)
    {
        store = new DsStore();

        if (!File.Exists(aasxPath))
            return new LoadResult { ErrorMessage = $"파일이 없습니다: {aasxPath}" };

        var importResult = Ds2.Aasx.AasxImporter.importIntoStoreWithError(store, aasxPath);
        if (importResult.IsError)
            return new LoadResult { ErrorMessage = $"AASX 임포트 실패: {importResult.ErrorValue}" };

        var issues = V10ValidationBatch.validateStore(store);
        var issueList = ListModule.IsEmpty(issues)
            ? new List<Ds2.Core.V10Validation.ValidationIssue>()
            : issues.ToList();
        return new LoadResult { Issues = issueList };
    }

    public sealed class LoadResult
    {
        public string? ErrorMessage { get; init; }
        public List<Ds2.Core.V10Validation.ValidationIssue> Issues { get; init; } = new();
        public bool Success => ErrorMessage is null;
    }

    /// <summary>
    /// 시뮬레이션 무한 루프. CancellationToken 으로 중단.
    /// store 는 LoadAndValidate 가 성공 반환한 인스턴스를 그대로 전달.
    /// </summary>
    public async Task RunAsync(DsStore store, PlcConnectionSettings plcSettings, CancellationToken cancellationToken)
    {
        Log?.Invoke($"PLC: {plcSettings.DisplayName}");

        var (callTagInfos, arrowsByWork) = CollectCalls(store);
        if (callTagInfos.Count == 0)
        {
            Log?.Invoke("⚠️  실행할 Flow/Call 이 없습니다.");
            return;
        }

        Log?.Invoke($"Flow {callTagInfos.GroupBy(c => c.Flow.Name).Count()}, Call {callTagInfos.Count}");

        var tagSpecs = BuildTagSpecs(callTagInfos);
        var dedupedCount = callTagInfos.Count * 2 - tagSpecs.Count;
        if (dedupedCount > 0)
            Log?.Invoke($"TagSpec {tagSpecs.Count} 개 생성 (주소 공유로 {dedupedCount} 건 dedup)");
        else
            Log?.Invoke($"TagSpec {tagSpecs.Count} 개 생성");

        var scanConfigs = new[] { plcSettings.CreateScanConfig(tagSpecs.ToArray()) };
        // PLCBackendService 는 싱글톤 트래킹 — Start() 가 throw 해서 disposable 이 null 인 채 끝나면
        // 서비스 인스턴스가 슬롯에 남아 다음 실행 시 "Instance already exists" 가 떠 버린다.
        // 따라서 service 도 외부 변수로 잡아 finally 에서 무조건 정리.
        PLCBackendService? plcService = null;
        IDisposable? disposable = null;

        try
        {
            plcService = new PLCBackendService(
                scanConfigs: scanConfigs,
                tagHistoricWAL: FSharpOption<TagHistoricWAL>.None
            );

            disposable = plcService.Start();
            var connectionName = plcService.AllConnectionNames.FirstOrDefault();
            Log?.Invoke($"🔌 연결: {connectionName}");

            await Task.Delay(2000, cancellationToken);

            Log?.Invoke($"▶ 시뮬레이션 시작 (속도 {SpeedMultiplier:0.##}x)");

            var flowGroups = callTagInfos.GroupBy(c => c.Flow.Name).ToList();
            int cycle = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                cycle++;
                CycleChanged?.Invoke(cycle);
                Log?.Invoke($"━━━ Cycle #{cycle} ━━━");

                var flowTasks = flowGroups.Select(flowGroup =>
                    SimulateFlowAsync(plcService, connectionName!, flowGroup.ToList(), arrowsByWork, cancellationToken)
                ).ToList();

                await Task.WhenAll(flowTasks);
                await Task.Delay(Scaled(100), cancellationToken);
            }
        }
        catch (OperationCanceledException) { /* 사용자 중단 */ }
        catch (Exception ex)
        {
            Log?.Invoke($"❌ {ex.Message}");
        }
        finally
        {
            try { disposable?.Dispose(); }
            catch (Exception ex) { Log?.Invoke($"⚠️  disposable dispose: {ex.Message}"); }

            // Start() 가 실패해 disposable 이 null 이면 서비스 자체가 싱글톤 슬롯을 잡고 있으므로
            // 별도로 명시 dispose 해야 다음 실행에서 재생성이 가능해진다.
            if (plcService is IDisposable d)
            {
                try { d.Dispose(); }
                catch (Exception ex) { Log?.Invoke($"⚠️  service dispose: {ex.Message}"); }
            }

            Log?.Invoke("🛑 PLC 서비스 종료");
        }
    }

    private (List<CallTagInfo>, Dictionary<Guid, List<ArrowBetweenCalls>>) CollectCalls(DsStore store)
    {
        var callTagInfos = new List<CallTagInfo>();
        var arrowsByWork = new Dictionary<Guid, List<ArrowBetweenCalls>>();

        foreach (var flow in Queries.allFlows(store))
        {
            foreach (var work in Queries.worksOf(flow.Id, store))
            {
                var arrows = new List<ArrowBetweenCalls>(Queries.arrowCallsOf(work.Id, store));
                arrowsByWork[work.Id] = arrows;

                foreach (var call in Queries.callsOf(work.Id, store))
                {
                    string? outAddress = null;
                    string? inAddress = null;

                    if (call.ApiCalls.Count > 0)
                    {
                        var apiCall = call.ApiCalls[0];
                        if (FSharpOption<IOTag>.get_IsSome(apiCall.OutTag))
                            outAddress = apiCall.OutTag.Value.Address;
                        if (FSharpOption<IOTag>.get_IsSome(apiCall.InTag))
                            inAddress = apiCall.InTag.Value.Address;
                    }

                    if (string.IsNullOrEmpty(outAddress) || string.IsNullOrEmpty(inAddress))
                    {
                        Log?.Invoke($"   ⚠️  Skipping {flow.Name}/{call.Name}: 주소 누락");
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
                        SensorTagAddress = inAddress,
                    });
                }
            }
        }

        return (callTagInfos, arrowsByWork);
    }

    private List<TagSpec> BuildTagSpecs(List<CallTagInfo> callTagInfos)
    {
        _tagNameAliases.Clear();
        // 같은 물리 주소를 가진 첫 logical name 만 TagSpec 으로 등록 — 이후 같은 주소의 다른 name 은 canonical 로 alias.
        // 같은 conveyor 의 정회전/역회전 등 4 동작이 동일 run sensor 를 공유하는 케이스 처리.
        var byAddress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tagSpecs = new List<TagSpec>(callTagInfos.Count * 2);

        void Register(string logicalName, string address)
        {
            if (byAddress.TryGetValue(address, out var canonical))
            {
                if (!string.Equals(logicalName, canonical, StringComparison.OrdinalIgnoreCase))
                    _tagNameAliases[logicalName] = canonical;
            }
            else
            {
                byAddress[address] = logicalName;
                tagSpecs.Add(MakeBoolSpec(logicalName, address));
            }
        }

        foreach (var info in callTagInfos)
        {
            Register(info.OutTagName, info.OutTagAddress);
            Register(info.SensorTagName, info.SensorTagAddress);
        }
        return tagSpecs;
    }

    private static TagSpec MakeBoolSpec(string name, string address) =>
        new(
            name: name,
            address: address,
            dataType: PlcDataType.Bool,
            walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
            comment: FSharpOption<string>.None,
            everyNScan: FSharpOption<int>.None,
            directionHint: FSharpOption<DirectionHint>.None,
            plcValue: FSharpOption<PlcValue>.None
        );

    private async Task SimulateFlowAsync(
        PLCBackendService plcService,
        string connectionName,
        List<CallTagInfo> callInfos,
        Dictionary<Guid, List<ArrowBetweenCalls>> arrowsByWork,
        CancellationToken cancellationToken)
    {
        if (callInfos.Count == 0) return;

        var workGroups = callInfos.GroupBy(c => c.Work.Id).ToList();
        foreach (var workGroup in workGroups)
        {
            if (cancellationToken.IsCancellationRequested) return;
            var arrows = arrowsByWork.GetValueOrDefault(workGroup.Key, new List<ArrowBetweenCalls>());
            await ExecuteWorkGraphAsync(plcService, connectionName, workGroup.ToList(), arrows, cancellationToken);
        }
    }

    private async Task ExecuteWorkGraphAsync(
        PLCBackendService plcService,
        string connectionName,
        List<CallTagInfo> callInfos,
        List<ArrowBetweenCalls> arrows,
        CancellationToken cancellationToken)
    {
        if (arrows.Count == 0)
        {
            foreach (var info in callInfos)
            {
                if (cancellationToken.IsCancellationRequested) return;
                await SimulateCallAsync(plcService, connectionName, info, cancellationToken);
            }
            return;
        }

        var callInfoById = callInfos.ToDictionary(c => c.Call.Id);
        var outgoing = callInfos.ToDictionary(c => c.Call.Id, _ => new List<Guid>());
        var remaining = callInfos.ToDictionary(c => c.Call.Id, _ => 0);

        // arrow source/target 가 둘 다 callInfos 에 있는 경우에만 그래프에 반영.
        // IOTag 누락으로 스킵된 Call 이나 비-Call 노드를 가리키면 다음 wave 에서 KeyNotFound 발생.
        foreach (var arrow in arrows)
        {
            if (!outgoing.ContainsKey(arrow.SourceId)) continue;
            if (!remaining.ContainsKey(arrow.TargetId)) continue;
            outgoing[arrow.SourceId].Add(arrow.TargetId);
            remaining[arrow.TargetId]++;
        }

        var currentWave = callInfos
            .Where(c => remaining[c.Call.Id] == 0)
            .Select(c => c.Call.Id)
            .ToList();

        while (currentWave.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var tasks = currentWave
                .Where(id => callInfoById.ContainsKey(id))
                .Select(id => SimulateCallAsync(plcService, connectionName, callInfoById[id], cancellationToken));
            await Task.WhenAll(tasks);

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

    private async Task SimulateCallAsync(
        PLCBackendService plcService,
        string connectionName,
        CallTagInfo callInfo,
        CancellationToken cancellationToken)
    {
        SendSignalToPlc(plcService, connectionName, callInfo.OutTagName, "1");
        Log?.Invoke($"  {callInfo.Flow.Name} → {callInfo.Call.Name}: OUT=1");

        await Task.Delay(Scaled(1000), cancellationToken);

        SendSignalToPlc(plcService, connectionName, callInfo.SensorTagName, "1");
        Log?.Invoke($"  {callInfo.Flow.Name} → {callInfo.Call.Name}: SENSOR=1");

        SendSignalToPlc(plcService, connectionName, callInfo.OutTagName, "0");
        Log?.Invoke($"  {callInfo.Flow.Name} → {callInfo.Call.Name}: OUT=0 (sensor detected)");

        await Task.Delay(Scaled(500), cancellationToken);
        SendSignalToPlc(plcService, connectionName, callInfo.SensorTagName, "0");
        Log?.Invoke($"  {callInfo.Flow.Name} → {callInfo.Call.Name}: SENSOR=0");
    }

    private void SendSignalToPlc(PLCBackendService plcService, string connectionName, string tagName, string value)
    {
        try
        {
            // 같은 물리 주소를 공유하는 Call 의 logical name 을 canonical 로 해석.
            if (_tagNameAliases.TryGetValue(tagName, out var canonical))
                tagName = canonical;
            var tagSpecOpt = plcService.TryGetTagSpec(connectionName, tagName);
            if (!FSharpOption<TagSpec>.get_IsSome(tagSpecOpt)) return;

            var tagSpec = tagSpecOpt.Value;
            var valueOpt = PlcValue.TryParse(value, tagSpec.DataType);
            if (!FSharpOption<PlcValue>.get_IsSome(valueOpt)) return;

            var commInfo = CommunicationInfo.Create(
                connectorName: connectionName,
                tagSpec: tagSpec,
                value: valueOpt.Value,
                origin: FSharpOption<ValueSource>.Some(ValueSource.FromWebClient)
            );
            GlobalCommunication.SubjectC2S.OnNext(commInfo);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"  ⚠️  PLC write failed for {tagName}: {ex.Message}");
        }
    }
}
