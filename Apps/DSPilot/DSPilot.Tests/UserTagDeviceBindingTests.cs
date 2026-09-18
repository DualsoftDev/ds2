// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Text;
using DSPilot.Models;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 이상알람TAG → 디바이스 귀속(<see cref="AbnormalAlarmSettings.UserTagDeviceBindings"/>) 규칙.
/// <para>
/// 이 귀속이 MTBF/MTTR 회복 게이트가 볼 flow 집합을 정하므로, 여기서 조용히 어긋나면 지표가 통째로
/// 틀어진다. 특히 <b>미지정(항목 없음)</b> 과 <b>전역(빈 문자열)</b> 의 구분이 핵심이다 — 둘 다 지표에서
/// 빠지지만 전자는 "아직 안 묶음"(커버리지가 세는 대상), 후자는 "묶을 수 없음"(셀 필요 없음)이다.
/// </para>
/// </summary>
public class UserTagDeviceBindingTests
{
    private static UserTagDeviceBinding B(string system, string address, string device) =>
        new() { System = system, TagAddress = address, Device = device };

    [Fact]
    public void 미지정과_전역은_다르게_읽힌다()
    {
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([B("Line1", "M950", "")]);

        // 전역 — 항목이 있고 값이 빈 문자열.
        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, "Line1", "M950", out var global));
        Assert.Equal(string.Empty, global);

        // 미지정 — 항목 자체가 없다. 빈 문자열로 접어 버리면 이 구분이 사라진다.
        Assert.False(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, "Line1", "M901", out _));
    }

    [Fact]
    public void 같은_주소라도_System_이_다르면_다른_귀속이다()
    {
        // 멀티 PLC 에서 두 System 이 같은 주소를 정의할 수 있다. 주소만으로 묶으면 한쪽이 다른 쪽을 덮는다
        // (UserTagFilters 가 안고 있는 약점 — 여기서는 반복하지 않는다).
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex(
        [
            B("Line1", "M901", "Conveyor1"),
            B("Line2", "M901", "Press2"),
        ]);

        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, "Line1", "M901", out var d1));
        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, "Line2", "M901", out var d2));
        Assert.Equal("Conveyor1", d1);
        Assert.Equal("Press2", d2);
    }

    [Fact]
    public void System_과_주소는_대소문자를_무시하고_공백을_턴다()
    {
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([B("  Line1 ", " m901 ", " Conveyor1 ")]);

        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, "LINE1", "M901", out var device));
        Assert.Equal("Conveyor1", device);
    }

    [Fact]
    public void 같은_키가_여러_번_오면_뒤엣것이_이긴다()
    {
        // 편집기는 그 System 의 최종 목록을 통째로 보낸다 — 마지막 값이 사용자의 의도다.
        var list = AbnormalDeviceFilterHelpers.NormalizeUserTagDeviceBindings(
        [
            B("Line1", "M901", "Conveyor1"),
            B("Line1", "M901", "Press1"),
        ]);

        var single = Assert.Single(list);
        Assert.Equal("Press1", single.Device);
    }

    [Fact]
    public void 주소가_빈_항목은_버린다()
    {
        var list = AbnormalDeviceFilterHelpers.NormalizeUserTagDeviceBindings(
        [
            B("Line1", "   ", "Conveyor1"),
            B("Line1", "M901", "Conveyor1"),
        ]);

        Assert.Single(list);
        Assert.Equal("M901", list[0].TagAddress);
    }

    [Fact]
    public void 정규화는_System_주소_순으로_정렬한다()
    {
        var list = AbnormalDeviceFilterHelpers.NormalizeUserTagDeviceBindings(
        [
            B("Line2", "M100", "B"),
            B("Line1", "M200", "A"),
            B("Line1", "M100", "A"),
        ]);

        Assert.Collection(list,
            b => { Assert.Equal("Line1", b.System); Assert.Equal("M100", b.TagAddress); },
            b => { Assert.Equal("Line1", b.System); Assert.Equal("M200", b.TagAddress); },
            b => { Assert.Equal("Line2", b.System); Assert.Equal("M100", b.TagAddress); });
    }

    [Fact]
    public void 빈_색인과_빈_주소는_미지정으로_떨어진다()
    {
        Assert.False(AbnormalDeviceFilterHelpers.TryGetBoundDevice(null, "Line1", "M901", out _));
        Assert.False(AbnormalDeviceFilterHelpers.TryGetBoundDevice(
            AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([]), "Line1", "M901", out _));

        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([B("Line1", "M901", "Conveyor1")]);
        Assert.False(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, "Line1", "  ", out _));
    }

    [Fact]
    public void System_이_비어도_주소만으로_귀속이_선다()
    {
        // System 이름을 못 얻는 경로(구 데이터·단일 PLC 현장)에서도 태그를 묶을 수 있어야 한다.
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([B("", "M901", "Conveyor1")]);

        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, null, "M901", out var device));
        Assert.Equal("Conveyor1", device);
    }

    // ── CSV 왕복 ──
    // CSV 는 그 System 의 최종 목록이라, 디바이스 칸이 없거나 뭉개지면 교체 가져오기가 귀속을 조용히 지운다.

    [Theory]
    [InlineData("", null)]                      // 빈 칸 = 미지정
    [InlineData("   ", null)]
    [InlineData("*", "")]                       // 센티넬 = 전역
    [InlineData("Conveyor1", "Conveyor1")]
    [InlineData(" Conveyor1 ", "Conveyor1")]
    public void CSV_디바이스_칸은_세_상태로_읽힌다(string cell, string? expected)
    {
        Assert.Equal(expected, UserTagEditorSupport.ParseCsvDevice(cell));
    }

    [Theory]
    [InlineData("Conveyor1")]
    [InlineData("")]        // 전역
    [InlineData(null)]      // 미지정
    public void 이상알람TAG_는_CSV_왕복에서_귀속이_살아남는다(string? device)
    {
        var row = new UtEditorTagDto(
            SystemId: Guid.NewGuid().ToString(), SystemName: "Line1", Name: "모터과부하", TagAddress: "M901",
            ValueType: "Bit", MatchOp: "RisingEdge", MatchValue: "",
            Level: UserTagEditorSupport.LevelAlarm, Device: device);

        var csv = UserTagEditorSupport.BuildCsv([row], includeExample: false, level: UserTagEditorSupport.LevelAlarm);
        var parsed = UserTagEditorSupport.ParseCsv(csv, UserTagEditorSupport.LevelAlarm);

        var back = Assert.Single(parsed.Rows);
        Assert.Null(back.Error);
        Assert.Equal(device, back.Device);
    }

    [Fact]
    public void 모니터링TAG_행의_8열은_단위이지_디바이스가_아니다()
    {
        // 두 양식이 같은 열 번호를 쓰므로, 종류를 헷갈리면 단위가 디바이스로 새어 들어간다.
        var row = new UtEditorTagDto(
            SystemId: Guid.NewGuid().ToString(), SystemName: "Line1", Name: "펌프압력", TagAddress: "D200",
            ValueType: "Real", MatchOp: "Changed", MatchValue: "",
            Level: UserTagEditorSupport.LevelMonitor, Unit: "bar");

        var csv = UserTagEditorSupport.BuildCsv([row], includeExample: false, level: UserTagEditorSupport.LevelMonitor);
        var back = Assert.Single(UserTagEditorSupport.ParseCsv(csv, UserTagEditorSupport.LevelMonitor).Rows);

        Assert.Equal("bar", back.Unit);
        Assert.Null(back.Device);
    }

    [Fact]
    public void Promaker_6컬럼_파일은_디바이스_없이도_읽힌다()
    {
        // 앞 7칸 위치를 지켰으므로 디바이스 칸이 없는 파일도 그대로 들어와야 한다(미지정으로).
        var csv = new UTF8Encoding(true).GetBytes("이름,로그 레벨,태그 주소,값 타입,매칭 조건,기준값\n모터과부하,Error,M901,Bit,RisingEdge,\n");

        var back = Assert.Single(UserTagEditorSupport.ParseCsv(csv, UserTagEditorSupport.LevelAlarm).Rows);
        Assert.Null(back.Error);
        Assert.Equal("M901", back.TagAddress);
        Assert.Null(back.Device);
    }

    [Fact]
    public void 이상알람_양식_헤더는_앞_7칸을_그대로_둔다()
    {
        // Promaker 6컬럼 파서와 열 위치가 어긋나면 기존 현장 파일이 통째로 깨진다.
        Assert.Equal(UserTagEditorSupport.CsvHeader, UserTagEditorSupport.CsvHeaderAlarm[..UserTagEditorSupport.CsvHeader.Length]);
        Assert.Equal("디바이스", UserTagEditorSupport.CsvHeaderAlarm[^1]);
    }
}
