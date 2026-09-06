// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Security.Cryptography;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using DSPilot.Infrastructure;
using Microsoft.FSharp.Collections;
using AidXgtEndpointSettings = Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidXgtEndpointSettings;
using AssetInterfacesDescription = Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AssetInterfacesDescription;

namespace DSPilot.Services;

public class DsProjectService
{
    private readonly DsStore _store;
    private readonly ILogger<DsProjectService> _logger;

    // AASX 재export 파라미터 — Promaker 기본값 미러(MainAppSettings.cs DefaultIriPrefix / SplitDeviceAasx=false).
    // 공유 project.aasx 는 단일 파일이므로 split=false 고정. DSPilot 은 사용자 템플릿 폴더를 쓰지 않아 autoCreate=false.
    private const string AasxIriPrefix = "https://dualsoft.com/";
    private const bool AasxSplitDevice = false;
    private const bool AasxAutoCreateEmptySubmodels = false;

    public string AasxFilePath { get; } = SharedPaths.AasxFilePath;
    public bool IsLoaded { get; private set; }
    public DateTime? LastLoadedUtc { get; private set; }

    /// <summary>
    /// 마지막으로 LoadProject 호출 시점에 계산된 AASX 파일 SHA256 (대문자 hex).
    /// 디스크 파일 변경 감지를 mtime 대신 콘텐츠 해시로 판정하기 위해 사용.
    /// 동일 모델을 재 export 해도 (zip 타임스탬프 차이로 mtime 은 갱신되지만) hash 는 동일하므로 오탐 방지.
    /// </summary>
    public string? LastLoadedSha256 { get; private set; }

    // ── 모델 flow 집합(SSOT) ────────────────────────────────────────────────
    // GetAllFlows() 는 F# store 를 매번 훑으므로, 읽기 경로(대시보드 폴링·OEE 임계 산출)가
    // 호출당 재계산하지 않도록 LoadProject 시점에 한 번 만들어 캐시한다. store 는
    // importIntoStore 의 ReplaceStore 로 통째 교체되므로 LoadProject 외에 무효화 지점이 없다.
    private volatile HashSet<string>? _modelFlowNames;

    // 비활성(IsDisabled) 포함 모델 flow 이름 캐시 — 삭제/prune 보존 기준 전용(GetModelFlowNamesIncludingDisabled).
    private volatile HashSet<string>? _modelFlowNamesAll;

    // AID 원천의 주소→시스템 이름 매핑 캐시 — LoadProject 마다 재구축(멀티 PLC 커버리지 그룹핑용).
    private volatile Dictionary<string, string>? _addressSystemMap;

    // Flow 이름 → 소유 System Guid 캐시 — 멀티 PLC 이력 조회를 한 PLC 로 한정할 때 쓴다.
    private volatile Dictionary<string, Guid>? _systemIdByFlowName;

    /// <summary>
    /// 외부에서 AASX 파일 콘텐츠가 변경됐을 때 발생. AasxFileWatcherService 가 발행.
    /// Settings 페이지가 구독해서 동기화 배지/토스트를 갱신.
    /// </summary>
    public event Action? AasxExternallyChanged;

    public void RaiseAasxExternallyChanged() => AasxExternallyChanged?.Invoke();

    public DsStore GetStore() => _store;

    public DsProjectService(ILogger<DsProjectService> logger)
    {
        _logger = logger;
        _store = new DsStore();

        _logger.LogInformation("[DsProject] AASX path = '{Path}', exists = {Exists}",
            AasxFilePath, File.Exists(AasxFilePath));

        if (File.Exists(AasxFilePath))
        {
            LoadProject(AasxFilePath);
        }
        else
        {
            _logger.LogWarning("AASX file not found: {Path}. Promaker 에서 같은 경로에 저장하면 자동 인식됩니다.", AasxFilePath);
        }
    }

    public void LoadProject(string path)
    {
        try
        {
            var result = Ds2.Aasx.AasxImporter.importIntoStore(_store, path);
            IsLoaded = result;
            _modelFlowNames = null;     // store 교체 — 다음 조회에서 재구축
            _modelFlowNamesAll = null;
            _addressSystemMap = null;
            _systemIdByFlowName = null;
            LastLoadedUtc = DateTime.UtcNow;
            LastLoadedSha256 = ComputeFileSha256(path);
            if (result)
                _logger.LogInformation("Project loaded from: {Path} (sha256={Sha})", path, LastLoadedSha256 ?? "<n/a>");
            else
                _logger.LogWarning("Failed to import AASX (구 포맷일 수 있음 — ds2 에디터에서 다시 Export 필요): {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading AASX file: {Path}", path);
            IsLoaded = false;
            _modelFlowNames = null;     // 실패한 import 가 store 를 절반만 바꿨을 수 있다 — 캐시 폐기
        }
    }

