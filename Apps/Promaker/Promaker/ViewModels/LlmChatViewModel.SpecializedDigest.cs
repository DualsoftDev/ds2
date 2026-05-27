using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Promaker.Knowledge;
using Promaker.LlmAgent;
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
/// **Backlog A (todo-documents-based-gfm.md §5.4 / §10.3 P2 hand-off)** — KbCollectionEntry.SourceFolder 추가 후
/// 본 partial 의 <see cref="GetActiveCollectionSourceRoots"/> 가 _config.KbCollections 의 Active=true + non-empty
/// SourceFolder 값 list 반환. caller (test / 후속 ViewModel hook) 가 <see cref="SetActiveCollectionSourceRoots"/>
/// 로 명시 박제 시 그 값이 우선 (headless smoke / 외부 자료 정렬 시연용 — config 우회).
/// <para/>
/// **thread-affinity (PR-F 정합)**: <see cref="ApplyPendingSpecializedDigest"/> 는 UI thread (dispatcher) 에서만 호출.
/// fetch 자체는 file IO 동기 — background thread 호출 가능하나 본 partial 은 sequential caller (RefreshKbDigestAsync
/// 안에서 호출) 가정.
/// </summary>
public partial class LlmChatViewModel
{
    /// <summary>
    /// **PR-I5 / Backlog A** — caller 명시 박제 override (test / 외부 hook). null 시 <see cref="_config"/>.KbCollections
    /// 기반 자동 추출. <see cref="SetActiveCollectionSourceRoots"/> 호출로 박제 → headless smoke / 시연 path.
    /// production GUI 는 본 override 사용 X — config 자동 추출만 진입.
    /// </summary>
    private IReadOnlyList<string>? _specializedDigestRootsOverride = null;

    /// <summary>
    /// **Backlog A** — 활성 KB collection 의 local source root list 반환.
    /// <list type="number">
    ///   <item><see cref="_specializedDigestRootsOverride"/> 가 박제되어 있으면 그 snapshot 반환 (test / 시연 path).</item>
    ///   <item>박제 안 됨 → <see cref="_config"/>.KbCollections 순회 — Active=true + non-empty SourceFolder 만 채집.</item>
    /// </list>
    /// fetch 자체 (`Directory.Exists` 검증 / 빈 디렉토리 skip) 는 <see cref="KbSpecializedDigestFetcher.FetchMany"/>
    /// 가 담당 — 본 메서드는 path string 만 반환. 빈 list = specialized digest skip = cache breakpoint 3 skip (회귀 0).
    /// </summary>
    private IReadOnlyList<string> GetActiveCollectionSourceRoots()
    {
        if (_specializedDigestRootsOverride is not null) return _specializedDigestRootsOverride;
        return ExtractActiveSourceRoots(_config);
    }

    /// <summary>
    /// **Backlog A — unit-testable filter helper**. <see cref="LlmConfig.KbCollections"/> 중 Active=true 이고
    /// SourceFolder 가 non-empty 인 entry 들의 SourceFolder 값 list 를 입력 순서 보존으로 반환.
    /// <para/>
    /// Promaker.Tests 의 `SpecializedDigestInjectionTests` 가 본 helper 를 직접 호출하여 회귀 검증
    /// (LlmChatViewModel 인스턴스 / WPF Dispatcher 의존 없이 schema-level 정합 fact 박제 가능).
    /// </summary>
    internal static IReadOnlyList<string> ExtractActiveSourceRoots(LlmConfig config)
    {
        if (config is null) return Array.Empty<string>();
        var roots = new List<string>();
        foreach (var entry in config.KbCollections)
        {
            if (!entry.Active) continue;
            if (string.IsNullOrEmpty(entry.SourceFolder)) continue;
            roots.Add(entry.SourceFolder);
        }
        return roots;
    }

