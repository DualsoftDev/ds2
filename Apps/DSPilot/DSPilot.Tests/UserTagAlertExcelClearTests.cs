// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using ClosedXML.Excel;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Services;
using Xunit;
using static DSPilot.Kpi.ErrorTagReliability;

namespace DSPilot.Tests;

/// <summary>
/// 이상·알람 Excel 내보내기(저장하기)에 해소 기록이 함께 실리는지 — 화면 목록의 "해소" 칸과 같은 원본(clearedAt).
/// 이상알람TAG: 해소되면 '해소'+시각+지속(초), 아직이면 '진행 중'. 자동감지(Abnormal)는 점 이벤트라 '—'.
/// </summary>
public class UserTagAlertExcelClearTests
{
    private static readonly DateTime T = new(2026, 9, 11, 10, 0, 0, DateTimeKind.Local);

    private static UserTagAlertRecord Row(string name, string valueType, DateTime? clearedAt) => new(
        Id: 0, OccurredAt: T, SystemId: Guid.Empty, SystemName: "SYS", Name: name, LogLevel: "Error",
        TagAddress: "%MX10", ValueType: valueType, MatchOp: "RisingEdge", MatchValue: "1",
        ActualValue: "true", SourceLogId: null, ClearedAt: clearedAt);

    [Fact]
    public void Export_carries_state_cleared_time_and_duration()
    {
        var bytes = UserTagAlertExcelExporter.Build(
        [
            Row("해소된 알람", "Bit", T.AddMinutes(2).AddSeconds(30)),
            Row("진행 중 알람", "Bit", null),
            Row("자동감지 이벤트", "Abnormal", null),
        ], T.AddHours(-1), T.AddHours(1), null);

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(1);

        // 헤더(4행) 끝 3칸 = 상태 / 해소 시각 / 지속(초)
        Assert.Equal("상태", ws.Cell(4, 10).GetString());
        Assert.Equal("해소 시각", ws.Cell(4, 11).GetString());
        Assert.Equal("지속(초)", ws.Cell(4, 12).GetString());

        Assert.Equal("해소", ws.Cell(5, 10).GetString());
        Assert.Equal(T.AddMinutes(2).AddSeconds(30).ToString("yyyy-MM-dd HH:mm:ss"), ws.Cell(5, 11).GetString());
        Assert.Equal(150d, ws.Cell(5, 12).GetDouble());

        Assert.Equal("진행 중", ws.Cell(6, 10).GetString());
        Assert.True(ws.Cell(6, 11).IsEmpty() || ws.Cell(6, 11).GetString() == "");
        Assert.True(ws.Cell(6, 12).IsEmpty());

        Assert.Equal("—", ws.Cell(7, 10).GetString());
        Assert.True(ws.Cell(7, 12).IsEmpty());
    }

    // ── 디바이스별 지표 시트 (2026-09-29, doc/31 §8.2) ──────────────────────────

    private static UserTagAlertRecord RowWithEndpoint(string name, DateTime? clearedAt) => new(
        Id: 0, OccurredAt: T, SystemId: Guid.Empty, SystemName: "SYS", Name: name, LogLevel: "Error",
        TagAddress: "%MX10", ValueType: "Bit", MatchOp: "RisingEdge", MatchValue: "1",
        ActualValue: "true", SourceLogId: null, ClearedAt: clearedAt, Endpoint: "10.0.0.1:2004");

    private static ErrorTagReliabilityService.Result FakeReliability()
    {
        var device = new DeviceSummary(
            System: "SYS", Flow: "F1", Device: "D1",
            FaultCount: 3, RecoveredCount: 3, InProgressCount: 0, AwaitingRestartCount: 0, RestartUnconfirmedCount: 0,
            NonStopWarningCount: 1, LinkSnapshotCount: 0, UnknownStopCount: 0,
            OperatingMs: 3_600_000, TotalDownMs: 600_000, EMtbfMs: 1_200_000, EMttrMs: 200_000);
        var totals = new Summary(
            FaultCount: 3, RecoveredCount: 3, InProgressCount: 0, AwaitingRestartCount: 0, RestartUnconfirmedCount: 0,
            NonStopWarningCount: 1, LinkSnapshotCount: 0, UnknownStopCount: 0,
            OperatingMs: 3_600_000, TotalDownMs: 600_000, EMttrMs: 200_000, EMtbfMs: 1_200_000);
        var flow = new ScopeSummary("flow", "F1", totals);
        var sys = new ScopeSummary("system", "SYS", totals);
        var verdict = new ErrorTagReliabilityService.AlertVerdict(
            OccurredAtUtc: T, ClearedAtUtc: T.AddMinutes(2).AddSeconds(30), RestartedAtUtc: T.AddMinutes(5),
            SystemName: "SYS", Name: "해소된 알람", NameAtTime: null, TagAddress: "%MX10", Device: "D1",
            State: RecoveryState.Recovered, Stop: StopVerdict.Stopped, Skip: SkipCause.None,
            EventNo: 1, RepairMs: 300_000, RestartFlow: "F1", Endpoint: "10.0.0.1:2004");
        var bound = new ErrorTagReliabilityService.BoundTag("SYS", "D1", "해소된 알람", "%MX10", "10.0.0.1:2004");
        var idle = new ErrorTagReliabilityService.BoundTag("SYS", "D2", "유휴 태그", "%MX99", "10.0.0.1:2004");
        return new ErrorTagReliabilityService.Result(
            Summary: totals, Flows: [flow], Systems: [sys], Devices: [device], Alerts: [verdict],
            BoundTags: [bound, idle], UnboundTagCount: 2, GlobalTagCount: 0, SkippedChangedCount: 0,
            MultiFlowDeviceCount: 0, LegacyAlertCount: 1, DeadBindingCount: 0, ProjectLoaded: true);
    }

