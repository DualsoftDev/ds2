using System;
using System.Collections.Generic;
using Promaker.Knowledge;
using Llm.Shared;
using Llm.Shared.Abstractions;
using Llm.Shared.Api;
using Llm.Shared.Mcp;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **PR-G (todo-lighthouse-index-summary.md §5.2)** — KbDigestBuilder 단위 시험.
///
/// scope: KbProfile dict → system prompt digest text 의 형식 / fallback / 빈 input 분기.
/// </summary>
public sealed class KbDigestBuilderTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<KbCollectionDescriptor>> One(string serviceId, params KbCollectionDescriptor[] colls) =>
        new Dictionary<string, IReadOnlyList<KbCollectionDescriptor>> { [serviceId] = colls };

    [Fact]
    public void Build_빈_dict_빈_string()
    {
        var result = KbDigestBuilder.Build(
            new Dictionary<string, IReadOnlyList<KbCollectionDescriptor>>());
        Assert.Equal("", result);
    }

    [Fact]
    public void Build_단일_collection_header_와_keywords_inline()
    {
        var profiles = One("svc-A",
            new KbCollectionDescriptor
            {
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
        // **PR-H3 (todo §11)** — γ hybrid: collection overview attachment_summary 안내 박제.
        Assert.Contains("attachment_summary(collectionId)", result);
    }

    [Fact]
    public void Build_keyword_빈_collection_title_만_노출()
    {
        // legacy collection (PR-B 이전 색인) — keywords 빈 array. title 만 노출.
        var profiles = One("svc-A",
            new KbCollectionDescriptor
            {
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
        var profiles = new Dictionary<string, IReadOnlyList<KbCollectionDescriptor>>
        {
            ["svc-A"] = new[]
            {
                new KbCollectionDescriptor { DisplayName = "Poc", Keywords = new[] { "k1", "k2" } },
            },
            ["svc-B"] = new[]
            {
                new KbCollectionDescriptor { DisplayName = "Promaker Docs", Keywords = new[] { "prompt", "MCP" } },
                new KbCollectionDescriptor { DisplayName = "LightHouse", Keywords = new[] { "indexer" } },
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
            new KbCollectionDescriptor { DisplayName = "", Keywords = new[] { "k1" } });

        var result = KbDigestBuilder.Build(profiles);
        Assert.Equal("", result);
    }

    [Fact]
    public void Build_description_없을_시_emdash_미부착()
    {
        // **review m-6** — description 빈 collection 은 `"Name" — desc` 의 emdash 자체 미부착.
        var profiles = One("svc-A",
            new KbCollectionDescriptor { DisplayName = "Foo", Description = "", Keywords = new[] { "k1" } });

        var result = KbDigestBuilder.Build(profiles);
        Assert.Contains("\"Foo\"", result);
        Assert.DoesNotContain("Foo\" —", result);
    }

    [Fact]
    public void Build_service_id_정렬_결정성_Ordinal()
    {
        // **review m-3** — Anthropic prompt cache prefix-match 정합 위해 service id 정렬 결정성.
        // 입력 순서가 B → A 라도 출력은 A → B (Ordinal).
        var profiles = new Dictionary<string, IReadOnlyList<KbCollectionDescriptor>>
        {
            ["svc-B"] = new[] { new KbCollectionDescriptor { DisplayName = "B-col", Keywords = new[] { "bk" } } },
            ["svc-A"] = new[] { new KbCollectionDescriptor { DisplayName = "A-col", Keywords = new[] { "ak" } } },
        };

        var result = KbDigestBuilder.Build(profiles);
        var aPos = result.IndexOf("\"A-col\"", StringComparison.Ordinal);
        var bPos = result.IndexOf("\"B-col\"", StringComparison.Ordinal);
        Assert.True(aPos > 0 && bPos > 0);
        Assert.True(aPos < bPos, "svc-A 가 svc-B 보다 먼저 출력 (Ordinal 정렬).");
    }

    [Fact]
    public void Build_dict_value_null_방어()
    {
        // **review m-8** — IReadOnlyDictionary contract 가 value not-null 미보장 → 방어 무사고.
        var profiles = new Dictionary<string, IReadOnlyList<KbCollectionDescriptor>>
        {
            ["svc-null"] = null!,
            ["svc-ok"] = new[] { new KbCollectionDescriptor { DisplayName = "Good", Keywords = new[] { "k" } } },
        };

        var result = KbDigestBuilder.Build(profiles);
        Assert.Contains("\"Good\"", result);
    }
}
