// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Models.Oee;

// ============================================================================
//  P5 OEE / 정지(다운타임) 엔티티 + DTO
//  설계 근거: doc/21_OEE_DOWNTIME_DESIGN.md
//  영속: 별도 oee.db (%ProgramData%/DualSoft/Shared/oee.db). DSPilot 단독 writer.
//  datetime = TEXT ISO8601 UTC (SqliteDateTimeHelpers). 컬럼은 doc/21 §2 DDL 그대로.
// ============================================================================

// ─────────────────────────────────────────────────────────────────────────
//  엔티티 (oee.db 테이블 ↔ 1:1)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// oeeDowntimeEvent — 정지/다운타임 라이프사이클 (open → recovered).
/// 자동 onset(detectSource='nocycle')은 category/reasonCode NULL, isFailure 1(고장 기본값)으로 시작.
/// 사용자가 '유지보수'로 해제(isFailure=0, reasonCode='planned_maint')하거나 수동 마감으로만 사람이 채운다.
/// </summary>
public sealed class OeeDowntimeEvent
{
    public long Id { get; set; }

    /// <summary>1차 조회 키 (자동 onset 은 보통 system 만 매핑됨).</summary>
    public string SystemName { get; set; } = string.Empty;

    /// <summary>설비(Station) = Flow. 무사이클 onset 은 항상 채워진다.</summary>
    public string? FlowName { get; set; }

    /// <summary>장치(Work/Call) — 옵션.</summary>
    public string? DeviceName { get; set; }

    /// <summary>정지 시작 (ISO8601 UTC).</summary>
    public DateTime StartAt { get; set; }

    /// <summary>정지 종료. NULL = 진행중(open).</summary>
    public DateTime? EndAt { get; set; }

    /// <summary>endAt 확정 시 계산된 지속시간(ms).</summary>
    public long? DurationMs { get; set; }

    /// <summary>NULL = 미분류. equipment_fault/material_wait/operator_wait/tooling/planned_maint/etc.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>NULL=미분류 / planned / unplanned. 자동 onset 기본값 NULL.</summary>
    public string? Category { get; set; }

    /// <summary>기본 0. 분류 확정 시에만 1 (MTBF/MTTR 분모 오염 방지).</summary>
    public int IsFailure { get; set; }

    /// <summary>'nocycle' / 'usertag' / 'manual'. (정지 구간을 만든 "감지" 출처 — 의미 고정.)</summary>
    public string DetectSource { get; set; } = "nocycle";

    /// <summary>
    /// 원인 "분류"가 어떻게 정해졌는지 (detectSource=감지 출처와 의미 구분):
    /// NULL=미분류 / 'manual'(작업자 분류) / 'auto-bit'(CauseBit 자동분류) / 'auto-heuristic'(5분/8h 휴리스틱).
    /// 'manual' 은 자동 휴리스틱이 덮지 않는다(수동 우선). doc/21 §12 개정.
    /// </summary>
    public string? ClassifySource { get; set; }

