// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;

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
    // 정의 목록(UtDefinitionDto)은 여기 싣지 않는다 — 응답의 99%(2,933건 555KB)를 차지하면서
    // 화면에서 쓰는 곳이 없었고, OEE 페이지가 사전계산 push 마다 이 스냅샷을 재조회한다.
    // 정의가 필요한 화면은 GET /api/user-tags/definitions 를 따로 호출할 것(2026-09-09).
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
    string ActualValue,
    // 해소(조건 풀림, 예: Bit 1→0) 시각. null = 아직 해소되지 않음(진행 중) 또는 해소 개념이 없는
    // 자동감지(Abnormal) 점 이벤트. DurationMs = 발생→해소 지속시간(표시 SSOT dspFmt.dur 로 포맷).
    string? ClearedAtLocal = null,
    long? DurationMs = null);

// Level 슬롯은 이제 구분(ABNORMAL/USERTAG)을 담는다 — 시계열 스택 막대의 스택 키(레벨 통일 후 구분 스택).
public record UtBucketDto(string BucketStartIso, string Level, int Count);

// Name = 그룹키(경로 기준 집계면 tagAddress), AltName = 반대편 라벨(태그 이름들, 콤마 구분).
// 차트가 "주소 + 이름"을 함께 보여주도록 두 값을 모두 내려보낸다.
public record UtTopDto(string Name, string Level, int Count, string? AltName = null);

/// <summary>
/// 시계열 막대 드릴다운 — 클릭한 버킷 한 칸([BucketStart, 다음 버킷))에 발생한 알람 원본.
/// 버킷 경계는 차트를 그린 서버 집계(GetBucketCountsAsync)와 같은 규칙으로 서버가 계산해 내려준다
/// (클라이언트가 시/일/주/월 경계를 다시 유추하면 DST·주 시작요일에서 어긋난다).
/// </summary>
public record UtBucketDrillDto(
    string BucketStartLocal,   // "yyyy-MM-dd HH:mm"
    string BucketEndLocal,     // "yyyy-MM-dd HH:mm" (배타적 끝)
    string BucketLabel,        // "1시간" | "1일" | ...
    int TotalCount,            // 구간 전체 건수(Limit 로 잘리기 전)
    List<UtAlertDto> Alerts);  // 시간 오름차순, 최대 Limit 건

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

// ── 설정▸사용자 태그 편집기 (/api/user-tags/editor) ──────────────────────────

/// <summary>편집 가능한 활성 System 1건. HasEndpoint=false 면 AID XGT 접속이 없어 새 주소가 Agent 수집 대상에 못 들어간다(UI 경고).</summary>
public record UtEditorSystemDto(string SystemId, string SystemName, bool HasEndpoint, string? Endpoint);

/// <summary>
/// 편집기 태그 행 — 정의(UtDefinitionDto)에 SystemId 를 더해 System 단위 교체 저장이 가능하게 한다.
/// <para>Level = 종류 축. "Error" = 이상알람TAG 탭, "Info" = 모니터링TAG 탭. 한 System 의 행들이
/// 두 레벨 섞여 오고, 탭은 이 값으로 거르는 <b>뷰 필터</b>일 뿐이다(저장은 언제나 두 레벨 전부).</para>
/// </summary>
public record UtEditorTagDto(
    string SystemId,
    string SystemName,
    string Name,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string? MatchValue,
    string Level,
    // 모니터링TAG 전용 표시·수집 메타. 단위는 AID interaction, 데드밴드·간격은 SignalPolicy 에서 온다.
    string? Unit = null,
    double? Deadband = null,
    int? MinIntervalMs = null,
    // 이상알람TAG 전용 귀속. 모니터링 메타의 거울상이며 사는 곳만 다르다 — 이쪽은 AASX 가 아니라
    // DSPilot 설정(AbnormalAlarm.UserTagDeviceBindings)이다. MTBF/MTTR 회복 게이트가 볼 flow 집합을 정한다.
    //   null = 미지정(아직 안 묶음, 지표 제외) · "" = 전역(고의, 역시 지표 제외) · 그 외 = 디바이스 이름
    string? Device = null);

