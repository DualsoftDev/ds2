module Ds2.LightHouse.Cli.Program

open System
open System.IO
open System.Threading
open Ds2.LightHouse
open Ds2.LightHouse.Extractors

/// Phase S6 scaffold — todo-lighthouse-kb-server.md §4.2 Phase S6.
///
/// 본 turn = `index` 명령 (in-process 색인) 만 스캐폴딩. `--upload` 는 follow-up.
///
/// exit code SSOT (D-S6-4):
///   0  ok
///   1  인증 실패 (upload 단계 — 본 turn 미구현)
///   2  IndexerVersion mismatch (upload 단계 — 본 turn 미구현)
///   3  zip / size 초과 (upload 단계 — 본 turn 미구현)
///   10 명령행 인자 오류 (사용법 mismatch)
///   11 폴더 미존재 / 접근 불가
///   99 기타

let private usage () =
    eprintfn "usage:"
    eprintfn "  lighthouse-cli index <folder> [--upload <url> --psk <key> --title <name>]"
    eprintfn "  lighthouse-cli --version"
    eprintfn ""
    eprintfn "options:"
    eprintfn "  --upload <url>     LightHouseService base URL (https://host:port)"
    eprintfn "  --psk <key>        PSK (DPAPI 미적용 평문 — env var 권장)"
    eprintfn "  --title <name>     collection 표시 이름 (생략 시 폴더명)"
    eprintfn "  --version          버전 출력 후 종료"

/// args 수동 parsing — System.CommandLine 없이 dependency minimize (D-S6-3).
/// 반환 = (flags map, positional list).
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
            // simple progress to stderr — log4net 미초기화 환경에서도 동작.
            let pct = if p.TotalFiles > 0 then (p.CompletedFiles * 100) / p.TotalFiles else 0
            if pct <> lastReported then
                let current = p.CurrentFile |> Option.defaultValue ""
                eprintfn "  [%d%%] %d/%d — %s" pct p.CompletedFiles p.TotalFiles current
                lastReported <- pct
        printfn "색인 시작 — %s" folder
        let results = Indexer.ingest folder extractors progressCb CancellationToken.None
        let ingested = results |> Array.filter (fun (_, r) -> match r with | Ingested _ -> true | _ -> false) |> Array.length
        let skipped  = results |> Array.filter (fun (_, r) -> match r with | Skipped  _ -> true | _ -> false) |> Array.length
        let failed   = results |> Array.filter (fun (_, r) -> match r with | Failed   _ -> true | _ -> false) |> Array.length
        printfn "색인 완료 — ingested=%d skipped=%d failed=%d (total=%d)" ingested skipped failed results.Length
        0

[<EntryPoint>]
let main args =
    // CP949 등 legacy code page 활성화 — TextEncoding 의 fallback (LightHouseService Program.fs 와 동일 패턴).
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance)
    let flags, positional = parseArgs args
    if Map.containsKey "version" flags then
        printfn "lighthouse-cli 0.1.0 (Phase S6 scaffold)"
        0
    else
        match positional with
        | "index" :: folder :: _ ->
            if Map.containsKey "upload" flags then
                // follow-up: upload 구현 후 인증 / IndexerVersion gate / zip 크기 분기 (exit code 1/2/3).
                eprintfn "오류: --upload 는 Phase S6 follow-up 에서 구현 예정 (본 turn = 색인만)"
                10
            else
                runIndex folder
        | _ ->
            usage ()
            10
