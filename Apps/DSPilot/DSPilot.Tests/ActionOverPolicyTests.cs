// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>ActionOver 판정 규칙(DSPilot 소유, 2026-08-24) 회귀 고정.</summary>
public class ActionOverPolicyTests
{
    // ── 임계 산출 ──────────────────────────────────────────────

    [Fact]
    public void 외부가_쓴_밴드에는_여유값을_더한다()
    {
        // Promaker 학습 반영이 덮어쓴 tight 밴드(713ms) → 713 + 5000
        Assert.Equal(5713, ActionOverPolicy.ResolveThresholdMs(713, 5000, marginAlreadyInModel: false));
    }

    [Fact]
    public void DSPilot이_구운_임계에는_더하지_않는다()
    {
        // 실측 보정 버튼이 이미 +5초를 포함해 쓴 값(6070ms) → 그대로. 더하면 11070ms 로 미탐이 된다.
        Assert.Equal(6070, ActionOverPolicy.ResolveThresholdMs(6070, 5000, marginAlreadyInModel: true));
    }

    [Fact]
    public void 모델_Max가_없으면_판정_제외()
    {
        Assert.Equal(0, ActionOverPolicy.ResolveThresholdMs(0, 5000, false));
        Assert.Equal(0, ActionOverPolicy.ResolveThresholdMs(-1, 5000, true));
    }

    [Fact]
    public void 음수_여유값은_0으로_취급()
    {
        Assert.Equal(713, ActionOverPolicy.ResolveThresholdMs(713, -100, false));
    }

    [Fact]
    public void 여유값_0이면_밴드가_곧_임계()
    {
        Assert.Equal(713, ActionOverPolicy.ResolveThresholdMs(713, 0, false));
    }

    // ── 완료대기 판정 ──────────────────────────────────────────

    [Fact]
    public void 임계_초과_전에는_발행하지_않는다()
    {
        Assert.False(ActionOverPolicy.ShouldEmit(startMs: 1000, nowMs: 9000, thresholdMs: 8100, alreadyEmitted: false));
    }

    [Fact]
    public void 임계를_넘기면_발행한다()
    {
        Assert.True(ActionOverPolicy.ShouldEmit(startMs: 1000, nowMs: 9200, thresholdMs: 8100, alreadyEmitted: false));
    }

    [Fact]
    public void 이송_실측_시나리오_명령_조기회수여도_잡는다()
    {
        // 2026-08-24 우진 라인 Conveyor2.MOVE 실측:
        //   10:50:38.663 OUT↑ → 10:50:40.154 OUT↓(IN 없음) → IN 은 10:54:47.600 (4분 5초 뒤).
        // OUT 회수 시점 경과는 1,491ms 로 임계(8,100ms) 미만이라 OUT-falling 경로는 못 잡는다.
        // 시계는 OUT 하강으로 지우지 않으므로, 임계 시점(약 8.1초)에 정상 발행돼야 한다.
        const long outRise = 0;
        const int threshold = 8100;

        Assert.False(ActionOverPolicy.ShouldEmit(outRise, 1491, threshold, false));   // OUT 회수 순간
        Assert.True(ActionOverPolicy.ShouldEmit(outRise, 8101, threshold, false));    // 임계 직후
        Assert.True(ActionOverPolicy.ShouldEmit(outRise, 245_900, threshold, false)); // IN 도달 직전
    }

    [Fact]
    public void 사이클당_1건_이미_발행했으면_재발행하지_않는다()
    {
        Assert.False(ActionOverPolicy.ShouldEmit(startMs: 0, nowMs: 999_999, thresholdMs: 8100, alreadyEmitted: true));
    }

    [Fact]
    public void 임계가_0이면_발행하지_않는다()
    {
        Assert.False(ActionOverPolicy.ShouldEmit(startMs: 0, nowMs: 999_999, thresholdMs: 0, alreadyEmitted: false));
    }
}
