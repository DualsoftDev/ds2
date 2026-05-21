module Ds2.LightHouseService.IntegrationTests.ZipBuilders

open System
open System.Globalization
open System.IO
open System.IO.Compression
open System.Text
open System.Threading
open Xunit
open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Protocol
open Ds2.LightHouseService

/// Phase S6 P2 — IntegrationTests 의 공용 zip builder.
///
/// E2eRoundTripTests (정상 round-trip) + NegativeRoundTripTests (multipart/zip 결함 7 Fact) 공유.
/// CLAUDE.md 의 "3줄 이상 반복 패턴 → 리팩터링" 정합 — 이전 `buildMinimalZip` (E2eRoundTripTests private)
/// 가 이미 50+ line 이라 두 번째 사용자 추가 시 추출 의무.

let private newTempDir (prefix: string) : string =
    let dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private tryDeleteDir (dir: string) =
    if not (String.IsNullOrEmpty dir) && Directory.Exists dir then
        try Directory.Delete(dir, true) with _ -> ()

let private tryDeleteFile (path: string) =
    if not (String.IsNullOrEmpty path) && File.Exists path then
        try File.Delete path with _ -> ()

/// stagingDir 안 source/.lighthouse-kb/meta.json 통째 ZipFile 패키징 → 바이트 반환.
/// staging 은 caller (withStagingDir) 가 정리.
let private packageStagingToZip (stagingDir: string) : byte[] =
    let zipPath = Path.Combine(Path.GetTempPath(), "lhs-zip-" + Guid.NewGuid().ToString("N") + ".zip")
    try
        ZipFile.CreateFromDirectory(stagingDir, zipPath, CompressionLevel.Fastest, false)
        File.ReadAllBytes zipPath
    finally
        tryDeleteFile zipPath

/// withStagingDir — 임시 staging 디렉토리 + 자동 cleanup. action 안에서 색인/meta 작성/zip 패키징.
let private withStagingDir (action: string -> byte[]) : byte[] =
    let stagingDir = newTempDir "lhs-zip-"
    try action stagingDir
    finally tryDeleteDir stagingDir

/// source/sample.txt 1개 작성 → in-process Indexer 색인 → 1+ Ingested variant 강제.
/// staging 안에 .lighthouse-kb/index.db 생성. 반환 = sample.txt byte length.
let private writeSampleAndIngest (stagingDir: string) (content: string) : int64 =
    let sourceDir = Path.Combine(stagingDir, "source")
    Directory.CreateDirectory sourceDir |> ignore
    let sampleTxt = Path.Combine(sourceDir, "sample.txt")
    File.WriteAllText(sampleTxt, content, Encoding.UTF8)
    let sampleBytes = (FileInfo sampleTxt).Length

    let extractors : IExtractor list = [ new TextExtractor() :> IExtractor ]
    let progressCb (_: IngestProgress) = ()
    let results = Indexer.ingest stagingDir extractors CaptionGenerator.noop None progressCb CancellationToken.None
    let ingestedCount =
        results
        |> Array.filter (fun (_, r) -> match r with | Ingested _ -> true | _ -> false)
        |> Array.length
    Assert.True(
        ingestedCount >= 1,
        sprintf "Indexer.ingest 결과에 Ingested variant 없음 — %A" results)
    sampleBytes

/// §3.3.1 SSOT meta.json — server 가 stamp 할 필드 (id/importedAt/...) 는 빈 값.
/// `indexerVersion` 인자: 통상은 `IndexerVersion.Current`. IndexerVersion gate 415 시나리오에서만 다른 값.
let private writeDefaultMeta
    (stagingDir: string)
    (title: string)
    (sourceDir: string)
    (fileCount: int)
    (totalBytes: int64)
    (clientUser: string)
    (indexerVersion: string)
    =
    let meta : MetaJson = {
        SchemaVersion = MetaJsonSchema.Current
        IndexerVersion = indexerVersion
        Title = title
        SourcePathHint = sourceDir
        FileCount = fileCount
        TotalSourceBytes = totalBytes
        CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        ClientHost = "integration-test-host"
        ClientUser = clientUser
        Id = ""
        ImportedAt = ""
        ImportedBy = ""
        StorageRelPath = ""
    }
    MetaJsonIO.save stagingDir meta

