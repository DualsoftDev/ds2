using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Ds2.LightHouse.Protocol;
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
/// 본 클래스가 `meta.json` 을 `.lighthouse-kb/meta.json` 으로 작성 후 zip 전체를 묶는다
/// (s6-r55+ 옵션 P — 이전 staging root 의 meta.json 에서 `.lighthouse-kb/` sub-dir 로 이전, §3.3 SSOT 정합).
/// zip 안 server-side 필드 (id / importedAt / importedBy / storageRelPath) 는 미작성 — server 가 import 시 발급/박제.
/// </summary>
public static class CollectionPackager
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(CollectionPackager));

    /// <summary>staging dir 안 client 측 zip entry 의 최상위 폴더 2종 (§3.3 SSOT).
    /// A2 (K4 Protocol 통합, 2026-05-20) 이후 `Ds2.LightHouse.Protocol.ZipLayout` 단일 SSOT 의존.
    /// 본 const 는 caller (CollectionPackagerTests / AttachmentIngestService) 의 기존 호출 호환 — drift 0 (compile-time inline).</summary>
    public const string SourceFolderName = ZipLayout.SourceFolderName;
    public const string KbFolderName = ZipLayout.KbFolderName;

    private const string MetaFileName = MetaJsonIO.FileName;

    /// <summary>A2 (K4 통합, 2026-05-20) — `JsonIgnoreCondition.Never` 로 통일 (Protocol `MetaJsonIO.jsonOptions()` 정합).
    /// 이전 `WhenWritingNull` 대비 wire output 차이는 0 (모든 string 필드를 `""` 박제). 향후 caller 가 `null` 박제 시
    /// wire 에 박혀 server load 시 deserialize 회귀 차단 (m2 정합).</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
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

        // meta.json 작성 (camelCase, §3.3.1 SSOT). A2 (K4 통합) — `Ds2.LightHouse.Protocol.MetaJson` 단일 record 사용.
        // server stamp 필드 (Id / ImportedAt / ImportedBy / StorageRelPath) 는 client 측 "" 박제 — server 가 import 시 덮어씀.
        var meta = new MetaJson
        {
            SchemaVersion = MetaJsonSchema.Current,
            IndexerVersion = inputs.IndexerVersion,
            Title = inputs.Title.Trim(),
            SourcePathHint = inputs.SourcePathHint ?? "",
            FileCount = fileCount,
            TotalSourceBytes = totalBytes,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            ClientHost = inputs.ClientHost ?? Environment.MachineName,
            ClientUser = inputs.ClientUser ?? Environment.UserName,
            Id = "",
            ImportedAt = "",
            ImportedBy = "",
            StorageRelPath = "",
        };
        // meta.json 위치 = `.lighthouse-kb/meta.json` (옵션 P, §3.3 zip layout SSOT).
        var metaPath = Path.Combine(kbDir, MetaFileName);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // zip 생성 — 임시 path 작성 후 atomic move (caller 가 partial zip 을 upload 하지 않도록)
        var tmpZip = zipDestPath + ".tmp-" + Environment.ProcessId;
        try
        {
            using (var zipStream = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                // meta.json 은 .lighthouse-kb/ 안에 박혔으므로 AddDirectoryEntries 가 자동 entry 박제.
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

    /// <summary>caller 진단용 — 본 메서드가 직렬화한 meta.json 내용을 string 으로 반환 (zip 안 entry 와 동일).
    /// A2 (K4 통합) — `Ds2.LightHouse.Protocol.MetaJson` 단일 SSOT.</summary>
    public static string SerializeMeta(PackagerInputs inputs, int fileCount, long totalSourceBytes) =>
        JsonSerializer.Serialize(new MetaJson
        {
            SchemaVersion = MetaJsonSchema.Current,
            IndexerVersion = inputs.IndexerVersion,
            Title = inputs.Title.Trim(),
            SourcePathHint = inputs.SourcePathHint ?? "",
            FileCount = fileCount,
            TotalSourceBytes = totalSourceBytes,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            ClientHost = inputs.ClientHost ?? Environment.MachineName,
            ClientUser = inputs.ClientUser ?? Environment.UserName,
            Id = "",
            ImportedAt = "",
            ImportedBy = "",
            StorageRelPath = "",
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

    // **A2 (K4 통합, 2026-05-20)** — 기존 `private sealed class MetaPayload` 폐기. `Ds2.LightHouse.Protocol.MetaJson`
    // 단일 record 가 client/server stamp 필드 모두 보유 — client 측은 server stamp 필드 "" 박제 후 wire 송신.
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
