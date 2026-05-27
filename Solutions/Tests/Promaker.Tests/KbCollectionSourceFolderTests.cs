using System;
using System.IO;
using Promaker.LlmAgent;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **Backlog A (todo-documents-based-gfm.md §5.4 / §10.3 P2 hand-off)** — KbCollectionEntry.SourceFolder schema
/// 확장 회귀 박제. PR-I5 (commit 8f149300) 의 specialized digest fetch path 가 production GUI 에서 활성화되도록
/// 본 필드가 LlmConfig.json 에 round-trip 되고 LlmChatViewModel 의 source root 추출 logic 이 본 필드를 참조.
/// <list type="bullet">
///   <item>schema round-trip — Save/Load 후 SourceFolder 값 보존</item>
///   <item>backward-compat — 기존 LlmConfig.json (SourceFolder 필드 부재) 로드 시 default null</item>
///   <item>JsonIgnore(WhenWritingNull) — 빈/null SourceFolder 는 disk JSON 에 키 작성 0 (legacy 호환)</item>
///   <item>LlmChatViewModel.ExtractActiveSourceRoots — Active=true + non-empty SourceFolder 만 채집</item>
/// </list>
/// </summary>
public sealed class KbCollectionSourceFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Promaker.Tests",
        nameof(KbCollectionSourceFolderTests),
        Guid.NewGuid().ToString("N"));

    public KbCollectionSourceFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── schema round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void SourceFolder_default_is_null_for_new_entry()
    {
        // Backlog A — backward-compat 의무. 신규 entry 는 SourceFolder=null (legacy fetch path 가 빈 list 반환).
        var entry = new KbCollectionEntry();
        Assert.Null(entry.SourceFolder);
    }

    [Fact]
    public void SourceFolder_round_trips_through_save_load_when_set()
    {
        // 값 박제 → Save → Load → 동일 값 복구. UTF-8 / case-insensitive deserialization 정합.
        var path = Path.Combine(_root, "kb-srcfolder.json");
        var cfg = new LlmConfig();
        cfg.LightHouseServices.Add(new LightHouseServiceConfig
        {
            ServiceId = "11111111-1111-1111-1111-111111111111",
            DisplayName = "테스트",
            BaseUrl = "https://kb.local:8443",
            Active = true,
        });
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "col-1",
            DisplayName = "***REDACTED***2",
            Active = true,
            ServiceId = "11111111-1111-1111-1111-111111111111",
            SourceFolder = @"C:\KB\***REDACTED***2",
        });
        cfg.SaveTo(path);

        var raw = File.ReadAllText(path);
        Assert.Contains("\"sourceFolder\":", raw);
        // 한글 path 도 round-trip 보존 (System.Text.Json default 가 한글을 \uXXXX escape 하든
        // UnsafeRelaxed 로 raw UTF-8 박제하든 LoadFrom 이 동일 값으로 회수).

        var reloaded = LlmConfig.LoadFrom(path);
        Assert.Single(reloaded.KbCollections);
        Assert.Equal(@"C:\KB\***REDACTED***2", reloaded.KbCollections[0].SourceFolder);
    }

    [Fact]
    public void SourceFolder_null_does_not_emit_key_to_disk()
    {
        // **backward-compat 의무** — JsonIgnore(WhenWritingNull) 박제. null 시 disk JSON 에 키 자체 누락 →
        // legacy LlmConfig.json schema 와 byte-동치 보장 (다른 reader 호환 / 사용자 disk 변화 최소).
        var path = Path.Combine(_root, "kb-srcfolder-null.json");
        var cfg = new LlmConfig();
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "col-1",
            DisplayName = "Doc",
            Active = false,
            ServiceId = "svc-1",
            SourceFolder = null,
        });
        cfg.SaveTo(path);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("\"sourceFolder\":", raw);
    }

    [Fact]
    public void Load_legacy_LlmConfig_without_sourceFolder_field_defaults_to_null()
    {
        // backward-compat 핵심 — 기존 (Backlog A 이전) LlmConfig.json 에 sourceFolder 키 부재 →
        // 로드 시 default null. 회귀 0.
        var path = Path.Combine(_root, "kb-legacy.json");
        const string json = """
            {
              "kbCollections": [
                {"collectionId":"c1","displayName":"라인A","active":true,"serviceId":"s1"}
              ]
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);
        Assert.Single(cfg.KbCollections);
        Assert.Null(cfg.KbCollections[0].SourceFolder);
        // 기존 fields 도 정상 박제.
        Assert.Equal("c1", cfg.KbCollections[0].CollectionId);
        Assert.True(cfg.KbCollections[0].Active);
    }

    // ── LlmChatViewModel.ExtractActiveSourceRoots filter semantic ─────────────────

    [Fact]
    public void ExtractActiveSourceRoots_returns_empty_when_no_collections()
    {
        var cfg = new LlmConfig();
        var roots = LlmChatViewModel.ExtractActiveSourceRoots(cfg);
        Assert.Empty(roots);
    }

    [Fact]
    public void ExtractActiveSourceRoots_skips_inactive_entries()
    {
        // Active=false 인 entry 는 specialized digest 진입 0 (cache breakpoint 3 skip 정합).
        var cfg = new LlmConfig();
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "c1", DisplayName = "A", ServiceId = "s1",
            Active = false, SourceFolder = @"C:\KB\A",
        });
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "c2", DisplayName = "B", ServiceId = "s1",
            Active = true, SourceFolder = @"C:\KB\B",
        });

        var roots = LlmChatViewModel.ExtractActiveSourceRoots(cfg);
        Assert.Single(roots);
        Assert.Equal(@"C:\KB\B", roots[0]);
    }

    [Fact]
    public void ExtractActiveSourceRoots_skips_entries_with_null_or_empty_sourceFolder()
    {
        // legacy entry (SourceFolder=null) 또는 빈 문자열 entry 는 fetcher 입력 부적합 — skip.
        var cfg = new LlmConfig();
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "c1", DisplayName = "legacy", ServiceId = "s1",
            Active = true, SourceFolder = null,
        });
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "c2", DisplayName = "empty", ServiceId = "s1",
            Active = true, SourceFolder = "",
        });
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "c3", DisplayName = "valid", ServiceId = "s1",
            Active = true, SourceFolder = @"C:\KB\valid",
        });

        var roots = LlmChatViewModel.ExtractActiveSourceRoots(cfg);
        Assert.Single(roots);
        Assert.Equal(@"C:\KB\valid", roots[0]);
    }

    [Fact]
    public void ExtractActiveSourceRoots_preserves_input_order_for_multi_collections()
    {
        // 다중 active collection — 입력 순서 보존 (FetchMany 의 합본 순서 결정 — caller 의 우선순위 의도 반영).
        var cfg = new LlmConfig();
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "c1", DisplayName = "first", ServiceId = "s1",
            Active = true, SourceFolder = @"C:\KB\first",
        });
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "c2", DisplayName = "second", ServiceId = "s1",
            Active = true, SourceFolder = @"C:\KB\second",
        });
        cfg.KbCollections.Add(new KbCollectionEntry
        {
            CollectionId = "c3", DisplayName = "third", ServiceId = "s1",
            Active = true, SourceFolder = @"C:\KB\third",
        });

        var roots = LlmChatViewModel.ExtractActiveSourceRoots(cfg);
        Assert.Equal(3, roots.Count);
        Assert.Equal(@"C:\KB\first", roots[0]);
        Assert.Equal(@"C:\KB\second", roots[1]);
        Assert.Equal(@"C:\KB\third", roots[2]);
    }

    [Fact]
    public void ExtractActiveSourceRoots_with_null_config_returns_empty()
    {
        // defensive — null config 입력 시 throw 0, 빈 list 반환 (caller 의 fail-safe 정합).
        var roots = LlmChatViewModel.ExtractActiveSourceRoots(null!);
        Assert.Empty(roots);
    }

    // ── Backlog K (s6-r? A+G 검열 M2) — SourceFolder canonical path 정규화 ─────────

    [Fact]
    public void SourceFolder_setter_strips_trailing_directory_separator()
    {
        // Backlog K — trailing separator 가 있는 입력은 canonical (no trailing sep) 으로 정규화.
        // Directory.GetFiles 결과 비결정성 / 중복 root 박제 / fetcher 입력 회피.
        var entry = new KbCollectionEntry { SourceFolder = @"C:\KB\Foo\" };
        Assert.Equal(@"C:\KB\Foo", entry.SourceFolder);

        // 다중 trailing sep 도 모두 제거.
        var entry2 = new KbCollectionEntry { SourceFolder = @"C:\KB\Bar\\" };
        Assert.Equal(@"C:\KB\Bar", entry2.SourceFolder);

        // drive root 는 예외로 trailing sep 유지 — "C:" 만 남으면 cwd-relative 로 변질.
        var entry3 = new KbCollectionEntry { SourceFolder = @"C:\" };
        Assert.Equal(@"C:\", entry3.SourceFolder);
    }

    [Fact]
    public void SourceFolder_setter_converts_relative_path_to_absolute()
    {
        // Backlog K — relative path 입력은 Path.GetFullPath 로 cwd 기준 절대경로 변환.
        // 운영시에는 absolute 입력이 의도이지만, 테스트 / 사용자 manual 입력 시 graceful.
        var entry = new KbCollectionEntry { SourceFolder = "./KB" };
        Assert.NotNull(entry.SourceFolder);
        Assert.True(Path.IsPathRooted(entry.SourceFolder));
        // trailing sep 미박제.
        Assert.False(entry.SourceFolder!.EndsWith(Path.DirectorySeparatorChar)
                  || entry.SourceFolder.EndsWith(Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public void SourceFolder_setter_preserves_null_and_empty_input()
    {
        // Backlog K — null / 빈 / whitespace-only 입력은 그대로 보존.
        // legacy entry + JsonIgnore(WhenWritingNull) 정합 + caller fail-safe 의무 위임.
        var entry1 = new KbCollectionEntry { SourceFolder = null };
        Assert.Null(entry1.SourceFolder);

        var entry2 = new KbCollectionEntry { SourceFolder = "" };
        Assert.Equal("", entry2.SourceFolder);

        var entry3 = new KbCollectionEntry { SourceFolder = "   " };
        Assert.Equal("   ", entry3.SourceFolder);
    }
}
