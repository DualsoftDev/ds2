// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services;

/// <summary>
/// 사이클 "완료 마커"의 <b>단일 소스 규칙</b> — 화면(<see cref="DSPilot.Controllers.CallTestController"/>)과
/// 과거 history 재계산(<see cref="CycleRecomputeService"/>)이 같은 규칙을 쓰게 해 측정 정의 드리프트를 막는다.
///
/// <para>진영 B 폴라리티 기준:</para>
/// <list type="bullet">
///   <item>Tail 에 InTag(응답)가 있으면 → 완료 = Tail <b>InTag↑</b>(rising). 정통 정의(명령→응답).</item>
///   <item>InTag 가 없는 OutOnly Tail 이면 → 완료 = Tail <b>OutTag↓</b>(falling = 명령 종료).
///     "명령 ON 시간"을 MT 로 보는 추정 — UI 는 <c>OutTag</c> source 로 표기해 "정통 완료"와 구분한다.</item>
///   <item>둘 다 없으면 완료 마커 없음(None) → CT(주기)만, MT/WT 분해 불가.</item>
/// </list>
/// <para>라이브 엔진은 OutOnly Call 의 Going 이탈(OutTag↓)을 이미 완료로 처리하므로
/// (<see cref="SimulationEngineService"/> finishingGoing) 이 규칙과 자동 정합한다.</para>
/// </summary>
public static class CycleCompletionResolver
{
    /// <summary>완료 마커 소스 종류.</summary>
    public enum CompletionSource { None, InTag, OutTag }

    /// <summary>완료 마커 태그 주소 + 엣지 방향(true=하강/OutTag↓, false=상승/InTag↑) + 소스 종류.</summary>
    public readonly record struct TailCompletion(string? Tag, bool Falling, CompletionSource Source);

    /// <summary>
    /// Tail Call 의 (InTag, OutTag) → 완료 마커 결정. InTag↑ 우선, 없으면 OutTag↓(추정).
    /// </summary>
    public static TailCompletion Resolve(string? tailInTag, string? tailOutTag)
    {
        if (!string.IsNullOrWhiteSpace(tailInTag))
            return new TailCompletion(tailInTag, false, CompletionSource.InTag);
        if (!string.IsNullOrWhiteSpace(tailOutTag))
            return new TailCompletion(tailOutTag, true, CompletionSource.OutTag);
        return new TailCompletion(null, false, CompletionSource.None);
    }

    /// <summary>DTO/UI 표기용 문자열("InTag" | "OutTag" | null). null 은 완료 마커 없음.</summary>
    public static string? SourceLabel(CompletionSource s) => s switch
    {
        CompletionSource.InTag => "InTag",
        CompletionSource.OutTag => "OutTag",
        _ => null,
    };
}
