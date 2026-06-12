using System;
using System.Collections.Generic;
using Ds2.Core;

namespace Promaker.ViewModels;

/// <summary>
/// 비-Simulation 모드(Control/Monitoring/VP) 의 Call 실측 duration 학습기.
/// Call Going→Finish 의 엔진 시계 구간(=출력 발행부터 센서 응답까지, 통신 왕복 포함 실측)을
/// 그 Call 이 부르는 device Work(ApiDef.RxGuid)들에 귀속해 work 별 슬라이딩 윈도우로 누적한다.
/// 정지 시 <see cref="Snapshot"/> 의 (중앙값, min, max) 가 기존 학습 반영 다이얼로그
/// (TryApplyLearnedDurationsOnStop) 로 합류한다.
///
/// 학습 오염 차단: abnormal 이 발생한 Call 의 진행 중 측정은 <see cref="Invalidate"/> 로 폐기,
/// Going→Finish 가 아닌 전이(강제 리셋 등)도 샘플로 쓰지 않는다.
/// 중앙값/min/max 는 로버스트 통계 — 윈도우 안에서 아웃라이어 한두 개는 중앙값을 거의 못 움직인다.
/// </summary>
internal sealed class CallDurationLearning
{
    /// <summary>work 별 보관하는 최근 샘플 수 — 정확도와 최신성(노화 추적)의 균형.</summary>
    internal const int WindowSize = 30;
    /// <summary>Snapshot 에 포함하는 최소 표본 수 — 중앙값이 의미를 가지려면 최소 3사이클.</summary>
    internal const int MinSamples = 3;
    /// <summary>Min/Max 허용 마진의 σ 배수 — 관측 극값을 그대로 abnormal 경계로 쓰면 자연 변동(±수 ms)이
    /// 즉시 Under/Over 오탐이 된다 (실측: 관측 max=513 적용 후 elapsed=514 ActionOver 폭발).
    /// 마진은 duration 크기 비례(%)가 아니라 그 공정의 변동 폭(표본 σ)에 비례해야 의미가 맞다 —
    /// 안정 공정은 좁게(민감 감지), 흔들리는 공정은 넓게(오탐 없음). 3σ ≈ 정상 사이클 오탐률 0.1%.</summary>
    internal const double RangeMarginSigma = 3.0;

    private readonly IReadOnlyDictionary<Guid, Guid[]> _callRxWorks;
    private readonly IReadOnlySet<Guid> _activeWorkIds;
    private readonly Dictionary<Guid, double> _goingAtMs = new();
    private readonly Dictionary<Guid, double> _workGoingAtMs = new();
    private readonly Dictionary<Guid, Queue<double>> _samples = new();

    /// <summary>정상 완료 실측 1건이 work 에 귀속될 때 발화 (workGuid, spanMs) — 건강 기준선
    /// 추적기 등 2차 소비자용. 측정/오염 차단 로직은 여기가 SSOT 라 소비자는 거를 것이 없다.</summary>
    public event Action<Guid, double>? SampleRecorded;

    /// <param name="callRxWorks">call guid(원본·참조 모두) → 그 Call 이 부르는 device Work guid 목록</param>
    /// <param name="activeWorkIds">Call 을 가진 Active Work guid 집합 — Work 자체의 Going→Finish 실측 대상.
    /// device 합산(critical path)에 안 잡히는 단계 간 전환 갭까지 포함한 전체 실측이라,
    /// 적용 시 Work.Duration 이 plan(부품 합)보다 길어져 간트 plan 틀이 actual 에 붙는다.</param>
    public CallDurationLearning(
        IReadOnlyDictionary<Guid, Guid[]> callRxWorks,
        IReadOnlySet<Guid> activeWorkIds)
    {
        _callRxWorks = callRxWorks;
        _activeWorkIds = activeWorkIds;
    }

