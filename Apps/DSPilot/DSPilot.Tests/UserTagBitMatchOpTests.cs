// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Linq;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// Bit 의 매칭 조건은 <b>ON(0→1) · OFF(1→0)</b> 둘뿐이다(2026-09-30). 두 상태짜리 신호에 값 비교는 뜻이
/// 겹치고(‘Eq 1’ = ON) 값 변경은 양쪽 엣지를 한꺼번에 잡아 어느 쪽이 알람인지 흐린다.
///
/// <para>줄이는 쪽만 고치면 현장 사고가 난다 — 편집기는 System 의 목록을 <b>통째로 교체</b>하므로, 옛 정의
/// 한 건을 거부하면 그 System 저장 전체가 막힌다. 그래서 (1) 뜻이 같은 Eq/Neq 는 엣지로 옮기고 (2) 대응이
/// 없는 Changed 는 받아만 준다. 이 두 갈래가 회귀 대상이다.</para>
/// </summary>
public class UserTagBitMatchOpTests
{
    [Fact]
    public void 편집기가_Bit_에_제시하는_조건은_ON_OFF_둘뿐이다()
        => Assert.Equal(new[] { "RisingEdge", "FallingEdge" }, UserTagEditorSupport.MatchOpsFor("Bit").AsEnumerable());

    [Fact]
    public void 수치_String_의_조건표는_그대로다()
    {
        Assert.Contains("Changed", UserTagEditorSupport.MatchOpsFor("Word"));
        Assert.Contains("Gte", UserTagEditorSupport.MatchOpsFor("Real"));
        Assert.Contains("Eq", UserTagEditorSupport.MatchOpsFor("String"));
        Assert.DoesNotContain("RisingEdge", UserTagEditorSupport.MatchOpsFor("Word"));
    }

    // F# shouldFire 기준: Bit 의 'Eq 1' 은 값이 1 이 되는 전이 1건 = RisingEdge, 'Eq 0' = FallingEdge.
    [Theory]
    [InlineData("Eq", "1", "RisingEdge")]
    [InlineData("Eq", "true", "RisingEdge")]
    [InlineData("Eq", "0", "FallingEdge")]
    [InlineData("Neq", "0", "RisingEdge")]
    [InlineData("Neq", "1", "FallingEdge")]
    public void 옛_Bit_비교조건은_뜻이_같은_엣지로_바뀌고_기준값은_비워진다(string op, string mv, string expected)
    {
        var (entry, err) = UserTagEditorSupport.Normalize("모터과부하", "M901", "Bit", op, mv);

        Assert.Null(err);
        Assert.Equal(expected, entry!.MatchOp);
        Assert.Equal(string.Empty, entry.MatchValue);   // 엣지에 기준값은 뜻이 없다
    }

    [Fact]
    public void 옛_Bit_값변경_조건은_대응이_없어_그대로_통과한다()
    {
        // 화면 칩에서는 빠졌지만 저장을 막으면 그 System 목록 전체가 저장 불가가 된다.
        var (entry, err) = UserTagEditorSupport.Normalize("구_토글", "M902", "Bit", "Changed", "");

        Assert.Null(err);
        Assert.Equal("Changed", entry!.MatchOp);
    }

    [Fact]
    public void Bit_에_수치비교는_여전히_거부한다()
    {
        var (entry, err) = UserTagEditorSupport.Normalize("모터과부하", "M901", "Bit", "Gte", "1");

        Assert.Null(entry);
        Assert.NotNull(err);
    }

    // 화면이 ON/OFF 로 말하므로 CSV 를 손으로 적는 사람도 그렇게 적는다. 저장 표기는 언제나 표준이다.
    [Theory]
    [InlineData("ON", "RisingEdge")]
    [InlineData("off", "FallingEdge")]
    [InlineData("Rising", "RisingEdge")]
    public void CSV_의_ON_OFF_표기도_읽는다(string raw, string expected)
        => Assert.Equal(expected, UserTagEditorSupport.NormalizeMatchOp(raw, "Bit"));
}
