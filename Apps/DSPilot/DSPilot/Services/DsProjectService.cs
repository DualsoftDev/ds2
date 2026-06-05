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
    /// <returns>(정규화/적용 시도 건수, export 성공 여부). 미로드·빈 입력·예외 시 Exported=false.</returns>
    public (int Applied, bool Exported) WriteWorkDurationCalibrationAndExport(
        IReadOnlyList<(Guid WorkId, int? DurationMs, int? MinMs, int? MaxMs)> changes)
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

            _logger.LogInformation("[DsProject] duration 보정 완료 — {Count}건 → {Path}", batch.Count, AasxFilePath);
            return (batch.Count, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DsProject] duration 보정 실패");
            return (0, false);
        }
    }

    public List<(double X, double Y)> ComputeArrowPath(Xywh source, Xywh target)
    {
        var visual = Ds2.Editor.ArrowPathCalculator.computePath(source, target);
        return [.. visual.Points.Select(p => (p.Item1, p.Item2))];
    }
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
