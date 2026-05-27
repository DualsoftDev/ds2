using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Promaker.Knowledge;
using Llm.Shared.Api;

namespace Promaker.ViewModels;

/// <summary>
/// LlmChatViewModel partial — **PR-I5 (todo-documents-based-gfm.md §2 PR-I5 + §2.1 +
/// documents-based-gfm.md §8.7~§8.9)** specialized digest fetch + ApiChatProvider 주입.
/// <para/>
/// 책임:
/// <list type="bullet">
///   <item>활성 KB collection 의 local root 디렉토리에서 <c>.lighthouse-kb/summary/*.md</c> 합본 fetch
///   (<see cref="KbSpecializedDigestFetcher"/> 위임)</item>
///   <item>합본 결과를 <see cref="ApiChatProvider.SetPendingSpecializedDigest"/> 에 주입 — 다음 firstTurn 의
///   system prompt cache breakpoint 3 박제</item>
///   <item>chat panel open / KB collection 변경 시 trigger — <see cref="RefreshKbDigestAsync"/> 와 동일 lifecycle
///   (PR-F 의 KB digest path 정합, KB digest 갱신 후 specialized digest 도 동반 refresh)</item>
/// </list>
/// <para/>
/// **PoC 한정 (todo §2.1 PR-I5)** — KB collection 의 local sourceFolder 가 <see cref="LlmConfig.KbCollectionEntry"/>
/// 에 박제 안 됨 (현 schema). 본 partial 은 fetch path skeleton + ApiChatProvider 주입만 박제 — 실 sourceFolder
/// 의 ViewModel 측 cache / metadata fetch path 는 후속 PR backlog (server-side API 또는 LlmConfig schema 확장).
/// 현 phase 에서는 <see cref="GetActiveCollectionSourceRoots"/> 가 빈 list 반환 → 빈 digest 주입 → cache breakpoint 3
/// 박제 skip (PR-G v-b 와 wire 동치, 회귀 0). headless smoke (<c>SpecializedDigestInjectionTests</c>) 는 fetch path
/// 자체를 검증 (실 sourceFolder fixture 박제).
/// <para/>
/// **thread-affinity (PR-F 정합)**: <see cref="ApplyPendingSpecializedDigest"/> 는 UI thread (dispatcher) 에서만 호출.
/// fetch 자체는 file IO 동기 — background thread 호출 가능하나 본 partial 은 sequential caller (RefreshKbDigestAsync
/// 안에서 호출) 가정.
/// </summary>
public partial class LlmChatViewModel
{
    /// <summary>
    /// **PR-I5** — specialized digest 의 fetch 시점 cache. KB collection 변경 시 invalidate.
    /// 현 PoC 단계는 sourceRoot metadata 부재로 항상 빈 list — 후속 PR 에서 KbCollectionEntry / server-side fetch
    /// path 확장 시 본 cache 채워짐. caller (test / 후속 ViewModel hook) 가 <see cref="SetActiveCollectionSourceRoots"/>
    /// 로 직접 박제 가능 (headless smoke / 외부 자료 정렬 시연용).
    /// </summary>
    private IReadOnlyList<string> _specializedDigestRoots = Array.Empty<string>();

    /// <summary>
    /// **PR-I5 (PoC hook)** — 활성 KB collection 의 local sourceRoot list 반환. 현 phase 는 caller 가 직접 박제한
    /// <see cref="_specializedDigestRoots"/> snapshot 반환 (LlmConfig.KbCollections 의 metadata 부재 — 후속 확장 대기).
    /// <para/>
    /// 후속 PR (KbCollectionEntry.SourceFolder 추가 또는 server-side <c>.lighthouse-kb/summary/*.md</c> fetch API 노출)
    /// 시점에 본 메서드가 LlmConfig 또는 server response 에서 root 추출하도록 patch — caller (RefreshSpecializedDigestAsync)
    /// 는 변경 0.
    /// </summary>
    private IReadOnlyList<string> GetActiveCollectionSourceRoots()
    {
        return _specializedDigestRoots;
    }

