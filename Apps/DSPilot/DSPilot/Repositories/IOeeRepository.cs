using DSPilot.Models.Oee;

namespace DSPilot.Repositories;

/// <summary>
/// OEE / 정지(다운타임) 저장소 — 별도 oee.db (수동입력 자산 보존).
/// 경로 = IDatabasePathResolver.GetSharedDbPath() 의 디렉터리 + "oee.db".
/// datetime = TEXT ISO8601 UTC (SqliteDateTimeHelpers). 컨벤션은 plc.db 와 동일.
/// 자동 파생(무사이클 정지)은 OeeDowntimeStateMachine 이 INSERT/CLOSE 하고,
/// 분류(reasonCode/category)·불량·시프트는 사람이 컨트롤러를 통해 입력한다.
/// </summary>
public interface IOeeRepository
{
    /// <summary>3 테이블 + 인덱스 보장 (oee.db). startup 에서 1회 호출.</summary>
    Task<bool> CreateSchemaAsync();

    // ── 정지(다운타임) ───────────────────────────────────────────────────

    /// <summary>정지 onset INSERT. id 반환. usertag 는 (detectSource, sourceLogId) 부분 유니크로 멱등.</summary>
    Task<long> InsertDowntimeAsync(OeeDowntimeEvent evt, CancellationToken ct = default);

    /// <summary>open 이벤트 마감 — endAt + durationMs 채움. 영향 행 수 반환.</summary>
    Task<int> CloseDowntimeAsync(long id, DateTime endAtUtc, CancellationToken ct = default);

    /// <summary>분류 PATCH — reasonCode/category, isFailure(category=unplanned 일 때 1). 영향 행 수 반환.</summary>
    Task<int> ClassifyDowntimeAsync(long id, string? reasonCode, string? category, bool isFailure, CancellationToken ct = default);

    /// <summary>일괄 분류 — 복수 id 에 동일 reasonCode/category 적용. 영향 행 수 반환.</summary>
    Task<int> BulkClassifyDowntimeAsync(IReadOnlyList<long> ids, string? reasonCode, string? category, bool isFailure, CancellationToken ct = default);

    /// <summary>일괄 수동 마감 — open 상태인 항목만 endAt/durationMs 채움. 영향 행 수 반환.</summary>
    Task<int> BulkCloseDowntimeAsync(IReadOnlyList<long> ids, DateTime endAtUtc, CancellationToken ct = default);

    /// <summary>
    /// oeeDowntimeEvent 전체 삭제 — plc.db 재구축 시 정지 이벤트도 동반 초기화하는 용도. 삭제 행 수 반환.
    /// oeeProductionCount(불량/생산)·oeeShiftException(시프트 예외)는 보존된다(doc/21 §1 수동입력 자산 보존 유지).
    /// 상태머신/폴러는 무상태(매 tick GetOpenEventsAsync 재조회)라 행 삭제만으로 안전(stale id 없음).
    /// </summary>
    Task<int> ClearDowntimeEventsAsync(CancellationToken ct = default);

    /// <summary>지정 시각(UTC) 이전 정지 이벤트 삭제. 선택 삭제용. 삭제 행 수 반환.</summary>
    Task<int> DeleteDowntimeEventsBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default);

    /// <summary>정지 로그 조회 (필터: 기간/status open|recovered/reason). 최신순.</summary>
    Task<IReadOnlyList<OeeDowntimeDto>> QueryDowntimeAsync(
        DateTime fromUtc, DateTime toUtc,
        string? status, string? reasonCode, string? flowName,
        CancellationToken ct = default);

    /// <summary>현재 open(endAt IS NULL) 인 이벤트들 (상태머신 dedupe / 자동 clear 후보).</summary>
    Task<IReadOnlyList<OeeDowntimeEvent>> GetOpenEventsAsync(string? flowName = null, CancellationToken ct = default);

    /// <summary>기간/flow 정지 합(ms)·건수 집계. flowName=null → 전체.</summary>
    Task<(long DowntimeMs, int Count)> GetDowntimeAggregateAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default);

    /// <summary>기간/flow 고장(isFailure=1) durationMs 합·건수 (MTBF/MTTR 분자).</summary>
    Task<(long FailureDurationMs, int FailureCount)> GetFailureAggregateAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default);

    /// <summary>기간 내 정지가 발생한 flow별 합·건수 (순위표용). flowName 이 NULL 인 이벤트는 제외.</summary>
    Task<IReadOnlyList<(string FlowName, long DowntimeMs, int Count)>> GetDowntimeByFlowAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    // ── 생산/품질 ─────────────────────────────────────────────────────────

    /// <summary>(bucketDate, flowName, shift) upsert — rejectCount/goodCount/source 갱신.</summary>
    Task<int> UpsertProductionAsync(OeeProductionCount row, CancellationToken ct = default);

    /// <summary>
    /// PLC 자동수집 생산/불량 주입 (source='plc', plc &gt; manual). (bucketDate, flowName, shift) upsert.
    /// total/reject 가 null 이면 그 필드는 기존값 보존(신호 미설정 → 수동 입력을 덮지 않음).
    /// goodCount = max(0, total - reject). OeeUserTagPollerService 전용.
    /// </summary>
    Task<int> UpsertProductionFromPlcAsync(
        string bucketDate, string flowName, string shift, int? total, int? reject, CancellationToken ct = default);

    /// <summary>
    /// 기간/flow 생산 합 (total/good/reject). bucketDate 로컬일 범위. plc 행이 있으면 plc 만(plc &gt; manual).
    /// HasReject = (선택된 소스에) 생산/불량 행이 존재 → quality 산출 가능 여부 게이트.
    /// </summary>
    Task<(int Total, int Good, int Reject, bool HasReject)> QueryProductionAsync(
        DateTime fromLocal, DateTime toLocal, string? flowName, CancellationToken ct = default);

    // ── 시프트 예외 (계획정비/비가동) ─────────────────────────────────────

    Task<long> InsertShiftExceptionAsync(OeeShiftException row, CancellationToken ct = default);
    Task<IReadOnlyList<OeeShiftException>> QueryShiftExceptionsAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default);
    Task<int> DeleteShiftExceptionAsync(long id, CancellationToken ct = default);
}
