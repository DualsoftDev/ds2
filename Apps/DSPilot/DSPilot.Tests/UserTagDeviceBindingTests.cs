// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System;
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

    [Theory]
    [InlineData(UserTagEditorSupport.LevelAlarm)]
    [InlineData(UserTagEditorSupport.LevelMonitor)]
    public void 내보낸_CSV_는_UTF8_BOM_으로_시작한다(string level)
    {
        // BOM 이 없으면 한글 Windows 의 Excel 이 CP949 로 열어 헤더("이름","로그 레벨"...)부터 깨진다.
        // Encoding.GetBytes 가 preamble 을 붙여 주지 않는 것이 예전 원인이었다.
        var row = new UtEditorTagDto(
            SystemId: Guid.NewGuid().ToString(), SystemName: "Line1", Name: "펌프압력", TagAddress: "D200",
            ValueType: "Real", MatchOp: "Changed", MatchValue: "", Level: level);

        foreach (var csv in new[]
                 {
                     UserTagEditorSupport.BuildCsv([row], includeExample: false, level: level),
                     UserTagEditorSupport.BuildCsv([], includeExample: true, level: level),
                 })
        {
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csv[..3]);
            // BOM 을 붙여도 자기 파일을 도로 읽을 수 있어야 한다(왕복).
            Assert.Equal("utf-8(BOM)", UserTagEditorSupport.ParseCsv(csv, level).Encoding);
        }
    }

    [Fact]
    public void 이상알람_양식_헤더는_앞_7칸을_그대로_둔다()
    {
        // Promaker 6컬럼 파서와 열 위치가 어긋나면 기존 현장 파일이 통째로 깨진다.
        Assert.Equal(UserTagEditorSupport.CsvHeader, UserTagEditorSupport.CsvHeaderAlarm[..UserTagEditorSupport.CsvHeader.Length]);
        Assert.Equal("디바이스", UserTagEditorSupport.CsvHeaderAlarm[^1]);
    }

    // ── 신호 기준 단일화 (2026-09-25) ────────────────────────────────────
    // 키는 (엔드포인트, 주소) 하나다. 이름·GUID 는 엔드포인트가 빈 옛 매핑을 채우는 1회 백필에만 쓴다.
    // 근거: 나흘 사이 System 이름이 두 번 바뀌었고(ub1_#121_#134 → UB_#121_#134 → UB_121_134),
    // GUID 는 프로젝트를 다시 만들 때마다 바뀐다. 엔드포인트만 그대로였다.

    private const string EpA = "192.168.0.10:2004";
    private const string EpB = "192.168.0.11:2004";
    private const string IdA = "11111111-1111-1111-1111-111111111111";

    private static UserTagDeviceBinding BE(string endpoint, string address, string device) =>
        new() { Endpoint = endpoint, TagAddress = address, Device = device };

    [Fact]
    public void 이름과_GUID_가_바뀌어도_엔드포인트로_찾는다()
    {
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([BE(EpA, "%MW147.8", "R121-2")]);

        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, EpA, "%MW147.8", out var d));
        Assert.Equal("R121-2", d);
    }

    [Fact]
    public void 엔드포인트가_다르면_같은_주소라도_다른_신호다()
    {
        // 실측: 알람 주소 149개 중 18개(12%)가 두 PLC 에 함께 있었다(%MW7000.15 등).
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex(
            [BE(EpA, "%MW7000.15", "R121-2"), BE(EpB, "%MW7000.15", "셔틀")]);

        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, EpA, "%MW7000.15", out var a));
        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, EpB, "%MW7000.15", out var b));
        Assert.Equal("R121-2", a);
        Assert.Equal("셔틀", b);
    }

    [Fact]
    public void 엔드포인트가_빈_옛_매핑은_GUID_로_백필한다()
    {
        var old = new UserTagDeviceBinding { System = "옛이름", SystemId = IdA, TagAddress = "%MW147.8", Device = "R121-2" };
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([old], [(IdA, "UB_121_134", EpA)]);

        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, EpA, "%MW147.8", out var d));
        Assert.Equal("R121-2", d);
        Assert.Equal(0, index.DeadBindingCount);
    }

    [Fact]
    public void GUID_가_안_맞으면_이름으로_백필한다()
    {
        // 프로젝트를 다시 만들면 GUID 가 통째로 바뀐다 — 이름이 마지막 단서다.
        var old = new UserTagDeviceBinding { System = "UB_121_134", TagAddress = "%MW147.8", Device = "R121-2" };
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([old], [("22222222-2222-2222-2222-222222222222", "UB_121_134", EpA)]);

        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, EpA, "%MW147.8", out var d));
        Assert.Equal("R121-2", d);
    }

    [Fact]
    public void 백필에_실패한_매핑은_죽은_것으로_센다()
    {
        // 그 System 이 모델에서 사라졌다 — 조용히 버리지 않고 커버리지로 알린다.
        var old = new UserTagDeviceBinding { System = "사라진시스템", TagAddress = "%MW147.8", Device = "R121-2" };
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([old], [(IdA, "UB_121_134", EpA)]);

        Assert.Equal(1, index.DeadBindingCount);
        Assert.False(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, EpA, "%MW147.8", out _));
    }

    [Fact]
    public void 엔드포인트가_없는_알람은_잇지_않는다()
    {
        // 2026-09-22 이전 행. 그 시절 표식은 이름·GUID 뿐인데 둘 다 그 뒤로 바뀌었다.
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([BE(EpA, "%MW147.8", "R121-2")]);

        Assert.False(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, null, "%MW147.8", out _));
        Assert.False(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, "  ", "%MW147.8", out _));
    }

    [Fact]
    public void 엔드포인트_표기는_공백과_대소문자를_무시한다()
    {
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([BE(" " + EpA + " ", "M901", "Conveyor1")]);
        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, EpA.ToUpperInvariant(), "M901", out var d));
        Assert.Equal("Conveyor1", d);
    }

    [Fact]
    public void 미지정과_전역은_여전히_다르게_읽힌다()
    {
        var index = AbnormalDeviceFilterHelpers.BuildUserTagDeviceIndex([BE(EpA, "M950", "")]);

        Assert.True(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, EpA, "M950", out var global));
        Assert.Equal(string.Empty, global);
        Assert.False(AbnormalDeviceFilterHelpers.TryGetBoundDevice(index, EpA, "M901", out _));
    }
}
