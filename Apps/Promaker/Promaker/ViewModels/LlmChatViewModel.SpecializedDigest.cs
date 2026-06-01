using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ds2.LlmAgent;
using Promaker.Knowledge;
using Llm.Shared.Api;

namespace Promaker.ViewModels;

/// <summary>
/// LlmChatViewModel partial — **specialized digest(RAG layer E) MCP fetch + ApiChatProvider 주입** (사용자 지시 2026-06-01).
/// <para/>
/// 책임:
/// <list type="bullet">
///   <item>active 셋(<see cref="_acceptedCollectionIds"/>)의 collection 마다 MCP <c>attachment_summary(includeSpecialized=true)</c>
///   로 specialized digest(<c>summary.md</c> + <c>summary/*.md</c> 합본) 수신
///   (<see cref="KbSpecializedDigestFetcher.FetchManyViaMcpAsync"/>)</item>
///   <item>합본 결과를 <see cref="ApiChatProvider.SetPendingSpecializedDigest"/> 에 주입 — 다음 firstTurn 의 system prompt
///   cache breakpoint 3 박제</item>
///   <item>chat panel open / provider 토글 / SSE invalidate 시 trigger (<see cref="RefreshKbDigestAsync"/> 와 동일 lifecycle)</item>
/// </list>
/// <para/>
/// **로컬 SourceFolder read 폐기**: 구 PR-I5 는 <c>&lt;SourceFolder&gt;/.lighthouse-kb/summary/*.md</c> 직접 read 라 로컬에
/// summary/ 가 없는 **원격 service 를 지원하지 못했음**. MCP fetch 전환으로 로컬/원격 모두 지원 — collection 식별자
/// (<see cref="_acceptedCollectionIds"/>)만 의존, 로컬 경로 / <c>KbCollectionEntry.SourceFolder</c> 불요.
/// <para/>
/// **thread-affinity**: <see cref="ApplyFetchedDigest"/> 의 <c>_provider</c> 박제는 UI thread (dispatcher). MCP fetch 는
/// async (네트워크) — <c>ConfigureAwait(true)</c> 로 UI thread 복귀 후 박제.
/// </summary>
public partial class LlmChatViewModel
{
    /// <summary>
    /// **specialized digest(layer E) fetch + 주입** — <see cref="RefreshKbDigestAsync"/> (초기 fetch / SSE invalidate) /
    /// <see cref="ConfigureProviderAsync"/> (provider 토글) 진입점에서 호출. active 셋의 collection 마다 MCP
    /// <c>attachment_summary(includeSpecialized=true)</c> 호출 → 합본 → <see cref="ApiChatProvider.SetPendingSpecializedDigest"/>
    /// → 다음 firstTurn cache breakpoint 3.
    /// <para/>
    /// best-effort — fetch 실패(인증/네트워크/직렬화)는 Log.Warn 후 빈 digest (chat 진입 차단 0). 빈 active 셋 / 전부
    /// 빈 응답 → 빈 digest → breakpoint 3 skip (회귀 0).
    /// </summary>
    private async Task RefreshSpecializedDigestAsync()
    {
        try
        {
            var digest = await KbSpecializedDigestFetcher
                .FetchManyViaMcpAsync(_acceptedCollectionIds, CancellationToken.None)
                .ConfigureAwait(true); // UI thread 복귀 — _provider 박제 invariant.
            ApplyFetchedDigest(digest, _acceptedCollectionIds.Count, nameof(RefreshSpecializedDigestAsync));
        }
        catch (Exception ex)
        {
            // MCP fetch best-effort — 인증/네트워크/직렬화 실패 모두 흡수 (chat 진입 자체는 막지 않음).
            Log.Warn("RefreshSpecializedDigestAsync 실패 — specialized digest 미갱신, chat 영향 0", ex);
        }
    }

    /// <summary>
    /// fetched specialized digest 를 <see cref="ApiChatProvider.SetPendingSpecializedDigest"/> 에 박제 + Log.Debug.
    /// API provider 만 적용 (Claude CLI / Codex CLI 는 별 path — KB digest 정합). <c>_provider</c> 가 null (init 미완료)
    /// 또는 다른 provider 일 때 silent skip. 빈 digest 박제 시 = cache breakpoint 3 skip (PR-G v-b wire 동치).
    /// <para/>
    /// **thread-affinity**: <c>_provider</c> 박제는 UI thread (dispatcher). <see cref="AssertUiThread"/> 로 진입 시 강제.
    /// </summary>
    private void ApplyFetchedDigest(string digest, int serviceScopeCount, string callerLabel)
    {
        AssertUiThread(callerLabel);
        // CLI(Claude/Codex) + API provider 모두 `ILlmSystemPromptDigestSink` 구현 → 단일 캐스팅 path.
        if (_provider is ILlmSystemPromptDigestSink sink)
            sink.SetPendingSpecializedDigest(digest);
        // **주입 로그 (사용자 요청 2026-06-01)** — Info 레벨로 주입 여부/크기 노출 (Debug 면 production 에서 놓침).
        // digest len=0 이면 cache breakpoint 3 skip = layer E 미주입 → 즉시 원인 식별 가능.
        Log.Info(
            $"[layer E] {callerLabel} — specialized digest len={digest.Length} " +
            $"(services={serviceScopeCount}, provider={_provider?.GetType().Name ?? "none"})");
    }

    /// <summary>
    /// **UI thread invariant fail-fast assert**. <c>_provider</c> 박제 / Log 출력은 WPF Dispatcher (UI thread) 에서만.
    /// <see cref="Application.Current"/> 가 null (xUnit headless) 인 경우 skip — WPF host 없이 schema-level 검증 가능.
    /// </summary>
    private static void AssertUiThread(string callerLabel)
    {
        var app = Application.Current;
        if (app is null) return; // test / headless path — Dispatcher 부재 우회.
        if (!app.Dispatcher.CheckAccess())
            throw new InvalidOperationException(
                $"{callerLabel}: UI thread invariant 위반 — Application.Current.Dispatcher 외 thread 에서 호출됨. " +
                "_provider 박제 / WPF binding 갱신은 UI thread 에서만 허용 (caller 의 ConfigureAwait(true) wrap 필요).");
    }
}
