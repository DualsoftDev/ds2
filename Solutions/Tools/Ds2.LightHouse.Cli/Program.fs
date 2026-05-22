module Ds2.LightHouse.Cli.Program

open System
open System.IO
open System.Net
open System.Threading
open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Cli
open Ds2.LightHouse.Ollama

/// Phase S6 — done-lighthouse-kb-server.md §4.2 Phase S6.
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
///   13 VLM API key 미박제 + --force-without-image-caption 미박제 (사용자 결정)
///   14 OLLAMA_FLASH_ATTENTION 박제 미달 (bge-m3 NaN known issue — --no-embedding 미박제 시 fail-fast)
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
/// 사용자 결정 — VLM API key 미박제 시 fail-fast. 본 flag 박제 시 명시 opt-out (caption 미생성 + 색인 자체만).
[<Literal>]
let private FlagForceWithoutImageCaption = "force-without-image-caption"
/// `/indexer` skill 의 Step 1 (index-only) flag — caption 자동 skip + upload 자동 skip.
/// 본 flag 박제 시 `--force-without-image-caption` 자동 박제 = noop captionGen 진입 (Vlm.fs:42).
/// skill 이 Step 2 에서 subagent caption-fill → Step 3 에서 별 upload entry 호출.
[<Literal>]
let private FlagSkipUpload = "skip-upload"

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
    eprintfn "  lighthouse-cli index <folder> [--no-embedding] [--force-without-image-caption | --skip-upload]"
    eprintfn "                              [--upload <url> --psk <key> [--title <name>] [--user <id>] [--allow-invalid-certs]]"
    eprintfn "  lighthouse-cli list-pending-captions <folder>"
    eprintfn "  lighthouse-cli caption-update <folder> <batch.json>"
    eprintfn "  lighthouse-cli print-caption-prompt"
    eprintfn "  lighthouse-cli list-pending-summaries <folder>"
    eprintfn "  lighthouse-cli summary-update <folder> <batch.json>"
    eprintfn "  lighthouse-cli print-summary-prompt"
    eprintfn "  lighthouse-cli --version"
    eprintfn ""
    eprintfn "options:"
    eprintfn "  --no-embedding                 vector embedding 생성 skip (BM25-only 색인). default = Ollama bge-m3 / 1024 dim"
    eprintfn "                                 env: %s / %s / %s 으로 override" EnvOllamaUrl EnvOllamaModel EnvOllamaDim
    eprintfn "  --force-without-image-caption  VLM caption 명시 skip (caption 미생성, image 색인 자체는 정상)."
    eprintfn "                                 default = LIGHTHOUSE_VLM_API_KEY 필수, 미박제 시 fail-fast."
    eprintfn "  --skip-upload                  /indexer skill Step 1 entry — 색인만 (upload skip + caption 자동 skip)."
    eprintfn "                                 본 flag 박제 시 --force-without-image-caption 자동 박제."
    eprintfn "                                 Step 2 (caption-update) + Step 3 (upload) 는 skill 측에서 별도 dispatch."
    eprintfn "  --upload <url>                 LightHouseService base URL (https://host:port)"
    eprintfn "  --psk <key>                    PSK (DPAPI 미적용 평문 — env var %s 권장)" EnvPsk
    eprintfn "  --title <name>                 collection 표시 이름 (생략 시 폴더명)"
    eprintfn "  --user <id>                    X-User-Identity (생략 시 USERNAME@MachineName)"
    eprintfn "  --allow-invalid-certs          self-signed cert 신뢰 우회 (dev only)"
    eprintfn "  --version                      버전 출력 후 종료"