    /// <summary>
    /// 디스크의 AASX 파일이 마지막으로 수정된 시각(UTC). 존재하지 않으면 null.
    /// </summary>
    public DateTime? GetAasxFileWriteTimeUtc()
    {
        try
        {
            return File.Exists(AasxFilePath)
                ? File.GetLastWriteTimeUtc(AasxFilePath)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 현재 디스크의 AASX 파일 SHA256 (대문자 hex). 파일 없거나 읽기 실패 시 null.
    /// 호출 비용은 파일 크기에 비례 — UI 가 자주 호출하지 않도록 캐시 권장.
    /// </summary>
    public string? GetAasxFileSha256()
    {
        return File.Exists(AasxFilePath) ? ComputeFileSha256(AasxFilePath) : null;
    }

    private static string? ComputeFileSha256(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return null;
        }
    }

    public Project? GetProject()
    {
        var projects = Queries.allProjects(_store);
        return ListModule.IsEmpty(projects) ? null : ListModule.Head(projects);
    }

    public List<DsSystem> GetActiveSystems()
    {
        var project = GetProject();
        if (project == null) return [];
        return [.. Queries.activeSystemsOf(project.Id, _store)];
    }

    public List<DsSystem> GetPassiveSystems()
    {
        var project = GetProject();
        if (project == null) return [];
        return [.. Queries.passiveSystemsOf(project.Id, _store)];
    }

    /// <summary>
    /// 시스템의 <b>활성</b> flow 목록 — Promaker 에서 비활성화(<c>Flow.IsDisabled</c>)한 flow 는 제외.
    /// 엔진(SimIndex Build)도 같은 기준으로 런타임에서 제외하므로, 여기서 걸러야 화면(라인 수·OEE
    /// 분모·nav)과 런타임이 일치한다 — 통신/보고성 flow(단일 call)를 모델에서 끄는 공식 경로.
    /// 삭제/prune 의 보존 기준에는 쓰지 말 것 — 그쪽은 <see cref="GetAllFlowsIncludingDisabled"/>
    /// (비활성은 숨김일 뿐, 복원하면 이력이 다시 보여야 하므로 데이터는 보존).
    /// </summary>
    public List<Flow> GetFlows(Guid systemId)
    {
        return [.. Queries.flowsOf(systemId, _store).Where(f => !f.IsDisabled)];
    }

    /// <summary>전체 <b>활성</b> flow — <see cref="GetFlows"/> 와 동일하게 IsDisabled 제외.</summary>
    public List<Flow> GetAllFlows()
    {
        return [.. Queries.allFlows(_store).Where(f => !f.IsDisabled)];
    }

    /// <summary>비활성 포함 전체 flow — 삭제/prune 보존 기준 전용(표시·집계에는 쓰지 않는다).</summary>
    public List<Flow> GetAllFlowsIncludingDisabled()
    {
        return [.. Queries.allFlows(_store)];
    }

    /// <summary>
    /// Flow 이름 → 소유 System Guid. 멀티 PLC 에서 이력 조회를 한 PLC 로 한정할 때 쓰는 표준 핸들이다
    /// (<c>IPlcRepository</c> 의 <c>systemId</c> 인자). Flow.ParentId 가 곧 System Guid 라 유도는 1단계.
    /// 이름이 중복되는 Flow 가 있으면 첫 번째만 잡히므로 null 대신 그 값을 쓰되, 그런 모델은
    /// 애초에 Flow 이름으로 조회하는 모든 화면이 모호하다(이 메서드가 만든 문제가 아니다).
    /// 미로드/미발견은 null → 호출부는 종전대로 전체 PLC 조회로 폴백한다.
    /// </summary>
    public Guid? TryGetSystemIdByFlowName(string? flowName)
    {
        if (string.IsNullOrWhiteSpace(flowName) || !IsLoaded) return null;
        var map = _systemIdByFlowName;
        if (map is null)
        {
            map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var flow in Queries.allFlows(_store))
                map.TryAdd(flow.Name, flow.ParentId);
            _systemIdByFlowName = map;
        }
        return map.TryGetValue(flowName.Trim(), out var systemId) ? systemId : null;
    }

    /// <summary>
    /// Call Guid → 그 Call 을 담은 <b>능동</b> System Guid (Call→Work→Flow→System).
    /// ※ <c>Queries.tryResolveCallTargetSystem</c> 은 호출 <i>대상</i> 디바이스(Passive) 시스템이라 다르다 —
    ///   PLC 이력 귀속은 Call 을 소유한 쪽이므로 이 메서드를 쓸 것.
    /// </summary>
    public Guid? TryGetSystemIdByCallId(Guid callId)
    {
        if (!IsLoaded) return null;
        var call = Queries.getCall(callId, _store);
        if (!Microsoft.FSharp.Core.FSharpOption<Call>.get_IsSome(call)) return null;
        var systemId = Queries.trySystemIdOfWork(call.Value.ParentId, _store);
        return Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(systemId) ? systemId.Value : null;
    }

    /// <summary>
    /// 모델 AID(AssetInterfacesDescription) 기준 시스템별 PLC 엔드포인트 — 멀티 PLC 접속정보의 정본.
    /// systemRef 로 활성 시스템에 배정된 XGT 엔드포인트를 모두 나열하고(같은 ip:port 를 여러 시스템이
    /// 공유하면 1건으로 합쳐 이름을 병기), 배정된 것이 하나도 없으면 legacy 단일(systemRef 없는
    /// 첫 엔드포인트)로 폴백한다 — 단일 PLC 시절 모델 호환.
    /// </summary>
    public List<PlcEndpointInfo> GetPlcEndpoints()
    {
        var endpoints = new List<PlcEndpointInfo>();
        if (!IsLoaded) return endpoints;

        var aidOption = GetProject()?.AssetInterfaces;
        if (aidOption is null
            || !Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption))
            return endpoints;
        var aid = aidOption.Value;

        foreach (var system in GetActiveSystems())
        {
            var info = AidXgtEndpointSettings.TryReadForSystem(aid, system.Id);
            if (info is null || string.IsNullOrWhiteSpace(info.IpAddress) || info.Port <= 0) continue;

            var dup = endpoints.FindIndex(e =>
                string.Equals(e.Ip, info.IpAddress, StringComparison.OrdinalIgnoreCase) && e.Port == info.Port);
            if (dup >= 0)
                endpoints[dup] = endpoints[dup] with { SystemName = $"{endpoints[dup].SystemName}·{system.Name}" };
            else
                endpoints.Add(new PlcEndpointInfo(system.Name, info.Vendor, info.IpAddress, info.Port, info.TimeoutMs));
        }

