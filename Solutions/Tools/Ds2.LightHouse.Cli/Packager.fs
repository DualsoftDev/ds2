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
open Ds2.LightHouse.Protocol

/// CLI 진입용 in-place packager (옵션 P + 마이그레이션 (가) 산출물 보관 정책).
///
/// 흐름:
///   사용자 폴더 → 시작 전 `<source>/.lighthouse-kb/` wipe → in-place 색인
///   → `<source>/.lighthouse-kb/{index.db, meta.json}` 생성
///   → zip 패키징 (사용자 원본 = `source/` prefix, `.lighthouse-kb/` = zip root, §3.3 layout)
///   → caller 가 zip upload + temp zip 폐기 (`.lighthouse-kb/` 는 source 안 보존).
///
/// SSOT:
///   - `.lighthouse-kb/` 위치 = `SqliteStore.KbFolderName` (lib core 박제) — 본 모듈이 참조.
///   - zip layout = §3.3 (done-lighthouse-kb-server.md)
///   - internal-artifact skip rule (`.lighthouse-kb/` + `.lighthouse-caption-cache.json`) = `Indexer.enumerateFiles`
///     와 동일 SSOT (lib core). 단 `guide/*` 는 실제 user content 라 upload 대상 — enumerate 와 달리 제외 안 함.