/// args 수동 parsing — System.CommandLine 없이 dependency minimize (D-S6-3).
/// `--key value` + `--flag` (no value) 두 형식만 지원. `=` form 미지원 (follow-up).
let private parseArgs (args: string array) : Map<string, string> * string list =
    let mutable flags = Map.empty
    let mutable positional = []
    let mutable i = 0
    // **s6-r70 review C-17** — boolean flag set 분리 (다음 토큰 흡수 차단).
    // 예: `--no-embedding <folder>` 가 folder 를 no-embedding 의 value 로 흡수했던 결함. boolean flag 는
    // value 없는 형식으로만 처리, 다음 토큰은 positional 또는 별 flag 로 그대로 분리.
    let booleanFlags = Set.ofList [ FlagNoEmbedding; FlagAllowInvalidCerts; FlagForceWithoutImageCaption; FlagSkipUpload ]
    while i < args.Length do
        let arg = args.[i]
        if arg.StartsWith "--" then
            let key = arg.Substring 2
            if booleanFlags.Contains key then
                // boolean flag — 다음 토큰 흡수 안 함 (presence-only).
                flags <- Map.add key "" flags
                i <- i + 1
            elif i + 1 < args.Length && not (args.[i + 1].StartsWith "--") then
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

/// **bge-m3 NaN 방지 사전조건 검사** — Ollama 의 Flash Attention long-context FP16 결함이 embedding 결과에
/// NaN 생성 → server 가 `"failed to encode response: json: unsupported value: NaN"` 으로 HTTP 500 반환 → 색인
/// abort. 회피 의무 = `OLLAMA_FLASH_ATTENTION` env var = `false`/`0`/`off` 박제 + Ollama 재시작.
///
/// 본 검사는 `--no-embedding` 미박제 (embedding backend 사용) 시점에만 의무 — BM25-only path 는 무관.
/// caller (`runIndex`/`runUpload`) 가 Result Error 분기 시 exit 14 으로 fail-fast. install-ollama.ps1 가 박제
/// 자동화 + search / indexer skill SKILL.md 사전조건 명시와 정합.
let private checkEmbeddingPreconditions () : Result<unit, string> =
    let raw = Environment.GetEnvironmentVariable("OLLAMA_FLASH_ATTENTION")
    let normalized = if isNull raw then "" else raw.Trim().ToLowerInvariant()
    let disabled = normalized = "false" || normalized = "0" || normalized = "off"
    if disabled then Ok ()
    else
        let actual = if String.IsNullOrEmpty raw then "<unset>" else sprintf "'%s'" raw
        Error (sprintf "OLLAMA_FLASH_ATTENTION 박제 미달 (현재값=%s). bge-m3 의 Flash Attention long-context FP16 결함이 embedding 결과에 NaN 생성 → ollama HTTP 500 → 색인 abort 가능. 조치:\n  1) setx OLLAMA_FLASH_ATTENTION false  (또는 SystemProperties → 환경변수, Machine scope 권장)\n  2) Ollama Desktop tray 종료 → 재실행 (또는 'sc restart Ds2.LightHouseService')\n  3) --no-embedding flag 박제 시 본 검사 우회 (BM25-only 색인)" actual)

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

/// **review A fix (r4)** — runIndex / runUpload 의 keyword + dump + summary 7줄 hook 중복 추출.
/// 색인 완료 직후 단일 read-only connection 안에서 세 lib hook 모두 수행 → SQLite open cost 1회 통합.
/// `meta.json` 박제는 caller 측 (runUpload 만 후속 단계에서 writeMeta 호출).
/// 반환 = KeywordExtractor 결과 (runUpload 가 writeMeta 의 description / keywords 박제용으로 재활용).
let private runPostIngestHooks (folder: string) : KeywordExtractionResult =
    let dbPath = SqliteStore.dbPath folder
    use conn = SqliteStore.openConnection dbPath true
    let kwResult = KeywordExtractor.extract conn
    let dumpFiles = TextDumper.dumpAll conn folder
    let summaries = SummaryBuilder.build conn
    eprintfn "  keyword 추출 — %d 개 (self-MATCH 통과)" kwResult.Keywords.Length
    eprintfn "  text dump — %d 파일 (.lighthouse-kb/text/)" dumpFiles.Length
    let _ = SummaryBuilder.write folder summaries
    eprintfn "  summary 박제 — %d doc (.lighthouse-kb/%s)" summaries.Length SummaryBuilder.SummaryFileName
    kwResult

