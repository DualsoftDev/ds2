using System;
using Promaker.Shared;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// CalibrationState.ReconcileWork — 모델 duration 갱신 후 사이드카 정합 규칙.
/// 어긋난 확정만 해제, 일치 확정 보존, 도장 신규 발행 없음 (ActionOver 문서 §1-2).
/// </summary>
public class CalibrationStateReconcileTests
{
    private static CalibrationState StateWith(Guid id, int? minMs, int? maxMs)
    {
        var s = new CalibrationState();
        if (minMs is { } mn) s.SetMinMeasured(id, mn, "sha");
        if (maxMs is { } mx) s.SetMaxMeasured(id, mx, "sha");
        return s;
    }

    [Fact]
    public void Max_변경시_Max확정만_해제되고_Min확정은_보존()
    {
        var id = Guid.NewGuid();
        var s = StateWith(id, minMs: 100, maxMs: 900);

        var changed = s.ReconcileWork(id, currentMinMs: 100, currentMaxMs: 1500);

        Assert.True(changed);
        Assert.True(s.IsMinMeasured(id, 100));
        Assert.False(s.IsMaxMeasured(id, 1500));
        Assert.False(s.IsMaxMeasured(id, 900));
    }

    [Fact]
    public void 둘다_어긋나면_엔트리_자체가_제거된다()
    {
        var id = Guid.NewGuid();
        var s = StateWith(id, minMs: 100, maxMs: 900);

        var changed = s.ReconcileWork(id, currentMinMs: 200, currentMaxMs: 1500);

        Assert.True(changed);
        Assert.Empty(s.Works);
    }

    [Fact]
    public void 값이_그대로면_확정_보존_및_무변경()
    {
        var id = Guid.NewGuid();
        var s = StateWith(id, minMs: 100, maxMs: 900);

        var changed = s.ReconcileWork(id, currentMinMs: 100, currentMaxMs: 900);

        Assert.False(changed);
        Assert.True(s.IsMinMeasured(id, 100));
        Assert.True(s.IsMaxMeasured(id, 900));
    }

    [Fact]
    public void 모델값이_None이_되면_해당_확정_해제()
    {
        var id = Guid.NewGuid();
        var s = StateWith(id, minMs: 100, maxMs: null);

        var changed = s.ReconcileWork(id, currentMinMs: null, currentMaxMs: null);

        Assert.True(changed);
        Assert.Empty(s.Works);
    }

    [Fact]
    public void 확정이_없는_Work는_아무_일도_없다()
    {
        var s = new CalibrationState();
        Assert.False(s.ReconcileWork(Guid.NewGuid(), 100, 900));
        Assert.Empty(s.Works);
    }
}
