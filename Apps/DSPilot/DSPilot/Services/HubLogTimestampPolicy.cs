// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.

namespace DSPilot.Services;

/// <summary>plcTagLog 기록 시각의 출처.</summary>
public enum HubLogTimeSource
{
    /// <summary>송신자가 원천시각을 안 실어 보냄(구버전/단건 OnTagChanged) — 도착시각으로 기록.</summary>
    Arrival,
    /// <summary>송신자의 원천 관측 시각(TagWrite.WallClockMs) 채택.</summary>
    Origin,
    /// <summary>원천시각이 미래 — 기각하고 도착시각 폴백(송신기 시계 앞섬).</summary>
    RejectedFuture,
    /// <summary>원천시각이 허용 backfill 창보다 과거 — 기각하고 도착시각 폴백(송신기 시계 뒤짐/미동기).</summary>
    RejectedTooOld,
}

/// <summary>plcTagLog 에 기록할 시각과 그 출처. <paramref name="IsBackfill"/> 는 "원천시각이 도착보다
/// 유의미하게 과거" = 수집기 store-and-forward replay 라는 뜻으로, 사이클 재도출 창을 그 지점까지
/// 넓히는 트리거다(<see cref="PeriodicCycleRecomputeService"/>).</summary>
public readonly record struct HubLogTimestamp(DateTime AtUtc, HubLogTimeSource Source, bool IsBackfill);

/// <summary>
/// Hub 태그 신호를 plcTagLog 에 기록할 때 쓸 시각 결정 규칙 — 순수 함수(테스트 가능).
///
/// <para>기본은 송신자가 실어 보낸 원천 관측 시각(<c>TagWrite.WallClockMs</c>, UTC epoch ms)이다.
/// 도착시각으로 찍으면 핑 두절 → 수집기 replay 신호가 전부 복구 순간에 뭉쳐 그래프·사이클이 왜곡된다.</para>
///
/// <para>★위생 검사가 필수인 이유: Pi5 는 RTC 배터리가 없으면 부팅 시 시계가 fake-hwclock 값(과거)
/// 이거나 epoch 근처다. 그리고 NTP 를 못 쓰는 상황이 바로 이 기능이 겨냥한 터널 두절 상황이라 동시
/// 발생 확률이 낮지 않다. 검사 없이 그대로 믿으면
///   - 과거로 크게 틀림 → 보존기간 삭제(<c>DELETE FROM plcTagLog WHERE dateTime &lt; cutoff</c>)가 방금
///     받은 데이터를 조용히 지우고, MIN(dateTime) 오염으로 차트 x축·데이터 범위 표시가 붕괴한다.
///   - 미래로 틀림 → 오늘 집계가 오염되고 <c>&lt;= now</c> 필터에서 사라졌다 나타난다.
/// 범위를 벗어나면 도착시각으로 폴백한다 — 시각이 뭉개지는 편이 데이터가 사라지는 것보다 낫다.</para>
/// </summary>
public static class HubLogTimestampPolicy
{
    /// <summary>허용 backfill 깊이. 수집기 버퍼가 이보다 깊게 밀리는 건 시계 오류로 본다.</summary>
    public static readonly TimeSpan MaxBackfill = TimeSpan.FromHours(24);

    /// <summary>허용 미래 스큐. 송신기·수신기 시계 차 + 전송 지연 여유분. 이 이상 미래는 무조건 난센스.</summary>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromSeconds(5);

    /// <summary>이보다 과거의 원천시각이면 replay(backfill)로 판정. 정상 경로의 스캔→수신 지연은 ms 급이라
    /// 60초는 라인 지터와 replay 를 안전하게 가른다.</summary>
    public static readonly TimeSpan BackfillThreshold = TimeSpan.FromSeconds(60);

    /// <summary>DateTimeOffset.FromUnixTimeMilliseconds 상한(9999-12-31). 넘으면 예외 대신 기각.</summary>
    private const long MaxUnixMs = 253402300799999L;

    /// <param name="wallClockMs">송신자 원천 관측 시각(UTC epoch ms). 0 이하 = 미제공.</param>
    /// <param name="nowUtc">도착 시각. Local 이 들어와도 UTC 로 정규화한다(Kind 혼용 footgun 방지).</param>
    public static HubLogTimestamp Resolve(long wallClockMs, DateTime nowUtc)
    {
        var now = nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        if (wallClockMs <= 0)
            return new HubLogTimestamp(now, HubLogTimeSource.Arrival, false);

        // 범위 초과는 변환 예외가 되므로 먼저 기각 — 신호 처리 경로라 예외를 던지면 안 된다.
        if (wallClockMs > MaxUnixMs)
            return new HubLogTimestamp(now, HubLogTimeSource.RejectedFuture, false);

        var at = DateTimeOffset.FromUnixTimeMilliseconds(wallClockMs).UtcDateTime;

        if (at > now + MaxFutureSkew)
            return new HubLogTimestamp(now, HubLogTimeSource.RejectedFuture, false);

        if (at < now - MaxBackfill)
            return new HubLogTimestamp(now, HubLogTimeSource.RejectedTooOld, false);

        return new HubLogTimestamp(at, HubLogTimeSource.Origin, now - at > BackfillThreshold);
    }
}

/// <summary>
/// 미소비 backfill 하한 추적기 — 수집기 replay 로 들어온 "도착보다 과거" 원천시각 중 가장 오래된 지점을
/// 들고 있다가, 사이클 재도출이 그 구간을 커버하면 비운다. Hub 수신(다수 스레드) ↔ 재도출(백그라운드 1개)
/// 사이의 lock-free 핸드오프라 CAS 로 구현한다.
///
/// <para>소비는 "내가 본 값과 현재 값이 같을 때만" 비운다(CAS). 재도출이 도는 동안 더 오래된 backfill 이
/// 들어오면 CAS 가 실패해 그 값이 남고 다음 주기가 처리한다 — 무조건 0 으로 밀면 그 구간이 영구 미재도출로
/// 남아(정확히 이 기능이 막으려던 증상) 조용히 사라진다.</para>
/// </summary>
public sealed class BackfillFloorTracker
{
    private long _floorUtcTicks;   // 0 = 미보유

    /// <summary>하한을 min 으로 갱신.</summary>
    public void Report(DateTime atUtc)
    {
        var ticks = (atUtc.Kind == DateTimeKind.Local ? atUtc.ToUniversalTime() : atUtc).Ticks;
        while (true)
        {
            var cur = Interlocked.Read(ref _floorUtcTicks);
            if (cur != 0 && cur <= ticks) return;                        // 이미 더 오래된 하한 보유
            if (Interlocked.CompareExchange(ref _floorUtcTicks, ticks, cur) == cur) return;
        }
    }

    /// <summary>현재 하한(UTC) — 미보유면 null. 읽기만 하고 비우지 않는다(재도출 성공 후 <see cref="Clear"/>).</summary>
    public DateTime? PeekUtc()
    {
        var ticks = Interlocked.Read(ref _floorUtcTicks);
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>재도출이 커버한 하한을 소비. 그 사이 더 오래된 값이 들어왔으면 남겨둔다.</summary>
    public void Clear(DateTime consumedUtc)
    {
        var ticks = (consumedUtc.Kind == DateTimeKind.Local ? consumedUtc.ToUniversalTime() : consumedUtc).Ticks;
        Interlocked.CompareExchange(ref _floorUtcTicks, 0, ticks);
    }
}
