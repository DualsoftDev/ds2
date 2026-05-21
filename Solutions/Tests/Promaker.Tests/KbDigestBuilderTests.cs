using System;
using System.Collections.Generic;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **PR-G (todo-lighthouse-index-summary.md §5.2)** — KbDigestBuilder 단위 시험.
///
/// scope: KbProfile dict → system prompt digest text 의 형식 / fallback / 빈 input 분기.
/// </summary>
public sealed class KbDigestBuilderTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<CollectionInfo>> One(string serviceId, params CollectionInfo[] colls) =>
        new Dictionary<string, IReadOnlyList<CollectionInfo>> { [serviceId] = colls };

    [Fact]
    public void Build_빈_dict_빈_string()
    {
        var result = KbDigestBuilder.Build(
            new Dictionary<string, IReadOnlyList<CollectionInfo>>());
        Assert.Equal("", result);
    }

    [Fact]
    public void Build_단일_collection_header_와_keywords_inline()
    {
        var profiles = One("svc-A",
            new CollectionInfo
            {
                Id = "id-1",
                DisplayName = "Poc",
                Description = "round-trip cache 측정",
                Keywords = new[] { "cache_rd", "cache_cr", "token", "turn" },
            });

        var result = KbDigestBuilder.Build(profiles);

        Assert.Contains(KbDigestBuilder.SectionHeader, result);
        Assert.Contains("\"Poc\"", result);
        Assert.Contains("round-trip cache 측정", result);
        Assert.Contains("keywords: cache_rd, cache_cr, token, turn", result);
        // PR-E 흡수 — fulltext fallback 안내 박제.
        Assert.Contains("attachment_fulltext(fileId)", result);
    }

    [Fact]
    public void Build_keyword_빈_collection_title_만_노출()
    {
        // legacy collection (PR-B 이전 색인) — keywords 빈 array. title 만 노출.
        var profiles = One("svc-A",
            new CollectionInfo
            {
                Id = "id-legacy",
                DisplayName = "OldDocs",
                Description = "",
                Keywords = Array.Empty<string>(),
            });

        var result = KbDigestBuilder.Build(profiles);

        Assert.Contains("\"OldDocs\"", result);
        Assert.DoesNotContain("keywords:", result);
    }

    [Fact]
    public void Build_다중_service_다중_collection_모두_나열()
    {
        var profiles = new Dictionary<string, IReadOnlyList<CollectionInfo>>
        {
            ["svc-A"] = new[]
            {
                new CollectionInfo { Id = "a1", DisplayName = "Poc", Keywords = new[] { "k1", "k2" } },
            },
            ["svc-B"] = new[]
            {
                new CollectionInfo { Id = "b1", DisplayName = "Promaker Docs", Keywords = new[] { "prompt", "MCP" } },
                new CollectionInfo { Id = "b2", DisplayName = "LightHouse", Keywords = new[] { "indexer" } },
            },
        };

        var result = KbDigestBuilder.Build(profiles);

        Assert.Contains("\"Poc\"", result);
        Assert.Contains("\"Promaker Docs\"", result);
        Assert.Contains("\"LightHouse\"", result);
        // displayName 빈 collection 은 skip — 모두 박제됐는지 검증 (3 entry).
        var poc = result.IndexOf("\"Poc\"", StringComparison.Ordinal);
        var docs = result.IndexOf("\"Promaker Docs\"", StringComparison.Ordinal);
        Assert.True(poc < docs, "service / collection 순서 박제 정합");
    }

    [Fact]
    public void Build_모든_collection_displayName_빈_시_빈_string()
    {
        // 모든 collection 의 displayName 이 빈 (server-side 결함 fail-safe) → digest 자체 비활성.
        var profiles = One("svc-A",
            new CollectionInfo { Id = "x", DisplayName = "", Keywords = new[] { "k1" } });

        var result = KbDigestBuilder.Build(profiles);
        Assert.Equal("", result);
    }
}
