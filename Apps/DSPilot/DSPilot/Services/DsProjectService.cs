// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Security.Cryptography;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using DSPilot.Infrastructure;
using Microsoft.FSharp.Collections;

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

    public List<Flow> GetFlows(Guid systemId)
    {
        return [.. Queries.flowsOf(systemId, _store)];
    }

    public List<Flow> GetAllFlows()
    {
        return [.. Queries.allFlows(_store)];
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
