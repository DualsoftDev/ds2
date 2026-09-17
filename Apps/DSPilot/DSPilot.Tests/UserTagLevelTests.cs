// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Text;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// UserTag 의 <b>종류 축 = 로그 레벨</b> 규약(2026-09-17). Error = 이상알람TAG, Info = 모니터링TAG.
///
/// <para>여기서 지키는 것은 "레벨이 왕복하는가" 하나다. 종전 코드는 레벨 칸을 읽되 무시하고 저장 때 Error 로
/// 덮었는데, 그 상태로 화면을 둘로 나누면 모니터링TAG 가 저장하는 순간 알람으로 바뀐다. 조용히 일어나는
/// 사고라 회귀로 잡아 둔다.</para>
/// </summary>
public class UserTagLevelTests
{
    [Theory]
    [InlineData("Info", "Info")]
    [InlineData("info", "Info")]
    [InlineData("Error", "Error")]
    [InlineData("Warning", "Error")]   // 두 앱 모두 쓴 적이 없다 — 알람 쪽으로 모은다
    [InlineData("", "Error")]          // 빈 값·미일치를 Info 로 떨어뜨리면 알람이 조용히 사라진다
    [InlineData(null, "Error")]
    [InlineData("헛소리", "Error")]
    public void 레벨_정규화는_Info_만_모니터링으로_본다(string? raw, string expected)
        => Assert.Equal(expected, UserTagEditorSupport.NormalizeLevel(raw));

    [Fact]
    public void 모니터링TAG_는_매칭조건을_검증하지_않고_Changed_로_고정된다()
    {
        // 기준값이 필요한 조건(Gte)을 기준값 없이 줘도 모니터링TAG 는 통과해야 한다 — 조건을 쓰지 않으므로.
        var (entry, err) = UserTagEditorSupport.Normalize(
            "펌프압력", "%MW100", "Real", "Gte", matchValue: "", level: "Info");

        Assert.Null(err);
        Assert.NotNull(entry);
        Assert.Equal("Info", entry!.Level);
        Assert.Equal("Changed", entry.MatchOp);
        Assert.Equal(string.Empty, entry.MatchValue);
    }

    [Fact]
    public void 이상알람TAG_는_기준값_없는_비교조건을_거부한다()
    {
        var (entry, err) = UserTagEditorSupport.Normalize(
            "고온경보", "D100", "Word", "Gte", matchValue: "", level: "Error");

        Assert.Null(entry);
        Assert.NotNull(err);
    }

    [Fact]
    public void 값타입은_종류와_무관하다_Word_이상알람도_Bit_모니터링도_된다()
    {
        var (word, wordErr) = UserTagEditorSupport.Normalize(
            "생산량초과", "D100", "Word", "Gte", "1000", "Error");
        Assert.Null(wordErr);
        Assert.Equal("Error", word!.Level);
        Assert.Equal("Word", word.ValueType);

        var (bit, bitErr) = UserTagEditorSupport.Normalize(
            "도어상태", "%MX0.1", "Bit", "RisingEdge", "", "Info");
        Assert.Null(bitErr);
        Assert.Equal("Info", bit!.Level);
        Assert.Equal("Bit", bit.ValueType);
    }

    [Fact]
    public void CSV_는_레벨을_왕복시킨다()
    {
        var rows = new List<UtEditorTagDto>
        {
            new("S1", "Line1", "펌프압력", "%MW100", "Real", "Changed", "", "Info", "bar", 0.5, 1000),
        };
        var csv = Encoding.UTF8.GetString(
            UserTagEditorSupport.BuildCsv(rows, includeExample: false, level: "Info"));

        Assert.Contains("Info", csv);
        Assert.Contains("bar", csv);

        // 되읽기 — 파일에 적힌 레벨을 따른다(탭 지정 없음).
        var parsed = UserTagEditorSupport.ParseCsv(Encoding.UTF8.GetBytes(csv));
        var row = Assert.Single(parsed.Rows);
        Assert.Null(row.Error);
        Assert.Equal("Info", row.Level);
        Assert.Equal("bar", row.Unit);
        Assert.Equal(0.5, row.Deadband);
        Assert.Equal(1000, row.MinIntervalMs);
        Assert.False(row.LevelAdjusted);
    }

    [Fact]
    public void 탭에서_가져오면_그_탭_레벨로_맞추고_알린다()
    {
        // 이상알람 양식(Error)을 모니터링 탭으로 가져온 상황. 거부하지 않고 맞추되 표시가 남아야 한다.
        var csv = "System,이름,로그 레벨,태그 주소,값 타입,매칭 조건,기준값\n"
                + "Line1,비상정지,Error,%MX0.0,Bit,RisingEdge,\n";
        var parsed = UserTagEditorSupport.ParseCsv(Encoding.UTF8.GetBytes(csv), targetLevel: "Info");

        var row = Assert.Single(parsed.Rows);
        Assert.Equal("Info", row.Level);
        Assert.True(row.LevelAdjusted);
    }

    [Fact]
    public void 구_Promaker_6컬럼_파일도_그대로_읽힌다()
    {
        // System 열이 없는 Promaker 내보내기. 레벨 칸은 살아 있고 메타 3칸은 없다.
        var csv = "이름,로그 레벨,태그 주소,값 타입,매칭 조건,기준값\n"
                + "모터과부하,Error,M901,Bit,RisingEdge,\n";
        var parsed = UserTagEditorSupport.ParseCsv(Encoding.UTF8.GetBytes(csv));

        var row = Assert.Single(parsed.Rows);
        Assert.Null(row.Error);
        Assert.False(parsed.HasSystemColumn);
        Assert.Equal("Error", row.Level);
        Assert.Null(row.Unit);
        Assert.Null(row.Deadband);
    }

    [Theory]
    [InlineData(-1.0, null, false)]
    [InlineData(0.5, 1000, true)]
    [InlineData(null, -5, false)]
    [InlineData(null, 86_400_001, false)]
    [InlineData(null, null, true)]      // 둘 다 없음 = 제한 없음, 정상
    public void 모니터링_메타_검증(double? deadband, int? interval, bool ok)
        => Assert.Equal(ok, UserTagEditorSupport.ValidateMonitorMeta(deadband, interval) is null);
}
