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
    // #198: 엔진 advance 감지를 _baseSim 과 분리해 추적한다. 속도 변경 시 _baseSim 에 적립한 보간분을
    // 엔진 재앵커가 덮어쓰지 않도록(아래 EstimateNow). _lastSpeed 는 속도 변경 감지(연속 적립)용.
    private TimeSpan _lastEngineClock = TimeSpan.Zero;
    private double   _lastSpeed       = 1.0;

    private readonly Func<ISimulationEngine?> _engine;
    private readonly Func<DateTime>           _simStart;
    private readonly Func<bool>               _isSimulating;
    private readonly Func<bool>               _isSimPaused;
    private readonly Func<double>             _simSpeed;

    /// STEP 진행 중에는 IsSimPaused=true 이지만 sim clock 이 실제 advance 하므로
    /// 보간을 켜야 매 advance 사이의 wall delay 동안에도 빨간선이 부드럽게 흘러간다.
    /// StepSimulationAsync 가 step 시작 시 true / 끝날 때 false 로 set.
    public bool IsStepping { get; set; }

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
        _baseWall        = DateTime.Now;
        _baseSim         = _engine()?.State.Clock ?? TimeSpan.Zero;
        _lastEngineClock = _baseSim;
        _lastSpeed       = CurrentSpeed();
    }

    private double CurrentSpeed()
    {
        var s = _simSpeed();
        return s > 0 ? s : 1.0;
    }

    /// <summary>현재 시점의 sim clock 추정 — 일시정지 중에는 base 그대로, 동작 중에는 wall 경과 × speed 만큼 추가.</summary>
    public DateTime EstimateNow()
    {
        var engine = _engine();
        if (engine is null) return _simStart() + _baseSim;

        var now = DateTime.Now;

        // 엔진이 새 sim clock 으로 advance(이벤트) → base 를 진실값으로 snap (이후 보간은 그 지점부터 다시 측정).
        // 비교 기준은 _baseSim 이 아니라 _lastEngineClock — 아래 속도 변경 적립으로 _baseSim 이 엔진 clock 과
        // 달라져도 '엔진이 실제 advance 했을 때만' 재앵커되어 적립분이 보존된다.
        var actualSim = engine.State.Clock;
        if (actualSim != _lastEngineClock)
        {
            _lastEngineClock = actualSim;
            _baseSim         = actualSim;
            _baseWall        = now;
            _lastSpeed       = CurrentSpeed();
        }

        // Pause 중에는 보간 정지. 단 STEP 진행 중이면 paused 라도 wall × speed 보간을 켠다.
        if (!_isSimulating()) return _simStart() + _baseSim;
        if (_isSimPaused() && !IsStepping) return _simStart() + _baseSim;

        // #198: 속도 변경 시 — 변경 전까지 투영된 양을 '직전 속도(_lastSpeed)'로 _baseSim 에 적립하고
        // wall 을 재시작한다. 그래야 새 속도가 '이후 경과'에만 적용되어, 과거 wall 구간이 새 속도로 통째
        // 재계산되거나(점프) base 를 엔진 clock 으로 되돌려(뒤로 점프) 표시가 늘었다 줄었다 하지 않는다.
        var speed = CurrentSpeed();
        if (speed != _lastSpeed)
        {
            var bankedElapsed = now - _baseWall;
            if (bankedElapsed > TimeSpan.Zero)
                _baseSim += TimeSpan.FromTicks((long)(bankedElapsed.Ticks * _lastSpeed));
            _baseWall  = now;
            _lastSpeed = speed;
        }

        var wallElapsed = now - _baseWall;
        var simDelta = TimeSpan.FromTicks((long)(wallElapsed.Ticks * speed));
        return _simStart() + _baseSim + simDelta;
    }
}