let private runIndex (folder: string) (noEmbedding: bool) (forceWithoutCaption: bool) : int =
    if not (Directory.Exists folder) then
        eprintfn "오류: 폴더 미존재 — %s" folder
        11
    else
        // 사용자 결정 — VLM captionGen build 가 색인 본격 진입 전에 fail-fast. API key 미박제 + force flag 미박제 시 exit 13.
        match Vlm.buildCaptionGen CancellationToken.None forceWithoutCaption with
        | Error msg ->
            eprintfn "오류: %s" msg
            13
        | Ok captionGen ->
        // bge-m3 NaN 방지 사전조건 — embedding 사용 시점에만 의무. `--no-embedding` 박제 시 skip.
        match (if noEmbedding then Ok () else checkEmbeddingPreconditions ()) with
        | Error msg ->
            eprintfn "오류: %s" msg
            14
        | Ok () ->
            let extractors : IExtractor list = [
                new TextExtractor() :> IExtractor
                new PdfExtractor() :> IExtractor
                new OoxmlExtractor() :> IExtractor
                new ImageExtractor() :> IExtractor
            ]
            let mutable lastReported = -1
            let progressCb (p: IngestProgress) =
                let pct = if p.TotalFiles > 0 then (p.CompletedFiles * 100) / p.TotalFiles else 0
                if pct <> lastReported then
                    let current = p.CurrentFile |> Option.defaultValue ""
                    eprintfn "  [%d%%] %d/%d — %s" pct p.CompletedFiles p.TotalFiles current
                    lastReported <- pct
            let opts =
                [ if noEmbedding then " --no-embedding"
                  if forceWithoutCaption then " --force-without-image-caption" ]
                |> String.concat ""
            printfn "색인 시작 — %s%s" folder opts
            // Phase 4 (s6-r37) P4-C.1: embedder lifecycle — Some 시 색인 종료 후 dispose 의무 (HttpClient 자원).
            let embedder = resolveEmbedder noEmbedding
            try
                let results = Indexer.ingest folder extractors captionGen embedder progressCb CancellationToken.None
                let ingested = results |> Array.filter (fun (_, r) -> match r with | Ingested _ -> true | _ -> false) |> Array.length
                let skipped  = results |> Array.filter (fun (_, r) -> match r with | Skipped  _ -> true | _ -> false) |> Array.length
                let failed   = results |> Array.filter (fun (_, r) -> match r with | Failed   _ -> true | _ -> false) |> Array.length
                printfn "색인 완료 — ingested=%d skipped=%d failed=%d (total=%d)" ingested skipped failed results.Length
                // upload 전 검수용 — keyword + text dump + summary hook (runUpload 와 동일 패턴).
                // ingested 만으로 분기하면 fast-skip 케이스에서 DB row 있어도 dump skip 되는 결함 → 항상 호출.
                // **review A fix (r4)**: 7줄 hook 중복 제거 — runPostIngestHooks helper 호출. runIndex 는 반환 무관.
                let _ = runPostIngestHooks folder
                0
            finally
                embedder |> Option.iter (fun e -> e.Dispose())

