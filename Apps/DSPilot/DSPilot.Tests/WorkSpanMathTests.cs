// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// 신호 → call 구간 → work 구간(doc/30 §2.2 · §2.3 · §3). 비가동 판정의 입력이므로 여기서 시간이
/// 부풀면 정상 사이클이 비가동이 되고, 여기서 사라지면 정지를 놓친다.
/// </summary>
public class WorkSpanMathTests
{
    // ── call 유형 ────────────────────────────────────────────────────────────

    [Fact]
    public void OUT이_없으면_유형_없음_IN_전용_call은_구간을_만들지_않는다()
    {
        Assert.Equal(CallKind.None, WorkSpanMath.KindOf([], [100, 200]));
        Assert.Empty(WorkSpanMath.CallSpans([], [100, 200]));
    }

    [Fact]
    public void IN이_없으면_o_o()
    {
        Assert.Equal(CallKind.OutOnly, WorkSpanMath.KindOf([100, 500], []));
    }

    [Fact]
    public void OUT_절반_이상이_응답을_받으면_o_i()
    {
        // OUT 4개 중 2개가 다음 OUT 전에 IN 을 받음 — 정확히 절반이면 o~i.
        Assert.Equal(CallKind.OutIn, WorkSpanMath.KindOf([100, 200, 300, 400], [150, 350]));
        // 1개만 받으면 o~o.
        Assert.Equal(CallKind.OutOnly, WorkSpanMath.KindOf([100, 200, 300, 400], [150]));
    }

    [Fact]
    public void o_o_call은_OUT_ON구간이_곧_동작구간()
    {
        // R131-1.잠김 · R131-3.용접 형태 — IN 이 OUT 사이에 안 들어와 OUT↑→OUT↓ 로 잡는다.
        var spans = WorkSpanMath.CallSpans([(100, 400), (1000, 1200)], []);
        Assert.Equal(2, spans.Count);
        Assert.Equal(new Span(100, 400), spans[0]);
        Assert.Equal(new Span(1000, 1200), spans[1]);
    }

    [Fact]
    public void o_o_에서_하강을_모르는_열린_구간은_버린다()
    {
        var spans = WorkSpanMath.CallSpans([(100, 400), (1000, 1000)], []);
        Assert.Single(spans);
    }

    [Fact]
    public void o_i_call은_짝짓기_결과다()
    {
        var spans = WorkSpanMath.CallSpans([(100, 900), (500, 950)], [300, 700]);
        Assert.Equal(2, spans.Count);
        Assert.Equal(new Span(100, 300), spans[0]);
        Assert.Equal(new Span(500, 700), spans[1]);
    }

    // ── 짝짓기 ───────────────────────────────────────────────────────────────

    [Fact]
    public void OUT_다음_첫_IN_에_짝지어진다()
    {
        var spans = WorkSpanMath.Pair([100, 500], [300, 700]);
        Assert.Equal(2, spans.Count);
        Assert.Equal(new Span(100, 300), spans[0]);
        Assert.Equal(new Span(500, 700), spans[1]);
    }

    [Fact]
    public void 뒤따르는_IN_은_무시된다()
    {
        // 한 동작에 응답이 여러 번 튀어도 첫 응답이 완료 시점 — #131 에서 3,664 짝에 379개가 이렇게 버려졌다.
        var spans = WorkSpanMath.Pair([100, 1000], [300, 400, 450, 1200]);
        Assert.Equal(2, spans.Count);
        Assert.Equal(new Span(100, 300), spans[0]);
        Assert.Equal(new Span(1000, 1200), spans[1]);
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
    public void 떨림은_마지막_라이징이_이기고_구간이_그만큼만_짧아진다()
    {
        // OUT 이 0.1초 간격으로 세 번 튀고 30초 뒤 IN — 채터 필터 없이도 짝짓기가 방어한다(doc/30 §15-②).
        var spans = WorkSpanMath.Pair([100, 200, 300], [30_000]);
        var only = Assert.Single(spans);
        Assert.Equal(new Span(300, 30_000), only);
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

    // ── work 구간 ────────────────────────────────────────────────────────────

    [Fact]
    public void work_구간은_call_구간들의_최소시작_최대끝이다()
    {
        // 겹치든 떨어져 있든 더하지 않는다 — 시간선의 시작과 끝만(doc/30 §2.3).
        var env = WorkSpanMath.Envelope([new Span(100, 400), new Span(300, 900), new Span(2000, 2500)]);
        Assert.Equal(new Span(100, 2500), env);
        Assert.Equal(2400, env!.Value.Length);   // 단순합이면 300+600+500=1400 — 다르다
    }

    [Fact]
    public void 구간이_없으면_work_구간도_없다()
    {
        Assert.Null(WorkSpanMath.Envelope([]));
        Assert.Null(WorkSpanMath.Envelope([new Span(100, 100)]));
    }

    [Fact]
    public void 겹치는_call_은_합집합이라_두_번_세지_않는다()
    {
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
        Assert.Equal(1500, WorkSpanMath.OverlapMs(spans, 500, 3000));
        Assert.Equal(0, WorkSpanMath.OverlapMs(spans, 6000, 7000));
    }

    [Fact]
    public void 길이_0_구간은_버린다()
    {
        Assert.Empty(WorkSpanMath.Union([new Span(100, 100)]));
        Assert.Empty(WorkSpanMath.Pair([100], [100]));
    }

    // ── 경계 스냅 ────────────────────────────────────────────────────────────

    [Fact]
    public void 경계_직전_한_스캔_안의_시작은_다음_사이클_것이다()
    {
        // #131 형제 unlock — head 보다 2~4ms 먼저 뜬다(doc/30 §15-③).
        long[] heads = [10_000, 20_000, 30_000];
        Assert.Equal(20_000, WorkSpanMath.Snap(19_997, heads, 100));
        Assert.Equal(20_000, WorkSpanMath.Snap(19_900, heads, 100));
    }

    [Fact]
    public void 스냅_범위_밖이거나_경계_이후면_그대로()
    {
        long[] heads = [10_000, 20_000];
        Assert.Equal(19_899, WorkSpanMath.Snap(19_899, heads, 100));   // 101ms 전 — 안 옮김
        Assert.Equal(20_001, WorkSpanMath.Snap(20_001, heads, 100));   // 경계 뒤 — 이미 다음 사이클
        Assert.Equal(20_000, WorkSpanMath.Snap(20_000, heads, 100));   // 경계 위 — 그대로 그 경계
        Assert.Equal(25_000, WorkSpanMath.Snap(25_000, heads, 100));   // 다음 경계 없음
    }

    [Fact]
    public void 스냅_0이면_아무것도_옮기지_않는다()
    {
        Assert.Equal(19_999, WorkSpanMath.Snap(19_999, [20_000], 0));
    }
}
