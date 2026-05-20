module Ds2.LightHouse.Cli.Program

open System
open System.IO
open System.Net
open System.Threading
open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Cli
open Ds2.LightHouse.Ollama

/// Phase S6 — todo-lighthouse-kb-server.md §4.2 Phase S6.
///
/// 본 turn (s6-r0, P1 follow-up) = `index --upload` 본격 구현.
///
/// exit code SSOT (D-S6-4):
///   0  ok
///   1  인증 실패 (401/403)
///   2  IndexerVersion mismatch (415)
///   3  zip / size 초과 (413 또는 stream IO 거부)
///   10 명령행 인자 오류 (사용법 mismatch)
///   11 폴더 미존재 / 접근 불가
///   12 ingested=0 (등록 가치 0 — server 거부 사전 차단)
///   99 기타

// **CLI flag key SSOT (s6-r36 P4-C.0)** — usage / parseArgs / call site 가 같은 literal 참조 의무.
// 외부 --review L-Min-3 / 자가 검열 m3 박제 — call site drift 회귀 차단.
// nested module 대신 top-level [<Literal>] 박제 (F# 9 strict-indentation 정합 단순화).
[<Literal>]
let private FlagNoEmbedding = "no-embedding"
[<Literal>]
let private FlagUpload = "upload"
[<Literal>]
let private FlagPsk = "psk"
[<Literal>]
let private FlagTitle = "title"
[<Literal>]
let private FlagUser = "user"
[<Literal>]
let private FlagAllowInvalidCerts = "allow-invalid-certs"
[<Literal>]
let private FlagVersion = "version"

// **env var key SSOT (s6-r41)** — 자가 검열 s6-r40 Minor-2 정합. cli scope 한정 (Promaker LlmConfig 의
// LIGHTHOUSE_VLM_API_KEY 박제 는 별 cross-project SSOT 박제 의무 — K4 Protocol 통합 phase 묶음).
// `LIGHTHOUSE_VLM_API_KEY` 는 사용처 (Vlm.fs) 단일 — 해당 module 안 박제.
[<Literal>]
let private EnvOllamaUrl = "LIGHTHOUSE_OLLAMA_URL"
[<Literal>]
let private EnvOllamaModel = "LIGHTHOUSE_OLLAMA_MODEL"
[<Literal>]
let private EnvOllamaDim = "LIGHTHOUSE_OLLAMA_DIM"
[<Literal>]
let private EnvPsk = "LIGHTHOUSE_PSK"

let private usage () =
    eprintfn "usage:"
    eprintfn "  lighthouse-cli index <folder> [--no-embedding] [--upload <url> --psk <key> [--title <name>] [--user <id>] [--allow-invalid-certs]]"
    eprintfn "  lighthouse-cli --version"
    eprintfn ""
    eprintfn "options:"
    eprintfn "  --no-embedding             vector embedding 생성 skip (BM25-only 색인). default = Ollama bge-m3 / 1024 dim"
    eprintfn "                             env: %s / %s / %s 으로 override" EnvOllamaUrl EnvOllamaModel EnvOllamaDim
    eprintfn "  --upload <url>             LightHouseService base URL (https://host:port)"
    eprintfn "  --psk <key>                PSK (DPAPI 미적용 평문 — env var %s 권장)" EnvPsk
    eprintfn "  --title <name>             collection 표시 이름 (생략 시 폴더명)"
    eprintfn "  --user <id>                X-User-Identity (생략 시 USERNAME@MachineName)"
    eprintfn "  --allow-invalid-certs      self-signed cert 신뢰 우회 (dev only)"
    eprintfn "  --version                  버전 출력 후 종료"

/// args 수동 parsing — System.CommandLine 없이 dependency minimize (D-S6-3).
/// `--key value` + `--flag` (no value) 두 형식만 지원. `=` form 미지원 (follow-up).
let private parseArgs (args: string array) : Map<string, string> * string list =
    let mutable flags = Map.empty
    let mutable positional = []
    let mutable i = 0
    while i < args.Length do
        let arg = args.[i]
        if arg.StartsWith "--" then
            let key = arg.Substring 2
            if i + 1 < args.Length && not (args.[i + 1].StartsWith "--") then
                flags <- Map.add key args.[i + 1] flags
                i <- i + 2
            else
                flags <- Map.add key "" flags
                i <- i + 1
        else
            positional <- arg :: positional
            i <- i + 1
    flags, List.rev positional

let private resolvePsk (flagValue: string option) : string option =
    match flagValue with
    | Some v when not (String.IsNullOrWhiteSpace v) -> Some v
    | _ ->
        let env = Environment.GetEnvironmentVariable EnvPsk
        if String.IsNullOrWhiteSpace env then None else Some env

let private defaultUserIdentity () =
    let user =
        match Environment.GetEnvironmentVariable "USERNAME" with
        | null | "" -> "anonymous"
        | u -> u
    sprintf "%s@%s" user Environment.MachineName