/// `--upload` 본격 분기 — in-place 색인 + zip + POST /collections (옵션 P + 보관 정책).
/// 산출물 = `<folder>/.lighthouse-kb/{index.db, meta.json}` (색인 시작 전 wipe + 색인 후 보관).
/// zip 은 temp 에 생성 후 업로드 완료 시 정리. `.lighthouse-kb/` 는 source 안 보존.
/// exit code (D-S6-4): 0/1/2/3/11/12/13/99 (review M-1 — 13 = VLM API key 미박제 + force 미박제).
let private runUpload
        (folder: string)
        (baseUrl: string)
        (psk: string)
        (title: string)
        (userIdentity: string)
        (allowInvalidCerts: bool)
        (noEmbedding: bool)
        (forceWithoutCaption: bool)
        : int =
    if not (Directory.Exists folder) then
        eprintfn "오류: 폴더 미존재 — %s" folder
        11
    elif not (Packager.verifyWritable folder) then
        eprintfn "오류: source 폴더 write 권한 없음 — %s" folder
        eprintfn "  in-place 색인을 위해 source 폴더에 .lighthouse-kb/ 생성 가능해야 합니다."
        11
    else
        // 사용자 결정 — VLM captionGen build 가 색인 본격 진입 전에 fail-fast.
        match Vlm.buildCaptionGen CancellationToken.None forceWithoutCaption with
        | Error msg ->
            eprintfn "오류: %s" msg
            13
        | Ok captionGen ->
        // bge-m3 NaN 방지 사전조건 — embedding 사용 시점에만 의무. `--no-embedding` 박제 시 skip.
        match (if noEmbedding then Ok () else checkEmbeddingPreconditions ()) with
        | Error msg ->
            eprintfn "오류: %s" msg
            14
        | Ok () ->
        let mutable zipPath = ""
        try
            try
                Packager.resetKbDir folder
                eprintfn "  in-place 색인 시작 — %s/.lighthouse-kb/" folder
                let embedder = resolveEmbedder noEmbedding
                try
                    let results = Packager.runIngest folder embedder captionGen CancellationToken.None
                    let summary = Packager.summarize results
                    if summary.IngestedCount = 0 then
                        eprintfn "오류: 색인 결과 ingested=0 — server 거부 사전 차단 (빈 폴더 또는 unsupported extension)"
                        12
                    else
                        eprintfn "  색인 완료 — ingested=%d, 파일=%d, %d bytes"
                            summary.IngestedCount summary.FileCount summary.TotalBytes
                        // **PR-B + PR-C + PR-H1 (todo §3.1 + §3.2 + §11)** — keyword + text dump + doc summary.
                        // **review A fix (r4)**: runPostIngestHooks helper — runIndex 와 동일 path 통합 + kwResult 재활용.
                        let kwResult = runPostIngestHooks folder
                        let description = kwResult.Topic |> Option.defaultValue ""
                        Packager.writeMeta folder title folder summary.FileCount summary.TotalBytes userIdentity
                            description kwResult.Keywords
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

/// **review E fix (r4)** — list-pending-* / *-update / index 의 folder + db 존재 가드 보일러플레이트 통합.
/// caption-* + summary-* 6 entry 의 동형 5~7줄 박제 → 단일 helper.
/// 반환 = Ok dbPath | Error exitCode (11 = folder/DB 미존재). caller 는 `match ... with | Ok ... | Error c -> c` 패턴.
let private requireIndexedFolder (folder: string) : Result<string, int> =
    if not (Directory.Exists folder) then
        eprintfn "오류: 폴더 미존재 — %s" folder
        Error 11
    else
        let dbPath = SqliteStore.dbPath folder
        if not (File.Exists dbPath) then
            eprintfn "오류: 색인 DB 미존재 — %s" dbPath
            eprintfn "  먼저 'lighthouse-cli index <folder> --skip-upload' 으로 Step 1 색인을 수행하세요."
            Error 11
        else Ok dbPath

/// camelCase JSON serializer opts — caption-* + summary-* wire contract (manifest fenced block).
/// F# record 직렬화 시 default 가 PascalCase 필드명 그대로 — naming policy 강제 의무.
let private camelJsonOpts () : System.Text.Json.JsonSerializerOptions =
    let opts = System.Text.Json.JsonSerializerOptions(WriteIndented = false)
    opts.PropertyNamingPolicy <- System.Text.Json.JsonNamingPolicy.CamelCase
    opts

/// `/indexer` skill Step 2 — caption-pending row JSON stdout stream.
/// `todo-lighthouse-indexer-claude-caption.md` §3 manifest fenced block 의 wire 정합.
/// 본 entry 는 read-only — `SqliteStore.openConnection ... true` 진입 (PRAGMA WAL/synchronous/busy_timeout 자동 박제).
let private runListPendingCaptions (folder: string) : int =
    match requireIndexedFolder folder with
    | Error code -> code
    | Ok dbPath ->
        use conn = SqliteStore.openConnection dbPath true
        let records = ImageStore.listPendingCaptions conn |> Seq.toArray
        let json = System.Text.Json.JsonSerializer.Serialize(records, camelJsonOpts())
        printfn "%s" json
        0

