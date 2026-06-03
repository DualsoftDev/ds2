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
///   4  409 동일 이름 collection 존재 (--no-overwrite, 옵션 A overwrite 거부)
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
/// `/indexer` skill Step 3 wipe 회피 — `.lighthouse-kb/` 의 기존 색인 산출물을 wipe 하지 않고 그대로 zip + POST.
/// Step 2 (caption-update) + Step 1-b (summary-update) 박제분 (DB ImageCache.CaptionText / Documents.SummaryText
/// + text dump / summary.md) 을 server 까지 전달 보장. 본 flag 미박제 시 종전 동작 (wipe + 재색인) 유지.
/// 사용 전제: `<folder>/.lighthouse-kb/index.db` 이미 존재 (Step 1-a 색인 완료). 부재 시 exit 11.
[<Literal>]
let private FlagReuseKb = "reuse-kb"
/// **옵션 A (2026-05-30)** — 동일 폴더명(collectionId = sanitized title) 이 server 에 이미 있으면 fail (409 시 abort).
/// 미박제 = default overwrite (server 가 기존 collection payload swap). server `parseMultipart` 의 overwrite 필드로 전달.
[<Literal>]
let private FlagNoOverwrite = "no-overwrite"

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
    eprintfn "                              [--upload <url> [--reuse-kb] --psk <key> [--title <name>] [--user <id>] [--allow-invalid-certs]]"
    eprintfn "  lighthouse-cli list-pending-captions <folder>"
    eprintfn "  lighthouse-cli caption-update <folder> <batch.json>"
    eprintfn "  lighthouse-cli print-caption-prompt"
    eprintfn "  lighthouse-cli list-pending-summaries <folder>"
    eprintfn "  lighthouse-cli summary-update <folder> <batch.json>"
    eprintfn "  lighthouse-cli print-summary-prompt"
    eprintfn "  lighthouse-cli export-image-cache <folder>"
    eprintfn "  lighthouse-cli import-image-cache <folder> <backup.json>"
    eprintfn "  lighthouse-cli probe-indexer-version <folder>"
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
    eprintfn "  --reuse-kb                     기존 <folder>/.lighthouse-kb/ 산출물을 wipe 없이 그대로 zip + POST."
    eprintfn "                                 /indexer skill Step 3 — Step 2 caption-update / Step 1-b summary-update"
    eprintfn "                                 박제분을 server 까지 전달 보장. <folder>/.lighthouse-kb/index.db 필수."
    eprintfn "  --no-overwrite                 동일 폴더명 collection 이 server 에 이미 있으면 fail (409). default = overwrite"
    eprintfn "                                 (옵션 A — collectionId = sanitized 폴더명, 같은 이름은 기존 것 덮어씀)."
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
    let booleanFlags = Set.ofList [ FlagNoEmbedding; FlagAllowInvalidCerts; FlagForceWithoutImageCaption; FlagSkipUpload; FlagReuseKb; FlagNoOverwrite ]
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
///
/// **--review 라운드 1 Major-5 fix**: `extractors` 인자 forward — 기존엔 runPostIngestHooks 가 내부에서
/// IExtractor 4종 (Text/Pdf/Ooxml/Image) 을 별도 인스턴스화 + try/finally dispose 했으나 caller (runIndex)
/// 가 이미 동일 list 보유. 동일 IExtractor type 의 동시 2개 인스턴스는 PdfExtractor / OoxmlExtractor 의
/// 내부 캐시 / file handle / NLog ctx 관점에서 중복 비용 + 잠재 충돌 (특히 strategy classifier 가 동일 file
/// 을 두 번 open). caller 가 단일 list 보유 + dispose 책임 단일화 → 본 helper 는 forward 만 담당.
let private runPostIngestHooks (folder: string) (extractors: IExtractor list) : KeywordExtractionResult =
    let dbPath = SqliteStore.dbPath folder
    use conn = SqliteStore.openConnection dbPath true
    let kwResult = KeywordExtractor.extract conn
    let dumpFiles = TextDumper.dumpAll conn folder
    let summaries = SummaryBuilder.build conn
    eprintfn "  keyword 추출 — %d 개 (self-MATCH 통과)" kwResult.Keywords.Length
    eprintfn "  text dump — %d 파일 (.lighthouse-kb/text/)" dumpFiles.Length
    let _ = SummaryBuilder.write folder summaries
    eprintfn "  summary 박제 — %d doc (.lighthouse-kb/%s)" summaries.Length SummaryBuilder.SummaryFileName
    // **PR-I3 (todo-documents-based-gfm.md §2 PR-I3 + §8.5.5)** — strategy 정제 markdown 박제 hook.
    // text dump / SummaryBuilder 와 직교 — `summary/` 디렉토리에 IoList / WorkOrder / PdfControlSpec 등
    // strategy 매치 결과의 정제 markdown 박제. 비매치 / signature 미달 파일은 rejected.json / near-miss.json
    // 에 누적 박제 (N1, N4 default). Packager.createZip 가 `.lighthouse-kb/` 전체를 zip 에 동봉 → server upload.
    // extractors 의 dispose 는 caller (runIndex) 의 finally 에서 단일 책임 — 본 helper 는 forward 만.
    //
    // **kb-caption fix (F·M2 best-effort 폐기)**: 종전엔 strategy summary 실패가 전체 색인 abort 로
    // 이어지지 않도록 try/with 로 catch + 빈 결과 fallback 했으나, 이 best-effort 가 lock / extractor 오류 등을
    // 삼켜 summary/ 정제본 silent 누락 (rejected/near-miss.json 도 0 → 진단 불가) 을 유발. dumpStrategySummaries
    // 는 내부 fail-fast (파일별 catch 0) 설계이므로 caller 도 throw 전파로 정합. CLAUDE.md "꼭 필요한 경우에만
    // catch, 그냥 오류 나게 두라" 정합 — 실패 시 색인 명령이 명확히 비정상 종료하여 사용자 즉시 인지.
    let dump = TextDumper.dumpStrategySummaries folder extractors CancellationToken.None
    eprintfn "  strategy summary 박제 — %d 파일 (.lighthouse-kb/%s/), rejected=%d, near-miss=%d"
        dump.SummaryFiles.Length TextDumper.SummarySubDirName dump.RejectedCount dump.NearMissCount
    // **PR-N15 (todo-documents-based-gfm.md §6.1 N15 + documents-based-gfm.md §8.5.4)** — 사용자 작성
    // `<folder>/guide/*.md` 를 `_user-guide-*.md` 로 박제. 호출 순서 — `dumpStrategySummaries` 가
    // `summary/` 의 기존 *.md (user-guide 포함) 를 삭제 후 재생성하므로 본 hook 은 반드시 그 *후*에 호출.
    // **kb-caption fix**: strategy 와 동일 — best-effort try-with 제거. UserGuideImporter 실패 (guide/ IO 등) 도
    // silent 누락 대신 throw 전파 (CLAUDE.md catch 자제 정합).
    let userGuideFiles = UserGuideImporter.importAll folder
    eprintfn "  user-guide 박제 — %d 파일 (.lighthouse-kb/%s/_user-guide-*.md)"
        userGuideFiles.Length TextDumper.SummarySubDirName
    kwResult

