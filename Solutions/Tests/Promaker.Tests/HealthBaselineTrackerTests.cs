using System;
using System.Collections.Generic;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>건강 기준선 동결 + 드리프트 추적 — 동결은 수렴/상한/수동 3경로,
/// 동결 후엔 드리프트 %·IQR 조기 경보·선형 외삽(정비 권고)이 나온다.</summary>
public sealed class HealthBaselineTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
    private static readonly Guid Work = Guid.NewGuid();

    private static HealthBaselineTracker BuildTracker(double? maxDurationMs = null)
    {
        var max = new Dictionary<Guid, double>();
        if (maxDurationMs is { } m) max[Work] = m;
        return new HealthBaselineTracker(max, new Dictionary<Guid, string> { [Work] = "DEV" });
    }

    [Fact]
    public void Converged_samples_auto_freeze_at_min_sample_floor()
    {
        var tracker = BuildTracker();

        HealthSampleResult last = default;
        for (var i = 0; i < HealthBaselineTracker.MinFreezeSamples; i++)
        {
            Assert.Null(last.JustFrozen);   // 직전까지는 미동결
            last = tracker.OnSample(Work, 100, T0.AddSeconds(i));
        }

        Assert.NotNull(last.JustFrozen);    // 안정 샘플 → 하한(5)에서 수렴 동결
        Assert.False(last.FrozenByCap);
        Assert.True(last.JustFrozen!.Auto);
        Assert.Equal(100, last.JustFrozen.MedianMs);
        Assert.True(tracker.HasFrozenBaseline);
    }

    [Fact]
    public void Oscillating_samples_force_freeze_at_sample_cap()
    {
        // 100/200 교대 — 러닝 중앙값이 100/150/200 을 오가 수렴 밴드에 못 들어간다.
        // 영영 안 얼면 드리프트 측정을 시작 못 하므로 상한(30)에서 강제 동결.
        var tracker = BuildTracker();

        HealthSampleResult last = default;
        for (var i = 0; i < HealthBaselineTracker.MaxFreezeSamples; i++)
        {
            Assert.Null(last.JustFrozen);
            last = tracker.OnSample(Work, i % 2 == 0 ? 100 : 200, T0.AddSeconds(i));
        }

        Assert.NotNull(last.JustFrozen);
        Assert.True(last.FrozenByCap);
    }

    [Fact]
    public void FreezeNow_freezes_only_works_with_minimum_samples()
    {
        var tracker = BuildTracker();
        var other = Guid.NewGuid();
        for (var i = 0; i < HealthBaselineTracker.MinManualFreezeSamples; i++)
            tracker.OnSample(Work, 100 + i * 20, T0.AddSeconds(i));   // 3샘플 — 수동 동결 가능
        tracker.OnSample(other, 100, T0);                              // 1샘플 — 부족

        var frozen = tracker.FreezeNow(T0.AddSeconds(10));

        var entry = Assert.Single(frozen);
        Assert.Equal(Work, entry.WorkId);
        Assert.False(entry.Baseline.Auto);

        // 이미 동결된 work 는 재동결 없음.
        Assert.Empty(tracker.FreezeNow(T0.AddSeconds(20)));
    }

    [Fact]
    public void Drift_percent_measures_against_frozen_median()
    {
        var tracker = BuildTracker();
        for (var i = 0; i < HealthBaselineTracker.MinFreezeSamples; i++)
            tracker.OnSample(Work, 100, T0.AddSeconds(i));   // 중앙값 100 으로 동결

        var r = tracker.OnSample(Work, 110, T0.AddSeconds(60));

        Assert.NotNull(r.DriftPct);
        Assert.Equal(10.0, r.DriftPct!.Value, precision: 3);   // (110-100)/100
    }

    [Fact]
    public void Iqr_expansion_raises_then_clears_alarm_with_hysteresis()
    {
        var tracker = BuildTracker();
        for (var i = 0; i < HealthBaselineTracker.MinFreezeSamples; i++)
            tracker.OnSample(Work, 100, T0.AddSeconds(i));   // 동결 IQR ≈ 1(바닥)

        // 변동 큰 샘플 — 단기 IQR 이 기준선 대비 1.5배 초과 → 경보.
        var raised = false;
        double[] noisy = [100, 105, 110, 115, 120];
        for (var i = 0; i < noisy.Length; i++)
            raised |= tracker.OnSample(Work, noisy[i], T0.AddSeconds(60 + i)).IqrAlarmRaised;
        Assert.True(raised);

        // 다시 안정 — 단기 윈도우가 동일값으로 차면 IQR 축소 → 해제.
        var cleared = false;
        for (var i = 0; i < HealthBaselineTracker.ShortWindowSize; i++)
            cleared |= tracker.OnSample(Work, 110, T0.AddSeconds(120 + i)).IqrAlarmCleared;
        Assert.True(cleared);
    }

    [Fact]
    public void Forecast_extrapolates_hours_to_max_duration_on_worsening_trend()
    {
        var tracker = BuildTracker(maxDurationMs: 5000);
        for (var i = 0; i < HealthBaselineTracker.MinFreezeSamples; i++)
            tracker.OnSample(Work, 1000, T0.AddSeconds(i));   // 기준선 1000ms

        // 분당 +10ms 선형 악화 — 외삽 최소 점 수를 채운다.
        for (var i = 1; i <= HealthBaselineTracker.MinForecastPoints + 2; i++)
            tracker.OnSample(Work, 1000 + i * 10, T0.AddMinutes(i));

        var forecast = tracker.TryForecast(Work);

        Assert.NotNull(forecast);
        Assert.True(forecast!.SlopeMsPerHour > 0);
        Assert.True(forecast.DriftPct > 0);
        Assert.NotNull(forecast.HoursToMaxDuration);
        Assert.True(forecast.HoursToMaxDuration > 0);
    }

    [Fact]
    public void Forecast_returns_null_without_worsening_trend_or_enough_points()
    {
        var tracker = BuildTracker(maxDurationMs: 5000);
        for (var i = 0; i < HealthBaselineTracker.MinFreezeSamples; i++)
            tracker.OnSample(Work, 1000, T0.AddSeconds(i));

        Assert.Null(tracker.TryForecast(Work));   // 드리프트 점 부족

        // 안정 추세(기울기 0) — HoursToMax 없음.
        for (var i = 1; i <= HealthBaselineTracker.MinForecastPoints + 2; i++)
            tracker.OnSample(Work, 1000, T0.AddMinutes(i));
        var forecast = tracker.TryForecast(Work);
        Assert.True(forecast is null || forecast.HoursToMaxDuration is null);
    }

    [Fact]
    public void SummaryLines_reports_frozen_works_with_drift()
    {
        var tracker = BuildTracker();
        for (var i = 0; i < HealthBaselineTracker.MinFreezeSamples; i++)
            tracker.OnSample(Work, 100, T0.AddSeconds(i));
        tracker.OnSample(Work, 110, T0.AddSeconds(60));

        var line = Assert.Single(tracker.SummaryLines());
        Assert.Contains("DEV", line);
        Assert.Contains("100", line);   // 기준선 중앙값
    }
}