/// <summary>편집기 초기 로드 — System 목록 + 태그 + 허용 값 표. HiddenPassiveCount = Passive System 에 남아 있는(편집 불가) 태그 수.</summary>
public record UtEditorDto(
    List<UtEditorSystemDto> Systems,
    List<UtEditorTagDto> Tags,
    string[] ValueTypes,
    Dictionary<string, string[]> MatchOpsByType,
    int HiddenPassiveCount,
    bool ProjectLoaded,
    // 디바이스 드롭다운 소스 = AASX 모든 Call 의 DevicesAlias(중복 제거·정렬). 귀속에만 남고 모델에서
    // 사라진 이름도 뒤에 붙여 내려보낸다 — 유령이 된 귀속을 화면에서 해제할 수 있어야 하기 때문이다.
    List<string>? Devices = null);

public record UtEditorTagInput(
    string? Name, string? TagAddress, string? ValueType, string? MatchOp, string? MatchValue, string? Level = null,
    string? Unit = null, double? Deadband = null, int? MinIntervalMs = null,
    string? Device = null);

/// <summary>System 별 최종 목록(통째 교체). 포함되지 않은 System 은 건드리지 않는다.</summary>
public record UtEditorSystemInput(string SystemId, List<UtEditorTagInput> Tags);

public record UtEditorSaveRequest(List<UtEditorSystemInput> Systems);

/// <summary>Ok=false 면 Error 에 사유(검증 실패 시 Errors 에 항목별). Warnings = 저장은 됐지만 수집 반영 주의.</summary>
public record UtEditorSaveResult(bool Ok, int Applied, List<string> Warnings, List<string> Errors, string? Error);

/// <summary>
/// CSV 한 행 파싱 결과. Error=null 이면 유효(정규화된 값). SystemName 은 System 컬럼이 있을 때만 채워진다.
/// <para>Level = 이 행이 들어갈 종류. 탭에서 가져오면 그 탭의 레벨로 맞추며, 파일에 적힌 레벨과 달랐던
/// 행은 LevelAdjusted=true 로 표시해 "N 건의 레벨을 맞췄습니다" 안내를 띄운다(조용한 이동 금지).</para>
/// </summary>
public record UtCsvRowDto(
    int Line,
    string SystemName,
    string Name,
    string TagAddress,
    string ValueType,
    string MatchOp,
    string MatchValue,
    string? Error,
    string Level = UserTagEditorSupport.LevelAlarm,
    bool LevelAdjusted = false,
    // 모니터링 양식(열 8~10)에서만 채워진다. 이상알람 행에서는 언제나 null 이다.
    string? Unit = null,
    double? Deadband = null,
    int? MinIntervalMs = null,
    // 이상알람 양식(열 8)에서만 채워진다. null = 미지정 · "" = 전역 · 그 외 = 디바이스 이름.
    string? Device = null);

public record UtCsvParseResult(List<UtCsvRowDto> Rows, bool HeaderDetected, bool HasSystemColumn, string Encoding);

// ── 등록 에러 태그 기반 신뢰성 (/api/user-tags/reliability) — doc/31 ──────────