let private runIndex (folder: string) (noEmbedding: bool) (forceWithoutCaption: bool) : int =
    if not (Directory.Exists folder) then
        eprintfn "오류: 폴더 미존재 — %s" folder
        11
    else
        // 사용자 결정 — VLM captionGen build 가 색인 본격 진입 전에 fail-fast. API key 미박제 + force flag 미박제 시 exit 13.
        // **P-R3a (N16)** — sourceFolder 인자 전달. closure 안 `CaptionCache.tryLookup` 가 동일 폴더 의
        // `.lighthouse-caption-cache.json` 우선 lookup → 재색인 시 Anthropic API call skip.
        match Vlm.buildCaptionGen CancellationToken.None folder forceWithoutCaption with
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
                // **--review 라운드 1 Major-5 fix**: extractors 인자 forward — helper 안 중복 인스턴스화 회피.
                let _ = runPostIngestHooks folder extractors
                0
            finally
                for ex in extractors do ex.Dispose()
                embedder |> Option.iter (fun e -> e.Dispose())

/// `--upload` 본격 분기 — in-place 색인 + zip + POST /collections (옵션 P + 보관 정책).
/// 산출물 = `<folder>/.lighthouse-kb/{index.db, meta.json}` (색인 시작 전 wipe + 색인 후 보관).
/// zip 은 temp 에 생성 후 업로드 완료 시 정리. `.lighthouse-kb/` 는 source 안 보존.
/// exit code (D-S6-4): 0/1/2/3/4/11/12/13/99 (review M-1 — 13 = VLM API key 미박제 + force 미박제.
///                       4 = 409 동일 이름 collection 존재 / --no-overwrite 옵션 A).
let private runUpload
        (folder: string)
        (baseUrl: string)
        (psk: string)
        (title: string)
        (userIdentity: string)
        (allowInvalidCerts: bool)
        (noEmbedding: bool)
        (forceWithoutCaption: bool)
        (reuseKb: bool)
        (overwrite: bool)
        : int =
    if not (Directory.Exists folder) then
        eprintfn "오류: 폴더 미존재 — %s" folder
        11
    elif not (Packager.verifyWritable folder) then
        eprintfn "오류: source 폴더 write 권한 없음 — %s" folder
        eprintfn "  in-place 색인을 위해 source 폴더에 .lighthouse-kb/ 생성 가능해야 합니다."
        11
    elif reuseKb && not (File.Exists (SqliteStore.dbPath folder)) then
        // `--reuse-kb` 박제 시 기존 색인 산출물 의무 — Step 1-a 미수행 시 fail-fast.
        eprintfn "오류: --%s 박제 + index.db 부재 — %s" FlagReuseKb (SqliteStore.dbPath folder)
        eprintfn "  먼저 'lighthouse-cli index <folder> --skip-upload' 으로 Step 1-a 색인 수행하세요."
        11
    else
        // **② --reuse-kb 자가 probe** — server 의 IndexerVersion gate 응답 받기 전 client 측 stale 인식.
        // schema / sqlite-vec extension drift / 이전 build 산출 → server 응답 후 진단보다 zip 생성 + POST round-trip
        // 비용 절감. probe 결과별 사용자 친화 메시지 + /indexer skill 의 caption 보존 자동 복구 path 안내.
        let probeFailureCode =
            if reuseKb then
                match KnowledgeBase.probeIndexerVersionDetailed folder with
                | Found _ -> None
                | KeyMissing ->
                    eprintfn "오류: --%s 박제 + Meta.indexer_version 키 미박제 — 이전 build 산출 가능성" FlagReuseKb
                    eprintfn "  /indexer skill 의 caption 보존 자동 복구 path 활용 권장:"
                    eprintfn "    1) lighthouse-cli export-image-cache <folder> > caption-backup.json"
                    eprintfn "    2) rm -rf <folder>/.lighthouse-kb"
                    eprintfn "    3) lighthouse-cli index <folder> --skip-upload"
                    eprintfn "    4) lighthouse-cli import-image-cache <folder> caption-backup.json"
                    eprintfn "    5) lighthouse-cli index <folder> --upload <url> --reuse-kb ..."
                    Some 2
                | OpenFailed reason ->
                    eprintfn "오류: --%s 박제 + 색인 DB open 실패 — %s" FlagReuseKb reason
                    eprintfn "  schema / sqlite-vec extension drift 가능 — .lighthouse-kb/ wipe 후 재색인 필요."
                    eprintfn "  caption 보존 시도는 open 실패라 불가 — 신규 caption 비용 발생."
                    Some 2
                | DbMissing ->
                    // 위 elif 분기에서 처리됨 — 도달 불가 (안전망).
                    eprintfn "내부 오류: probe DbMissing reached after exists check"
                    Some 11
            else None
        match probeFailureCode with
        | Some code -> code
        | None ->
        // `--reuse-kb` 박제 시 captionGen / embedder 진입 자체 skip — runIngest 미진입이므로 두 의존성 모두 dead.
        // VLM API key 미박제 + force 미박제 사용자에게도 reuse path 는 허용해야 (Step 2 가 이미 caption 박제).
        // 자가 검열 Minor-1 — dummy null 박제 회피, `CaptionGenerator.noop` SSOT 재사용. runIngest 가 잘못 호출되어도
        // `SkippedCaption "no caption gen"` 반환 → NullReferenceException 회귀 차단.
        let captionPrecheck =
            if reuseKb then Ok CaptionGenerator.noop
            // **P-R3a (N16)** — sourceFolder=folder 전달. closure 의 cache lookup hook 진입점.
            else Vlm.buildCaptionGen CancellationToken.None folder forceWithoutCaption
        match captionPrecheck with
        | Error msg ->
            eprintfn "오류: %s" msg
            13
        | Ok captionGen ->
        // bge-m3 NaN 방지 사전조건 — embedding 사용 시점에만 의무. `--no-embedding` / `--reuse-kb` 박제 시 skip.
        match (if noEmbedding || reuseKb then Ok () else checkEmbeddingPreconditions ()) with
        | Error msg ->
            eprintfn "오류: %s" msg
            14
        | Ok () ->
        let mutable zipPath = ""
        try
            try
                let summary =
                    if reuseKb then
                        eprintfn "  in-place 색인 재사용 — %s/.lighthouse-kb/ (wipe 없이 기존 산출물 그대로 zip)" folder
                        Packager.summarizeReuse folder
                    else
                        Packager.resetKbDir folder
                        eprintfn "  in-place 색인 시작 — %s/.lighthouse-kb/" folder
                        let embedder = resolveEmbedder noEmbedding
                        try
                            let results = Packager.runIngest folder embedder captionGen CancellationToken.None
                            Packager.summarize results
                        finally
                            embedder |> Option.iter (fun e -> e.Dispose())
                if summary.IngestedCount = 0 then
                    eprintfn "오류: 색인 결과 ingested=0 — server 거부 사전 차단 (빈 폴더 또는 unsupported extension)"
                    12
                else
                    eprintfn "  %s — ingested=%d, 파일=%d, %d bytes"
                        (if reuseKb then "기존 색인 재사용" else "색인 완료")
                        summary.IngestedCount summary.FileCount summary.TotalBytes
                    // **PR-B + PR-C + PR-H1 (todo §3.1 + §3.2 + §11)** — keyword + text dump + doc summary.
                    // `--reuse-kb` 경로는 Step 1-b / Step 2 가 이미 박제 → 본 hook idempotent 재호출 시 동일 결과 (DB SSOT).
                    // TextDumper / SummaryBuilder 모두 DB 의 ImageCache.CaptionText / Documents.SummaryText 박제분 우선
                    // 사용 → 재호출 시 caption / summary 동일 결과 박제 (덮어쓰기 없음).
                    // **--review 라운드 1 Major-5 fix**: runPostIngestHooks 가 extractors 인자 forward 받도록 변경 —
                    // runUpload 는 본 hook 호출 시점에만 extractors 필요 (runIngest 는 Packager 내부에서 별도 인스턴스화).
                    // 본 helper 호출 범위 안에서만 사용 + finally dispose.
                    let postHookExtractors : IExtractor list = [
                        new TextExtractor() :> IExtractor
                        new PdfExtractor() :> IExtractor
                        new OoxmlExtractor() :> IExtractor
                        new ImageExtractor() :> IExtractor
                    ]
                    let kwResult =
                        try runPostIngestHooks folder postHookExtractors
                        finally
                            for ex in postHookExtractors do ex.Dispose()
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
                            client psk userIdentity title overwrite stream CancellationToken.None
                        |> fun t -> t.GetAwaiter().GetResult()
                    printfn "업로드 완료 — collectionId=%s" id
                    let retainMsg =
                        if reuseKb then "보존 (--reuse-kb)"
                        else "다음 색인 시작 시 wipe 됩니다"
                    eprintfn "  [안내] 산출물 보관: %s/.lighthouse-kb/ (%s)" folder retainMsg
                    eprintfn "  [안내] .gitignore 에 '.lighthouse-kb/' 추가 권장"
                    0
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
            | LightHouseProtocolError(msg, Some status) when int status = 409 ->
                // 옵션 A — --no-overwrite 박제 + 동일 폴더명 collection 이미 존재. fail 모드 의도된 거부.
                eprintfn "동일 이름 collection 이미 존재 (HTTP 409, --no-overwrite) — %s" msg
                4
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
/// `done-lighthouse-indexer-claude-caption.md` §3 manifest fenced block 의 wire 정합.
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
                    // **PR-Img-Chunk**: caption back-fill 후 text dump 재생성 — gallery `(caption 미생성)` → 실제
                    // caption text 갱신 + 본문 caption-chunk (updateCaptionBatch 가 박제) 가 페이지 inline 으로 surface.
                    // summary.md 는 caption 무관 (A 안) 이라 SummaryBuilder 재호출 없음. dumpAll idempotent — 1회차
                    // 산출물 wipe + 재생성 (TextDumper.dumpAll 의 File.WriteAllText overwrite 정합).
                    let dumpFiles = TextDumper.dumpAll conn folder
                    printfn "caption-update 완료 — updated=%d (rows=%d, text-dump 재생성=%d 파일)"
                        updated rows.Length dumpFiles.Length
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

