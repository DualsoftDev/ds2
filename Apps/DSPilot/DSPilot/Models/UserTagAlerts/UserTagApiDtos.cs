// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Models.UserTagAlerts;

// 격리형 호스팅 UserTag(이상발생 관리) API DTO. 전역 camelCase 정책으로 직렬화.
// 시각은 서버 로컬(=DB/표시 tz)로 미리 변환해 내려보내 클라이언트 이중변환을 피한다.

public record UserTagSnapshotDto(
    string PeriodPreset,
    string PeriodStartLocal,
    string PeriodEndLocal,
    string Granularity,
    string BucketLabel,
    int TotalCount,
    int Page,
    int MaxPage,
    int PageSize,
    List<UtAlertDto> Alerts,
    List<UtBucketDto> Buckets,
    List<UtTopDto> TopRows,
    Dictionary<string, int> LevelCounts,   // 키(Info/Warning/Error)는 그대로 — DictionaryKeyPolicy 미설정
    int ActiveErrorCount,
    int TodayErrorCount,
    string? LastAlertAtLocal,
    List<UtDefinitionDto> Definitions,
    List<string> SystemOptions,
    // 이상 띠(진행중/오늘 최근) 세로 티커 전환 간격(초) — 서버설정 Ui.AlarmTickerIntervalSec.
    // 대시보드 알람 배너와 동일 속도를 쓰도록 클라가 스냅샷에서 읽어 사용한다.
    int AlarmTickerIntervalSec = 3);

public record UtAlertDto(
    string OccurredAtLocal,   // "yyyy-MM-dd HH:mm:ss.fff" (테이블은 앞 19자, CSV 는 전체)
    string LogLevel,
    string SystemName,
    string Name,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string? MatchValue,
    string ActualValue);

public record UtBucketDto(string BucketStartIso, string Level, int Count);

public record UtTopDto(string Name, string Level, int Count);

public record UtDefinitionDto(
    string SystemName,
    string Name,
    string LogLevel,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string? MatchValue);

/// <summary>
/// 대시보드 이상(Error) 배너 전용 경량 상태 — 5초 폴링에 적합하게 카운트 2건 + 최신 Error 1건만 담는다.
/// (snapshot 의 8개 쿼리를 띄우지 않으려고 별도 분리.)
/// LatestErrorId 는 배너 "닫기 후 새 Error 발생 시 재등장" 판정 키(클라이언트가 닫은 id 와 비교).
/// </summary>
public record UserTagErrorStatusDto(
    int ActiveErrorCount,           // 최근 10분 Error (nav/summary anomalyActiveCount 와 동일 정의)
    int TodayErrorCount,            // 오늘(로컬 자정~) Error
    long? LatestErrorId,            // 활성 창 최신 Error 의 id (없으면 null)
    string? LatestErrorAtLocal,     // "MM-dd HH:mm:ss" (로컬)
    string? LatestErrorSystem,
    string? LatestErrorName);
