// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using ClosedXML.Excel;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 이상·알람 Excel 내보내기(저장하기)에 해소 기록이 함께 실리는지 — 화면 목록의 "해소" 칸과 같은 원본(clearedAt).
/// 수동등록TAG: 해소되면 '해소'+시각+지속(초), 아직이면 '진행 중'. 자동감지(Abnormal)는 점 이벤트라 '—'.
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
}