/// `/indexer` skill Step 1-b — summary 미박제 doc enumeration (caption path 와 동형).
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

/// `/indexer` skill Step 1-b → batch JSON 입력 → Documents.SummaryText UPDATE 단일 transaction.
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
                    // summary.md regenerate — DB SSOT 흡수 (PR-H2 (a) SummaryBuilder.build 의 SummaryText 우선
                    // 분기를 활용). 미진행 시 Step 1 시점 fallback 박제분이 stale 로 남음.
                    let summaries = SummaryBuilder.build conn
                    let summaryPath = SummaryBuilder.write folder summaries
                    eprintfn "  summary.md 재작성 — %d doc (%s)" summaries.Length summaryPath
                    0
                with
                | :? System.IO.InvalidDataException as ex ->
                    eprintfn "오류: %s" ex.Message
                    10

/// `/indexer` skill Step 1-b — summary-prompt SSOT (lib `SummaryStore.SummaryPrompt`) stdout 노출.
/// caption-prompt 와 동형 — subagent prompt template literal 사본 박제 차단.
let private runPrintSummaryPrompt () : int =
    printfn "%s" SummaryStore.SummaryPrompt
    0

/// `/indexer` skill — caption 보존 자동화 (build-drift 인식 → `.lighthouse-kb/` wipe 전 dump).
/// stdout JSON array (camelCase: `hash`, `captionText`, `captionAt`, `captionModel`).
/// caption 박제된 ImageCache row 만 dump — `CaptionText IS NOT NULL AND != ''`.
let private runExportImageCache (folder: string) : int =
    match requireIndexedFolder folder with
    | Error code -> code
    | Ok dbPath ->
        use conn = SqliteStore.openConnection dbPath true
        let rows = ImageStore.exportCaptions conn |> Seq.toArray
        let json = System.Text.Json.JsonSerializer.Serialize(rows, camelJsonOpts())
        printfn "%s" json
        0