    [Fact]
    public void Export_adds_verdict_columns_and_device_sheets()
    {
        var bytes = UserTagAlertExcelExporter.Build(
        [
            RowWithEndpoint("해소된 알람", T.AddMinutes(2).AddSeconds(30)),
            Row("진행 중 알람", "Bit", null),          // 접속 정보 없음 → 판정 없음
            Row("자동감지 이벤트", "Abnormal", null),
        ], T.AddHours(-1), T.AddHours(1), null, FakeReliability(), system: null, listFiltered: true);

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(3, wb.Worksheets.Count);

        // 시트 1 — 판정 4열이 붙고, 판정된 행만 디바이스·재가동·정지(초)를 갖는다.
        var alarm = wb.Worksheet("알람");
        Assert.Equal("디바이스", alarm.Cell(4, 13).GetString());
        Assert.Equal("판정", alarm.Cell(4, 16).GetString());
        Assert.Equal("D1", alarm.Cell(5, 13).GetString());
        Assert.Equal(T.AddMinutes(5).ToString("yyyy-MM-dd HH:mm:ss"), alarm.Cell(5, 14).GetString());
        Assert.Equal(300d, alarm.Cell(5, 15).GetDouble());
        Assert.Equal("복구 완료", alarm.Cell(5, 16).GetString());
        Assert.Equal("구 데이터(접속 정보 없음)", alarm.Cell(6, 16).GetString());
        Assert.Equal("—", alarm.Cell(7, 16).GetString());

        // 시트 2 — 디바이스 행 + 묶인 에러코드 + 분 단위 지표. 알람 없던 매핑(D2)도 0 건으로 나온다.
        var dev = wb.Worksheet("디바이스별");
        Assert.Contains("등록 에러태그 기준", dev.Cell(1, 1).GetString());
        Assert.Contains("구 데이터(접속 정보 없음) 1", dev.Cell(3, 1).GetString());
        Assert.Contains("적용되지 않습니다", dev.Cell(3, 1).GetString());
        Assert.Equal("eMTBF(분)", dev.Cell(5, 11).GetString());
        Assert.Equal("D1", dev.Cell(6, 2).GetString());
        Assert.Equal("해소된 알람 (%MX10)", dev.Cell(6, 4).GetString());
        Assert.Equal(3, dev.Cell(6, 5).GetDouble());
        Assert.Equal(60d, dev.Cell(6, 10).GetDouble());
        Assert.Equal(20d, dev.Cell(6, 11).GetDouble());
        Assert.Equal(3.3, dev.Cell(6, 12).GetDouble(), 1);
        Assert.Equal("D2", dev.Cell(7, 2).GetString());
        Assert.Equal(0, dev.Cell(7, 5).GetDouble());
        Assert.Equal("—", dev.Cell(7, 11).GetString());
        // 합산 행 — 빈 줄 하나 뒤에 설비 · PLC · 전체.
        Assert.Equal("설비", dev.Cell(9, 1).GetString());
        Assert.Equal("F1", dev.Cell(9, 2).GetString());
        Assert.Equal("PLC", dev.Cell(10, 1).GetString());
        Assert.Equal("전체", dev.Cell(11, 1).GetString());

        // 시트 3 — 매핑 1행 = 태그 1개, 건수는 태그의 것.
        var map = wb.Worksheet("에러코드 매핑");
        Assert.Equal("해소된 알람", map.Cell(5, 3).GetString());
        Assert.Equal(1, map.Cell(5, 6).GetDouble());   // 발생
        Assert.Equal(1, map.Cell(5, 7).GetDouble());   // 해소
        Assert.Equal(1, map.Cell(5, 9).GetDouble());   // 복구 완료
        Assert.Equal("유휴 태그", map.Cell(6, 3).GetString());
        Assert.Equal(0, map.Cell(6, 6).GetDouble());
    }

    [Fact]
    public void Export_without_reliability_keeps_single_sheet()
    {
        var bytes = UserTagAlertExcelExporter.Build([Row("알람", "Bit", null)], T.AddHours(-1), T.AddHours(1), null);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(1, wb.Worksheets.Count);
        Assert.True(wb.Worksheet(1).Cell(5, 16).IsEmpty());
    }
}
