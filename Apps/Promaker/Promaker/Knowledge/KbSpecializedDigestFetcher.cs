using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ds2.LightHouse;
using log4net;

namespace Promaker.Knowledge;

/// <summary>
/// **PR-I5 (todo-documents-based-gfm.md §2 PR-I5 + §2.1 + documents-based-gfm.md §8.7~§8.9)** —
/// 활성 KB collection 의 local root 디렉토리에서 `.lighthouse-kb/summary/*.md` 합본 markdown 을 fetch.
/// <para/>
/// 본 fetcher 는 <see cref="SpecializedDigestBuilder"/> (Ds2.LightHouse F# lib) 의 thin wrapper —
/// ViewModel 친화성 (C# 호출 + Promaker.Knowledge namespace 정합) + null-graceful 변환 (rootDir 부재 / 빈
/// 디렉토리 시 빈 string 반환) 만 추가. <see cref="ApiChatProvider.SetPendingSpecializedDigest"/> 가 빈 string
/// 박제 시 cache breakpoint 3 skip → wire 동치 (PR-G v-b 와 회귀 0, todo §2.1 PR-I5 정합).
/// <para/>
/// **PoC 한정**: rootDir 입력은 caller (LlmChatViewModel) 의 책임. 현 phase 에서는 server-side
/// `.lighthouse-kb/summary/` fetch path 가 별도 LightHouseClient API 로 노출 안 됨 (PR-I3 의 Packager
/// whitelist 만 활성) — production 통합은 후속 PR backlog.
/// <para/>
/// **PR-F (KbProfileExtractor) 정합**: static helper + fail-safe (file IO 실패 시 Log.Warn + 빈 string).
/// <see cref="OperationCanceledException"/> 등 cancellation 은 본 helper 가 동기 IO 만 수행하므로 무관.
/// </summary>
internal static class KbSpecializedDigestFetcher
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(KbSpecializedDigestFetcher));

    // **A·m2 (Outlier/Minor 묶음 2) — dead const 제거**: 합본 markdown 사이 separator 의 *실제 SSOT* 는
    // `SpecializedDigestBuilder.fs` 의 `FileSeparator = "\n\n---\n\n"` (F# private literal). 본 C# wrapper 는
    // F# `buildMany` 결과 `Combined` 만 forward 하므로 separator literal 사본을 보유할 필요 0. 종전 박제된
    // `CollectionSeparator` const 는 호출처 0 의 dead code 였으며, 사본 보유 자체가 drift 위험 (F# 측 변경
    // 시 C# 사본 stale silent). 제거 후 SSOT 위치만 본 anchor 주석으로 명시.

    /// <summary>
    /// 단일 collection root → `SpecializedDigestBuilder.build` 결과의 합본 string 반환.
    /// <list type="bullet">
    ///   <item><paramref name="collectionRoot"/> 가 null / 빈 string / 디렉토리 부재 → 빈 string</item>
    ///   <item><c>.lighthouse-kb/summary/</c> 디렉토리 부재 또는 `*.md` 0개 → 빈 string (cache breakpoint 3 skip)</item>
    ///   <item>합본 성공 → markdown string (`StrategyMarkdown` 머리말/footer 포함)</item>
    /// </list>
    /// 호출 비용 = `Directory.GetFiles` + 각 파일 `ReadAllText` 동기 IO. 광명2 3 자료 기준 ~38K tokens ≈ 수십 KB,
    /// localhost 가정 시 ~수십 ms.
    /// </summary>
    public static string Fetch(string? collectionRoot)
    {
        if (string.IsNullOrEmpty(collectionRoot)) return "";
        if (!Directory.Exists(collectionRoot))
        {
            Log.Debug($"KbSpecializedDigestFetcher.Fetch — collectionRoot 부재 skip: {collectionRoot}");
            return "";
        }
        // F# 모듈 호출 — Ds2.LightHouse.SpecializedDigestBuilder.build(rootDir) → DigestResult.Combined.
        // file IO 실패 (permission / disk full 등) 는 fail-fast — caller 의 ApplyPendingSpecializedDigest 가
        // try/catch 흡수 (KB digest 와 동일 fail-safe path).
        var result = SpecializedDigestBuilder.build(collectionRoot);
        if (Log.IsDebugEnabled)
            Log.Debug(
                $"KbSpecializedDigestFetcher.Fetch — root={collectionRoot} files={result.Metadata.FileCount} " +
                $"tokens={result.Metadata.EstimatedTokens}");
        return result.Combined;
    }

    /// <summary>
    /// 다중 collection root → `SpecializedDigestBuilder.buildMany` 결과의 합본 string 반환.
    /// 빈 list / null 입력 → 빈 string. 각 root 의 빈 합본은 separator skip (F# 측 동일 정책).
    /// <para/>
    /// 사용처 = <see cref="LlmChatViewModel"/> 의 multi-collection 시나리오. 현 PoC 단계에서는 단일 collection
    /// 시연이지만 본 helper 는 multi 진입 가능 (future-proof, todo §2.1 PR-I5 영역 안).
    /// </summary>
    public static string FetchMany(IEnumerable<string>? collectionRoots)
    {
        if (collectionRoots is null) return "";
        var existing = collectionRoots
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .ToArray();
        if (existing.Length == 0) return "";
        // F# 측 buildMany 사용 — 빈 합본은 자동 skip + separator 박제 (SpecializedDigestBuilder.FileSeparator SSOT).
        var result = SpecializedDigestBuilder.buildMany(existing);
        if (Log.IsDebugEnabled)
            Log.Debug(
                $"KbSpecializedDigestFetcher.FetchMany — roots={existing.Length} files={result.Metadata.FileCount} " +
                $"tokens={result.Metadata.EstimatedTokens}");
        return result.Combined;
    }

    // ── Backlog G (todo-documents-based-gfm.md §2 PR-I5 review minor) ───────────
    // 비동기 overload — `LlmChatViewModel.RefreshSpecializedDigestAsync` 의 `Task.Yield()` 후
    // 동기 IO 패턴을 정합 async 로 전환. 동기 `Fetch` / `FetchMany` 는 backward-compat 위해 그대로 유지.
    // F# `buildAsync` / `buildManyAsync` thin wrapper — null-graceful / 디렉토리 부재 graceful 동기 path SSOT 재사용.

    /// <summary>
    /// 단일 collection root → <see cref="SpecializedDigestBuilder.buildAsync"/> 결과의 합본 string 비동기 반환.
    /// 동기 <see cref="Fetch"/> 와 byte-identical (FileSeparator / path-sort / UTF-8 동일).
    /// null / 빈 / 부재 root → 빈 string. cancellation token 지원 (각 파일 IO 사이 throw).
    /// </summary>
    public static async Task<string> FetchAsync(string? collectionRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(collectionRoot)) return "";
        if (!Directory.Exists(collectionRoot))
        {
            Log.Debug($"KbSpecializedDigestFetcher.FetchAsync — collectionRoot 부재 skip: {collectionRoot}");
            return "";
        }
        var result = await SpecializedDigestBuilder.buildAsync(collectionRoot, ct).ConfigureAwait(false);
        if (Log.IsDebugEnabled)
            Log.Debug(
                $"KbSpecializedDigestFetcher.FetchAsync — root={collectionRoot} files={result.Metadata.FileCount} " +
                $"tokens={result.Metadata.EstimatedTokens}");
        return result.Combined;
    }

    /// <summary>
    /// 다중 collection root → <see cref="SpecializedDigestBuilder.buildManyAsync"/> 결과의 합본 string 비동기 반환.
    /// 동기 <see cref="FetchMany"/> 와 byte-identical (separator skip 정책 동일).
    /// null / 빈 list → 빈 string. 각 root 의 빈 합본은 separator skip (F# 측 동일 정책).
    /// </summary>
    public static async Task<string> FetchManyAsync(
        IEnumerable<string>? collectionRoots, CancellationToken ct = default)
    {
        if (collectionRoots is null) return "";
        var existing = collectionRoots
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .ToArray();
        if (existing.Length == 0) return "";
        var result = await SpecializedDigestBuilder.buildManyAsync(existing, ct).ConfigureAwait(false);
        if (Log.IsDebugEnabled)
            Log.Debug(
                $"KbSpecializedDigestFetcher.FetchManyAsync — roots={existing.Length} files={result.Metadata.FileCount} " +
                $"tokens={result.Metadata.EstimatedTokens}");
        return result.Combined;
    }
}