/// `/indexer` skill Step 2 → Step 3 사이 — subagent caption 결과 batch JSON 입력 → SQLite UPDATE.
/// 단일 transaction 안 N 회 `ImageStore.updateCaption` 호출 → atomic commit.
/// 빈 batch (§2 #16) → exit 0 (no-op).
let private runCaptionUpdate (folder: string) (batchPath: string) : int =
    if not (File.Exists batchPath) then
        eprintfn "오류: batch 파일 미존재 — %s" batchPath
        11
    else
        match requireIndexedFolder folder with
        | Error code -> code
        | Ok dbPath ->
            let json = File.ReadAllText(batchPath, Text.Encoding.UTF8)
            use doc = System.Text.Json.JsonDocument.Parse(json)
            let root = doc.RootElement
            if root.ValueKind <> System.Text.Json.JsonValueKind.Array then
                eprintfn "오류: batch JSON root 가 array 아님 — %s" batchPath
                10
            elif root.GetArrayLength() = 0 then
                eprintfn "  batch 빈 array — no-op (idempotent)"
                0
            else
                // **review D fix** (r4): JSON null / whitespace captionText/captionModel 도 fail-fast — silent NULL UPDATE 차단.
                try
                    let rows =
                        root.EnumerateArray()
                        |> Seq.map (fun el ->
                            let hash = el.GetProperty("hash").GetString()
                            let text = el.GetProperty("captionText").GetString()
                            let model = el.GetProperty("captionModel").GetString()
                            if isNull text || System.String.IsNullOrWhiteSpace text then
                                raise (System.IO.InvalidDataException(
                                    sprintf "caption-update batch row 의 captionText 가 null/whitespace — hash=%s" hash))
                            if isNull model || System.String.IsNullOrWhiteSpace model then
                                raise (System.IO.InvalidDataException(
                                    sprintf "caption-update batch row 의 captionModel 가 null/whitespace — hash=%s" hash))
                            hash, text, model)
                        |> Seq.toArray
                    use conn = SqliteStore.openConnection dbPath false
                    let updated = ImageStore.updateCaptionBatch conn rows
                    printfn "caption-update 완료 — updated=%d (rows=%d)" updated rows.Length
                    0
                with
                | :? System.IO.InvalidDataException as ex ->
                    eprintfn "오류: %s" ex.Message
                    10

/// `/indexer` skill — caption-prompt SSOT (lib `CaptionGenerator.promptText`) stdout 노출.
/// skill 진입 시 1회 호출 → subagent prompt template 박제. literal 사본 박제 차단 (drift 원천 차단).
let private runPrintCaptionPrompt () : int =
    printfn "%s" (CaptionGenerator.promptText ())
    0

/// `/indexer` skill Step 2b — summary 미박제 doc enumeration (caption path 와 동형).
/// stdout JSON array (camelCase: `docId`, `originalPath`, `textDumpPath`).
let private runListPendingSummaries (folder: string) : int =
    match requireIndexedFolder folder with
    | Error code -> code
    | Ok dbPath ->
        use conn = SqliteStore.openConnection dbPath true
        let records = SummaryStore.listPendingSummaries conn |> Seq.toArray
        let json = System.Text.Json.JsonSerializer.Serialize(records, camelJsonOpts())
        printfn "%s" json
        0