    /// <summary>
    /// **PR-I5 (test / 후속 ViewModel hook 진입점)** — sourceRoot list 박제 + 즉시 <see cref="ApplyPendingSpecializedDigest"/>
    /// trigger. headless smoke 가 본 메서드로 fixture 박제 → ApiChatProvider 의 SetPendingSpecializedDigest 호출 확인.
    /// <para/>
    /// **production GUI override entry — 현재 Test only** (--review 라운드 1 Critical-2 hand-off 박제).
    /// PR-I5 시점에는 본 메서드를 호출하는 production GUI wiring 이 부재 (Initialize.cs / KbProfile.cs SSE
    /// invalidate / chat panel open 진입점 모두 미연결). `SpecializedDigestInjectionTests` 와 headless smoke
    /// 만 본 메서드를 호출하여 schema-level 정합 검증. 후속 PR (별 backlog — todo-documents-based-gfm.md §0
    /// "진입 대기 (Phase P2)") 에서 ConfigureProviderAsync / SubscribeKbProfileEvents hook 에 본 메서드 호출 추가
    /// 의무. PR-I5 commit message 의 "chat panel open / KB collection 변경 시 trigger" 명세는 본 후속 wiring PR 의
    /// 의무로 hand-off.
    /// <para/>
    /// caller 가 null / 빈 list 박제 시 = specialized digest 비활성 (cache breakpoint 3 박제 skip, PR-G v-b 와 wire 동치).
    /// </summary>
    internal void SetActiveCollectionSourceRoots(IReadOnlyList<string>? roots)
    {
        _specializedDigestRootsOverride = roots;
        ApplyPendingSpecializedDigest();
    }

