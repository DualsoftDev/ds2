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
    List<UtTopDto> TopRows,          // 태그별 Top N — 이름(name) 기준(abnormal 은 4개 유형으로 묶임)
    List<UtTopDto> TopRowsByPath,    // 태그별 Top N — 경로(tagAddress) 기준(abnormal 을 경로별로 펼침)
    Dictionary<string, int> CategoryCounts,   // 키 = "ABNORMAL" | "USERTAG" (구분 도넛용) — DictionaryKeyPolicy 미설정
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

// Level 슬롯은 이제 구분(ABNORMAL/USERTAG)을 담는다 — 시계열 스택 막대의 스택 키(레벨 통일 후 구분 스택).
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

// ── 설정▸수동등록TAG 편집기 (/api/user-tags/editor) ──────────────────────────

/// <summary>편집 가능한 활성 System 1건. HasEndpoint=false 면 AID XGT 접속이 없어 새 주소가 Agent 수집 대상에 못 들어간다(UI 경고).</summary>
public record UtEditorSystemDto(string SystemId, string SystemName, bool HasEndpoint, string? Endpoint);

/// <summary>편집기 태그 행 — 정의(UtDefinitionDto)에 SystemId 를 더해 System 단위 교체 저장이 가능하게 한다.</summary>
public record UtEditorTagDto(
    string SystemId,
    string SystemName,
    string Name,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string? MatchValue);

/// <summary>편집기 초기 로드 — System 목록 + 태그 + 허용 값 표. HiddenPassiveCount = Passive System 에 남아 있는(편집 불가) 태그 수.</summary>
public record UtEditorDto(
    List<UtEditorSystemDto> Systems,
    List<UtEditorTagDto> Tags,
    string[] ValueTypes,
    Dictionary<string, string[]> MatchOpsByType,
    int HiddenPassiveCount,
    bool ProjectLoaded);

public record UtEditorTagInput(string? Name, string? TagAddress, string? ValueType, string? MatchOp, string? MatchValue);

/// <summary>System 별 최종 목록(통째 교체). 포함되지 않은 System 은 건드리지 않는다.</summary>
public record UtEditorSystemInput(string SystemId, List<UtEditorTagInput> Tags);

public record UtEditorSaveRequest(List<UtEditorSystemInput> Systems);

/// <summary>Ok=false 면 Error 에 사유(검증 실패 시 Errors 에 항목별). Warnings = 저장은 됐지만 수집 반영 주의.</summary>
public record UtEditorSaveResult(bool Ok, int Applied, List<string> Warnings, List<string> Errors, string? Error);

/// <summary>CSV 한 행 파싱 결과. Error=null 이면 유효(정규화된 값). SystemName 은 System 컬럼이 있을 때만 채워진다.</summary>
public record UtCsvRowDto(
    int Line,
    string SystemName,
    string Name,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string MatchValue,
    string? Error);

public record UtCsvParseResult(List<UtCsvRowDto> Rows, bool HeaderDetected, bool HasSystemColumn, string Encoding);
