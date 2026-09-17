// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;

namespace DSPilot.Kpi;

/// <summary>
/// 시간 기반 코어의 시각 변환 SSOT — 전부 Unix epoch ms(UTC).
/// <para>
/// 구 스키마는 텍스트 DATETIME 을 쓰다 파싱 결과가 Kind=Local 로 돌아와 9시간 오차를 만드는 함정이 있었다.
/// 새 코어는 정수 epoch 하나만 쓰고, DateTime 으로 나갈 때 항상 Kind=Utc 를 명시한다.
/// </para>
/// </summary>
public static class KpiTime
{
    /// <summary>DateTime → epoch ms. Kind 가 Unspecified 면 UTC 로 간주한다(내부 저장 규약).</summary>
    public static long ToMs(DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Local => dt.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _ => dt,
        };
        return (long)(utc - DateTime.UnixEpoch).TotalMilliseconds;
    }

    /// <summary>epoch ms → DateTime(Kind=Utc).</summary>
    public static DateTime ToUtc(long ms) => DateTime.UnixEpoch.AddMilliseconds(ms);

    /// <summary>epoch ms → 로컬 시각. 화면·일자 버킷 전용.</summary>
    public static DateTime ToLocal(long ms) => ToUtc(ms).ToLocalTime();

    /// <summary>현재 시각(epoch ms).</summary>
    public static long NowMs() => ToMs(DateTime.UtcNow);

    /// <summary>epoch ms → 로컬 날짜 문자열(yyyy-MM-dd). 기준선 일별 스냅샷 키.</summary>
    public static string LocalDate(long ms) =>
        ToLocal(ms).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>ISO 8601(UTC, Z) 문자열 — API 응답용.</summary>
    public static string ToIso(long ms) =>
        ToUtc(ms).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