/// `/indexer` skill — caption 보존 복원 (`.lighthouse-kb/` wipe 후 재색인 직후 진입).
/// backup JSON 의 (hash, captionText, captionAt, captionModel) 4 컬럼을 신 DB 의 ImageCache 에 UPDATE.
/// hash 매치 row 만 갱신, CaptionText 이미 박제된 row 보존. text-dump 재생성 동반 (caption-update 와 동형).
let private runImportImageCache (folder: string) (jsonPath: string) : int =
    if not (File.Exists jsonPath) then
        eprintfn "오류: backup 파일 미존재 — %s" jsonPath
        11
    else
        match requireIndexedFolder folder with
        | Error code -> code
        | Ok dbPath ->
            let json = File.ReadAllText(jsonPath, Text.Encoding.UTF8)
            let rows = System.Text.Json.JsonSerializer.Deserialize<ImageStore.ImageCaptionBackup[]>(json, camelJsonOpts())
            if isNull rows then
                eprintfn "오류: backup JSON parse 결과 null — %s" jsonPath
                10
            elif rows.Length = 0 then
                eprintfn "  backup 빈 array — no-op (idempotent)"
                0
            else
                use conn = SqliteStore.openConnection dbPath false
                let updated, skipped = ImageStore.importCaptions conn rows
                let dumpFiles = TextDumper.dumpAll conn folder
                printfn "import-image-cache 완료 — updated=%d, skipped=%d (rows=%d, text-dump 재생성=%d 파일)"
                    updated skipped rows.Length dumpFiles.Length
                0