    /// <summary>
    /// **PR-I5 (test / 후속 ViewModel hook 진입점)** — sourceRoot list 박제 + 즉시 <see cref="ApplyPendingSpecializedDigest"/>
    /// trigger. headless smoke 가 본 메서드로 fixture 박제 → ApiChatProvider 의 SetPendingSpecializedDigest 호출 확인.
    /// 후속 PR 의 KbCollectionEntry.SourceFolder 추가 시점에는 ConfigureProviderAsync / SubscribeKbProfileEvents
    /// hook 에서 본 메서드 호출하여 자동 박제.
    /// <para/>
    /// caller 가 null / 빈 list 박제 시 = specialized digest 비활성 (cache breakpoint 3 박제 skip, PR-G v-b 와 wire 동치).
    /// </summary>
    internal void SetActiveCollectionSourceRoots(IReadOnlyList<string>? roots)
    {
        _specializedDigestRoots = roots ?? Array.Empty<string>();
        ApplyPendingSpecializedDigest();
    }

    /// <summary>
    /// **PR-I5** — specialized digest fetch + ApiChatProvider 주입. <see cref="RefreshKbDigestAsync"/> 와 동일
    /// lifecycle (chat panel open / KB collection 변경 / SSE invalidate 시) 진입점. PR-F 의 KB digest path 정합 —
    /// fetch 실패 시 silent skip (Log.Warn) + 다음 firstTurn 에 영향 0.
    /// <para/>
    /// **review fail-safe (CLAUDE.md 정합)**: file IO 예외 (permission / disk full 등) 만 catch (광범위 흡수가 아닌
    /// 의도된 best-effort — chat 진입 자체는 막지 않는다). 외 예외는 fail-fast (root cause 노출).
    /// </summary>
    private async Task RefreshSpecializedDigestAsync()
    {
        try
        {
            await Task.Yield(); // UI thread block 회피 — file IO 양은 작으나 호출 시점 분리 정합.
            ApplyPendingSpecializedDigest();
        }
        catch (IOException ex)
        {
            Log.Warn("RefreshSpecializedDigestAsync 실패 — specialized digest 미갱신, chat 영향 0", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn("RefreshSpecializedDigestAsync 권한 실패 — specialized digest 미갱신, chat 영향 0", ex);
        }
    }

    /// <summary>
    /// **PR-I5 (PR-G ApplyPendingKbDigest 패턴 정합)** — sourceRoot snapshot → <see cref="KbSpecializedDigestFetcher.FetchMany"/>
    /// → <see cref="ApiChatProvider.SetPendingSpecializedDigest"/> path. 다음 firstTurn 진입 시점에 system message 의
    /// 3번째 TextContent (cache breakpoint 3) 로 swap (lazy apply, chat-scoped invariant 정합).
    /// <para/>
    /// API provider 만 적용 (Claude CLI / Codex CLI 는 별 path, 본 phase 미적용 — KB digest 정합). <c>_provider</c> 가
    /// null (init 미완료) 또는 다른 provider 일 때 silent skip.
    /// <para/>
    /// 빈 sourceRoot list / 모든 root 에 summary/ 부재 → 빈 digest 박제 (cache breakpoint 3 skip, PR-G v-b 와 wire 동치).
    /// </summary>
    private void ApplyPendingSpecializedDigest()
    {
        var roots = GetActiveCollectionSourceRoots();
        var digest = KbSpecializedDigestFetcher.FetchMany(roots);
        if (_provider is ApiChatProvider api)
            api.SetPendingSpecializedDigest(digest);
        if (Log.IsDebugEnabled)
            Log.Debug(
                $"ApplyPendingSpecializedDigest — digest len={digest.Length} (roots={roots.Count}, " +
                $"provider={_provider?.GetType().Name ?? "none"})");
    }
}