        if (endpoints.Count == 0)
        {
            var first = AidXgtEndpointSettings.TryReadFirst(aid);
            if (first is not null && !string.IsNullOrWhiteSpace(first.IpAddress) && first.Port > 0)
                endpoints.Add(new PlcEndpointInfo("PLC", first.Vendor, first.IpAddress, first.Port, first.TimeoutMs));
        }
        return endpoints;
    }

    /// <summary>
    /// AID 원천의 PLC 주소→시스템 이름 매핑(대소문자 무시) — 멀티 PLC 에서 "이 주소는 어느 PLC 것인가"의
    /// 정본. 모든 바인딩(Xgt·OpcUa·Modbus·Mqtt·Http)의 interaction Href/SignalId 를 키로,
    /// endpoint.SystemId → 활성 시스템 이름을 값으로 만든다.
    /// 모델 미로드/AID 없음/systemRef 없는 구 모델이면 null(호출측은 flow 기반 추정으로 폴백).
    /// LoadProject 마다 캐시 재구축.
    /// </summary>
    public Dictionary<string, string>? GetAddressSystemMap()
    {
        var cached = _addressSystemMap;
        if (cached is not null) return cached;
        if (!IsLoaded) return null;

        var aidOption = GetProject()?.AssetInterfaces;
        if (aidOption is null
            || !Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption))
            return null;

        var nameBySystemId = new Dictionary<Guid, string>();
        foreach (var system in GetActiveSystems())
            nameBySystemId.TryAdd(system.Id, system.Name);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? key, string systemName)
        {
            if (!string.IsNullOrWhiteSpace(key)) map.TryAdd(key, systemName);
        }

        string? ResolveSystem(Microsoft.FSharp.Core.FSharpOption<Guid> sysIdOpt)
        {
            if (!Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(sysIdOpt)) return null;
            return nameBySystemId.TryGetValue(sysIdOpt.Value, out var name) ? name : null;
        }

        foreach (var binding in aidOption.Value.Interfaces)
        {
            switch (binding)
            {
                case Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidBinding.Xgt xgt:
                    if (ResolveSystem(xgt.endpoint.SystemId) is { } xgtSystem)
                        foreach (var i in xgt.interactions) { Add(i.Href, xgtSystem); Add(i.SignalId.Value, xgtSystem); }
                    break;
                case Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidBinding.OpcUa ua:
                    if (ResolveSystem(ua.endpoint.SystemId) is { } uaSystem)
                        foreach (var i in ua.interactions) { Add(i.Href, uaSystem); Add(i.SignalId.Value, uaSystem); }
                    break;
                case Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidBinding.Modbus mb:
                    if (ResolveSystem(mb.endpoint.SystemId) is { } mbSystem)
                        foreach (var i in mb.interactions) { Add(i.Href, mbSystem); Add(i.SignalId.Value, mbSystem); }
                    break;
                case Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidBinding.Mqtt mq:
                    if (ResolveSystem(mq.endpoint.SystemId) is { } mqSystem)
                        foreach (var i in mq.interactions) { Add(i.Href, mqSystem); Add(i.SignalId.Value, mqSystem); }
                    break;
                case Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidBinding.Http ht:
                    if (ResolveSystem(ht.endpoint.SystemId) is { } htSystem)
                        foreach (var i in ht.interactions) { Add(i.Href, htSystem); Add(i.SignalId.Value, htSystem); }
                    break;
            }
        }

        // UserTag(센서/에러로그/워드 주소)는 AID 바인딩 밖이지만 모델에서 소속 시스템이 명시된다 —
        // 시스템별 커버리지의 '기타' 뭉치를 줄이는 두 번째 소스. AID 매핑이 이미 있으면 그쪽 우선(TryAdd).
        try
        {
            foreach (var row in _store.GetAllUserTagsForProject())
                if (!string.IsNullOrWhiteSpace(row.TagAddress) && !string.IsNullOrWhiteSpace(row.SystemName))
                    Add(row.TagAddress.Trim(), row.SystemName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DsProject] UserTag 주소→시스템 매핑 수집 실패 — AID 매핑만 사용");
        }

        if (map.Count == 0) return null;

        _addressSystemMap = map;
        return map;
    }

    /// <summary>
    /// 내부용 flow 이름 판정 SSOT — Promaker 가 만드는 '*_Flow' 접미사 flow 는 생산 설비가 아니라
    /// 모델 내부 배선이라 DSPilot 의 어떤 목록에도 노출하지 않는다(기존 부트스트랩/resync 필터와 동일 규칙).
    /// </summary>
    public static bool IsInternalFlowName(string? flowName)
        => flowName is not null && flowName.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 현재 로드된 AASX 의 flow 이름 집합('*_Flow' 제외, 대소문자 무시) — "모델에 등록된 설비" 판정 SSOT.
    /// <para>
    /// DB(dspFlow / dspFlowHistory / oeeDowntimeEvent)는 UPSERT 누적이라 예전 모델의 flow 가 남는다
    /// (부팅 경로에 prune 이 없고, prune 은 실행 중 AASX 교체 또는 수동 동기화에서만 돈다). 화면은
    /// 항상 <b>현재 AASX 기준</b>이어야 하므로 읽기 시점에 이 집합으로 걸러낸다 — 삭제는 사용자가
    /// 설정의 '오래된 데이터 삭제'를 실행할 때만 한다(가역/비가역 분리).
    /// </para>
    /// <para>
    /// ★ 반환 null = "필터 비활성". AASX 미로드·파싱 실패·빈 모델일 때 빈 집합으로 거르면 정상 데이터가
    /// 전부 사라져 화면이 백지가 된다(모델이 늦게 도착하는 배포 시나리오가 실제로 있다). 그 경우는
    /// 필터를 끄는 쪽이 안전하므로 호출측은 null 을 반드시 "걸르지 않음"으로 처리해야 한다.
    /// </para>
    /// </summary>
    public HashSet<string>? GetModelFlowNames()
    {
        var cached = _modelFlowNames;
        if (cached is not null) return cached;
        if (!IsLoaded) return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in GetAllFlows())
            if (!IsInternalFlowName(f.Name)) names.Add(f.Name);

        if (names.Count == 0) return null;   // 빈 모델 → 필터 비활성(전량 숨김 방지)
        _modelFlowNames = names;
        return names;
    }

    /// <summary>
    /// <b>비활성(IsDisabled) 포함</b> 모델 flow 이름 집합 — 삭제/prune 의 보존 기준 전용.
    /// 비활성 flow 는 "숨김"이지 "모델에서 제거"가 아니다 — 보존 기준에서 빠지면 유령 정리·AASX 교체
    /// prune 이 비활성 설비의 이력을 삭제해 버려, Promaker 에서 복원해도 이력이 돌아오지 않는다(비가역).
    /// 표시·집계의 읽기 필터는 <see cref="GetModelFlowNames"/>(활성만)를 쓸 것.
    /// null 규약은 동일 — 미로드/빈 모델이면 null(보존 기준 없음 = 정리 금지).
    /// </summary>
    public HashSet<string>? GetModelFlowNamesIncludingDisabled()
    {
        var cached = _modelFlowNamesAll;
        if (cached is not null) return cached;
        if (!IsLoaded) return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in GetAllFlowsIncludingDisabled())
            if (!IsInternalFlowName(f.Name)) names.Add(f.Name);

        if (names.Count == 0) return null;
        _modelFlowNamesAll = names;
        return names;
    }

    /// <summary>
    /// <paramref name="flowName"/> 이 현재 AASX 에 있는 설비인가. 필터 비활성(모델 미로드) 상태에서는
    /// 판정 근거가 없으므로 true — 즉 "확실히 유령일 때만 숨긴다".
    /// </summary>
    public bool IsModelFlow(string? flowName)
    {
        if (string.IsNullOrEmpty(flowName)) return false;
        var set = GetModelFlowNames();
        return set is null || set.Contains(flowName);
    }

    public List<Work> GetWorks(Guid flowId)
    {
        return [.. Queries.worksOf(flowId, _store)];
    }

    public int GetTotalWorkCount()
    {
        return GetAllFlows().Sum(f => GetWorks(f.Id).Count);
    }

    public List<Call> GetCalls(Guid workId)
    {
        return [.. Queries.callsOf(workId, _store)];
    }

    public List<Call> GetAllCalls()
    {
        var allCalls = new List<Call>();
        foreach (var flow in GetAllFlows())
        {
            var works = GetWorks(flow.Id);
            foreach (var work in works)
            {
                allCalls.AddRange(GetCalls(work.Id));
            }
        }
        return allCalls;
    }

    /// <summary>
    /// Flow의 첫 번째 Call을 가져옵니다 (Head Call)
    /// </summary>
    public Call? GetHeadCall(Guid flowId)
    {
        var works = GetWorks(flowId);
        if (works.Count == 0) return null;

        var firstWork = works[0];
        var calls = GetCalls(firstWork.Id);
        return calls.Count > 0 ? calls[0] : null;
    }

    /// <summary>
    /// Flow 이름으로 Flow 객체 찾기
    /// </summary>
    public Flow? GetFlowByName(string flowName)
    {
        return GetAllFlows().FirstOrDefault(f => f.Name == flowName);
    }

    /// <summary>
    /// Call ID로 해당 Call이 속한 Flow 찾기
    /// </summary>
    public Flow? GetFlowByCallId(Guid callId)
    {
        var call = Queries.getCall(callId, _store);
        if (!Microsoft.FSharp.Core.FSharpOption<Call>.get_IsSome(call))
            return null;

        var work = Queries.getWork(call.Value.ParentId, _store);
        if (!Microsoft.FSharp.Core.FSharpOption<Work>.get_IsSome(work))
            return null;

        var flow = Queries.getFlow(work.Value.ParentId, _store);
        return Microsoft.FSharp.Core.FSharpOption<Flow>.get_IsSome(flow) ? flow.Value : null;
    }

    /// <summary>
    /// Call ID → 모델상 (FlowName, WorkName, CallName). 알람 경로(FLOW/WORK/CALL) 표시용.
    /// CallName 은 "{DevicesAlias}.{ApiName}" 형태(대상 디바이스 포함). 미해석 시 null.
    /// dspCall 테이블의 WorkName 은 flow 명으로 채워지는 quirk 가 있어, 실제 Work 이름은 store 에서 직접 해석한다.
    /// </summary>
    public (string FlowName, string WorkName, string CallName)? GetCallPath(Guid callId)
    {
        var callOpt = Queries.getCall(callId, _store);
        if (!Microsoft.FSharp.Core.FSharpOption<Call>.get_IsSome(callOpt))
            return null;
        var call = callOpt.Value;

        var workOpt = Queries.getWork(call.ParentId, _store);
        if (!Microsoft.FSharp.Core.FSharpOption<Work>.get_IsSome(workOpt))
            return null;
        var work = workOpt.Value;

        var flowOpt = Queries.getFlow(work.ParentId, _store);
        if (!Microsoft.FSharp.Core.FSharpOption<Flow>.get_IsSome(flowOpt))
            return null;
        var flow = flowOpt.Value;

        return (flow.Name, work.Name, call.Name);
    }

    /// <summary>
    /// 한 Call 에 소속된 ApiCall 목록 + 각 ApiCall 의 In/Out 태그와 보정 대상(Device Work)의 현재 AASX duration 값을 반환.
    /// cycle-analysis 의 Call lane 확장 UI 용(읽기 전용 — store 불변). 매핑 경로는
    /// ApiCall → ApiDefId → ApiDef → RxGuid → Device Work (Queries.callDeviceDurationRangeMs 와 동일) 이고,
    /// Device Work 의 Duration/Min/MaxDuration(TimeSpan option) 을 ms 로 변환해 함께 내려준다(없으면 null).
    /// 현재 PoC 는 Call:ApiCall 1:1 이지만 복합 Call(N&gt;1)도 그대로 나열한다.
    /// </summary>
    public List<CallApiCallDetail> GetCallApiCallDetails(Guid callId)
    {
        var result = new List<CallApiCallDetail>();
        if (!IsLoaded) return result;

        var callOpt = Queries.getCall(callId, _store);
        if (!Microsoft.FSharp.Core.FSharpOption<Call>.get_IsSome(callOpt))
            return result;

        foreach (var ac in callOpt.Value.ApiCalls)
        {
            Guid? targetWorkId = null;
            int? curDur = null, curMin = null, curMax = null;

            var defId = ac.ApiDefId;
            if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(defId))
            {
                var apiDefOpt = Queries.getApiDef(defId.Value, _store);
                if (Microsoft.FSharp.Core.FSharpOption<ApiDef>.get_IsSome(apiDefOpt))
                {
                    var rx = apiDefOpt.Value.RxGuid;
                    if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(rx))
                    {
                        var workOpt = Queries.getWork(rx.Value, _store);
                        if (Microsoft.FSharp.Core.FSharpOption<Work>.get_IsSome(workOpt))
                        {
                            var w = workOpt.Value;
                            targetWorkId = w.Id;
                            curDur = ToMs(w.Duration);
                            curMin = ToMs(w.MinDuration);
                            curMax = ToMs(w.MaxDuration);
                        }
                    }
                }
            }

            result.Add(new CallApiCallDetail(
                ac.Id,
                ac.Name,
                ac.InTag?.Value.Address,
                ac.OutTag?.Value.Address,
                targetWorkId,
                curDur, curMin, curMax));
        }

        return result;

        static int? ToMs(Microsoft.FSharp.Core.FSharpOption<TimeSpan> ts)
            => Microsoft.FSharp.Core.FSharpOption<TimeSpan>.get_IsSome(ts)
                ? (int)ts.Value.TotalMilliseconds
                : null;
    }

    /// <summary>
    /// 지정한 Flow 의 Head/Tail Call 을 <see cref="Call.SequenceLabel"/> 에 박제한 뒤 공유 project.aasx 로 재export 한다.
    /// flow.html "적용(저장)" 경로에서 호출 — DSPilot 자신의 사이클 판정은 바뀌지 않고(토폴로지+override 유지),
    /// 이 라벨은 Promaker/모니터링 등 외부 소비자가 읽을 영속 정보다.
    ///
    /// 동작: 해당 Flow 의 모든 Call 을 먼저 Body 로 리셋 → headCallName 을 Head, tailCallName 을 Tail 로 지정 →
    /// store 전체를 exportFromStore 로 재export. export 성공 시 LastLoadedSha256 을 새 파일 해시로 갱신해
    /// AasxFileWatcherService 의 자기-쓰기 재로드(케이스 2 = sha256 일치 skip)를 유발하지 않는다.
    /// </summary>
    /// <returns>export 에 성공하면 true. 미로드 / Project 없음 / 실패 시 false (호출 측 적용은 무효화하지 않음).</returns>
    public bool WriteSequenceLabelsAndExport(Guid flowId, string? headCallName, string? tailCallName)
    {
        if (!IsLoaded || GetProject() is null)
        {
            _logger.LogWarning("[DsProject] SequenceLabel 박제 skip — 프로젝트 미로드");
            return false;
        }

        try
        {
            // 1) Flow 의 모든 Call 을 Body 로 리셋 후 Head/Tail 지정 (재적용 시 이전 라벨이 깨끗이 이동).
            //    head==tail(단일 Call) 이면 else-if 특성상 Head 가 우선 지정된다.
            var calls = GetWorks(flowId).SelectMany(work => GetCalls(work.Id)).ToList();
            int headHits = 0, tailHits = 0;
            foreach (var call in calls)
            {
                if (!string.IsNullOrEmpty(headCallName) &&
                    string.Equals(call.Name, headCallName, StringComparison.OrdinalIgnoreCase))
                {
                    call.SequenceLabel = SequenceLabel.Head;
                    headHits++;
                }
                else if (!string.IsNullOrEmpty(tailCallName) &&
                         string.Equals(call.Name, tailCallName, StringComparison.OrdinalIgnoreCase))
                {
                    call.SequenceLabel = SequenceLabel.Tail;
                    tailHits++;
                }
                else
                {
                    call.SequenceLabel = SequenceLabel.Body;
                }
            }

            // 2) store 전체 재export → 공유 project.aasx (도메인 서브모델 전체 덮어쓰기).
            var ok = Ds2.Aasx.AasxExporter.exportFromStore(
                _store, AasxFilePath, AasxIriPrefix, AasxSplitDevice, AasxAutoCreateEmptySubmodels);
            if (!ok)
            {
                _logger.LogWarning("[DsProject] SequenceLabel 박제 — exportFromStore 가 false 반환 (Project 없음?)");
                return false;
            }

            // 3) 자기-쓰기 억제: 방금 쓴 파일 해시로 LastLoadedSha256 갱신 → 워처가 외부 변경으로 오인하지 않음.
            LastLoadedSha256 = ComputeFileSha256(AasxFilePath);
            LastLoadedUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "[DsProject] SequenceLabel 박제 완료 — Flow={FlowId}, Head='{Head}'({HeadN}), Tail='{Tail}'({TailN}) → {Path}",
                flowId, headCallName, headHits, tailCallName, tailHits, AasxFilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DsProject] SequenceLabel 박제 실패 — Flow={FlowId}", flowId);
            return false;
        }
    }

    /// <summary>
    /// 실측 보정값을 각 Device Work 의 Duration/MinDuration/MaxDuration 에 기록하고 공유 project.aasx 로 재export 한다.
    /// flow.html 의 'Call lane 확장 → 실측 적용' 경로. <see cref="WriteSequenceLabelsAndExport"/> 와 동일한
    /// store 변경 → exportFromStore → LastLoadedSha256 갱신(AasxFileWatcher 자기-쓰기 재로드 억제) 패턴이며,
    /// Promaker DurationBatch 가 쓰는 <c>Store.UpdateWorkDurationRangesBatch</c> 와 동일 메서드를 사용한다(형상 호환).
    /// changes: (workId, durationMs?, minMs?, maxMs?) — ms. min ≤ duration ≤ max 로 정규화 후 기록(실측은 자연 성립하나 방어적).
    /// </summary>
    /// <param name="markMinMeasured">true 면(FillMin = 사용자가 '최소값도 실측으로 기록' 의사 확정) Min 을 실제로
    /// 기록한 Work 를 calibration-state 사이드카에 'Min 실측 확정' 으로 박아 ActionUnder(시간 미만) 게이트를 연다.
    /// false 면(Min 보존/단순 보정) 사이드카는 건드리지 않아 모델에 Min 값이 있어도 ActionUnder 는 비활성을 유지한다.</param>
    /// <param name="markMaxMeasured">true 면 Max 를 기록한 Work 를 'Max 실측 확정' 으로 박아 ActionOver(시간 초과) 게이트를 연다.
    /// 자동 보정/실측 적용은 Max 를 항상 실측으로 채우므로 보통 true 로 전달한다.</param>
    /// <returns>(정규화/적용 시도 건수, export 성공 여부). 미로드·빈 입력·락 점유·예외 시 Exported=false.</returns>
    public (int Applied, bool Exported) WriteWorkDurationCalibrationAndExport(
        IReadOnlyList<(Guid WorkId, int? DurationMs, int? MinMs, int? MaxMs)> changes,
        bool markMinMeasured = false,
        bool markMaxMeasured = false)
    {
        if (!IsLoaded || GetProject() is null)
        {
            _logger.LogWarning("[DsProject] duration 보정 skip — 프로젝트 미로드");
            return (0, false);
        }
        if (changes is null || changes.Count == 0)
            return (0, false);

        // min ≤ duration ≤ max 정규화 후 Promaker 와 동일 시그니처의 batch 구성.
        var batch = new List<(Guid, int?, int?, int?)>();
        foreach (var c in changes)
        {
            int? min = c.MinMs, max = c.MaxMs, dur = c.DurationMs;
            if (min.HasValue && max.HasValue && min > max) (min, max) = (max, min);
            if (dur.HasValue)
            {
                if (min.HasValue && dur < min) dur = min;
                if (max.HasValue && dur > max) dur = max;
            }
            batch.Add((c.WorkId, dur, min, max));
        }

        // cross-process 직렬화 — Promaker publish / Agent 업로드 / 다른 인스턴스의 동시 export·사이드카 쓰기 충돌 방지.
        if (!SharedWriteLock.TryAcquire("DSPilot", out var holder))
        {
            _logger.LogWarning(
                "[DsProject] duration 보정 skip — 공유 쓰기 락 점유 중 (holder={Holder}, pid={Pid}). 다음 기회에 재시도.",
                holder.Holder, holder.Pid);
            return (0, false);
        }
        try
        {
            _store.UpdateWorkDurationRangesBatch(batch);

            var ok = Ds2.Aasx.AasxExporter.exportFromStore(
                _store, AasxFilePath, AasxIriPrefix, AasxSplitDevice, AasxAutoCreateEmptySubmodels);
            if (!ok)
            {
                _logger.LogWarning("[DsProject] duration 보정 — exportFromStore 가 false 반환 (Project 없음?)");
                return (batch.Count, false);
            }

            LastLoadedSha256 = ComputeFileSha256(AasxFilePath);
            LastLoadedUtc = DateTime.UtcNow;

            // 실측 확정 — Min(ActionUnder 게이트) / Max(ActionOver 게이트) 를 사이드카에 박는다.
            // 같은 락 안에서 read-modify-write 해야 Promaker/Agent 의 동시 갱신과 안전하게 직렬화된다.
            if ((markMinMeasured || markMaxMeasured) && LastLoadedSha256 is { Length: > 0 } sha)
            {
                var calib = CalibrationState.Load();
                int marked = 0;
                foreach (var item in batch)
                {
                    if (markMinMeasured && item.Item3.HasValue) { calib.SetMinMeasured(item.Item1, item.Item3.Value, sha); marked++; }
                    if (markMaxMeasured && item.Item4.HasValue) { calib.SetMaxMeasured(item.Item1, item.Item4.Value, sha); marked++; }
                }
                if (marked > 0 && calib.TrySave())
                    _logger.LogInformation("[DsProject] 실측 확정 {Marked}건 기록 (calibration-state, ActionUnder/Over 게이트).", marked);
            }

            _logger.LogInformation("[DsProject] duration 보정 완료 — {Count}건 → {Path}", batch.Count, AasxFilePath);
            return (batch.Count, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DsProject] duration 보정 실패");
            return (0, false);
        }
        finally
        {
            SharedWriteLock.Release("DSPilot");
        }
    }

    /// <summary>
    /// 설정▸수동등록TAG 편집기의 적용 경로 — 지정한 활성 System 들의 UserTag 목록을 <b>통째로 교체</b>하고
    /// 공유 project.aasx 로 재export 한다. Promaker UserTagPanel 이 쓰는 <c>Store.ReplaceUserTags</c> 와 동일
    /// 메서드(인코딩 = UserTagHelpers.format 6필드)라 형상 호환된다.
    ///
    /// ★ AID 병합이 핵심: Agent(Promaker.Agent) 의 PLC 스캔 대상은 AID InterfaceXGT 의 interaction 목록이지
    /// LoggingProperties.UserTags 가 아니다(AidXgtConfig.buildForProject 는 SignalPolicies 만 읽음). Promaker 는
    /// 저장 직전 StampPlcConnection 이 IO맵+UserTag 주소를 AID 에 병합해 주기 때문에 이 차이가 드러나지 않았다.
    /// 여기서 그 단계를 빠뜨리면 "정의는 보이는데 알람이 영원히 안 뜨는" 파일이 만들어지므로, System 별 기존
    /// endpoint 값을 그대로 읽어(<see cref="AidXgtEndpointSettings.TryReadForSystem"/>) 주소만 병합한다
    /// (<see cref="AidXgtEndpointSettings.EnsureBindingForSystem"/>). endpoint 가 없는 System 은 병합할 곳이 없어
    /// 경고로 돌려준다(삭제된 태그의 interaction 은 Promaker 와 같이 남긴다 — 병합만, 제거 없음).
    ///
    /// LogLevel 은 운영 정책상 항상 Error 로 기록한다(Promaker UserTagEditDialog 와 동일).
    /// </summary>
    /// <param name="bySystem">System Id → 그 System 의 최종 UserTag 목록(빈 목록 = 전부 삭제).</param>
    public UserTagWriteResult WriteUserTagsAndExport(
        IReadOnlyDictionary<Guid, IReadOnlyList<UserTagWriteEntry>> bySystem)
    {
        var warnings = new List<string>();
        if (!IsLoaded || GetProject() is not { } project)
            return new UserTagWriteResult(false, 0, warnings, "프로젝트(AASX)가 로드되지 않았습니다.");
        if (bySystem is null || bySystem.Count == 0)
            return new UserTagWriteResult(false, 0, warnings, "변경할 System 이 없습니다.");

        var activeById = GetActiveSystems().ToDictionary(s => s.Id, s => s);
        foreach (var sid in bySystem.Keys)
        {
            if (!activeById.ContainsKey(sid))
                return new UserTagWriteResult(false, 0, warnings,
                    $"System {sid} 는 활성 System 이 아닙니다(Passive/디바이스 System 은 수동등록TAG 를 지원하지 않음).");
        }

        // cross-process 직렬화 — Promaker publish / Agent 업로드 / 다른 인스턴스의 동시 export 충돌 방지.
        if (!SharedWriteLock.TryAcquire("DSPilot", out var holder))
        {
            _logger.LogWarning(
                "[DsProject] UserTag 적용 skip — 공유 쓰기 락 점유 중 (holder={Holder}, pid={Pid}).",
                holder.Holder, holder.Pid);
            return new UserTagWriteResult(false, 0, warnings,
                $"공유 project.aasx 를 다른 프로세스({holder.Holder})가 쓰고 있습니다. 잠시 후 다시 시도하세요.");
        }
        try
        {
            // 1) System 별 UserTag 통째 교체 (한 System = 한 transaction).
            var applied = 0;
            foreach (var (sid, entries) in bySystem)
            {
                var tuples = entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.TagAddress))
                    .Select(e => (e.Name.Trim(), "Error", e.TagAddress.Trim(), e.ValueType ?? "Bit",
                                  e.MatchOp ?? string.Empty, e.MatchValue ?? string.Empty))
                    .ToList();
                applied += _store.ReplaceUserTags(sid, tuples);
            }

            // 2) AID XGT interaction 병합 — System 별 IO맵 주소 + UserTag 주소 (Promaker EnumeratePlcAddressesForSystem 미러).
            var aidOption = project.AssetInterfaces;
            var aid = aidOption is not null
                      && Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption)
                ? aidOption.Value : null;
            var singleActive = activeById.Count == 1;
            foreach (var (sid, entries) in bySystem)
            {
                var sysName = activeById[sid].Name;
                if (entries.Count == 0) continue; // 전부 삭제 — 병합할 주소 없음(기존 interaction 은 보존).
                if (aid is null)
                {
                    warnings.Add($"{sysName}: 모델에 PLC 접속 정보(AID)가 없어 새 주소가 수집 대상에 포함되지 않습니다. Promaker 에서 PLC 접속을 지정해 저장하세요.");
                    continue;
                }
                // System 에 배정된 endpoint 우선, 단일 System 모델이면 legacy(systemRef 없는) endpoint 를 승계.
                var info = AidXgtEndpointSettings.TryReadForSystem(aid, sid)
                           ?? (singleActive ? AidXgtEndpointSettings.TryReadFirst(aid) : null);
                if (info is null || string.IsNullOrWhiteSpace(info.IpAddress) || info.Port <= 0)
                {
                    warnings.Add($"{sysName}: 이 System 에 배정된 PLC 접속(XGT endpoint)이 없어 새 주소가 수집 대상에 포함되지 않습니다.");
                    continue;
                }
                var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in Queries.plcAddressesOfSystem(sid, _store)) addresses.Add(a);
                foreach (var e in entries)
                    if (!string.IsNullOrWhiteSpace(e.TagAddress)) addresses.Add(e.TagAddress.Trim());
                var touched = AidXgtEndpointSettings.EnsureBindingForSystem(
                    aid, sid, info.Vendor, info.IpAddress, info.Port, info.IsUdp, info.LocalEthernet,
                    info.NetworkNumber, info.StationNumber, info.TimeoutMs, info.ScanIntervalMs, addresses);
                if (touched <= 0)
                    warnings.Add($"{sysName}: PLC 접속 정보 병합에 실패했습니다(vendor '{info.Vendor}'). 새 주소가 수집되지 않을 수 있습니다.");
            }

            // 3) store 전체 재export → 공유 project.aasx.
            var ok = Ds2.Aasx.AasxExporter.exportFromStore(
                _store, AasxFilePath, AasxIriPrefix, AasxSplitDevice, AasxAutoCreateEmptySubmodels);
            if (!ok)
            {
                _logger.LogWarning("[DsProject] UserTag 적용 — exportFromStore 가 false 반환 (Project 없음?)");
                return new UserTagWriteResult(false, applied, warnings, "project.aasx 내보내기에 실패했습니다.");
            }

            // 4) 자기-쓰기 억제 + 파생 캐시 무효화. LastLoadedUtc 갱신으로 UserTagAlertService 가 정의를 재로딩하고
            //    엔진 plcTag 캐시(EnsureUserTagAddressesRegistered)까지 자동 갱신된다. 주소→System 커버리지 맵은
            //    LoadProject 에서만 비워지므로 여기서 직접 비운다.
            LastLoadedSha256 = ComputeFileSha256(AasxFilePath);
            LastLoadedUtc = DateTime.UtcNow;
            _addressSystemMap = null;

            _logger.LogInformation(
                "[DsProject] UserTag 적용 완료 — System {SysCount}개, 태그 {Applied}건, 경고 {Warn}건 → {Path}",
                bySystem.Count, applied, warnings.Count, AasxFilePath);
            return new UserTagWriteResult(true, applied, warnings, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DsProject] UserTag 적용 실패");
            return new UserTagWriteResult(false, 0, warnings, $"적용 실패: {ex.Message}");
        }
        finally
        {
            SharedWriteLock.Release("DSPilot");
        }
    }

    /// <summary>
    /// 모든 Work 의 이상감지 임계 MinDuration/MaxDuration 을 전부 비우고(null=clear) 공유 project.aasx 로 재export 한다.
    /// 자동 보정(<see cref="WriteWorkDurationCalibrationAndExport"/>)의 역연산 — 잘못 보정된 임계를 한 번에 초기화할 때 사용.
    /// Duration(거동 구동 평균)은 <b>현재값을 그대로 다시 기록해 보존</b>한다(batch 의 null=clear 규칙 때문에 생략하면 Duration 도 지워진다).
    /// 동일한 store 변경 → exportFromStore → LastLoadedSha256 갱신(자기-쓰기 재로드 억제) 패턴을 따른다.
    /// </summary>
    /// <returns>(임계가 초기화된 Work 건수, export 성공 여부). 미로드/예외 시 Exported=false, 비울 값이 없으면 (0, true).</returns>
    public (int Cleared, bool Exported) ClearAllWorkDurationRangesAndExport()
    {
        if (!IsLoaded || GetProject() is null)
        {
            _logger.LogWarning("[DsProject] Min/Max 초기화 skip — 프로젝트 미로드");
            return (0, false);
        }

        // Min 또는 Max 가 설정된 Work 만 대상. Duration 은 현재값을 다시 넘겨 보존.
        var batch = new List<(Guid, int?, int?, int?)>();
        foreach (var w in _store.WorksReadOnly.Values)
        {
            bool hasMin = Microsoft.FSharp.Core.FSharpOption<TimeSpan>.get_IsSome(w.MinDuration);
            bool hasMax = Microsoft.FSharp.Core.FSharpOption<TimeSpan>.get_IsSome(w.MaxDuration);
            if (!hasMin && !hasMax) continue;
            int? dur = Microsoft.FSharp.Core.FSharpOption<TimeSpan>.get_IsSome(w.Duration)
                ? (int)w.Duration.Value.TotalMilliseconds
                : null;
            batch.Add((w.Id, dur, null, null));
        }

        if (batch.Count == 0)
            return (0, true); // 비울 게 없음 = 정상 no-op.

        if (!SharedWriteLock.TryAcquire("DSPilot", out var holder))
        {
            _logger.LogWarning(
                "[DsProject] Min/Max 초기화 skip — 공유 쓰기 락 점유 중 (holder={Holder}, pid={Pid}).",
                holder.Holder, holder.Pid);
            return (0, false);
        }
        try
        {
            _store.UpdateWorkDurationRangesBatch(batch);

            var ok = Ds2.Aasx.AasxExporter.exportFromStore(
                _store, AasxFilePath, AasxIriPrefix, AasxSplitDevice, AasxAutoCreateEmptySubmodels);
            if (!ok)
            {
                _logger.LogWarning("[DsProject] Min/Max 초기화 — exportFromStore 가 false 반환 (Project 없음?)");
                return (batch.Count, false);
            }

            LastLoadedSha256 = ComputeFileSha256(AasxFilePath);
            LastLoadedUtc = DateTime.UtcNow;

            // Min/Max 를 비웠으므로 해당 Work 의 실측 확정도 모두 해제 — 사이드카가 모델과 어긋난 stale 게이트를 열지 않도록.
            var calib = CalibrationState.Load();
            foreach (var item in batch) calib.ClearWork(item.Item1);
            calib.TrySave();

            _logger.LogInformation("[DsProject] Min/Max 초기화 완료 — {Count}건 → {Path}", batch.Count, AasxFilePath);
            return (batch.Count, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DsProject] Min/Max 초기화 실패");
            return (0, false);
        }
        finally
        {
            SharedWriteLock.Release("DSPilot");
        }
    }

    public List<(double X, double Y)> ComputeArrowPath(Xywh source, Xywh target)
    {
        var visual = Ds2.Editor.ArrowPathCalculator.computePath(source, target);
        return [.. visual.Points.Select(p => (p.Item1, p.Item2))];
    }

    /// <summary>
    /// calibration-state 사이드카의 각 확정 Work 를 현재 모델 duration 과 대조해 이상감지 게이트 상태를 산출한다.
    /// stale = 확정(MaxMeasured/MinMeasured)돼 있으나 모델의 현재 Min/MaxDuration 이 확정값과 달라
    /// <see cref="CalibrationState.IsMaxMeasured"/>/<see cref="CalibrationState.IsMinMeasured"/> 게이트가 닫힌 Work
    /// (= ActionOver/ActionUnder 가 조용히 발화 안 하는 상태). 모델 변경(Promaker 재발행 등) 후 실측 재확정을 안 하면 발생.
    /// 판정 규칙: 모델 값이 null(ClearRanges/미설정)이면 "의도적 비활성"으로 보고 stale 로 치지 않는다 —
    /// 모델이 여전히 임계를 원하는데 값만 달라진 경우(non-null 불일치)만 재측정 대상 stale.
    /// 설정 페이지 게이트 배지와 수동 재보정 대상 확인의 단일 소스.
    /// </summary>
    public IReadOnlyList<CalibrationWorkStatus> GetCalibrationStatus()
    {
        var list = new List<CalibrationWorkStatus>();
        var calib = CalibrationState.Load();
        if (calib.Works.Count == 0) return list;

        foreach (var kv in calib.Works)
        {
            if (!Guid.TryParse(kv.Key, out var wid)) continue;
            var w = kv.Value;

            int? modelMin = null, modelMax = null;
            string name = wid.ToString("D")[..8];
            if (IsLoaded)
            {
                var wo = Queries.getWork(wid, _store);
                if (Microsoft.FSharp.Core.FSharpOption<Work>.get_IsSome(wo))
                {
                    var mw = wo.Value;
                    name = mw.Name;
                    modelMin = DurationToMs(mw.MinDuration);
                    modelMax = DurationToMs(mw.MaxDuration);
                }
            }

            bool staleMax = w.MaxMeasured && modelMax.HasValue && modelMax.Value != w.MaxMs;
            bool staleMin = w.MinMeasured && modelMin.HasValue && modelMin.Value != w.MinMs;

            list.Add(new CalibrationWorkStatus(
                wid, name,
                w.MaxMeasured, w.MaxMs, modelMax, staleMax,
                w.MinMeasured, w.MinMs, modelMin, staleMin));
        }
        return list;
    }

    private static int? DurationToMs(Microsoft.FSharp.Core.FSharpOption<TimeSpan> ts)
        => Microsoft.FSharp.Core.FSharpOption<TimeSpan>.get_IsSome(ts)
            ? (int)ts.Value.TotalMilliseconds
            : null;
}