    /// <summary>
    /// **PR-I5 + Backlog G (todo §2 PR-I5 review minor — async wiring)** — specialized digest fetch +
    /// ApiChatProvider 주입. <see cref="RefreshKbDigestAsync"/> 와 동일 lifecycle (chat panel open / KB collection 변경
    /// / SSE invalidate 시) 진입점. PR-F 의 KB digest path 정합 — fetch 실패 시 silent skip (Log.Warn) + 다음
    /// firstTurn 에 영향 0.
    /// <para/>
    /// **production GUI wiring 부재 — hand-off** (--review 라운드 1 Critical-2 박제). 본 메서드는 PR-I5 시점에는
    /// <see cref="SpecializedDigestInjectionTests"/> + headless smoke 만 호출. ConfigureProviderAsync /
    /// SubscribeKbProfileEvents / chat panel open 의 production GUI 진입점은 후속 PR (별 backlog —
    /// todo-documents-based-gfm.md §0 "진입 대기 (Phase P2)") 에서 wire. PR-I5 commit message 의 "chat open 시
    /// trigger" 명세는 본 후속 wiring PR 의 의무로 hand-off — PR-I5 자체는 schema + fetch logic + cache
    /// breakpoint 3 주입 path 박제만 책임.
    /// <para/>
    /// **Backlog G fix** — 기존 <c>await Task.Yield()</c> 후 동기 IO 패턴 (정합 부족) 을 진정한 async 로 전환:
    /// <see cref="KbSpecializedDigestFetcher.FetchManyAsync"/> (F# <c>buildManyAsync</c> wrap) 가
    /// <see cref="File.ReadAllTextAsync(string, System.Text.Encoding, CancellationToken)"/> 로 IO 수행 → UI thread
    /// block 0. 합본 결과는 동기 <see cref="KbSpecializedDigestFetcher.FetchMany"/> 와 byte-identical 보장 (회귀 0).
    /// 동기 <see cref="ApplyPendingSpecializedDigest"/> 는 <see cref="SetActiveCollectionSourceRoots"/> 등 sync caller
    /// 호환 위해 그대로 유지 — fetch 비용 작아 sync 도 허용 (test/시연 path).
    /// <para/>
    /// **Backlog J (LlmChatViewModel.SpecializedDigest.cs §J)** — provider 박제 + Log 의 인라인 복제를
    /// <see cref="ApplyFetchedDigest"/> SSOT helper 로 추출. 본 메서드는 (1) roots 채집 (2) async fetch
    /// (3) helper 호출 만 담당 — sync path (<see cref="ApplyPendingSpecializedDigest"/>) 와 wire byte-equal.
    /// <para/>
    /// **review fail-safe (CLAUDE.md 정합)**: file IO 예외 (permission / disk full 등) 만 catch (광범위 흡수가 아닌
    /// 의도된 best-effort — chat 진입 자체는 막지 않는다). 외 예외는 fail-fast (root cause 노출).
    /// </summary>
    private async Task RefreshSpecializedDigestAsync()
    {
        try
        {
            var roots = GetActiveCollectionSourceRoots();
            // Backlog G — 진정한 async IO (F# `buildManyAsync` 의 `File.ReadAllTextAsync` path).
            // 동기 FetchMany 와 byte-identical (FileSeparator / path-sort / UTF-8 동일 SSOT 재사용).
            var digest = await KbSpecializedDigestFetcher
                .FetchManyAsync(roots, CancellationToken.None)
                .ConfigureAwait(true); // UI thread 로 복귀 — _provider 박제는 UI thread invariant.
            ApplyFetchedDigest(digest, roots.Count, nameof(RefreshSpecializedDigestAsync));
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
    /// <para/>
    /// **production GUI wiring 부재 — hand-off** (--review 라운드 1 Critical-2 박제). sync path 역시 PR-I5 시점에는
    /// <see cref="SetActiveCollectionSourceRoots"/> (test override) 만 호출. production GUI 진입점 (chat panel open
    /// / KB collection 변경 등) wiring 은 별 PR backlog 로 hand-off — todo-documents-based-gfm.md §0 "진입 대기
    /// (Phase P2)" 박제 정합.
    /// <para/>
    /// **Backlog J (todo §J)** — provider 박제 + Log 인라인 복제를 <see cref="ApplyFetchedDigest"/> SSOT helper 로
    /// 추출. 본 메서드는 sync fetch + helper 호출만 담당 — async path 와 wire byte-equal.
    /// </summary>
    private void ApplyPendingSpecializedDigest()
    {
        var roots = GetActiveCollectionSourceRoots();
        var digest = KbSpecializedDigestFetcher.FetchMany(roots);
        ApplyFetchedDigest(digest, roots.Count, nameof(ApplyPendingSpecializedDigest));
    }

    /// <summary>
    /// **Backlog J (todo-documents-based-gfm.md §J — SSOT 통합)** — fetched specialized digest 를
    /// <see cref="ApiChatProvider.SetPendingSpecializedDigest"/> 에 박제 + Log.Debug 출력하는 SSOT helper.
    /// <see cref="RefreshSpecializedDigestAsync"/> (async) 와 <see cref="ApplyPendingSpecializedDigest"/> (sync)
    /// 양 path 가 본 helper 호출 — 인라인 복제 제거 (drift 방지). caller prefix (<paramref name="callerLabel"/>) 만
    /// path 별 다르게 전달하여 기존 Log 메시지 wire byte-equal 보장 (회귀 0).
    /// <para/>
    /// **thread-affinity**: 본 helper 는 <c>_provider</c> 박제 — UI thread (dispatcher) 에서만 호출 가정.
    /// async path 는 <c>ConfigureAwait(true)</c> 로 UI thread 복귀 후 호출, sync path 는 caller 가 이미 UI thread.
    /// </summary>
    private void ApplyFetchedDigest(string digest, int rootsCount, string callerLabel)
    {
        if (_provider is ApiChatProvider api)
            api.SetPendingSpecializedDigest(digest);
        if (Log.IsDebugEnabled)
            Log.Debug(
                $"{callerLabel} — digest len={digest.Length} (roots={rootsCount}, " +
                $"provider={_provider?.GetType().Name ?? "none"})");
    }
}