    public void OnCallStateChanged(Guid callGuid, Status4 newState, double clockMs)
    {
        if (newState == Status4.Going)
        {
            _goingAtMs[callGuid] = clockMs;
            return;
        }

        if (!_goingAtMs.TryGetValue(callGuid, out var startMs))
            return;
        _goingAtMs.Remove(callGuid);

        if (newState != Status4.Finish)
            return;   // 강제 리셋/Homing 직행 — 정상 완료가 아니므로 학습 제외

        var spanMs = clockMs - startMs;
        if (spanMs <= 0 || !_callRxWorks.TryGetValue(callGuid, out var works))
            return;

        foreach (var work in works)
        {
            if (!_samples.TryGetValue(work, out var queue))
                _samples[work] = queue = new Queue<double>();
            queue.Enqueue(spanMs);
            while (queue.Count > WindowSize)
                queue.Dequeue();
            SampleRecorded?.Invoke(work, spanMs);
        }
    }

    /// <summary>Active Work 자체의 Going→Finish 실측 — 단계 간 전환 갭 포함 전체 사이클 길이.</summary>
    public void OnWorkStateChanged(Guid workGuid, Status4 newState, double clockMs)
    {
        if (!_activeWorkIds.Contains(workGuid))
            return;

        if (newState == Status4.Going)
        {
            _workGoingAtMs[workGuid] = clockMs;
            return;
        }

        if (!_workGoingAtMs.TryGetValue(workGuid, out var startMs))
            return;
        _workGoingAtMs.Remove(workGuid);

        if (newState != Status4.Finish)
            return;

        var spanMs = clockMs - startMs;
        if (spanMs <= 0)
            return;

        if (!_samples.TryGetValue(workGuid, out var queue))
            _samples[workGuid] = queue = new Queue<double>();
        queue.Enqueue(spanMs);
        while (queue.Count > WindowSize)
            queue.Dequeue();
        SampleRecorded?.Invoke(workGuid, spanMs);
    }

    /// <summary>abnormal 발생 Call 의 진행 중 측정 폐기 — 비정상 사이클로 기준선을 오염시키지 않는다.</summary>
    public void Invalidate(Guid callGuid) => _goingAtMs.Remove(callGuid);

    /// <summary>abnormal 이 속한 Work 의 진행 중 사이클 측정 폐기.</summary>
    public void InvalidateWork(Guid workGuid) => _workGoingAtMs.Remove(workGuid);

    /// <summary>통신 blackout(PLC 단절/신호 두절) — 진행 중인 모든 측정 폐기.
    /// 두절 시간이 포함된 span 이 윈도우에 들어가 기준선을 오염시키지 않게 한다.
    /// 누적된 윈도우 샘플(학습 줄자)은 보존 — backend 어댑터의 InvalidateObservations 와 동형.</summary>
    public void InvalidateAll()
    {
        _goingAtMs.Clear();
        _workGoingAtMs.Clear();
    }

    /// <summary>work 별 (중앙값, 하한, 상한) — 표본 <see cref="MinSamples"/> 미만인 work 는 제외.
    /// 경계는 abnormal 판정(Work.Min/MaxDuration)과 간트 plan 틀에 쓰이므로
    /// Max = max(관측max, 중앙값+3σ), Min = max(0, min(관측min, 중앙값−3σ)) — 변동 폭 비례 마진.</summary>
    public Dictionary<Guid, (int AvgMs, int MinMs, int MaxMs)> Snapshot()
    {
        var result = new Dictionary<Guid, (int, int, int)>();
        foreach (var (work, queue) in _samples)
        {
            if (queue.Count < MinSamples) continue;
            var sorted = queue.ToArray();
            Array.Sort(sorted);
            var median = sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;

            // 표본 표준편차(n-1) — 윈도우 안 자연 변동 폭.
            var mean = 0.0;
            foreach (var v in sorted) mean += v;
            mean /= sorted.Length;
            var sumSq = 0.0;
            foreach (var v in sorted) sumSq += (v - mean) * (v - mean);
            var sigma = Math.Sqrt(sumSq / (sorted.Length - 1));

            var margin = RangeMarginSigma * sigma;
            result[work] = (
                (int)Math.Round(median),
                (int)Math.Round(Math.Max(0.0, Math.Min(sorted[0], median - margin))),
                (int)Math.Round(Math.Max(sorted[^1], median + margin)));
        }
        return result;
    }

    public bool HasSamples
    {
        get
        {
            foreach (var queue in _samples.Values)
                if (queue.Count >= MinSamples)
                    return true;
            return false;
        }
    }
}