/// <summary>
/// 한 Call 의 ApiCall 하나 — In/Out 태그 + 보정 대상 Device Work(RxGuid) 의 현재 AASX duration(ms).
/// <see cref="DsProjectService.GetCallApiCallDetails"/> 산출물. duration 값이 없는 필드는 null.
/// </summary>
public record CallApiCallDetail(
    Guid ApiCallId,
    string Name,
    string? InTag,
    string? OutTag,
    Guid? TargetWorkId,
    int? CurrentDurationMs,
    int? CurrentMinMs,
    int? CurrentMaxMs);

/// <summary>
/// 한 Device Work 의 이상감지 게이트 상태 — calibration-state 확정값 vs 현재 모델 duration 대조 결과.
/// <see cref="DsProjectService.GetCalibrationStatus"/> 산출물. <c>StaleMax</c>/<c>StaleMin</c> 가 true 면
/// 그 방향 게이트가 닫혀 ActionOver(Max)/ActionUnder(Min) 가 발화하지 않는다 → 실측 재측정 필요.
/// </summary>
public record CalibrationWorkStatus(
    Guid WorkId,
    string WorkName,
    bool MaxMeasured,
    int CalibMaxMs,
    int? ModelMaxMs,
    bool StaleMax,
    bool MinMeasured,
    int CalibMinMs,
    int? ModelMinMs,
    bool StaleMin);

/// <summary>
/// 모델(AID)에서 읽은 PLC 엔드포인트 1건. <see cref="DsProjectService.GetPlcEndpoints"/> 산출물.
/// SystemName = 배정된 활성 시스템 이름(여러 시스템이 한 PLC 를 공유하면 '·' 병기).
/// </summary>
public sealed record PlcEndpointInfo(string SystemName, string Vendor, string Ip, int Port, int TimeoutMs);

/// <summary>수동등록TAG 편집기 → <see cref="DsProjectService.WriteUserTagsAndExport"/> 입력 1건 (LogLevel 은 서버가 Error 로 고정).</summary>
public sealed record UserTagWriteEntry(string Name, string TagAddress, string ValueType, string MatchOp, string MatchValue);

/// <summary>
/// <see cref="DsProjectService.WriteUserTagsAndExport"/> 결과. Exported=false 면 Error 에 사유.
/// Warnings = 적용은 됐지만 수집 반영이 불완전한 System(AID endpoint 부재 등) 안내.
/// </summary>
public sealed record UserTagWriteResult(bool Exported, int Applied, List<string> Warnings, string? Error);
