using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Promaker.Knowledge;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase S5b — CollectionPackager 의 zip layout / meta.json schema 정합 시험
/// (todo-lighthouse-kb-server.md §3.3 zip layout SSOT / §3.3.1 meta.json schema SSOT).
///
/// scope: staging dir → zip 변환의 client 측 contract — entry path / meta.json field 박제 / fileCount + totalSourceBytes
/// 자체 계산 / source/ 빈 staging 거부 / .lighthouse-kb/ 누락 거부. server-side import 시험은 Ds2.LightHouseService.Tests.
/// </summary>
public sealed class CollectionPackagerTests : IDisposable
{
    private readonly string _stagingDir;
    private readonly string _zipPath;

    public CollectionPackagerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "promaker-packager-" + Guid.NewGuid().ToString("N"));
        _stagingDir = Path.Combine(root, "staging");
        _zipPath = Path.Combine(root, "payload.zip");
        Directory.CreateDirectory(_stagingDir);
    }

    public void Dispose()
    {
        try
        {
            var parent = Path.GetDirectoryName(_stagingDir);
            if (parent != null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private void SeedStaging(int sourceFileCount = 3, bool seedKb = true, int sourceBytesEach = 7)
    {
        var srcDir = Path.Combine(_stagingDir, CollectionPackager.SourceFolderName);
        Directory.CreateDirectory(srcDir);
        for (int i = 0; i < sourceFileCount; i++)
        {
            var p = Path.Combine(srcDir, $"doc-{i}.txt");
            File.WriteAllBytes(p, new byte[sourceBytesEach]);
        }
        if (seedKb)
        {
            var kbDir = Path.Combine(_stagingDir, CollectionPackager.KbFolderName);
            Directory.CreateDirectory(kbDir);
            File.WriteAllBytes(Path.Combine(kbDir, "index.db"), new byte[16]);
        }
    }

    private static PackagerInputs DefaultInputs(string title = "라인A 사양서 v3") => new()
    {
        Title = title,
        IndexerVersion = "1.0.0",
        SourcePathHint = @"C:\Users\kwak\라인A",
        ClientHost = "WIN-TEST",
        ClientUser = "kwak@dualsoft.com",
    };

    [Fact]
    public void Package_emits_zip_with_three_top_entries()
    {
        SeedStaging();
        using var zip = CollectionPackager.Package(_stagingDir, _zipPath, DefaultInputs());
        Assert.True(File.Exists(_zipPath));
        Assert.Equal(0, zip.Position);

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(".lighthouse-kb/meta.json", entries);
        Assert.Contains(entries, n => n.StartsWith("source/"));
        Assert.Contains(entries, n => n.StartsWith(".lighthouse-kb/"));
    }

    [Fact]
    public void Package_meta_is_camelCase_and_lists_required_fields()
    {
        SeedStaging(sourceFileCount: 4, sourceBytesEach: 10);
        using var zip = CollectionPackager.Package(_stagingDir, _zipPath, DefaultInputs());
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var meta = archive.GetEntry(".lighthouse-kb/meta.json");
        Assert.NotNull(meta);
        using var sr = new StreamReader(meta!.Open());
        var json = sr.ReadToEnd();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.0.0", root.GetProperty("indexerVersion").GetString());
        Assert.Equal("라인A 사양서 v3", root.GetProperty("title").GetString());
        Assert.Equal(4, root.GetProperty("fileCount").GetInt32());
        Assert.Equal(40L, root.GetProperty("totalSourceBytes").GetInt64());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("createdAt").GetString()));
        Assert.Equal("WIN-TEST", root.GetProperty("clientHost").GetString());
        Assert.Equal("kwak@dualsoft.com", root.GetProperty("clientUser").GetString());
        // server-side 필드 (id/importedAt/importedBy/storageRelPath) 는 client meta 에 미작성 — server 가 import 시 박제.
        Assert.False(root.TryGetProperty("id", out _));
        Assert.False(root.TryGetProperty("importedAt", out _));
        Assert.False(root.TryGetProperty("importedBy", out _));
        Assert.False(root.TryGetProperty("storageRelPath", out _));
    }

    [Fact]
    public void Package_throws_when_source_missing()
    {
        // .lighthouse-kb 만 — source 누락
        Directory.CreateDirectory(Path.Combine(_stagingDir, CollectionPackager.KbFolderName));
        var ex = Assert.Throws<InvalidOperationException>(
            () => CollectionPackager.Package(_stagingDir, _zipPath, DefaultInputs()));
        Assert.Contains("source/", ex.Message);
    }

    [Fact]
    public void Package_throws_when_kb_folder_missing()
    {
        // source 만 — Indexer 호출 누락 시나리오
        var srcDir = Path.Combine(_stagingDir, CollectionPackager.SourceFolderName);
        Directory.CreateDirectory(srcDir);
        File.WriteAllBytes(Path.Combine(srcDir, "x.txt"), new byte[1]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => CollectionPackager.Package(_stagingDir, _zipPath, DefaultInputs()));
        Assert.Contains(".lighthouse-kb/", ex.Message);
    }

    [Fact]
    public void Package_throws_when_source_empty()
    {
        Directory.CreateDirectory(Path.Combine(_stagingDir, CollectionPackager.SourceFolderName));
        Directory.CreateDirectory(Path.Combine(_stagingDir, CollectionPackager.KbFolderName));
        var ex = Assert.Throws<InvalidOperationException>(
            () => CollectionPackager.Package(_stagingDir, _zipPath, DefaultInputs()));
        Assert.Contains("등록 가치", ex.Message);
    }

    [Fact]
    public void Package_preserves_nested_source_paths_with_forward_slash()
    {
        var srcDir = Path.Combine(_stagingDir, CollectionPackager.SourceFolderName);
        Directory.CreateDirectory(Path.Combine(srcDir, "subdir", "deep"));
        File.WriteAllBytes(Path.Combine(srcDir, "subdir", "deep", "x.md"), new byte[5]);
        Directory.CreateDirectory(Path.Combine(_stagingDir, CollectionPackager.KbFolderName));
        File.WriteAllBytes(Path.Combine(_stagingDir, CollectionPackager.KbFolderName, "index.db"), new byte[8]);

        using var zip = CollectionPackager.Package(_stagingDir, _zipPath, DefaultInputs());
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entry = archive.GetEntry("source/subdir/deep/x.md");
        Assert.NotNull(entry);
        // zip spec 의 forward-slash 정합 — Windows backslash 박제 금지.
        Assert.DoesNotContain("\\", archive.Entries.Select(e => e.FullName).Aggregate("", (a, b) => a + "|" + b));
    }

    [Fact]
    public void Package_requires_title_and_indexerVersion()
    {
        SeedStaging();
        Assert.Throws<ArgumentException>(() => CollectionPackager.Package(
            _stagingDir, _zipPath, new PackagerInputs { Title = "", IndexerVersion = "1.0.0" }));
        Assert.Throws<ArgumentException>(() => CollectionPackager.Package(
            _stagingDir, _zipPath, new PackagerInputs { Title = "x", IndexerVersion = "" }));
    }

    [Fact]
    public void Package_cancellation_throws_before_zip_finalized()
    {
        SeedStaging();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
        {
            using var _ = CollectionPackager.Package(_stagingDir, _zipPath, DefaultInputs(), cts.Token);
        });
        Assert.False(File.Exists(_zipPath));
    }

    [Fact]
    public void Package_overwrites_existing_destination_atomically()
    {
        SeedStaging();
        File.WriteAllText(_zipPath, "stale-content");
        using var zip = CollectionPackager.Package(_stagingDir, _zipPath, DefaultInputs());
        // 새 zip 은 valid archive — stale-content 가 아닌 정상 zip 으로 대체됨.
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        Assert.NotNull(archive.GetEntry(".lighthouse-kb/meta.json"));
    }
}