/// <summary>
/// eMTBF · eMTTR 과 상태 분포. OEE 의 MTBF/MTTR(비가동 기준)과 <b>별개 축</b>이라 값이 다른 것이 정상이다.
/// <para>
/// ★두 지표의 모집단이 다르다 — <paramref name="FaultCount"/>(발생 전체)가 eMTBF 의 분모이고
/// <paramref name="RecoveredCount"/>(복구 완료)가 eMTTR 의 분모다. 화면이 둘을 함께 밝혀야 한다.
/// </para>
/// Ms 값이 null 이면 표본 미달(<paramref name="MinSample"/> 미만) — 숫자 대신 "표본 부족(n=…)" 을 띄운다.
/// </summary>
public record UtReliabilityDto(
    double? EMtbfMs,
    double? EMttrMs,
    int FaultCount,
    int RecoveredCount,
    int InProgressCount,
    int AwaitingRestartCount,
    int RestartUnconfirmedCount,
    // ── 집계에서 빠진 것들(2026-09-22) — 숫자 하나만 내놓고 틀리는 것보다 분해를 보이는 편이 낫다.
    //    실측에서 종전 '고장 34건' 의 82%가 이 셋이었다.
    int NonStopWarningCount,     // 설비가 도는 중에 울린 경고
    int LinkSnapshotCount,       // 통신 재접속 순간의 스냅샷
    int UnknownStopCount,        // 리듬 기준이 없어 정지 여부 판정 불가
    long OperatingMs,            // eMTBF 의 분모 — flow 가동 구간의 합집합(겹친 시간은 한 번만)
    long TotalDownMs,            // 총 정지시간 — 설비 수에 안 흔들려 스코프를 가로질러 비교 가능
    int MinSample,
    // 묶이지 않아 계산에서 빠진 태그 수. 0 이 아니면 화면이 설정으로 유도한다.
    int UnboundTagCount,
    int GlobalTagCount,
    int SkippedChangedCount,
    // 여러 설비에 걸친 디바이스 수. 0 이 아니면 설비 행의 합이 전체보다 크다 — 공유 디바이스가
    // 고장 나면 그걸 쓰는 설비가 전부 서기 때문이다(중복이 아니라 사실).
    int MultiFlowDeviceCount,
    // 엔드포인트가 없어 계산에서 뺀 알람 수 — 2026-09-22 이전에 쌓인 행이다.
    int LegacyAlertCount,
    // 엔드포인트를 끝내 못 채운 매핑 수 — 그 System 이 모델에서 사라졌다.
    int DeadBindingCount,
    bool ProjectLoaded,
    // 설비(flow)·PLC(System) 별 롤업. DSPilot 에 '라인' 개념이 없어 System 이 가장 위 스코프다 —
    // 한 라인이 PLC 두 대로 나뉘기도 하므로(현장 UB) System 이 여럿이면 합산 값은 실체가 없을 수 있다.
    List<UtReliabilityScopeDto> Flows,
    List<UtReliabilityScopeDto> Systems,
    List<UtReliabilityDeviceDto> Devices,
    List<UtReliabilityAlertDto> Alerts);

/// <summary>스코프 1칸(설비 또는 PLC). Kind = "flow" | "system".</summary>
public record UtReliabilityScopeDto(
    string Kind,
    string Name,
    int FaultCount,
    int RecoveredCount,
    int NonStopWarningCount,
    long OperatingMs,
    long TotalDownMs,
    double? EMtbfMs,
    double? EMttrMs);

/// <summary>
/// 디바이스 1대의 지표 — 보전 액션이 붙는 단위다. 라인 값은 이것들을 직렬 합산해 굴려 올린 것이다.
/// </summary>
public record UtReliabilityDeviceDto(
    string SystemName,
    string Device,
    int FaultCount,
    int RecoveredCount,
    int InProgressCount,
    int AwaitingRestartCount,
    int RestartUnconfirmedCount,
    int NonStopWarningCount,
    int LinkSnapshotCount,
    int UnknownStopCount,
    long OperatingMs,
    long TotalDownMs,
    double? EMtbfMs,
    double? EMttrMs);

/// <summary>
/// 알람 1건의 판정. <paramref name="State"/> 는 InProgress · AwaitingRestart · RestartUnconfirmed · Recovered.
/// <paramref name="RestartFlow"/> 는 회복 근거가 된 flow — 분기 우회로 오판이 났을 때 사후 추적의 실마리다.
/// </summary>
public record UtReliabilityAlertDto(
    string OccurredAtLocal,
    string? ClearedAtLocal,
    string? RestartedAtLocal,
    string SystemName,
    // 라벨은 언제나 현재 정의의 이름이다 — 옛 이름의 행이 같은 신호로 모이려면 표시가 하나여야 한다.
    string Name,
    // 발생 당시 박제된 이름. 현재 라벨과 다를 때만 값이 있고, 이름 칸 아래 비고로 붙인다.
    string? NameAtTime,
    string TagAddress,
    string Device,
    string State,
    // 정지 판정 — Stopped(멈춤) · NonStopWarning(안 멈춤) · Unknown(판정 불가). doc/31 §3.1.
    string Stop,
    // 집계에서 빠진 이유 — None(집계 대상) · NonStopWarning · LinkSnapshot · ChangedOp · UnknownStop.
    string Skip,
    // 묶인 사건 번호. 같은 번호 = 같은 정지에서 울린 알람들이라 표에서 접어 보일 수 있다.
    int EventNo,
    long? RepairMs,
    string? RestartFlow);