    /// <summary>plcTagLog.id (usertag onset dedupe 키). nocycle 은 NULL.</summary>
    public long? SourceLogId { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// oeeNonProdDetectionLog — 자동 인식 비생산(무변화 정지 ≥ 10×14일평균CT, doc/22 §3.3)을 감지 시점에 영속화한 로그.
/// 기존엔 ComputeCycleAggregateAsync 가 조회마다 재계산 후 버렸음(ephemeral) → TEEP(생산효율) 이 장기간에도 일관·저비용으로
/// 쓰도록 조회 시 materialize(UPSERT)한다. <see cref="CtThresholdMs"/> 는 감지 당시의 14일 임계값 스냅샷 —
/// 나중에 임계가 바뀌어도 과거 판정이 흔들리지 않게 한다(감사·재현성). 정지 '원천'은 여전히 oeeDowntimeEvent 이고,
/// 이 로그는 "왜 비생산으로 분류됐는가"의 감사기록 + TEEP 소스다(이중계상 금지 — dedup 키로 멱등).
/// </summary>
public sealed class OeeNonProdDetectionLog
{
    public long Id { get; set; }

    /// <summary>설비(Flow). 라인(전체) 스코프 감지는 저장 시 ""(빈문자열)로 정규화(SQLite UNIQUE NULL footgun 회피).</summary>
    public string? FlowName { get; set; }

    /// <summary>비생산 구간 시작 (ISO8601 UTC).</summary>
    public DateTime OnsetAt { get; set; }

    /// <summary>비생산 구간 끝 (ISO8601 UTC). 진행중이면 조회 상한(min(now,to))으로 캡해 갱신.</summary>
    public DateTime? ClearAt { get; set; }

    /// <summary>ClearAt − OnsetAt (ms).</summary>
    public long DurationMs { get; set; }

    /// <summary>감지 출처 — 현재 'auto-10xct' 뿐.</summary>
    public string DetectionSource { get; set; } = "auto-10xct";

    /// <summary>'idle-cycle'(미완료 멈춤 사이클) / 'nocycle-gap'(무사이클 정지). dedup 키의 일부.</summary>
    public string DetectionReason { get; set; } = string.Empty;

    /// <summary>감지 당시 14일 평균 CT(이상치, ms) 스냅샷.</summary>
    public double CtThresholdMs { get; set; }

    /// <summary>적용 배수(기본 10). 향후 파라미터화 대비.</summary>
    public double CtMultiplier { get; set; } = 10.0;

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// oeeProductionCount — 생산/품질. (bucketDate, flowName, shift) 복합 PK.
/// totalCount 는 dspFlowHistory row count 로 자동, rejectCount 만 수동 입력.
/// </summary>
public sealed class OeeProductionCount
{
    /// <summary>yyyy-MM-dd (로컬일).</summary>
    public string BucketDate { get; set; } = string.Empty;

    public string FlowName { get; set; } = string.Empty;

    public string Shift { get; set; } = string.Empty;

    /// <summary>dspFlowHistory row count 로 자동 채움 가능.</summary>
    public int TotalCount { get; set; }

    public int GoodCount { get; set; }

    /// <summary>수동 입력 (불량만).</summary>
    public int RejectCount { get; set; }

    /// <summary>cycle(자동) / manual / plc.</summary>
    public string Source { get; set; } = "cycle";
}

/// <summary>
/// oeeShiftException — 계획생산시간/계획정비 (가용성 분모). Phase 4 진짜 가용성용.
/// </summary>
public sealed class OeeShiftException
{
    public long Id { get; set; }

    /// <summary>NULL = 전체 라인.</summary>
    public string? FlowName { get; set; }

    public DateTime StartAt { get; set; }

    public DateTime EndAt { get; set; }

    /// <summary>planned_maint / planned_stop / non_production.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? Note { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────
//  DTO (API 응답 — camelCase 자동 직렬화)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// 생산효율(TEEP) — 캘린더 대비 실제 가동. 설비효율 탭의 OEE 와 별개 관점(P6). 단순 가동형: 분자=가동(NormalCtMs), P·Q 미반영.
/// TEEP = 가동 ÷ 캘린더(전체, 비생산 포함). Utilization(가동률) = (캘린더−비생산) ÷ 캘린더(보조).
/// 라인(flow=null)은 flow별 합산 — 캘린더 = 기간 × 임계 보유 flow 수(FlowCount). 잔여 = 캘린더 − 가동 − 정지 − 비생산(≥0).
/// 비생산(NonProdMs)은 자동 10×CT 감지 + 수동 시간대 합(Phase 2 로그와 동일 소스 — 같은 사이클 집계가 로그로도 materialize).
/// </summary>
public sealed record OeeTeepDto(
    string? FlowName,                 // null = 전체(라인 합산)
    DateTime FromUtc,
    DateTime ToUtc,
    int FlowCount,                    // 캘린더 배수 (단일 flow=1, 라인=임계 보유 flow 수). 0 이면 산출 불가.
    double CalendarMs,                // 기간 × FlowCount
    double RunningMs,                 // 가동 = Σ실측CT
    double DownMs,                    // 정지 = Σ비가동CT
    double NonProdMs,                 // 비생산 = 자동10× + 수동(flow별 합산, A 분모 밖)
    double ResidualMs,                // 잔여 = 캘린더 − 가동 − 정지 − 비생산 (≥0)
    double? Teep,                     // 가동 ÷ 캘린더 (0~1). null = 산출 불가(캘린더 0 / 임계 없음)
    string? TeepNote,
    double? Utilization,              // (캘린더 − 비생산) ÷ 캘린더 (0~1). 보조지표
    double? CtThresholdMs,            // 참고 (14일 평균)
    double UnmeasuredMs = 0);         // 미계측(수신 공백, §3.4) — flowCount 배수 적용. 잔여(Residual)에서 분리 표기

/// <summary>
/// 생산효율 매트릭스(P6 L0) — flow × 시간버킷별 TEEP·OEE. /uptime-teep 의 라인 3D(설비×시간)·설비 2D(TEEP·OEE/시간) 차트 데이터.
/// 버킷 규칙은 /api/oee/daily 와 동일(스팬 ≤2일=시간, 초과=일, 로컬 달력 클립). 셀 산출은 <c>OeeMath.BuildTeepMatrixCells</c>(순수함수) —
/// 가동·사이클수=시작버킷 귀속, 정지·비생산=구간 overlap 분배. KPI(/api/oee/teep)와 같은 사이클 집계지만 flow별 재집계라
/// 무사이클 갭 10× 판정 임계(라인=flow 평균 vs 여기=flow 자신)가 달라 라인 합산치와 미세 오차 가능(차트용 허용).
/// </summary>
public sealed record OeeTeepMatrixDto(
    DateTime FromUtc,
    DateTime ToUtc,
    string Granularity,               // "hour"(≤2일) | "day"
    double Quality,                   // 셀 OEE 에 곱한 품질 Q (0~1) — 수기 전역값, 미설정 = 1.0 가정
    string QualitySource,             // "manual" | "assumed"
    List<OeeTeepMatrixBucketDto> Buckets,
    List<OeeTeepMatrixFlowDto> Flows,
    // 계획 기준선(P6 L0) — 캘린더 대비 "가동하기로 한" 비율(가용성 분모 ÷ 기간). 3D 아이소가 이 높이에
    // 점선 평면("계획 Nh/day")을 그려 큐브 총높이(가동+정지)를 계획과 대비시킨다(목업 원의도 복원).
    // 소스 = 가용성 폴백 체인(shift/auto/calendar). calendar = 계획 미설정(=1.0, 프론트는 기준선 생략).
    double? PlannedFraction = null,
    string? PlannedSource = null);

/// <summary>매트릭스 시간버킷 — Label 은 daily 와 동일 포맷("yyyy-MM-dd HH:00" | "yyyy-MM-dd"), 표시 축약은 프론트 몫.</summary>
public sealed record OeeTeepMatrixBucketDto(string Label, DateTime StartUtc, DateTime EndUtc);

public sealed record OeeTeepMatrixFlowDto(
    string FlowName,
    double? CtThresholdMs,            // flow CT이상치(14일 평균) — 셀 성능 P 의 표준(참고 표기용)
    List<OeeTeepMatrixCellDto> Cells);

/// <summary>매트릭스 셀 — 산출 불가 지표는 null(정직 표기). 버킷 순서는 Buckets 와 1:1.</summary>
public sealed record OeeTeepMatrixCellDto(
    double CalendarMs,                // 버킷 길이(기간 클립 후)
    double RunningMs,                 // 가동 = 버킷 시작 정상 사이클 ΣCT
    double DownMs,                    // 정지 = 비가동 구간 overlap
    double NonProdMs,                 // 비생산 = 자동10× + 수동 시간대 overlap
    int CycleCount,                   // 정상 사이클 수(성능 P 분자)
    double? Teep,                     // 가동 ÷ 버킷캘린더 (0~1)
    double? Availability,             // 가동 ÷ (가동+정지)
    double? Performance,              // (N × CT이상치) ÷ 가동, max 1.0
    double? Oee);                     // A × P × Q(전역 수기)

/// <summary>
/// OEE 6지표 + 구성요소. 산출 불가 항목은 null + 사유(*Note) 정직 표기 (doc/21 §10, §12 개정).
/// availability = 달력근사(Phase1), performance = idealCT 기반(수동 입력 또는 실측 자동기입),
/// quality = (사이클수 − 입력불량) / 사이클수 — 불량 미입력이면 불량 0 으로 보아 100% 가정
/// (QualitySource="assumed" 로 명시), mtbf/mttr = isFailure 이벤트 기반.
/// </summary>
public sealed record OeeSummaryDto(
    string? FlowName,                 // null = 전체(라인 합산)
    DateTime FromUtc,
    DateTime ToUtc,
    double PeriodMs,                  // 달력 기간 (to - from)
    long DowntimeMs,                  // 기간 내 정지 합(ms)
    int DowntimeCount,                // 기간 내 정지 건수
    int? TotalCount,                  // dspFlowHistory row count (자동)
    int? RejectCount,                 // 입력 불량 합 (미입력 = 0 가정)
    int? GoodCount,                   // total - reject
    int? IdealCycleTimeMs,            // FlowCycleOverride.IdealCycleTimeMs
    string? IdealCycleTimeSource,     // "auto" = 실측 자동기입 / null = 수동(또는 미설정)

    double? Availability,             // 사이클기반(doc/22): Σ실측CT / Σ전체CT. 표본 부족 시 시간기반 폴백 체인.
    string? AvailabilityNote,         // 산출 방식/한계 사유
    string? AvailabilitySource,       // "cycle"(사이클기반 1차) / "shift" / "auto" / "calendar" / null
    double? Performance,              // 사이클기반(doc/22): (N × CT이상치) / Σ실측CT, min(1.0). 표본 부족 시 null
    string? PerformanceNote,
    double? Quality,                  // (total-reject)/total. 사이클 0 이면 null
    string? QualityNote,
    string? QualitySource,            // "measured"(불량 데이터 있음) / "assumed"(불량 0 가정) / null(산출 불가)
    double? Oee,                      // A*P*Q. 한 요소라도 null 이면 null
    string? OeeNote,

    int FailureCount,                 // 비가동 이벤트(고장) 건수 — 사이클기반: 비가동 사이클 + 무사이클(dedup) 건수
    double? Mtbf,                     // 연속 비가동 onset 간격 평균 (ms) — doc/22 §5
    string? MtbfNote,
    double? Mttr,                     // 비가동 onset → going 회복 구간 평균 (ms) — doc/22 §5 (KPI 복원)
    string? MttrNote,

    // ── 사이클기반 OEE 구성요소 (doc/22 §7) — UI 사이클 분해 시각화/노트용 ──
    double NormalCtMs = 0,            // Σ실측CT (정상 사이클 CT 합)
    double IdleCtMs = 0,              // Σ비가동CT (미계획 비가동 — 계획정지 제외, dedup 후)
    int? NormalCycleCount = null,     // N (정상 사이클 수)
    double? CtThresholdMs = null,     // CT이상치 (14일 평균 표준CT, flow별/라인 가중평균)
    double PlannedDownMs = 0,         // 비생산 시간 비가동(가용성 분모서 제외 — 표준 OEE)
    string? PlannedStopSource = null, // 비생산 출처: "manual"(사용자 시각대) / "auto"(10×CT 장시간정지 자동) / "none"(없음)
    int? CtSampleCount = null,        // CT이상치 산출에 쓰인 클린샘플 수(라인=임계 보유 flow 중 최소). 임계 없으면 null
    bool CtSampleLow = false,         // 클린샘플 < 신뢰선(5) — A·P 는 잠정값(샘플 쌓이면 자동 정상화). UI '샘플 부족' 표시용
    double IdleMaintCtMs = 0,         // Σ비가동CT 중 유지보수(isFailure=0 이벤트 겹침) 귀속분 — 가용성 바 분할(고장 = IdleCtMs − 이 값)
    double IdleCalendarMs = 0,        // 비가동 구간의 달력(벽시계) 환산 — Union 후 Total. ΣCT(설비 합산)와의 단위 오독 방지 병기용
    int CycleFlowCount = 0,           // ΣCT 합산에 참여한 설비(flow) 수 — "설비시간 합산 ×N" 칩 표기용(설비 필터 시 1)
    double UnmeasuredMs = 0);         // 미계측(수신 공백, doc/22 §3.4) 달력시간 — 가동/비가동/비생산 어디에도 미포함(정직 표기)

/// <summary>비생산 시간대 한 칸 DTO (반복 일일, 로컬 자정 기준 분).</summary>
public sealed record PlannedStopWindowDto(int StartMinutes, int EndMinutes, string? Label);

/// <summary>
/// 비생산 시간대 설정 상태 — GET /api/oee/planned-stops 응답.
/// Auto=true → 10×(14일 평균 CT) 장시간 무변화 정지 자동 비생산(Source="auto", Windows 미사용).
/// Auto=false → 사용자 수동 시각대(Source="manual" Windows 권위, 또는 "none"). CtMultiplier = 자동판정 배수(10).
/// </summary>
public sealed record PlannedStopsDto(
    string Source,
    IReadOnlyList<PlannedStopWindowDto> Windows,
    bool Auto,
    int CtMultiplier);

/// <summary>
/// 자동 비생산 시간대 windows. 14일 평균 패턴(auto-pattern, DaysAnalyzed=14) 또는 이번 기간 실제 제외분(actual, DaysAnalyzed=0).
/// CurrentlyNonProd = 조회범위가 실시간(현재 포함)이고 지금 이 순간이 비생산 구간에 속하면 true(actual 전용, 패턴은 항상 false).
/// </summary>
public sealed record PlannedAutoPatternDto(
    IReadOnlyList<PlannedStopWindowDto> Windows,
    DateTime DataFrom,
    DateTime DataTo,
    int DaysAnalyzed,
    bool CurrentlyNonProd = false,
    IReadOnlyList<PlannedStopWindowDto>? UnmeasuredWindows = null,  // 미계측(수신 공백, §3.4) — actual 전용, 비생산과 분리 표기
    bool CurrentlyUnmeasured = false,                               // 지금 이 순간이 미계측(수신 공백) — 배지 3-상태용
    IReadOnlyList<PlannedStopDayDto>? Days = null,                  // 날짜별 접기(actual 전용) — TEEP "날짜별 비생산 패턴" 행
    bool DaysClipped = false,                                       // Days 가 상한(최근 N일)으로 잘렸는가 — UI 정직 표기용
    int ActiveDays = 0,                                             // 패턴 학습 투표 분모(활동일 수) — auto-pattern 전용(§3.5)
    double PromoteRatio = 0);                                       // 승격 컷(활동일 대비 반복 비율) — auto-pattern 전용

/// <summary>
/// 날짜별 비생산 패턴 한 행 (actual 전용) — 해당 로컬 날짜의 자정 경계로 클립해 접은 windows.
/// 각 날을 독립으로 접으므로 union 접기의 "≥24h 정지 → 1440분 전체 채움" 퇴화가 없다
/// (주말 정지는 해당 날짜 행들이 꽉 차는 것이 날짜별 뷰에선 정확한 표현). Date = 로컬 날짜(자정).
/// </summary>
public sealed record PlannedStopDayDto(
    DateTime Date,
    IReadOnlyList<PlannedStopWindowDto> Windows,
    IReadOnlyList<PlannedStopWindowDto> UnmeasuredWindows);

/// <summary>정지 이벤트 로그 한 건 (필터 조회용). 시각은 로컬 변환된 DateTime.</summary>
public sealed record OeeDowntimeDto(
    long Id,
    string SystemName,
    string? FlowName,
    string? DeviceName,
    DateTime StartAt,
    DateTime? EndAt,
    long? DurationMs,
    string? ReasonCode,
    string? Category,
    bool IsFailure,
    string DetectSource,              // 감지 출처(정지 구간 소스): nocycle / usertag / manual
    long? SourceLogId,
    string? Note,
    string Status,                    // "open" | "recovered"
    string? ClassifySource = null,    // 분류 출처: manual / auto-bit / auto-heuristic / null(미분류)
    OeeDowntimeClue? Clue = null);    // abnormal/usertag 시간겹침 단서(표시 전용 — 건수·MTBF 미반영, doc/21 §4)

/// <summary>
/// 정지 구간에 시간이 겹친 abnormal/usertag 점 이벤트 단서 (읽기전용 표시 — 정지 소스 아님).
/// 건수·길이·MTBF 산출에 절대 반영하지 않는다(doc/21 §4 정직성).
/// </summary>
public sealed record OeeDowntimeClue(
    string Label,
    string Src);                      // "abnormal" | "usertag"

/// <summary>설비(Flow)별 OEE 순위 한 행.</summary>
public sealed record OeeRankingDto(
    string FlowName,
    long DowntimeMs,
    int DowntimeCount,
    int? TotalCount,
    double? Availability,
    double? Performance,
    double? Quality,
    double? Oee);

/// <summary>일자별/시간별 가동·정지·점검 버킷 (API 응답 컨테이너).</summary>
public sealed record OeeDailyResponse(
    string Granularity,                  // "day" | "hour"
    IReadOnlyList<OeeDailySlotDto> Slots);

/// <summary>
/// 단일 버킷: Slot 문자열·슬롯 지속시간 + 정지 분해(가동/고장/기타/미분류/점검/비생산).
/// 가동 = SlotMs − FailureMs − OtherMs − UnclassifiedMs − PlannedMs − NonProdMs. 분해는 상호배타(정지 이벤트 = category·isFailure 분기):
///   PlannedMs=category 'planned' / UnclassifiedMs=category NULL / FailureMs=isFailure 1 / OtherMs=그 외 unplanned.
/// NonProdMs=비생산(사이클 10×CT/수동 시각대 — A 분모 밖) 을 가동(초록)에서 카빙한 시간. UnplannedMs=하위호환 합산.
/// </summary>
public sealed record OeeDailySlotDto(
    string Slot,            // "yyyy-MM-dd" 또는 "yyyy-MM-dd HH:00"
    long SlotMs,            // 슬롯 달력 지속시간 (ms)
    long UnplannedMs,       // 비계획 정지 합 (Failure+Other+Unclassified) — 하위호환
    long PlannedMs,         // 계획정비 (category = 'planned')
    long FailureMs = 0,     // 고장 (isFailure=1, 비계획)
    long OtherMs = 0,       // 기타 비계획 (category='unplanned' AND isFailure=0)
    long UnclassifiedMs = 0, // 미분류 (category IS NULL)
    long NonProdMs = 0,     // 비생산(제외) — 가동에서 카빙, A 분모 밖 (사이클 10×CT / 수동 시각대)
    long UnmeasuredMs = 0); // 미계측(수신 공백, §3.4) — 최우선 카빙(모르는 시간은 어떤 상태도 주장 안 함)
