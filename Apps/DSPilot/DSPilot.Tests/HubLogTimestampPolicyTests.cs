// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>plcTagLog 기록 시각 결정 규칙 — 원천시각 채택/기각 경계와 backfill 판정.</summary>
public class HubLogTimestampPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    private static long ToMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public void 미제공이면_도착시각_폴백()
    {
        var r = HubLogTimestampPolicy.Resolve(0, Now);

        Assert.Equal(HubLogTimeSource.Arrival, r.Source);
        Assert.Equal(Now, r.AtUtc);
        Assert.False(r.IsBackfill);
    }

    [Fact]
    public void 음수도_미제공_취급()
    {
        var r = HubLogTimestampPolicy.Resolve(-1, Now);

        Assert.Equal(HubLogTimeSource.Arrival, r.Source);
        Assert.Equal(Now, r.AtUtc);
    }

    [Fact]
    public void 정상_원천시각_채택_backfill_아님()
    {
        var at = Now.AddSeconds(-2);

        var r = HubLogTimestampPolicy.Resolve(ToMs(at), Now);

        Assert.Equal(HubLogTimeSource.Origin, r.Source);
        Assert.Equal(at, r.AtUtc);
        Assert.False(r.IsBackfill);
    }

    [Fact]
    public void 두절_replay_는_원천시각_채택_backfill_판정()
    {
        var at = Now.AddMinutes(-86);   // 우진 사례 규모의 두절

        var r = HubLogTimestampPolicy.Resolve(ToMs(at), Now);

        Assert.Equal(HubLogTimeSource.Origin, r.Source);
        Assert.Equal(at, r.AtUtc);
        Assert.True(r.IsBackfill);      // 재도출 창을 여기까지 넓히는 트리거
    }

    [Theory]
    [InlineData(-59, false)]            // 정상 지터 범위
    [InlineData(-60, false)]            // 경계 — 초과여야 backfill
    [InlineData(-61, true)]
    public void backfill_임계_경계(int offsetSeconds, bool expected)
    {
        var r = HubLogTimestampPolicy.Resolve(ToMs(Now.AddSeconds(offsetSeconds)), Now);

        Assert.Equal(HubLogTimeSource.Origin, r.Source);
        Assert.Equal(expected, r.IsBackfill);
    }

    [Fact]
    public void 허용_스큐_내_미래는_채택()
    {
        var at = Now.AddSeconds(5);     // MaxFutureSkew 경계 — 초과여야 기각

        var r = HubLogTimestampPolicy.Resolve(ToMs(at), Now);

        Assert.Equal(HubLogTimeSource.Origin, r.Source);
        Assert.Equal(at, r.AtUtc);
    }

    [Fact]
    public void 미래_시각은_기각하고_도착시각_폴백()
    {
        var r = HubLogTimestampPolicy.Resolve(ToMs(Now.AddHours(1)), Now);

        Assert.Equal(HubLogTimeSource.RejectedFuture, r.Source);
        Assert.Equal(Now, r.AtUtc);     // 미래로 찍으면 오늘 집계가 오염된다
        Assert.False(r.IsBackfill);
    }

    [Fact]
    public void 허용_backfill_경계_내_과거는_채택()
    {
        var at = Now.AddHours(-24);     // MaxBackfill 경계 — 미만이어야 기각

        var r = HubLogTimestampPolicy.Resolve(ToMs(at), Now);

        Assert.Equal(HubLogTimeSource.Origin, r.Source);
        Assert.Equal(at, r.AtUtc);
    }

    [Fact]
    public void 창_초과_과거는_기각하고_도착시각_폴백()
    {
        // Pi5 RTC 배터리 없음 + NTP 미동기 시나리오. 그대로 믿으면 보존기간 삭제가 방금 받은 데이터를 지운다.
        var r = HubLogTimestampPolicy.Resolve(ToMs(Now.AddHours(-48)), Now);

        Assert.Equal(HubLogTimeSource.RejectedTooOld, r.Source);
        Assert.Equal(Now, r.AtUtc);
    }

    [Fact]
    public void epoch_근처_시계_미설정도_기각()
    {
        // fake-hwclock 미복원 → 1970. DateTimeOffset 변환은 되지만 창 밖이라 기각돼야 한다.
        var r = HubLogTimestampPolicy.Resolve(1000, Now);

        Assert.Equal(HubLogTimeSource.RejectedTooOld, r.Source);
        Assert.Equal(Now, r.AtUtc);
    }

    [Fact]
    public void 범위_초과값도_예외없이_기각()
    {
        // 신호 처리 경로라 변환 예외(ArgumentOutOfRange)가 새어나가면 안 된다.
        var r = HubLogTimestampPolicy.Resolve(long.MaxValue, Now);

        Assert.Equal(HubLogTimeSource.RejectedFuture, r.Source);
        Assert.Equal(Now, r.AtUtc);
    }

    [Fact]
    public void 도착시각을_Local_로_줘도_UTC_정규화()
    {
        // Kind 혼용 footgun 회귀 — Local 을 그대로 비교하면 tz offset 만큼 창이 밀린다.
        var nowLocal = Now.ToLocalTime();
        var at = Now.AddSeconds(-2);

        var r = HubLogTimestampPolicy.Resolve(ToMs(at), nowLocal);

        Assert.Equal(HubLogTimeSource.Origin, r.Source);
        Assert.Equal(at, r.AtUtc);
        Assert.False(r.IsBackfill);
    }

    [Fact]
    public void 폴백_시각은_항상_UTC_Kind()
    {
        // ToSqliteUtcString 은 Local 만 변환하고 Unspecified 는 UTC 로 간주한다 — Kind 를 흘리면 9시간 밀린다.
        var r = HubLogTimestampPolicy.Resolve(0, Now.ToLocalTime());

        Assert.Equal(DateTimeKind.Utc, r.AtUtc.Kind);
        Assert.Equal(Now, r.AtUtc);
    }
}