/// **Phase 4 (s6-r37) P4-C.1** — embedder backend 선택 본격화. `noEmbedding=true` 시 강제 None (BM25-only).
///
/// default backend = **OllamaSharp adapter** (`OllamaEmbedder` — bge-m3 / 1024 dim / http://localhost:11434).
/// env var override 의무 (SSOT = `EnvOllamaUrl` / `EnvOllamaModel` / `EnvOllamaDim` literal):
///   - `LIGHTHOUSE_OLLAMA_URL` → default `OllamaDefaults.BaseUrl`
///   - `LIGHTHOUSE_OLLAMA_MODEL` → default `OllamaDefaults.Model`
///   - `LIGHTHOUSE_OLLAMA_DIM` → default `OllamaDefaults.Dimension`
///
/// 사용자 명시 `--no-embedding` 이 최우선 (BM25-only path). env var 없으면 default. backend 검증 (Ollama
/// daemon up / 모델 미설치) 은 색인 첫 chunk 호출 시점에 진입 (lazy fail-fast — backend down 시 색인 abort).
let private resolveEmbedder (noEmbedding: bool) : IEmbeddingProvider option =
    if noEmbedding then None
    else
        let envOrDefault (name: string) (defaultValue: string) =
            match Environment.GetEnvironmentVariable name with
            | null | "" -> defaultValue
            | v -> v
        let baseUrl = envOrDefault EnvOllamaUrl OllamaDefaults.BaseUrl
        let model = envOrDefault EnvOllamaModel OllamaDefaults.Model
        let dim =
            let raw = envOrDefault EnvOllamaDim (string OllamaDefaults.Dimension)
            match Int32.TryParse raw with
            | true, v when v > 0 -> v
            | _ ->
                eprintfn "경고: %s 값 '%s' parse 실패 — default %d 사용" EnvOllamaDim raw OllamaDefaults.Dimension
                OllamaDefaults.Dimension
        let embedder = new OllamaEmbedder(baseUrl, model, dim) :> IEmbeddingProvider
        eprintfn "  embedding backend = Ollama (%s, model=%s, dim=%d)" baseUrl model dim
        Some embedder

let private runIndex (folder: string) (noEmbedding: bool) : int =
    if not (Directory.Exists folder) then
        eprintfn "오류: 폴더 미존재 — %s" folder
        11
    else
        let extractors : IExtractor list = [
            new TextExtractor() :> IExtractor
            new PdfExtractor() :> IExtractor
            new OoxmlExtractor() :> IExtractor
        ]
        let mutable lastReported = -1
        let progressCb (p: IngestProgress) =
            let pct = if p.TotalFiles > 0 then (p.CompletedFiles * 100) / p.TotalFiles else 0
            if pct <> lastReported then
                let current = p.CurrentFile |> Option.defaultValue ""
                eprintfn "  [%d%%] %d/%d — %s" pct p.CompletedFiles p.TotalFiles current
                lastReported <- pct
        printfn "색인 시작 — %s%s" folder (if noEmbedding then " (--no-embedding)" else "")
        // s6-r20 (D-iii / --review M1): cli VLM captionGen builder SSOT = Vlm.buildCaptionGen.
        let captionGen = Vlm.buildCaptionGen CancellationToken.None
        // Phase 4 (s6-r37) P4-C.1: embedder lifecycle — Some 시 색인 종료 후 dispose 의무 (HttpClient 자원).
        let embedder = resolveEmbedder noEmbedding
        try
            let results = Indexer.ingest folder extractors captionGen embedder progressCb CancellationToken.None
            let ingested = results |> Array.filter (fun (_, r) -> match r with | Ingested _ -> true | _ -> false) |> Array.length
            let skipped  = results |> Array.filter (fun (_, r) -> match r with | Skipped  _ -> true | _ -> false) |> Array.length
            let failed   = results |> Array.filter (fun (_, r) -> match r with | Failed   _ -> true | _ -> false) |> Array.length
            printfn "색인 완료 — ingested=%d skipped=%d failed=%d (total=%d)" ingested skipped failed results.Length
            0
        finally
            embedder |> Option.iter (fun e -> e.Dispose())

