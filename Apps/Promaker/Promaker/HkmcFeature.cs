using System;

namespace Promaker;

/// <summary>
/// HKMC (현대/기아) 전용 기능 게이팅 SSOT. <c>ENABLE_HKMC</c> 환경변수 평가는 본 클래스에서만 수행한다.
/// process 시작 시 1회 평가되며 재시작 없이 toggle 되지 않는다.
/// </summary>
internal static class HkmcFeature
{
    /// <summary>
    /// <c>ENABLE_HKMC</c> 환경변수가 null/빈 문자열이 아니면 true.
    /// </summary>
    public static bool IsEnabled { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ENABLE_HKMC"));
}