// ── public builders ──────────────────────────────────────────────────────

/// 정상 minimal zip — source/sample.txt + 색인 + meta.json. E2eRoundTrip 와 NegativeRoundTrip 공용.
let buildMinimalZip (title: string) (clientUser: string) : byte[] =
    withStagingDir (fun stagingDir ->
        let sourceDir = Path.Combine(stagingDir, "source")
        let sampleBytes =
            writeSampleAndIngest stagingDir
                "# Heading\n\nSample content for integration round-trip.\n"
        writeDefaultMeta stagingDir title sourceDir 1 sampleBytes clientUser IndexerVersion.Current
        packageStagingToZip stagingDir)

/// 색인 후 `.lighthouse-kb/index.db` 의 Meta.indexer_version 행을 임의 값으로 override.
/// meta.json 의 indexerVersion 도 동일 값 (client meta consistency). IndexerVersion gate 415 (§3.12)
/// 시나리오 검증용. production 색인 경로는 `IndexerVersion.Current` 만 stamp 하므로 override 는 test 전용.
let buildZipWithIndexerVersion
    (title: string)
    (clientUser: string)
    (indexerVersion: string)
    : byte[] =
    withStagingDir (fun stagingDir ->
        let sourceDir = Path.Combine(stagingDir, "source")
        let sampleBytes =
            writeSampleAndIngest stagingDir
                "# Heading\n\nIndexerVersion gate test content.\n"
        // index.db 안 Meta.indexer_version 행 override (lib facade)
        KnowledgeBase.stampIndexerVersion stagingDir indexerVersion
        // meta.json 도 동일 값 — server 가 향후 meta dual-check 시 정합 + audit 일관성
        writeDefaultMeta stagingDir title sourceDir 1 sampleBytes clientUser indexerVersion
        packageStagingToZip stagingDir)

/// meta.json 누락 zip — source/ + .lighthouse-kb/ 만. server `MetaJson.load` 가 FileNotFoundException.
let buildZipWithoutMeta () : byte[] =
    withStagingDir (fun stagingDir ->
        writeSampleAndIngest stagingDir "negative-test content (no meta.json)\n"
        |> ignore
        // meta.json 작성 skip → server `MetaJson.load` 가 FileNotFoundException
        packageStagingToZip stagingDir)

/// zip bomb — 0-byte 8KB × 1024 = 8 MiB decompressed, Deflate 압축률 매우 높음 (~수 KB compressed).
/// server cfg.ZipBombRatioLimit = 50 → 첫 entry 의 decompressed > compressed × 50 시 abort.
/// 자가 검열 M1: 압축률 invariant 사후 박제 — compressed × 50 < 8 MiB 보장. 향후 .NET Deflate
/// 구현 변경 / runtime 업그레이드로 압축률 저하 시 silent flake 차단 (8 MiB ÷ 50 ≈ 163 KB 까지 허용).
let buildZipBomb () : byte[] =
    use ms = new MemoryStream()
    (
        use archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen = true)
        let entry = archive.CreateEntry("source/zeros.bin", CompressionLevel.Optimal)
        use es = entry.Open()
        let buf = Array.zeroCreate<byte> 8192
        for _ in 1 .. 1024 do
            es.Write(buf, 0, buf.Length)
    )
    let bytes = ms.ToArray()
    Assert.True(
        int64 bytes.Length * 50L < 8L * 1024L * 1024L,
        sprintf "buildZipBomb 압축률 불충분 — compressed=%d, ratio 50 가드 미달성 (decompressed=8 MiB)"
            bytes.Length)
    bytes

/// garbage bytes — zip 의 magic (PK\x03\x04) / EOCD (PK\x05\x06) 둘 다 부재 보장 위해
/// **결정론적 ASCII 패턴** ('a' 반복 1024 byte). RandomNumberGenerator 사용 시 EOCD 시그니처
/// 4 byte 우연 일치 확률 ≈ 1.3 × 10⁻⁷ → CI 다회 반복 시 잠재 flake. ASCII 패턴은 모든
/// 0x50 byte 등장 0 → `new ZipArchive(.., Read)` 가 결정론적으로 InvalidDataException.
let buildGarbageZip () : byte[] =
    Encoding.ASCII.GetBytes (String.replicate 1024 "a")