/// `/indexer` skill Step 2b → batch JSON 입력 → Documents.SummaryText UPDATE 단일 transaction.
/// 빈 batch → exit 0 (no-op). batch row schema = `{"docId":<int>,"summary":"..."}`.
let private runSummaryUpdate (folder: string) (batchPath: string) : int =
    if not (File.Exists batchPath) then
        eprintfn "오류: batch 파일 미존재 — %s" batchPath
        11
    else
        match requireIndexedFolder folder with
        | Error code -> code
        | Ok dbPath ->
            let json = File.ReadAllText(batchPath, Text.Encoding.UTF8)
            use doc = System.Text.Json.JsonDocument.Parse(json)
            let root = doc.RootElement
            if root.ValueKind <> System.Text.Json.JsonValueKind.Array then
                eprintfn "오류: batch JSON root 가 array 아님 — %s" batchPath
                10
            elif root.GetArrayLength() = 0 then
                eprintfn "  batch 빈 array — no-op (idempotent)"
                0
            else
                // transaction lifecycle 은 lib `updateSummaryBatch` 가 흡수 (caption-update 와 동형 — sub-agent M-1 정정 정합).
                // **review D fix**: JSON null / whitespace summary 는 fail-fast (silent NULL UPDATE 차단 — listPending
                // 재진입 무한 retry / 진단 표면 손실 회피). CLAUDE.md "간단 exception 우선" 정합.
                try
                    let rows =
                        root.EnumerateArray()
                        |> Seq.map (fun el ->
                            let docId = el.GetProperty("docId").GetInt64()
                            let summary = el.GetProperty("summary").GetString()
                            if isNull summary || System.String.IsNullOrWhiteSpace summary then
                                raise (System.IO.InvalidDataException(
                                    sprintf "summary-update batch row 의 summary 가 null/whitespace — docId=%d" docId))
                            docId, summary)
                        |> Seq.toArray
                    use conn = SqliteStore.openConnection dbPath false
                    let updated = SummaryStore.updateSummaryBatch conn rows
                    printfn "summary-update 완료 — updated=%d (rows=%d)" updated rows.Length
                    0
                with
                | :? System.IO.InvalidDataException as ex ->
                    eprintfn "오류: %s" ex.Message
                    10

/// `/indexer` skill Step 2b — summary-prompt SSOT (lib `SummaryStore.SummaryPrompt`) stdout 노출.
/// caption-prompt 와 동형 — subagent prompt template literal 사본 박제 차단.
let private runPrintSummaryPrompt () : int =
    printfn "%s" SummaryStore.SummaryPrompt
    0

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
            let skipUpload = Map.containsKey FlagSkipUpload flags
            match Map.tryFind FlagUpload flags with
            | Some _ when skipUpload ->
                // `--upload` 와 `--skip-upload` 동시 박제 = 의도 모순. silent 우선순위 결정 회피.
                eprintfn "오류: --%s 와 --%s 는 동시 사용 불가" FlagUpload FlagSkipUpload
                10
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
                    let forceWithoutCaption = Map.containsKey FlagForceWithoutImageCaption flags
                    runUpload folder baseUrl psk title userIdentity allowInvalidCerts noEmbedding forceWithoutCaption
            | Some _ ->
                // 자가 검열 C1 — `--upload` 가 value 없거나 빈 string 이면 silent `runIndex` fallback 차단.
                // 사용자 의도는 upload 였으므로 explicit reject 후 exit 10 (usage hint).
                eprintfn "오류: --upload <url> 인자 누락"
                10
            | None ->
                let noEmbedding = Map.containsKey FlagNoEmbedding flags
                // `--skip-upload` 박제 시 caption 자동 skip (force-without-caption 자동 박제) — skill Step 1 의 의미.
                let forceWithoutCaption =
                    skipUpload || Map.containsKey FlagForceWithoutImageCaption flags
                runIndex folder noEmbedding forceWithoutCaption
        | "list-pending-captions" :: folder :: _ ->
            runListPendingCaptions folder
        | "caption-update" :: folder :: batchPath :: _ ->
            runCaptionUpdate folder batchPath
        | [ "print-caption-prompt" ] ->
            runPrintCaptionPrompt ()
        | "list-pending-summaries" :: folder :: _ ->
            runListPendingSummaries folder
        | "summary-update" :: folder :: batchPath :: _ ->
            runSummaryUpdate folder batchPath
        | [ "print-summary-prompt" ] ->
            runPrintSummaryPrompt ()
        | _ ->
            usage ()
            10
