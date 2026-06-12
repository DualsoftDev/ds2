using System;
using System.Collections.Generic;
using Ds2.Core;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

// 로컬 실측 duration 학습기 — Call Going→Finish 구간을 device Work 에 귀속,
// 중앙값/min/max 스냅샷, abnormal/비정상 전이 폐기, 최소 표본(3사이클) 게이트 검증.
public sealed class CallDurationLearningTests
{
    private static readonly Guid Call = Guid.NewGuid();
    private static readonly Guid DeviceWork = Guid.NewGuid();
    private static readonly Guid ActiveWork = Guid.NewGuid();

    private static CallDurationLearning Create() =>
        new(new Dictionary<Guid, Guid[]> { [Call] = [DeviceWork] },
            new HashSet<Guid> { ActiveWork });

    private static void Cycle(CallDurationLearning learning, double startMs, double spanMs)
    {
        learning.OnCallStateChanged(Call, Status4.Going, startMs);
        learning.OnCallStateChanged(Call, Status4.Finish, startMs + spanMs);
    }

    [Fact]
    public void Going_to_finish_span_accumulates_per_device_work_with_median()
    {
        var learning = Create();
        Cycle(learning, 0, 500);
        Cycle(learning, 1000, 560);
        Cycle(learning, 2000, 520);

        var snapshot = learning.Snapshot();

        Assert.True(snapshot.ContainsKey(DeviceWork));
        var (avg, min, max) = snapshot[DeviceWork];
        Assert.Equal(520, avg);   // 중앙값 — 평균(527)이 아님
        // 마진 = 3σ (σ≈30.6) — Min = min(관측min, 중앙값−3σ)=428, Max = max(관측max, 중앙값+3σ)=612.
        Assert.Equal(428, min);
        Assert.Equal(612, max);
    }

    [Fact]
    public void Snapshot_requires_minimum_three_samples()
    {
        var learning = Create();
        Cycle(learning, 0, 500);
        Cycle(learning, 1000, 510);

        Assert.False(learning.HasSamples);
        Assert.Empty(learning.Snapshot());

        Cycle(learning, 2000, 520);

        Assert.True(learning.HasSamples);
        Assert.True(learning.Snapshot().ContainsKey(DeviceWork));
    }

    [Fact]
    public void Abnormal_invalidate_discards_in_flight_measurement()
    {
        var learning = Create();
        Cycle(learning, 0, 500);
        Cycle(learning, 1000, 500);
        Cycle(learning, 2000, 500);

        // 4번째 사이클 진행 중 abnormal → 그 사이클은 학습에서 제외
        learning.OnCallStateChanged(Call, Status4.Going, 3000);
        learning.Invalidate(Call);
        learning.OnCallStateChanged(Call, Status4.Finish, 9000);   // 비정상으로 길었던 완료

        var (avg, _, max) = learning.Snapshot()[DeviceWork];
        Assert.Equal(500, avg);
        Assert.Equal(500, max);   // 동일 샘플 σ=0 → 마진 0 — 6000ms 샘플이 들어왔다면 max 가 오염됐을 것
    }

    [Fact]
    public void Non_finish_transition_is_not_sampled()
    {
        var learning = Create();
        Cycle(learning, 0, 500);
        Cycle(learning, 1000, 500);

        // Going → Homing (강제 리셋) — 정상 완료가 아니므로 샘플 아님
        learning.OnCallStateChanged(Call, Status4.Going, 2000);
        learning.OnCallStateChanged(Call, Status4.Homing, 2400);

        Assert.False(learning.HasSamples);   // 여전히 2개뿐
    }

    [Fact]
    public void Active_work_going_to_finish_is_sampled_on_the_work_itself()
    {
        var learning = Create();
        for (var i = 0; i < 3; i++)
        {
            learning.OnWorkStateChanged(ActiveWork, Status4.Going, i * 10_000);
            learning.OnWorkStateChanged(ActiveWork, Status4.Finish, i * 10_000 + 5500);
        }

        var snapshot = learning.Snapshot();
        Assert.Equal(5500, snapshot[ActiveWork].AvgMs);
    }

    [Fact]
    public void Untracked_work_is_ignored()
    {
        var learning = Create();
        var deviceLikeWork = Guid.NewGuid();   // activeWorkIds 에 없는 Work — 측정 안 함
        for (var i = 0; i < 3; i++)
        {
            learning.OnWorkStateChanged(deviceLikeWork, Status4.Going, i * 1000);
            learning.OnWorkStateChanged(deviceLikeWork, Status4.Finish, i * 1000 + 500);
        }

        Assert.False(learning.HasSamples);
    }

    [Fact]
    public void Invalidate_work_discards_in_flight_cycle()
    {
        var learning = Create();
        for (var i = 0; i < 3; i++)
        {
            learning.OnWorkStateChanged(ActiveWork, Status4.Going, i * 10_000);
            learning.OnWorkStateChanged(ActiveWork, Status4.Finish, i * 10_000 + 5000);
        }

        learning.OnWorkStateChanged(ActiveWork, Status4.Going, 30_000);
        learning.InvalidateWork(ActiveWork);
        learning.OnWorkStateChanged(ActiveWork, Status4.Finish, 49_000);   // abnormal 로 늘어진 사이클

        Assert.Equal(5000, learning.Snapshot()[ActiveWork].MaxMs);   // σ=0 → 마진 0, 19000ms 오염 없음
    }

    [Fact]
    public void InvalidateAll_discards_in_flight_measurements_but_keeps_window()
    {
        // 통신 blackout — 단절 시간이 포함될 진행 중 측정은 전부 폐기, 누적 샘플(줄자)은 보존.
        var learning = Create();
        for (var i = 0; i < 3; i++)
            Cycle(learning, i * 10_000, 500);
        learning.OnCallStateChanged(Call, Status4.Going, 30_000);
        learning.OnWorkStateChanged(ActiveWork, Status4.Going, 30_000);

        learning.InvalidateAll();

        learning.OnCallStateChanged(Call, Status4.Finish, 99_000);          // 단절 시간 포함 — 무시돼야
        learning.OnWorkStateChanged(ActiveWork, Status4.Finish, 99_000);

        var snapshot = learning.Snapshot();
        Assert.Equal(500, snapshot[DeviceWork].MaxMs);                       // 69초 샘플 미유입
        Assert.False(snapshot.ContainsKey(ActiveWork));                      // Work 도 미유입 (표본 0)
    }

    [Fact]
    public void Sliding_window_keeps_only_recent_samples()
    {
        var learning = Create();
        // 윈도우(30)보다 많은 50사이클 — 앞쪽 20개(1000ms대)는 밀려나야 함
        for (var i = 0; i < 20; i++)
            Cycle(learning, i * 10_000, 1000);
        for (var i = 20; i < 50; i++)
            Cycle(learning, i * 10_000, 500);

        var (avg, min, max) = learning.Snapshot()[DeviceWork];
        Assert.Equal(500, avg);
        Assert.Equal(500, min);   // 윈도우에 500 만 남음 — σ=0
        Assert.Equal(500, max);   // 1000ms 샘플이 남아 있으면 σ·max 에 드러남
    }
}
