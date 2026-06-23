// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Models.Dashboard;

// 격리형 호스팅 Dashboard API 전송 DTO.
// 도메인 타입을 그대로 직렬화하지 않는 이유:
//  - DspDbSnapshot.CallsByFlow 는 순환 가능 dict (불필요)
//  - BlueprintLayout.CellWidth/CellHeight 는 [JsonIgnore] 계산 속성인데 클라이언트가 필요로 함 → DTO 로 노출
// 전역 camelCase 정책(Program.cs) 이 속성명을 변환한다. (MT→mt, AvgMT→avgMT, ColSpan→colSpan ...)

public record DashboardSnapshotDto(
    List<FlowStateDto> Flows,
    LayoutDto Layout,
    bool HasData,
    DateTimeOffset Timestamp,
    // 이상 알람 배너 세로 티커 전환 간격(초) — 서버설정 Ui.AlarmTickerIntervalSec. 클라가 스냅샷에서 읽어 사용.
    int AlarmTickerIntervalSec = 3);

public record FlowStateDto(
    string FlowName,
    string State,
    int? MT,
    int? WT,
    int? CT,
    double? AvgMT,
    double? AvgWT,
    double? AvgCT,
    string? MovingStartName,
    string? MovingEndName);

// 자유 배치 모델: 격자(Grid/Offset/Cell/Col/Row/Span) 폐지. 카드 = 공통 크기(CardScale) + 중심좌표 X/Y(0..1).
public record LayoutDto(
    int CanvasWidth,
    int CanvasHeight,
    double CardScale,
    string? BlueprintImagePath,
    long ImageVersion,
    List<FlowPlacementDto> FlowPlacements,
    List<FlowOrderDto> FlowProcessOrder);

public record FlowPlacementDto(
    string FlowName,
    System.Guid SystemId,
    double X,
    double Y);

public record FlowOrderDto(string FlowName);

public record FlowHistoryDto(
    int? CycleNo,
    int? MT,
    int? WT,
    int? CT,
    System.DateTime RecordedAt,
    bool IsIdle);

// 시프트 운영 — 서버 공유 설정 + 실시간 진행(만든 수).
// Start/End 는 로컬 "HH:mm". MadeCount = 현재 시프트 시작 이후 만든 수
//   · TargetWork 설정 시 → 그 Work 의 완료(InTag↑ rising edge) 횟수
//   · TargetWork 미설정(Flow 만) 시 → TargetFlow 의 완료(비가동 제외) 사이클 수 (구버전 폴백)
// (서버에서 시프트 윈도우 해석 후 집계). 미설정(TargetFlow 빈값)이면 0.
public record ShiftDto(
    string Start,
    string End,
    string ShiftType,
    string? TargetFlow,
    string? TargetWork,
    int TargetCount,
    int MadeCount);

// POST 본문 — TargetCount 음수는 서버에서 0 으로 정규화.
public record ShiftSaveDto(
    string? Start,
    string? End,
    string? ShiftType,
    string? TargetFlow,
    string? TargetWork,
    int TargetCount);

// 히스토리 이상치 제외 필터 — Flow별 최소·최대 CT 범위(초). 서버(appsettings) 공유.
// CT 가 [MinSec, MaxSec] 밖이면 제외. MinSec/MaxSec 가 null 이면 해당 방향 제한 없음.
public record CycleExclusionDto(
    string FlowName,
    double? MinSec,
    double? MaxSec);

// POST 본문 — FlowName 필수. MinSec/MaxSec 둘 다 null 이면 해당 Flow 제외 해제.
public record CycleExclusionSaveDto(
    string? FlowName,
    double? MinSec,
    double? MaxSec);

// v12 경로이탈 이상감지(4종) 1건 — 엔진 AbnormalRecord(Ds2.Core)를 UI/SignalR 용으로 평탄화.
//   Kind/KindName : AbnormalKind int 값(0..3) + enum 이름(SensorOpen/SensorShort/ActionOver/ActionUnder)
//   Label/Level   : DSPilot severity 정책 적용본(한글 라벨 + Error/Warning/Info — shell.js LEVEL_COLOR 와 동일)
//   Source        : NavController '이상코드' 사이드바 피드 합류용 형상("ds-error-0".."ds-error-3")
//   FlowName/WorkName/CallName : Target.CallId → DsProjectService.GetCallPath 로 모델상 실제 이름 해석(미해석 시 빈 문자열)
//   SystemName    : FlowName 소속 시스템(AASX DsProjectService 로 해석, 미로드 시 빈 문자열)
//   ElapsedMs     : Action* 에만(동작 소요 ms), Observed : Sensor* 에만(관측 상태)
//   SensorTag     : Sensor* 에만 — 이상 감지 트리거가 된 실제 InTag PLC 주소 (PlcToCallMapperService 해석, 미해석 시 null)
//   CallName      : "{DevicesAlias}.{ApiName}" — 경로(FLOW/WORK/CALL) 마지막 칸. 대상 디바이스 포함.
/// <summary>N일 히스토리 기반 평균 MT/WT/CT (비가동 사이클 제외).</summary>
public record FlowAverageDto(
    string FlowName,
    double? AvgMT,
    double? AvgWT,
    double? AvgCT,
    int SampleCount);

public record AbnormalEventDto(
    int Kind,
    string KindName,
    string Label,
    string Level,
    string Source,
    string FlowName,
    string WorkName,
    string SystemName,
    int? ElapsedMs,
    bool? Observed,
    System.DateTime OccurredAtUtc,
    string OccurredAtLocal,
    string? SensorTag = null,
    string CallName = "");