/// `/indexer` skill 자가 진단 — `--reuse-kb` 직전 호출 또는 사용자 수동 진단용.
/// `<folder>/.lighthouse-kb/index.db` 의 `Meta.indexer_version` probe 결과를 JSON 으로 stdout 노출.
/// stdout 한 줄 = `{"status":"ok|key-missing|open-failed|db-missing","version":"<v>|null","reason":"..."}`.
/// SSOT = `KnowledgeBase.probeIndexerVersionDetailed` (server-side `evaluateIndexerVersionGate` 와 공유).
let private runProbeIndexerVersion (folder: string) : int =
    let payload =
        match KnowledgeBase.probeIndexerVersionDetailed folder with
        | Found v -> {| status = "ok"; version = v; reason = "" |}
        | KeyMissing ->
            {| status = "key-missing"; version = ""
               reason = "Meta.indexer_version 키 미박제 (이전 build 산출 가능성)" |}
        | DbMissing ->
            {| status = "db-missing"; version = ""
               reason = sprintf "%s 미존재" (SqliteStore.dbPath folder) |}
        | OpenFailed reason ->
            {| status = "open-failed"; version = ""
               reason = reason |}
    printfn "%s" (System.Text.Json.JsonSerializer.Serialize(payload, camelJsonOpts()))
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
                    let reuseKb = Map.containsKey FlagReuseKb flags
                    let overwrite = not (Map.containsKey FlagNoOverwrite flags)
                    runUpload folder baseUrl psk title userIdentity allowInvalidCerts noEmbedding forceWithoutCaption reuseKb overwrite
            | Some _ ->
                // 자가 검열 C1 — `--upload` 가 value 없거나 빈 string 이면 silent `runIndex` fallback 차단.
                // 사용자 의도는 upload 였으므로 explicit reject 후 exit 10 (usage hint).
                eprintfn "오류: --upload <url> 인자 누락"
                10
            | None when Map.containsKey FlagReuseKb flags ->
                // 자가 검열 Minor-2 — `--reuse-kb` 는 `--upload` 동반 의무. 단독 박제 시 silent 무시 회피 (대칭성).
                eprintfn "오류: --%s 는 --%s <url> 동반 의무" FlagReuseKb FlagUpload
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
        | "export-image-cache" :: folder :: _ ->
            runExportImageCache folder
        | "import-image-cache" :: folder :: jsonPath :: _ ->
            runImportImageCache folder jsonPath
        | "probe-indexer-version" :: folder :: _ ->
            runProbeIndexerVersion folder
        | _ ->
            usage ()
            10