[<RequireQualifiedAccess>]
module Packager =

    // **A2 (K4 Protocol 통합, 2026-05-20)** — meta.json filename + `.lighthouse-kb/` 둘 다 `Ds2.LightHouse.Protocol`
    // 단일 SSOT (`MetaJsonIO.FileName` / `ZipLayout.KbFolderName`) 의존. 이전 외부 --review Mj-4 의 4곳 박제는 본 phase 로 흡수.
    // `.lighthouse-kb/` 위치 = lib `SqliteStore.KbFolderName` 도 Protocol 의 `ZipLayout.KbFolderName` 와 동일 wire literal — drift 시 본 module 진단.

    /// CLI Packager 가 만든 산출물 표식 — 다음 색인 시 `resetKbDir` 가 marker 또는 `index.db` 존재 시에만 wipe.
    /// 사용자의 동명 `.lighthouse-kb/` (외부 도구 산출물) 실수 wipe 차단 (외부 --review Cr-1 정합).
    [<Literal>]
    let private MarkerFileName = ".indexer-marker"

    /// `summarize` 결과 — `mn-2/mn-6` 정합 단일 traverse + 캡슐화.
    [<NoComparison; NoEquality>]
    type IngestSummary = {
        FileCount: int
        TotalBytes: int64
        IngestedCount: int
    }

    // **A2 (K4 통합, 2026-05-20)** — 기존 `MetaDto` record + `metaJsonOptions` 폐기. `Ds2.LightHouse.Protocol.MetaJson`
    // 단일 SSOT + `MetaJsonIO.jsonOptions()` 사용. cli/server/Promaker 3중 박제 → 단일.

    /// source 폴더의 `<source>/.lighthouse-kb/` 절대 경로.
    let kbDir (sourceFolder: string) : string =
        SqliteStore.kbDir sourceFolder

    /// best-effort recursive delete — log 핸들 잠금 등의 fail 은 swallow (다음 색인 시 재시도 가능).
    let safeDelete (path: string) : unit =
        if not (String.IsNullOrEmpty path) then
            try
                if Directory.Exists path then Directory.Delete(path, true)
                elif File.Exists path then File.Delete path
            with _ -> ()

    /// source 폴더 write 권한 사전 검증 — 실패 시 caller (Program.runUpload) 가 exit 11.
    /// probe 파일 try/finally cleanup — Delete throw 시 잔재가 Indexer enumerate 에 잡혀
    /// meta.fileCount 오염되는 회귀 차단 (외부 --review Mj-1 정합).
    let verifyWritable (sourceFolder: string) : bool =
        let probe = Path.Combine(sourceFolder, ".lighthouse-write-probe-" + Guid.NewGuid().ToString("N"))
        let mutable ok = false
        try
            try
                File.WriteAllText(probe, "")
                ok <- true
            with _ -> ()
        finally
            if File.Exists probe then
                try File.Delete probe with _ -> ()
        ok

    /// `<source>/.lighthouse-kb/` 가 있으면 통째 삭제 + 빈 폴더 재생성 + marker 작성.
    /// 색인 시작 전 1회 호출 — 이전 색인 잔재 청소 (산출물 보관 정책의 입자).
    ///
    /// **안전 가드 (외부 --review Cr-1)**: 기존 폴더가 CLI 산출물 (marker 또는 index.db 존재) 일 때만 wipe.
    /// 사용자가 다른 용도로 만든 동명 폴더는 invalidOp 으로 거부 → 수동 확인 책임 이관.
    let resetKbDir (sourceFolder: string) : unit =
        let kb = kbDir sourceFolder
        if Directory.Exists kb then
            let marker = Path.Combine(kb, MarkerFileName)
            let indexDb = SqliteStore.dbPath sourceFolder
            let isCliManaged = File.Exists marker || File.Exists indexDb
            if not isCliManaged then
                invalidOp (
                    sprintf "기존 폴더 %s 가 lighthouse-cli 산출물이 아닌 듯합니다 (marker / index.db 모두 부재). 수동 확인 후 삭제하십시오." kb)
            // **P-R3a (todo-documents-based-gfm.md §6.1 N16)** — wipe 직전 caption cache 박제.
            // 기존 색인 산출물의 `ImageCache.CaptionText` (Anthropic VLM API call 결과) 를 sourceFolder 안
            // `.lighthouse-caption-cache.json` 으로 박제 → 재색인 시 `Vlm.buildCaptionGen` 의 closure 가
            // 동일 hash 의 cache 우선 lookup → API call skip path 활성. 사용자 결정 (1a 즉시) 정합.
            // fail-safe — sweep 실패 시 log + 진행 (색인 자체는 정상, cache 만 부재).
            if File.Exists indexDb then
                try
                    use conn = SqliteStore.openConnection indexDb true
                    let saved = CaptionCache.saveFromDb sourceFolder conn
                    if saved > 0 then
                        eprintfn "  caption cache 박제 — %d row (%s)"
                            saved CaptionCache.CacheFileName
                with ex ->
                    eprintfn "  warning: caption cache 박제 실패 (재색인 진행) — %s: %s"
                        (ex.GetType().Name) ex.Message
            safeDelete kb
        Directory.CreateDirectory kb |> ignore
        File.WriteAllText(Path.Combine(kb, MarkerFileName), "lighthouse-cli managed\n")

    /// in-place 색인 — `<source>/.lighthouse-kb/index.db` 생성.
    /// `Indexer.enumerateFiles` 가 `.lighthouse-kb/` 를 skip 하므로 자기 자신 색인 재귀 차단.
    ///
    /// **Phase 4 (s6-r35) P4-B.2**: `embedderOpt` parameter — caller (Program.runUpload) 가 `--no-embedding`
    /// flag 분기로 None / Some 결정.
    /// **사용자 결정 (CLI API key 필수 변경)** — `captionGen` parameter caller 박제. caller (Program.runUpload)
    /// 가 `Vlm.buildCaptionGen` 의 Result 분기 처리 후 Ok 시 본 함수 호출. Packager 자체는 caption 정책 무관.
    let runIngest
        (sourceFolder: string)
        (embedderOpt: IEmbeddingProvider option)
        (captionGen: byte[] -> ImageFormat -> CaptionResult)
        (ct: CancellationToken)
        : (string * FileIngestResult) array =
        let extractors : IExtractor list = [
            new TextExtractor() :> IExtractor
            new PdfExtractor() :> IExtractor
            new OoxmlExtractor() :> IExtractor
            new ImageExtractor() :> IExtractor
        ]
        let progressCb (_: IngestProgress) = ()
        Indexer.ingest sourceFolder extractors captionGen embedderOpt progressCb ct

    /// ingest 결과 → fileCount + totalBytes + ingestedCount 단일 traverse (외부 --review mn-2 정합).
    /// FileInfo.Length 실패는 fail-fast — silent swallow 시 totalBytes 진단 무근거 (외부 --review Mj-3 정합).
    /// `Indexer.enumerateFiles` 가 `.lighthouse-kb/` 를 이미 skip → 결과 array 의 path 만 합산.
    let summarize (results: (string * FileIngestResult) array) : IngestSummary =
        let mutable bytes = 0L
        let mutable ingested = 0
        for path, r in results do
            bytes <- bytes + (FileInfo path).Length
            match r with
            | Ingested _ -> ingested <- ingested + 1
            | _ -> ()
        { FileCount = results.Length; TotalBytes = bytes; IngestedCount = ingested }

    /// **wipe-free upload path (`--reuse-kb`)** — `runIngest` 미진입 시 fileCount / totalBytes / ingestedCount 도출.
    /// fileCount / totalBytes: source 폴더 직접 enumerate + `.lighthouse-kb/` skip (Indexer.enumerateFiles 와 동형 SSOT).
    /// ingestedCount: 기존 `<source>/.lighthouse-kb/index.db` 의 Documents row count — Step 1-a 색인 산출물 재사용.
    /// `kbPrefix` 비교는 createZip 의 패턴과 동일 (외부 --review Mj-2 정합 — `.lighthouse-kb-foo/` 거짓 매치 차단).
    let summarizeReuse (sourceFolder: string) : IngestSummary =
        let srcFull = Path.GetFullPath sourceFolder
        let kbPrefix =
            (Path.GetFullPath(kbDir sourceFolder)).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
        let mutable fileCount = 0
        let mutable bytes = 0L
        for filePath in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories) do
            // `.lighthouse-kb/` + caption cache (`.lighthouse-caption-cache.json`) 제외 — Indexer.enumerateFiles
            // 와 동일 SSOT. 내부 산출물이라 fileCount / totalBytes (meta.json) 통계에 포함 안 함.
            if not (filePath.StartsWith(kbPrefix, StringComparison.OrdinalIgnoreCase))
               && not (String.Equals(Path.GetFileName filePath, CaptionCache.CacheFileName, StringComparison.OrdinalIgnoreCase)) then
                fileCount <- fileCount + 1
                bytes <- bytes + (FileInfo filePath).Length
        let ingested =
            let dbPath = SqliteStore.dbPath sourceFolder
            use conn = SqliteStore.openConnection dbPath true
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM Documents"
            Convert.ToInt32(cmd.ExecuteScalar())
        { FileCount = fileCount; TotalBytes = bytes; IngestedCount = ingested }

    /// in-place meta.json 생성 — `<source>/.lighthouse-kb/meta.json` (옵션 P).
    /// **A2 (K4 통합, 2026-05-20)** — `Ds2.LightHouse.Protocol.MetaJson` 단일 SSOT 사용.
    /// client-fill 부분만 채움 (server 가 import 시 server stamp 필드 덮어씀).
    /// **PR-B (r0)** — `description` / `keywords` 인자 추가. caller (cli `runUpload`) 가
    /// `KeywordExtractor.extract` 결과 박제 (Phase 1 description="" / Phase 2 b2 도입 시 합성).
    let writeMeta
        (sourceFolder: string)
        (title: string)
        (sourcePathHint: string)
        (fileCount: int)
        (totalBytes: int64)
        (clientUser: string)
        (description: string)
        (keywords: string array)
        : unit =
        let meta : MetaJson = {
            SchemaVersion = MetaJsonSchema.Current
            IndexerVersion = IndexerVersion.Current
            Title = title
            SourcePathHint = sourcePathHint
            FileCount = fileCount
            TotalSourceBytes = totalBytes
            CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            ClientHost = Environment.MachineName
            ClientUser = clientUser
            // **PR-B** — KeywordExtractor 결과 박제. caller 가 빈 "" / [||] 박제 시 legacy 동작 유지.
            Description = if isNull description then "" else description
            Keywords = if isNull keywords then [||] else keywords
            Id = ""
            ImportedAt = ""
            ImportedBy = ""
            StorageRelPath = ""
        }
        let kb = kbDir sourceFolder
        Directory.CreateDirectory kb |> ignore
        let metaPath = Path.Combine(kb, MetaJsonIO.FileName)
        let json = JsonSerializer.Serialize(meta, MetaJsonIO.jsonOptions())
        File.WriteAllText(metaPath, json, UTF8Encoding(false))

    /// source 폴더 → temp zip file. 반환 = zip path (caller 가 stream open 후 upload + 폐기).
    /// zip layout (§3.3):
    ///   <zip>/source/<사용자 원본 rel-path>      — 사용자 원본 파일은 `source/` prefix
    ///   <zip>/.lighthouse-kb/{meta.json, index.db, blobs/...}  — KB 산출물은 zip root
    ///
    /// **PR-I3 (todo-documents-based-gfm.md §2 PR-I3)** — zip whitelist 는 `.lighthouse-kb/` prefix glob
    /// (아래 `isKb` 분기) — 본 prefix 안의 모든 파일이 자동 포함되므로 PR-I3 의 `summary/` 디렉토리도
    /// 별도 entry append 없이 동봉. 단 정합 확인: `summary/` 박제 위치는 `TextDumper.SummarySubDirName`
    /// (`.lighthouse-kb/summary/`) — `.lighthouse-kb/` prefix 안이라 본 glob 이 그대로 흡수. drift 시 본 주석
    /// 진단.
    let createZip (sourceFolder: string) : string =
        let zipPath = Path.Combine(Path.GetTempPath(), "lh-cli-payload-" + Guid.NewGuid().ToString("N") + ".zip")
        let srcFull = Path.GetFullPath sourceFolder
        // `.lighthouse-kb/` 경계 정확 — `.lighthouse-kb-backup/` false-positive 차단 (외부 --review Mj-2 정합).
        let kbPrefix =
            (Path.GetFullPath(kbDir sourceFolder)).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
        use archive = ZipFile.Open(zipPath, ZipArchiveMode.Create)
        for filePath in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories) do
            let isKb = filePath.StartsWith(kbPrefix, StringComparison.OrdinalIgnoreCase)
            // caption cache (`.lighthouse-caption-cache.json`) 는 내부 산출물 → zip 동봉 제외 (Indexer.enumerateFiles
            // 와 동일 SSOT). server 가 source 재색인 안 하므로 불필요 전송. cache 파일 자체는 source 에 보존(미삭제)
            // 되어 다음 client 재색인의 caption 재활용 유지.
            let isCaptionCache =
                String.Equals(Path.GetFileName filePath, CaptionCache.CacheFileName, StringComparison.OrdinalIgnoreCase)
            if not isCaptionCache then
                let rel = Path.GetRelativePath(srcFull, filePath).Replace('\\', '/')
                let entryName =
                    if isKb then rel               // .lighthouse-kb/...  → zip root
                    else "source/" + rel           // 사용자 원본 → source/ prefix
                archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal) |> ignore
        zipPath
