module Ds2.LightHouse.Cli.Program

open System
open System.IO
open System.Net
open System.Threading
open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Cli

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

let private usage () =
    eprintfn "usage:"
    eprintfn "  lighthouse-cli index <folder> [--upload <url> --psk <key> [--title <name>] [--user <id>] [--allow-invalid-certs]]"
    eprintfn "  lighthouse-cli --version"
    eprintfn ""
    eprintfn "options:"
    eprintfn "  --upload <url>             LightHouseService base URL (https://host:port)"
    eprintfn "  --psk <key>                PSK (DPAPI 미적용 평문 — env var LIGHTHOUSE_PSK 권장)"
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
        let env = Environment.GetEnvironmentVariable "LIGHTHOUSE_PSK"
        if String.IsNullOrWhiteSpace env then None else Some env

let private defaultUserIdentity () =
    let user =
        match Environment.GetEnvironmentVariable "USERNAME" with
        | null | "" -> "anonymous"
        | u -> u
    sprintf "%s@%s" user Environment.MachineName

let private runIndex (folder: string) : int =
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
        printfn "색인 시작 — %s" folder
        // s6-r20 (D-iii / --review M1): cli VLM captionGen builder SSOT = Vlm.buildCaptionGen.
        let captionGen = Vlm.buildCaptionGen CancellationToken.None
        let results = Indexer.ingest folder extractors captionGen progressCb CancellationToken.None
        let ingested = results |> Array.filter (fun (_, r) -> match r with | Ingested _ -> true | _ -> false) |> Array.length
        let skipped  = results |> Array.filter (fun (_, r) -> match r with | Skipped  _ -> true | _ -> false) |> Array.length
        let failed   = results |> Array.filter (fun (_, r) -> match r with | Failed   _ -> true | _ -> false) |> Array.length
        printfn "색인 완료 — ingested=%d skipped=%d failed=%d (total=%d)" ingested skipped failed results.Length
        0

/// `--upload` 본격 분기 — staging copy + 색인 + zip + POST /collections.
/// exit code (D-S6-4): 0/1/2/3/11/12/99.
let private runUpload
        (folder: string)
        (baseUrl: string)
        (psk: string)
        (title: string)
        (userIdentity: string)
        (allowInvalidCerts: bool)
        : int =
    if not (Directory.Exists folder) then
        eprintfn "오류: 폴더 미존재 — %s" folder
        11
    else
        let mutable stagingDir = ""
        let mutable zipPath = ""
        try
            try
                stagingDir <- Packager.createStaging ()
                eprintfn "  staging 사본 — %s" stagingDir
                let fileCount, totalBytes = Packager.copyToStaging folder stagingDir
                if fileCount = 0 then
                    eprintfn "오류: source 가 비어 있음 — 등록 가치 0"
                    12
                else
                    eprintfn "  색인 시작 — %d 파일 (%d bytes)" fileCount totalBytes
                    let ingested = Packager.runIngestInStaging stagingDir CancellationToken.None
                    if ingested = 0 then
                        eprintfn "오류: 색인 결과 ingested=0 — server 거부 사전 차단"
                        12
                    else
                        eprintfn "  색인 완료 — ingested=%d" ingested
                        Packager.writeMeta stagingDir title folder fileCount totalBytes userIdentity
                        zipPath <- Packager.createZip stagingDir
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
            | LightHouseProtocolError(msg, _) ->
                eprintfn "프로토콜 오류 — %s" msg
                99
            | ex ->
                eprintfn "오류 — %s: %s" (ex.GetType().Name) ex.Message
                99
        finally
            Packager.safeDelete zipPath
            Packager.safeDelete stagingDir

[<EntryPoint>]
let main args =
    // CP949 등 legacy code page 활성화 — TextEncoding 의 fallback (LightHouseService Program.fs 와 동일 패턴).
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance)
    let flags, positional = parseArgs args
    if Map.containsKey "version" flags then
        printfn "lighthouse-cli 0.1.0 (Phase S6)"
        0
    else
        match positional with
        | "index" :: folder :: _ ->
            match Map.tryFind "upload" flags with
            | Some baseUrl when not (String.IsNullOrWhiteSpace baseUrl) ->
                match resolvePsk (Map.tryFind "psk" flags) with
                | None ->
                    eprintfn "오류: --psk 또는 LIGHTHOUSE_PSK 환경 변수 필수"
                    10
                | Some psk ->
                    let title =
                        Map.tryFind "title" flags
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                        |> Option.defaultWith (fun () -> Path.GetFileName(Path.GetFullPath folder))
                    let userIdentity =
                        Map.tryFind "user" flags
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                        |> Option.defaultWith defaultUserIdentity
                    let allowInvalidCerts = Map.containsKey "allow-invalid-certs" flags
                    runUpload folder baseUrl psk title userIdentity allowInvalidCerts
            | Some _ ->
                // 자가 검열 C1 — `--upload` 가 value 없거나 빈 string 이면 silent `runIndex` fallback 차단.
                // 사용자 의도는 upload 였으므로 explicit reject 후 exit 10 (usage hint).
                eprintfn "오류: --upload <url> 인자 누락"
                10
            | None ->
                runIndex folder
        | _ ->
            usage ()
            10
