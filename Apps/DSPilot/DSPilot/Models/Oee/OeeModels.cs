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

    /// <summary>'nocycle' / 'usertag' / 'manual'.</summary>
    public string DetectSource { get; set; } = "nocycle";

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
/// OEE 6지표 + 구성요소. 산출 불가 항목은 null + 사유(*Note) 정직 표기 (doc/21 §10).
/// availability = 달력근사(Phase1), performance = idealCT 기반(미설정 시 null),
/// quality = good/total(reject 입력 시), mtbf/mttr = isFailure 이벤트 기반.
/// </summary>
public sealed record OeeSummaryDto(
    string? FlowName,                 // null = 전체(라인 합산)
    DateTime FromUtc,
    DateTime ToUtc,
    double PeriodMs,                  // 달력 기간 (to - from)
    long DowntimeMs,                  // 기간 내 정지 합(ms)
    int DowntimeCount,                // 기간 내 정지 건수
    int? TotalCount,                  // dspFlowHistory row count (자동)
    int? RejectCount,                 // 수동 입력 합 (없으면 null)
    int? GoodCount,                   // total - reject (둘 다 있을 때)
    int? IdealCycleTimeMs,            // FlowCycleOverride.IdealCycleTimeMs

    double? Availability,             // 1 - downtime/period (달력근사)
    string? AvailabilityNote,         // 산출 방식/한계 사유
    double? Performance,              // (idealCT*total)/runtime, min(1.0). idealCT 없으면 null
    string? PerformanceNote,
    double? Quality,                  // good/total. reject 미입력 시 null
    string? QualityNote,
    double? Oee,                      // A*P*Q. 한 요소라도 null 이면 null
    string? OeeNote,

    int FailureCount,                 // isFailure=1 이벤트 건수
    double? Mtbf,                     // Σ runtime / 고장건수 (ms)
    string? MtbfNote,
    double? Mttr,                     // Σ 고장 durationMs / 고장건수 (ms)
    string? MttrNote);

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
    string DetectSource,
    long? SourceLogId,
    string? Note,
    string Status);                   // "open" | "recovered"

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

/// <summary>단일 버킷: Slot 문자열·슬롯 지속시간·비계획정지·계획정비.</summary>
public sealed record OeeDailySlotDto(
    string Slot,        // "yyyy-MM-dd" 또는 "yyyy-MM-dd HH:00"
    long SlotMs,        // 슬롯 달력 지속시간 (ms) — 가동 = SlotMs - UnplannedMs - PlannedMs
    long UnplannedMs,   // 비계획 정지 (category != 'planned')
    long PlannedMs);    // 계획정비 (category = 'planned')
