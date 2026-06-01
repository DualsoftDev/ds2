using System;
using System.IO;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **KbCollectionEntry.SourceFolder schema 회귀 박제**. 본 필드는 KB collection 등록 / payload swap 시
/// (KbManagerDialog) 로컬 색인 폴더 경로를 박제한다.
/// <list type="bullet">
///   <item>schema round-trip — Save/Load 후 SourceFolder 값 보존</item>
///   <item>backward-compat — 기존 LlmConfig.json (SourceFolder 필드 부재) 로드 시 default null</item>
///   <item>JsonIgnore(WhenWritingNull) — 빈/null SourceFolder 는 disk JSON 에 키 작성 0 (legacy 호환)</item>
///   <item>setter canonical 정규화 (Backlog K) — trailing sep trim / alt-sep 변환 / relative→absolute</item>
/// </list>
/// <para/>
/// **참고 (2026-06-01)**: specialized digest(layer E) fetch 가 로컬 SourceFolder read → MCP
/// <c>attachment_summary(includeSpecialized=true)</c> 로 전환되면서, SourceFolder 를 layer E 입력으로 소비하던
/// <c>LlmChatViewModel.ExtractActiveSourceRoots</c> 및 그 회귀 테스트는 제거됐다. SourceFolder field 자체는 KB
/// 등록 메타로 유지되므로 본 schema/normalize 회귀만 남긴다.
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
        // backward-compat 의무. 신규 entry 는 SourceFolder=null.
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
        // backward-compat 핵심 — 기존 LlmConfig.json 에 sourceFolder 키 부재 → 로드 시 default null. 회귀 0.
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

    // ── Backlog K — SourceFolder canonical path 정규화 ───────────────────────────

    [Fact]
    public void SourceFolder_setter_strips_trailing_directory_separator()
    {
        // Backlog K — trailing separator 가 있는 입력은 canonical (no trailing sep) 으로 정규화.
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
        var entry1 = new KbCollectionEntry { SourceFolder = null };
        Assert.Null(entry1.SourceFolder);

        var entry2 = new KbCollectionEntry { SourceFolder = "" };
        Assert.Equal("", entry2.SourceFolder);

        var entry3 = new KbCollectionEntry { SourceFolder = "   " };
        Assert.Equal("   ", entry3.SourceFolder);
    }

    // ── B·M5 — silent rewrite 명세 round-trip ───────────────────────────────────

    [Fact]
    public void BM5_SourceFolder_round_trip_alt_separator_with_trailing_slash_normalizes_to_canonical()
    {
        // B·M5 — raw JSON `"C:/KB/***REDACTED***2/"` → setter NormalizeSourceFolder → disk 값 `"C:\KB\***REDACTED***2"`.
        var path = Path.Combine(_root, "bm5-alt-sep.json");
        var rawInput = "C:/KB/***REDACTED***2/";
        var entry = new KbCollectionEntry { SourceFolder = rawInput };

        // setter 진입 직후 in-memory 값이 canonical.
        Assert.Equal(@"C:\KB\***REDACTED***2", entry.SourceFolder);

        // disk save → load round-trip 후도 canonical 보존.
        var cfg = new LlmConfig();
        cfg.KbCollections.Add(entry);
        cfg.SaveTo(path);
        var reloaded = LlmConfig.LoadFrom(path);
        Assert.Single(reloaded.KbCollections);
        Assert.Equal(@"C:\KB\***REDACTED***2", reloaded.KbCollections[0].SourceFolder);
    }

    [Fact]
    public void BM5_SourceFolder_disk_json_external_edit_then_load_normalizes_silently()
    {
        // B·M5 — disk JSON 을 외부 텍스트 편집기로 raw 박제 → Load 시점에 setter 진입 → canonical 정렬.
        var path = Path.Combine(_root, "bm5-external-edit.json");
        const string externallyEditedJson = """
            {
              "kbCollections": [
                {"collectionId":"c1","displayName":"***REDACTED***2","active":true,"serviceId":"s1","sourceFolder":"C:\\KB\\***REDACTED***2\\"}
              ]
            }
            """;
        File.WriteAllText(path, externallyEditedJson);

        var cfg = LlmConfig.LoadFrom(path);
        Assert.Single(cfg.KbCollections);
        // Load 시점에 setter 진입 → trailing sep 제거된 canonical 값.
        Assert.Equal(@"C:\KB\***REDACTED***2", cfg.KbCollections[0].SourceFolder);

        // Save 후 disk 의 sourceFolder 값도 canonical (drift 회피).
        cfg.SaveTo(path);
        var reloaded2 = LlmConfig.LoadFrom(path);
        Assert.Single(reloaded2.KbCollections);
        Assert.Equal(@"C:\KB\***REDACTED***2", reloaded2.KbCollections[0].SourceFolder);
        // raw disk JSON 에 trailing-sep 박제 흔적 부재.
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain(@"\\\\", raw);
    }

    [Fact]
    public void SourceFolder_setter_returns_raw_on_invalid_path_chars()
    {
        // narrow catch (ArgumentException / PathTooLongException / NotSupportedException) + raw 보존.
        // NULL char ('\0') 는 ArgumentException trigger SSOT 입력.
        const string invalidRaw = "C:\\foo\0bar";
        var entry = new KbCollectionEntry { SourceFolder = invalidRaw };
        // fail-safe — raw 그대로 반환 (caller / UI 가 사용 시점에 다시 검증).
        Assert.Equal(invalidRaw, entry.SourceFolder);
    }
}
