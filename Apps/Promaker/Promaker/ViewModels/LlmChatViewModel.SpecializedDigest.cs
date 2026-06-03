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
///   <item>provider 구성/토글/재시작 및 SSE invalidate 시 trigger</item>
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
    /// <summary>layer E fetch 의 IsReady 블로킹 상한 (backstop). <see cref="ConfigureProviderAsync"/> 가 IsReady 전에
    /// 동기 await 하므로, 무응답/느린 service 가 chat 진입을 무한 지연시키지 않도록 제한. 초과 시
    /// OperationCanceledException → best-effort 빈 digest 로 진행 (다음 SSE / 재시작에서 재시도).</summary>
    private static readonly TimeSpan SpecializedDigestFetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// **specialized digest(layer E) fetch + 주입** — <see cref="ConfigureProviderAsync"/> (provider 구성/토글/재시작,
    /// <paramref name="targetProvider"/> 명시 전달로 자기 provider 에만 박제) / <see cref="RefreshKbDigestAsync"/>
    /// (SSE invalidate, 인자 없이 호출 → <c>_provider</c> fallback best-effort) 진입점에서 호출. active 셋의 collection 마다 MCP
    /// <c>attachment_summary(includeSpecialized=true)</c> 호출 → 합본 → <see cref="ApiChatProvider.SetPendingSpecializedDigest"/>
    /// → 다음 firstTurn cache breakpoint 3.
    /// <para/>
    /// best-effort — fetch 실패(인증/네트워크/직렬화)는 Log.Warn 후 빈 digest (chat 진입 차단 0). 빈 active 셋 / 전부
    /// 빈 응답 → 빈 digest → breakpoint 3 skip (회귀 0).
    /// </summary>
    private async Task RefreshSpecializedDigestAsync(ILlmProvider? targetProvider = null)
    {
        try
        {
            // layer E fetch 는 ConfigureProviderAsync 가 IsReady 전에 동기 await 하므로, 무응답/느린 service 가 chat
            // 진입을 무한 지연(LightHouseClient HttpClient.Timeout=10분)시키지 않도록 timeout backstop. 초과 시
            // OperationCanceledException → 아래 catch 가 빈 digest 로 흡수 (다음 SSE / 재시작에서 재시도).
            using var fetchCts = new CancellationTokenSource(SpecializedDigestFetchTimeout);
            var digest = await KbSpecializedDigestFetcher
                .FetchManyViaMcpAsync(_acceptedCollectionIds, fetchCts.Token)
                .ConfigureAwait(true); // UI thread 복귀 — _provider 박제 invariant.
            ApplyFetchedDigest(digest, _acceptedCollectionIds.Count, nameof(RefreshSpecializedDigestAsync), targetProvider);
        }
        catch (Exception ex)
        {
            // MCP fetch best-effort — 인증/네트워크/직렬화/timeout(OperationCanceled) 실패 모두 흡수 (chat 진입 차단 0).
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
    private void ApplyFetchedDigest(
        string digest,
        int serviceScopeCount,
        string callerLabel,
        ILlmProvider? targetProvider = null)
    {
        AssertUiThread(callerLabel);
        // stale 가드 — targetProvider(ConfigureProviderAsync 가 넘긴 자기 provider)가 더 이상 active(_provider)가 아니면
        // (await 사이 다른 switch 가 _provider 교체) 폐기 예정 provider 박제는 무의미 → skip. SSE 경로(targetProvider=null)는
        // _provider fallback 이라 본 가드 비대상 (debounce 후 UI marshalling 시점의 현재 provider 가 맞음).
        if (targetProvider is not null && !ReferenceEquals(targetProvider, _provider))
        {
            Log.Debug($"[layer E] {callerLabel} — stale provider 박제 skip (target={targetProvider.GetType().Name})");
            return;
        }
        var provider = targetProvider ?? _provider;
        // CLI(Claude/Codex) + API provider 모두 `ILlmSystemPromptDigestSink` 구현 → 단일 캐스팅 path.
        if (provider is ILlmSystemPromptDigestSink sink)
            sink.SetPendingSpecializedDigest(digest);
        // **주입 로그 (사용자 요청 2026-06-01)** — Info 레벨로 주입 여부/크기 노출 (Debug 면 production 에서 놓침).
        // digest len=0 이면 cache breakpoint 3 skip = layer E 미주입 → 즉시 원인 식별 가능.
        Log.Info(
            $"[layer E] {callerLabel} — specialized digest len={digest.Length} " +
            $"(services={serviceScopeCount}, provider={provider?.GetType().Name ?? "none"})");
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
