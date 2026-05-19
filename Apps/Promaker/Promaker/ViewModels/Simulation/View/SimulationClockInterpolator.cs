using System;
using Ds2.Runtime.Engine;

namespace Promaker.ViewModels;

/// <summary>
/// Sim clock 의 event-driven 갱신을 frame 보간으로 부드럽게 만드는 helper.
/// Gantt 빨간선이 점프 없이 진행되도록 매 frame 에 wall 경과 × speed 만큼 sim clock 을 추정한다.
/// Engine 이 새 sim clock 으로 advance 하면 base 가 갱신되어 이후 보간이 그 지점부터 다시 측정된다.
/// </summary>
public sealed class SimulationClockInterpolator
{
    private DateTime _baseWall = DateTime.Now;
    private TimeSpan _baseSim  = TimeSpan.Zero;

    private readonly Func<ISimulationEngine?> _engine;
    private readonly Func<DateTime>           _simStart;
    private readonly Func<bool>               _isSimulating;
    private readonly Func<bool>               _isSimPaused;
    private readonly Func<double>             _simSpeed;

    public SimulationClockInterpolator(
        Func<ISimulationEngine?> engine,
        Func<DateTime>           simStart,
        Func<bool>               isSimulating,
        Func<bool>               isSimPaused,
        Func<double>             simSpeed)
    {
        _engine       = engine;
        _simStart     = simStart;
        _isSimulating = isSimulating;
        _isSimPaused  = isSimPaused;
        _simSpeed     = simSpeed;
    }

    /// <summary>IsSimulating / IsSimPaused 변경 시 호출 — base 를 현재 sim clock 으로 freeze.</summary>
    public void ResetBase()
    {
        _baseWall = DateTime.Now;
        _baseSim  = _engine()?.State.Clock ?? TimeSpan.Zero;
    }

    /// <summary>현재 시점의 sim clock 추정 — 일시정지 중에는 base 그대로, 동작 중에는 wall 경과 × speed 만큼 추가.</summary>
    public DateTime EstimateNow()
    {
        var engine = _engine();
        if (engine is null) return _simStart() + _baseSim;

        var actualSim = engine.State.Clock;
        // 엔진이 새 sim clock 으로 advance → base 갱신 (이후 보간은 그 지점부터 다시 측정)
        if (actualSim != _baseSim)
        {
            _baseSim  = actualSim;
            _baseWall = DateTime.Now;
        }

        // Pause 중에는 보간 정지 — 마지막 base 그대로 반환
        if (_isSimPaused() || !_isSimulating()) return _simStart() + _baseSim;

        var wallElapsed = DateTime.Now - _baseWall;
        var speed = _simSpeed() > 0 ? _simSpeed() : 1.0;
        var simDelta = TimeSpan.FromTicks((long)(wallElapsed.Ticks * speed));
        return _simStart() + _baseSim + simDelta;
    }
}
