using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using log4net;

namespace Promaker.Knowledge;

/// <summary>
/// Staging folder (`source/` + `.lighthouse-kb/`) → `meta.json` 생성 + zip 패키징
/// (todo-lighthouse-kb-server.md §3.3 zip layout / §3.3.1 meta.json SSOT).
///
/// 책임 분담:
/// - **CollectionPackager (본 클래스)** = staging dir 구조 검증 + meta.json 직렬화 + zip 생성. 색인 자체는 안 함.
/// - <c>AttachmentIngestService</c> = staging dir 준비 + Indexer 호출 + Packager 호출 + LightHouseClient.Upload.
///
/// staging dir layout (caller 가 사전 구성):
/// <code>
/// &lt;stagingDir&gt;/
///   source/         ← 사용자 폴더 원본 사본 (caller 가 copy)
///   .lighthouse-kb/ ← Indexer.ingest 산출물 (index.db ...)
/// </code>
///
/// 본 클래스가 `meta.json` 을 stagingDir 루트에 작성 후 zip 전체를 묶는다. zip 안 server-side 필드
/// (id / importedAt / importedBy / storageRelPath) 는 미작성 — server 가 import 시 발급/박제.
/// </summary>
public static class CollectionPackager
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(CollectionPackager));

    /// <summary>staging dir 안 client 측 zip entry 의 최상위 폴더 2종 (§3.3 SSOT).</summary>
    public const string SourceFolderName = "source";
    public const string KbFolderName = ".lighthouse-kb";

    private const string MetaFileName = "meta.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// staging dir 에 `meta.json` 작성 후 `zipDestPath` 에 zip 생성. **client 측 staging → zip 1회성**.
    /// `source/` 가 존재하지 않거나 비어 있으면 throw (등록 가치 0). `.lighthouse-kb/index.db` 미존재 시도 throw
    /// (caller 가 Indexer 호출 누락).
    ///
    /// 반환 = `zipDestPath` 를 가리키는 읽기용 <see cref="FileStream"/> (position 0). caller 가 dispose +
    /// 작업 종료 후 `zipDestPath` 파일 cleanup. <see cref="FileShare.Read"/> 로 열어 같은 파일을 다른 thread 가 stat 가능.
    ///
    /// `meta.fileCount` / `meta.totalSourceBytes` 는 본 메서드가 `source/` walk 후 자체 계산 — caller 가 박제하지 않음.
    /// `meta.indexerVersion` 은 caller (AttachmentIngestService) 가 `Ds2.LightHouse.IndexerVersion.Current` 박제.
    /// </summary>
    public static FileStream Package(
        string stagingDir,
        string zipDestPath,
        PackagerInputs inputs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stagingDir)) throw new ArgumentException("stagingDir 필수.", nameof(stagingDir));
        if (string.IsNullOrWhiteSpace(zipDestPath)) throw new ArgumentException("zipDestPath 필수.", nameof(zipDestPath));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));
        if (string.IsNullOrWhiteSpace(inputs.Title)) throw new ArgumentException("Title 필수.", nameof(inputs));
        if (string.IsNullOrWhiteSpace(inputs.IndexerVersion)) throw new ArgumentException("IndexerVersion 필수.", nameof(inputs));

        var sourceDir = Path.Combine(stagingDir, SourceFolderName);
        var kbDir = Path.Combine(stagingDir, KbFolderName);
        if (!Directory.Exists(sourceDir))
            throw new InvalidOperationException($"staging 의 source/ 없음 — {sourceDir}. caller 가 사용자 폴더 사본 누락.");
        if (!Directory.Exists(kbDir))
            throw new InvalidOperationException($"staging 의 .lighthouse-kb/ 없음 — {kbDir}. Indexer.ingest 호출 누락.");

        var (fileCount, totalBytes) = WalkSource(sourceDir, ct);
        if (fileCount == 0)
            throw new InvalidOperationException($"staging 의 source/ 가 비어 있음 — {sourceDir}. 등록 가치 0.");

        // meta.json 작성 (camelCase, §3.3.1 SSOT)
        var meta = new MetaPayload
        {
            SchemaVersion = 1,
            IndexerVersion = inputs.IndexerVersion,
            Title = inputs.Title.Trim(),
            SourcePathHint = inputs.SourcePathHint ?? "",
            FileCount = fileCount,
            TotalSourceBytes = totalBytes,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            ClientHost = inputs.ClientHost ?? Environment.MachineName,
            ClientUser = inputs.ClientUser ?? Environment.UserName,
        };
        var metaPath = Path.Combine(stagingDir, MetaFileName);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // zip 생성 — 임시 path 작성 후 atomic move (caller 가 partial zip 을 upload 하지 않도록)
        var tmpZip = zipDestPath + ".tmp-" + Environment.ProcessId;
        try
        {
            using (var zipStream = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                AddEntry(archive, metaPath, MetaFileName, ct);
                AddDirectoryEntries(archive, sourceDir, SourceFolderName, ct);
                AddDirectoryEntries(archive, kbDir, KbFolderName, ct);
            }
            if (File.Exists(zipDestPath)) File.Delete(zipDestPath);
            File.Move(tmpZip, zipDestPath);
        }
        catch
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* best-effort */ }
            throw;
        }

        Log.Info($"CollectionPackager — title='{meta.Title}' files={fileCount} bytes={totalBytes} zip={zipDestPath}");
        return new FileStream(zipDestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>caller 진단용 — 본 메서드가 직렬화한 meta.json 내용을 string 으로 반환 (zip 안 entry 와 동일).</summary>
    public static string SerializeMeta(PackagerInputs inputs, int fileCount, long totalSourceBytes) =>
        JsonSerializer.Serialize(new MetaPayload
        {
            SchemaVersion = 1,
            IndexerVersion = inputs.IndexerVersion,
            Title = inputs.Title.Trim(),
            SourcePathHint = inputs.SourcePathHint ?? "",
            FileCount = fileCount,
            TotalSourceBytes = totalSourceBytes,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            ClientHost = inputs.ClientHost ?? Environment.MachineName,
            ClientUser = inputs.ClientUser ?? Environment.UserName,
        }, JsonOptions);

    private static (int Count, long Bytes) WalkSource(string sourceDir, CancellationToken ct)
    {
        int count = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            count++;
            bytes += new FileInfo(path).Length;
        }
        return (count, bytes);
    }

    private static void AddDirectoryEntries(ZipArchive archive, string sourceDir, string entryPrefix, CancellationToken ct)
    {
        var prefix = sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var path in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(prefix, path).Replace(Path.DirectorySeparatorChar, '/');
            // zip entry 의 디렉토리 구분자는 forward-slash 고정 (zip spec). entryPrefix 도 forward-slash.
            AddEntry(archive, path, entryPrefix + "/" + rel, ct);
        }
    }

    private static void AddEntry(ZipArchive archive, string filePath, string entryName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dst = entry.Open();
        src.CopyTo(dst);
    }

    /// <summary>§3.3.1 meta.json — client 측 작성 필드 (server-side 필드는 미작성).</summary>
    private sealed class MetaPayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("indexerVersion")] public string IndexerVersion { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("sourcePathHint")] public string SourcePathHint { get; set; } = "";
        [JsonPropertyName("fileCount")] public int FileCount { get; set; }
        [JsonPropertyName("totalSourceBytes")] public long TotalSourceBytes { get; set; }
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
        [JsonPropertyName("clientHost")] public string ClientHost { get; set; } = "";
        [JsonPropertyName("clientUser")] public string ClientUser { get; set; } = "";
    }
}

/// <summary>
/// CollectionPackager 입력 — meta.json 의 client-side 필드 SSOT.
/// fileCount / totalSourceBytes / createdAt 는 packager 가 자체 계산.
/// </summary>
public sealed class PackagerInputs
{
    public required string Title { get; init; }
    public required string IndexerVersion { get; init; }
    public string? SourcePathHint { get; init; }
    public string? ClientHost { get; init; }
    public string? ClientUser { get; init; }
}
