// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Models.UserTagAlerts;

/// <summary>
/// userTagAlertLog 한 행 — UI/Repository 공용.
/// </summary>
public sealed record UserTagAlertRecord(
    long Id,
    DateTime OccurredAt,           // UTC
    Guid SystemId,
    string SystemName,
    string Name,
    string LogLevel,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string? MatchValue,
    string ActualValue,
    long? SourceLogId,
    // 해소 시각 — 알람 조건이 풀린 시점(Bit 1→0 등). NULL = 아직 해소되지 않음(진행 중).
    // 조회 경로가 발생/해소를 한 행으로 함께 내려보내므로 목록·Excel 이 "지속시간"을 계산할 수 있다.
    DateTime? ClearedAt = null);

/// <summary>
/// 해소 1건의 지정 키 — 어떤 발생 행에 어느 시각을 찍을지 정확히 지목한다.
/// 주소만으로 묶으면 ①같은 주소를 정의한 다른 System 의 행까지 찍히고 ②재시작으로 남은 과거
/// 미해소 행이 엉뚱한 시각으로 소급 마감된다. 그래서 (주소, System, 발생시각 이후) 3중 한정.
/// </summary>
public sealed record UserTagClearKey(
    string TagAddress,
    string? SystemName,
    DateTime OccurredAt,           // 이 발생 시각 이후의 미해소 행만 대상
    DateTime ClearedAt);           // 조건이 풀린 신호의 시각(폴링 시각이 아니라 PLC 로그 시각)

/// <summary>시계열 버킷 — 차트의 1포인트.</summary>
public sealed record UserTagAlertBucket(
    DateTime BucketStart,          // UTC, 버킷 시작 시각
    string LogLevel,
    int Count);

/// <summary>태그명 별 Top-N 행. LogLevel 슬롯엔 구분(ABNORMAL/USERTAG)이 담긴다 — 막대색 구분용.</summary>
/// <remarks>
/// AltName = 그룹키의 반대편 라벨(경로 기준 집계면 태그 이름들, 이름 기준 집계면 경로들).
/// 같은 경로에 여러 이름이 붙을 수 있어(자동감지 4유형 등) 콤마로 이어 붙인다 — 표시 측에서 축약.
/// </remarks>
public sealed record UserTagAlertTopRow(
    string Name,
    string LogLevel,
    int Count,
    string? AltName = null);