/// `--upload` 본격 분기 — in-place 색인 + zip + POST /collections (옵션 P + 보관 정책).
/// 산출물 = `<folder>/.lighthouse-kb/{index.db, meta.json}` (색인 시작 전 wipe + 색인 후 보관).
/// zip 은 temp 에 생성 후 업로드 완료 시 정리. `.lighthouse-kb/` 는 source 안 보존.
/// exit code (D-S6-4): 0/1/2/3/11/12/99.
let private runUpload
        (folder: string)
        (baseUrl: string)
        (psk: string)
        (title: string)
        (userIdentity: string)
        (allowInvalidCerts: bool)
        (noEmbedding: bool)
        : int =
    if not (Directory.Exists folder) then
        eprintfn "오류: 폴더 미존재 — %s" folder
        11
    elif not (Packager.verifyWritable folder) then
        eprintfn "오류: source 폴더 write 권한 없음 — %s" folder
        eprintfn "  in-place 색인을 위해 source 폴더에 .lighthouse-kb/ 생성 가능해야 합니다."
        11
    else
        let mutable zipPath = ""
        try
            try
                Packager.resetKbDir folder
                eprintfn "  in-place 색인 시작 — %s/.lighthouse-kb/" folder
                let embedder = resolveEmbedder noEmbedding
                try
                    let results = Packager.runIngest folder embedder CancellationToken.None
                    let summary = Packager.summarize results
                    if summary.IngestedCount = 0 then
                        eprintfn "오류: 색인 결과 ingested=0 — server 거부 사전 차단 (빈 폴더 또는 unsupported extension)"
                        12
                    else
                        eprintfn "  색인 완료 — ingested=%d, 파일=%d, %d bytes"
                            summary.IngestedCount summary.FileCount summary.TotalBytes
                        Packager.writeMeta folder title folder summary.FileCount summary.TotalBytes userIdentity
                        zipPath <- Packager.createZip folder
                        let zipBytes = (FileInfo zipPath).Length
                        eprintfn "  zip 생성 — %s (%d bytes)" zipPath zipBytes
                        use client = LightHouseClient.createHttpClient baseUrl allowInvalidCerts
                        use stream = File.OpenRead zipPath
                        eprintfn "  POST /collections → %s" baseUrl
                        let id =
                            LightHouseClient.uploadCollection
                                client psk userIdentity title stream CancellationToken.None
                            |> fun t -> t.GetAwaiter().GetResult()
                        printfn "업로드 완료 — collectionId=%s" id
                        eprintfn "  [안내] 산출물 보관: %s/.lighthouse-kb/ (다음 색인 시작 시 wipe 됩니다)" folder
                        eprintfn "  [안내] .gitignore 에 '.lighthouse-kb/' 추가 권장"
                        0
                finally
                    embedder |> Option.iter (fun e -> e.Dispose())
            with
            | LightHouseAuthError(msg, status) ->
                eprintfn "인증 실패 (HTTP %d) — %s" (int status) msg
                1
            | LightHouseProtocolError(msg, Some status) when int status = 415 ->
                eprintfn "IndexerVersion mismatch (HTTP 415) — %s" msg
                2
            | LightHouseProtocolError(msg, Some status) when int status = 413 ->
                eprintfn "zip size 초과 (HTTP 413) — %s" msg
                3
            | LightHouseProtocolError(msg, _) ->
                eprintfn "프로토콜 오류 — %s" msg
                99
            | ex ->
                eprintfn "오류 — %s: %s" (ex.GetType().Name) ex.Message
                99
        finally
            // zip 만 정리. `<folder>/.lighthouse-kb/` 는 보관 정책에 따라 유지.
            Packager.safeDelete zipPath

[<EntryPoint>]
let main args =
    // CP949 등 legacy code page 활성화 — TextEncoding 의 fallback (LightHouseService Program.fs 와 동일 패턴).
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance)
    let flags, positional = parseArgs args
    if Map.containsKey FlagVersion flags then
        printfn "lighthouse-cli 0.1.0 (Phase S6)"
        0
    else
        match positional with
        | "index" :: folder :: _ ->
            match Map.tryFind FlagUpload flags with
            | Some baseUrl when not (String.IsNullOrWhiteSpace baseUrl) ->
                match resolvePsk (Map.tryFind FlagPsk flags) with
                | None ->
                    eprintfn "오류: --%s 또는 %s 환경 변수 필수" FlagPsk EnvPsk
                    10
                | Some psk ->
                    let title =
                        Map.tryFind FlagTitle flags
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                        |> Option.defaultWith (fun () -> Path.GetFileName(Path.GetFullPath folder))
                    let userIdentity =
                        Map.tryFind FlagUser flags
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                        |> Option.defaultWith defaultUserIdentity
                    let allowInvalidCerts = Map.containsKey FlagAllowInvalidCerts flags
                    let noEmbedding = Map.containsKey FlagNoEmbedding flags
                    runUpload folder baseUrl psk title userIdentity allowInvalidCerts noEmbedding
            | Some _ ->
                // 자가 검열 C1 — `--upload` 가 value 없거나 빈 string 이면 silent `runIndex` fallback 차단.
                // 사용자 의도는 upload 였으므로 explicit reject 후 exit 10 (usage hint).
                eprintfn "오류: --upload <url> 인자 누락"
                10
            | None ->
                let noEmbedding = Map.containsKey FlagNoEmbedding flags
                runIndex folder noEmbedding
        | _ ->
            usage ()
            10