/// <summary>backfill 하한 핸드오프 — min 유지와 "소비 중 신규 유입은 남긴다" 규칙.</summary>
public class BackfillFloorTrackerTests
{
    private static readonly DateTime Base = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 초기값은_미보유()
    {
        Assert.Null(new BackfillFloorTracker().PeekUtc());
    }

    [Fact]
    public void 더_오래된_값으로만_내려간다()
    {
        var t = new BackfillFloorTracker();

        t.Report(Base.AddMinutes(-30));
        t.Report(Base.AddMinutes(-86));   // 더 오래됨 → 채택
        t.Report(Base.AddMinutes(-10));   // 더 최근 → 무시

        Assert.Equal(Base.AddMinutes(-86), t.PeekUtc());
    }

    [Fact]
    public void Peek_는_비우지_않는다()
    {
        var t = new BackfillFloorTracker();
        t.Report(Base.AddMinutes(-5));

        Assert.NotNull(t.PeekUtc());
        Assert.NotNull(t.PeekUtc());      // 재도출 실패 시 다음 주기가 다시 봐야 한다
    }

    [Fact]
    public void 본_값을_소비하면_비워진다()
    {
        var t = new BackfillFloorTracker();
        t.Report(Base.AddMinutes(-5));

        var seen = t.PeekUtc()!.Value;
        t.Clear(seen);

        Assert.Null(t.PeekUtc());
    }

    [Fact]
    public void 소비중_들어온_더_오래된_값은_남는다()
    {
        // 재도출이 도는 동안 더 깊은 replay 가 들어온 경우 — 무조건 0 으로 밀면 그 구간이 영구 미재도출.
        var t = new BackfillFloorTracker();
        t.Report(Base.AddMinutes(-5));
        var seen = t.PeekUtc()!.Value;

        t.Report(Base.AddMinutes(-90));   // 재도출 중 유입
        t.Clear(seen);                    // 내가 본 값만 소비 시도 → CAS 실패

        Assert.Equal(Base.AddMinutes(-90), t.PeekUtc());
    }

    [Fact]
    public void 소비후_재유입은_다시_보유()
    {
        var t = new BackfillFloorTracker();
        t.Report(Base.AddMinutes(-5));
        t.Clear(t.PeekUtc()!.Value);

        t.Report(Base.AddMinutes(-1));

        Assert.Equal(Base.AddMinutes(-1), t.PeekUtc());
    }

    [Fact]
    public void Local_입력도_UTC_로_정규화()
    {
        var t = new BackfillFloorTracker();

        t.Report(Base.AddMinutes(-20).ToLocalTime());

        Assert.Equal(Base.AddMinutes(-20), t.PeekUtc());
        Assert.Equal(DateTimeKind.Utc, t.PeekUtc()!.Value.Kind);
    }
}
