// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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

    /// <summary>
    /// 자세(midCycle) 승격 — NULL/0 → 1 방향만 갱신(유발자 증거는 강등되지 않는다).
    /// <see cref="Models.Oee.OeeDowntimeEvent.MidCycle"/> 참조. 영향 행 수 반환(0=이미 같거나 높음).
    /// </summary>
    Task<int> SetDowntimeMidCycleAsync(long id, int midCycle, CancellationToken ct = default);

    /// <summary>
    /// 분류 PATCH — reasonCode/category, isFailure(category=unplanned 일 때 1), classifySource(출처).
    /// 수동 분류는 'manual'(기본), CauseBit 자동분류는 'auto-bit'. 무조건 UPDATE(수동·비트는 권위적).
    /// </summary>
    Task<int> ClassifyDowntimeAsync(long id, string? reasonCode, string? category, bool isFailure, string? classifySource = "manual", CancellationToken ct = default);

    /// <summary>
    /// 비생산↔비가동 재분류(2026-07-08). toNonProd=true 는 현재 비가동 분류를 prev* 에 스태시한 뒤 비생산으로,
    /// false 는 스태시가 있으면 원래 분류(유지보수 등) 복원(없으면 기본 고장) — 왕복해도 유지보수 상태가 보존된다.
    /// 항상 classifySource='manual'(KPI 오버라이드). 영향 행 수 반환.
    /// </summary>
    Task<int> ReclassifyDowntimeAsync(long id, bool toNonProd, CancellationToken ct = default);

    /// <summary>일괄 분류 — 복수 id 에 동일 reasonCode/category/classifySource 적용. 영향 행 수 반환.</summary>
    Task<int> BulkClassifyDowntimeAsync(IReadOnlyList<long> ids, string? reasonCode, string? category, bool isFailure, string? classifySource = "manual", CancellationToken ct = default);

    /// <summary>
    /// 휴리스틱 자동분류(5분/8h) — 미분류(category IS NULL)이고 classifySource ≠ 'manual' 인 행만 채운다
    /// (수동 우선 — 작업자 분류를 자동이 덮지 않게). classifySource='auto-heuristic' 스탬프. 영향 행 수 반환.
    /// </summary>
    Task<int> AutoClassifyHeuristicAsync(long id, string? reasonCode, string? category, bool isFailure, CancellationToken ct = default);

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

    /// <summary>
    /// <paramref name="retainFlowNames"/> 에 없는 flow 의 정지 이벤트 삭제(= 현재 AASX 에 없는 유령 설비 정리).
    /// flowName IS NULL(라인 전체 귀속) 행은 특정 설비 소유가 아니라 보존한다.
    /// <paramref name="countOnly"/>=true 면 삭제하지 않고 대상 행 수만 센다(정리 미리보기).
    /// retain 이 비면 no-op(0) — 전량 삭제는 ClearDowntimeEventsAsync 의 영역.
    /// </summary>
    Task<int> PruneDowntimeEventsByFlowNamesAsync(
        IEnumerable<string> retainFlowNames, bool countOnly = false, CancellationToken ct = default);

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

    /// <summary>
    /// 기간 정지 합(ms)·건수 — flow <b>집합</b> 스코프(시스템 단위 묶음, 2026-08-25). 라인(flowName=null) 경로와
    /// 동일하게 구간 union 으로 잰다 — flow별 합산은 동시 정지를 flow 수만큼 이중 계상한다(실측 6배).
    /// flowName IS NULL(라인 귀속) 이벤트는 포함(라인 정지는 이 시스템에도 걸친 시간). 빈 집합 → (0, 0).
    /// </summary>
    Task<(long DowntimeMs, int Count)> GetDowntimeAggregateForFlowsAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string> flowNames, CancellationToken ct = default);

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

    /// <summary>
    /// 기간 [from,to] 과 겹치는 정지 이벤트를 raw 구간(UTC epoch ms)+종류로 반환. 슬롯 분배는 컨트롤러가 overlap 으로 수행.
    /// Kind: 0=계획정비(category='planned') / 1=고장(isFailure=1) / 2=기타 비계획 / 3=미분류(category NULL) — 상호배타.
    /// IsAuto: detectSource='nocycle' 여부 — 자동 파생 무사이클 정지(=사이클 모델의 비생산과 같은 유휴)면 true.
    ///   컨트롤러가 비생산 카빙을 이 자동 정지보다 우선 적용해 상단 KPI A(정본)와 추이를 일치시킨다(고장비트/수동 정지는 그대로 우선).
    /// open(endAt NULL) 은 min(now, to) 로 캡. startAt 이 from 이전이라도 겹치면 포함(장시간·다일 정지 정확 분배).
    /// FlowName: 이벤트의 flow — 라인 조회(flowName=null) 시 비가동 ΣCT 고장/유지보수 분리의 flow별 귀속에 사용.
    /// </summary>
    Task<IReadOnlyList<(long StartMs, long EndMs, int Kind, bool IsAuto, string? FlowName)>> GetDowntimeIntervalsAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default);

    // ── 자동 비생산 감지 로그 (10×CT, doc/22 §3.3) ────────────────────────

    /// <summary>
    /// 자동 인식 비생산(≥10×14일평균CT) 감지들을 UPSERT(멱등 — (flowName, onsetAt, detectionReason) 키).
    /// ComputeCycleAggregateAsync 가 조회 시 materialize. 라인 스코프(flowName=null)는 "" 로 정규화 저장. 영향 행 수 반환.
    /// 재확인 시 lastConfirmedAt 갱신 + invalidatedAt 해제(부활) — 자가치유(doc/25 §4.1)의 생존 마커.
    /// </summary>
    Task<int> UpsertNonProdDetectionsAsync(IReadOnlyList<OeeNonProdDetectionLog> entries, CancellationToken ct = default);

    /// <summary>
    /// 자가치유(doc/25 §4.1) — 창 안(onsetAt ∈ [fromUtc, toUtc))의 감지 행 중 이번 집계 패스(batchMarkUtc 이후
    /// UPSERT)가 재확인하지 않은 행을 invalidatedAt 마킹(삭제 대신 — 감사 보존, 표시에서만 제외).
    /// flows = 이번 패스가 실제 처리한 flow 목록(임계 없는 flow 의 과거 행 오폭 방지), includeLineScope = 라인
    /// 스코프('') 행 포함 여부(라인 집계 패스만 true). 마킹 행 수 반환.
    /// </summary>
    Task<int> InvalidateStaleNonProdDetectionsAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> flows, bool includeLineScope,
        DateTime batchMarkUtc, CancellationToken ct = default);

    /// <summary>기간 내 자동 비생산 감지 구간(UTC epoch ms)을 로그에서 조회. flow 지정=그 flow, null=전체(라인 — union 은 호출측). open 은 min(now,to) 캡.</summary>
    Task<IReadOnlyList<(double S, double E)>> GetNonProdIntervalsFromLogAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default);

    /// <summary>
    /// [fromUtc, toUtc] 와 겹치는 자동 비생산 감지 로그 행을 invalidatedAt 마킹 — 사용자가 그 구간을 '비가동으로
    /// 보내기' 확정했을 때 stale 감지가 actual/추이 표시에 되살아나지 않게 한다(2026-07-08, doc/25 §4.1 부터
    /// 삭제 대신 마킹 — 감사 행 보존). 마킹 행 수 반환.
    /// </summary>
    Task<int> DeleteNonProdDetectionsOverlappingAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>
    /// 기간과 겹치는 <b>수동 분류</b>(classifySource='manual') 정지 이벤트 구간(UTC epoch ms) — 당일 비생산 판정의
    /// 사용자 오버라이드 소스(2026-07-08). ToNonProd=true(reasonCode='non_production') → 그 구간을 비생산으로 강제,
    /// false(고장/유지보수 등) → 자동 10×CT 승격을 억제(비가동 유지). open(endAt NULL)은 min(now,to) 캡.
    /// </summary>
    Task<IReadOnlyList<(string? FlowName, double S, double E, bool ToNonProd)>> GetManualReclassIntervalsAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    Task<long> InsertShiftExceptionAsync(OeeShiftException row, CancellationToken ct = default);
    Task<IReadOnlyList<OeeShiftException>> QueryShiftExceptionsAsync(
        DateTime fromUtc, DateTime toUtc, string? flowName, CancellationToken ct = default);
    Task<int> DeleteShiftExceptionAsync(long id, CancellationToken ct = default);
}
