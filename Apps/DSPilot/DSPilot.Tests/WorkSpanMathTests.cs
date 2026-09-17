// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// work 지속시간 산출(doc/30 §1) — call 의 OUT↑→IN↑ 짝짓기와 work 단위 합집합.
/// 비가동 판정의 입력이므로 여기서 시간이 부풀면 정상 사이클이 비가동이 된다.
/// </summary>
public class WorkSpanMathTests
{
    [Fact]
    public void OUT_다음_첫_IN_에_짝지어진다()
    {
        var spans = WorkSpanMath.Pair([100, 500], [300, 700]);
        Assert.Equal(2, spans.Count);
        Assert.Equal(new Span(100, 300), spans[0]);
        Assert.Equal(new Span(500, 700), spans[1]);
    }

    [Fact]
    public void 다음_OUT_이후의_IN_은_짝이_아니다()
    {
        // OUT(100) 의 응답이 오기 전에 다음 OUT(200) 이 오면 앞 OUT 은 버린다 — 신호 누락을 복원하지 않는다.
        var spans = WorkSpanMath.Pair([100, 200], [500]);
        var only = Assert.Single(spans);
        Assert.Equal(new Span(200, 500), only);
    }

    [Fact]
    public void IN_이_없으면_스팬이_없다()
    {
        Assert.Empty(WorkSpanMath.Pair([100, 200], []));
        Assert.Empty(WorkSpanMath.Pair([], [100]));
    }

    [Fact]
    public void OUT_보다_앞선_IN_은_무시된다()
    {
        var spans = WorkSpanMath.Pair([500], [100, 600]);
        var only = Assert.Single(spans);
        Assert.Equal(new Span(500, 600), only);
    }

    [Fact]
    public void 겹치는_call_은_합집합이라_두_번_세지_않는다()
    {
        // 같은 work 의 call 둘이 동시에 돌아도 work 가 움직인 시간은 한 번만 센다.
        var merged = WorkSpanMath.Union([new Span(0, 1000), new Span(500, 1500)]);
        var only = Assert.Single(merged);
        Assert.Equal(new Span(0, 1500), only);
    }

    [Fact]
    public void 맞닿은_구간도_합쳐진다()
    {
        var merged = WorkSpanMath.Union([new Span(0, 1000), new Span(1000, 2000)]);
        Assert.Single(merged);
        Assert.Equal(2000, merged[0].E);
    }

    [Fact]
    public void 떨어진_구간은_따로_남는다()
    {
        var merged = WorkSpanMath.Union([new Span(0, 1000), new Span(2000, 3000)]);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void 사이클_구간과_겹치는_만큼만_센다()
    {
        var spans = WorkSpanMath.Union([new Span(0, 1000), new Span(2000, 5000)]);
        // 사이클 [500, 3000) 과의 겹침 = 500 + 1000
        Assert.Equal(1500, WorkSpanMath.OverlapMs(spans, 500, 3000));
        Assert.Equal(0, WorkSpanMath.OverlapMs(spans, 6000, 7000));
    }

    [Fact]
    public void 길이_0_구간은_버린다()
    {
        Assert.Empty(WorkSpanMath.Union([new Span(100, 100)]));
        Assert.Empty(WorkSpanMath.Pair([100], [100]));
    }
}
