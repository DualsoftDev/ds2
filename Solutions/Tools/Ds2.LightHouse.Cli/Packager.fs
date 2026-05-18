namespace Ds2.LightHouse.Cli

open System
open System.Globalization
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json
open System.Threading
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

/// CLI 진입용 staging + zip packager.
///
/// 흐름 (Phase S6 P1):
///   사용자 폴더 → temp staging dir (source/ copy + .lighthouse-kb/index.db 색인)
///   → meta.json 생성 (server `MetaJson` schema 정합, camelCase wire)
///   → zip 패키징 (staging dir root → entry path)
///   → caller 가 zip 을 upload + temp dir 폐기
///
/// 사용자 폴더에는 흔적 0 (server phase 회귀 매트릭스 §3.9 정합).

[<RequireQualifiedAccess>]
module Packager =

    /// server `MetaJson` 과 wire 정합 — camelCase 직접 직렬화 (외부 의존 minimize).
    /// `[<CLIMutable>]` + non-private — JsonSerializer reflection 이 mutable property 로 인식해야 schemaVersion=1 등 record 초기값 보존.
    /// (private type 은 System.Text.Json reflection 이 모든 필드 default 로 직렬화 → server schemaVersion=0 reject.)
    [<CLIMutable>]
    type MetaDto = {
        schemaVersion: int
        indexerVersion: string
        title: string
        sourcePathHint: string
        fileCount: int
        totalSourceBytes: int64
        createdAt: string
        clientHost: string
        clientUser: string
        // server stamp 필드 (client 가 "" 보내고 server 가 덮어씀)
        id: string
        importedAt: string
        importedBy: string
        storageRelPath: string
    }

    let private metaJsonOptions =
        JsonSerializerOptions(WriteIndented = true)

    /// staging dir 안 source/ + .lighthouse-kb/ 두 sub-dir 생성. caller 가 dispose 의무 (try/finally).
    let createStaging () : string =
        let dir = Path.Combine(Path.GetTempPath(), "lh-cli-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        dir

    /// 사용자 폴더 → staging/source/ 사본. read-only 폴더라도 stagе 안에서는 색인 가능.
    /// recursive copy — 큰 폴더는 시간/disk 부담 큼 (Phase S6 P1 trade-off, follow-up 에서 hard-link 검토).
    let copyToStaging (srcFolder: string) (stagingDir: string) : int * int64 =
        if not (Directory.Exists srcFolder) then
            invalidArg (nameof srcFolder) (sprintf "사용자 폴더 미존재 — %s" srcFolder)
        let stagingSource = Path.Combine(stagingDir, "source")
        Directory.CreateDirectory stagingSource |> ignore
        let mutable count = 0
        let mutable bytes = 0L
        let srcFull = Path.GetFullPath srcFolder
        for path in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories) do
            // 사용자 폴더 안 `.lighthouse-kb/` 잔재가 있더라도 staging 으로 가져가지 않음 (server 가 자체 index.db 가짐).
            let rel = Path.GetRelativePath(srcFull, path)
            if not (rel.StartsWith(".lighthouse-kb", StringComparison.OrdinalIgnoreCase)) then
                let dest = Path.Combine(stagingSource, rel)
                let destDir = Path.GetDirectoryName dest
                if not (String.IsNullOrEmpty destDir) then
                    Directory.CreateDirectory destDir |> ignore
                File.Copy(path, dest, true)
                let fi = FileInfo dest
                count <- count + 1
                bytes <- bytes + fi.Length
        count, bytes

    /// staging dir 안 색인 (Indexer.ingest) — `<staging>/.lighthouse-kb/index.db` 생성.
    let runIngestInStaging (stagingDir: string) (ct: CancellationToken) : int =
        let extractors : IExtractor list = [
            new TextExtractor() :> IExtractor
            new PdfExtractor() :> IExtractor
            new OoxmlExtractor() :> IExtractor
        ]
        let progressCb (_: IngestProgress) = ()
        // s6-r19: D-2-2 eager — Packager 의 staging 색인 path 는 무인 (Promaker upload 흐름의 일부),
        // 현재 noop = caption 미생성. LIGHTHOUSE_VLM_API_KEY env var 활성 시 실 Anthropic helper
        // 로 치환 의무 (Phase S6 P5 정합 / s6-r19 followup 박제).
        let results = Indexer.ingest stagingDir extractors CaptionGenerator.noop progressCb ct
        let ingested = results |> Array.filter (fun (_, r) -> match r with | Ingested _ -> true | _ -> false) |> Array.length
        ingested

    /// staging 의 meta.json 생성. server `MetaJson` 의 client-fill 부분만 채움.
    let writeMeta
        (stagingDir: string)
        (title: string)
        (sourcePathHint: string)
        (fileCount: int)
        (totalBytes: int64)
        (clientUser: string)
        : unit =
        let meta : MetaDto = {
            schemaVersion = 1
            indexerVersion = IndexerVersion.Current
            title = title
            sourcePathHint = sourcePathHint
            fileCount = fileCount
            totalSourceBytes = totalBytes
            createdAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            clientHost = Environment.MachineName
            clientUser = clientUser
            id = ""
            importedAt = ""
            importedBy = ""
            storageRelPath = ""
        }
        let metaPath = Path.Combine(stagingDir, "meta.json")
        let json = JsonSerializer.Serialize(meta, metaJsonOptions)
        File.WriteAllText(metaPath, json, UTF8Encoding(false))

    /// staging dir 전체 → temp zip file. 반환 = zip path (caller 가 stream open 후 upload + 폐기).
    let createZip (stagingDir: string) : string =
        let zipPath = Path.Combine(Path.GetTempPath(), "lh-cli-" + Guid.NewGuid().ToString("N") + ".zip")
        ZipFile.CreateFromDirectory(stagingDir, zipPath, CompressionLevel.Optimal, false)
        zipPath

    /// best-effort recursive delete — log 핸들 잠금 등의 fail 은 swallow.
    let safeDelete (path: string) : unit =
        if not (String.IsNullOrEmpty path) then
            try
                if Directory.Exists path then Directory.Delete(path, true)
                elif File.Exists path then File.Delete path
            with _ -> ()
