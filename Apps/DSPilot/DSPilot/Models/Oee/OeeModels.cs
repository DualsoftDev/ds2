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
/// 자동 onset(detectSource='nocycle')은 category/reasonCode NULL, isFailure 0 으로 시작.
/// 분류(PATCH)·수동 마감으로만 사람이 채운다.
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
    double PlannedDownMs = 0,         // 계획정지 시간대 비가동(가용성 분모서 제외 — 표준 OEE)
    string? PlannedStopSource = null, // 계획정지 출처: "manual"(사용자 설정) / "auto"(5일 자동감지) / "none"(없음)
    string? PerformanceBasis = null,  // 성능 P 표준CT 기준: "avg"(14일 평균, 기본) / "p10"(클린 최속). CtThresholdMs 가 이 기준값.
    int? CtSampleCount = null,        // CT이상치 산출에 쓰인 클린샘플 수(라인=임계 보유 flow 중 최소). 임계 없으면 null
    bool CtSampleLow = false);        // 클린샘플 < 신뢰선(5) — A·P 는 잠정값(샘플 쌓이면 자동 정상화). UI '샘플 부족' 표시용

/// <summary>계획정지 시간대 한 칸 DTO (반복 일일, 로컬 자정 기준 분).</summary>
public sealed record PlannedStopWindowDto(int StartMinutes, int EndMinutes, string? Label);

/// <summary>
/// 계획정지 시간대 설정 상태 — GET /api/oee/planned-stops 응답.
/// Source="manual"(사용자 설정, Windows 권위) / "auto"(미설정 → AutoSuggested 적용) / "none"(둘 다 없음).
/// Windows = 현재 적용 중. AutoSuggested = 5일 자동감지 미리보기(수동 설정 중에도 참고용). AutoSampleDays = 자동감지 표본일수.
/// </summary>
public sealed record PlannedStopsDto(
    string Source,
    IReadOnlyList<PlannedStopWindowDto> Windows,
    IReadOnlyList<PlannedStopWindowDto> AutoSuggested,
    int AutoSampleDays);

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
/// 단일 버킷: Slot 문자열·슬롯 지속시간 + 정지 5분해(가동/고장/기타/미분류/점검).
/// 가동 = SlotMs − FailureMs − OtherMs − UnclassifiedMs − PlannedMs. 4분해는 상호배타(category·isFailure 로 분기):
///   PlannedMs=category 'planned' / UnclassifiedMs=category NULL / FailureMs=isFailure 1 / OtherMs=그 외 unplanned.
/// UnplannedMs(= Failure+Other+Unclassified) 는 하위호환 합산값.
/// </summary>
public sealed record OeeDailySlotDto(
    string Slot,            // "yyyy-MM-dd" 또는 "yyyy-MM-dd HH:00"
    long SlotMs,            // 슬롯 달력 지속시간 (ms)
    long UnplannedMs,       // 비계획 정지 합 (Failure+Other+Unclassified) — 하위호환
    long PlannedMs,         // 계획정비 (category = 'planned')
    long FailureMs = 0,     // 고장 (isFailure=1, 비계획)
    long OtherMs = 0,       // 기타 비계획 (category='unplanned' AND isFailure=0)
    long UnclassifiedMs = 0); // 미분류 (category IS NULL)
