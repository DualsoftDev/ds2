// Pass 2 spike — McpHostService 동시성 검증
//
// 목적: 단일 client connection 의 multi tool_use 가 concurrent dispatch 되는지 직렬인지 판정.
// 흐름:
//   1. Promaker spawn `--autostart-llm --measure-prompt <S2 prompt> --measure-then-exit`
//   2. Promaker 가 chat panel 자동 토글 → ClaudeCliProvider 가 정상 LlmTurnContext.Begin → claude CLI spawn
//      → mcp tool 호출 → ToolCall trace 로그 (enter seq=N t=<thread> nanos=...) → ApplyImportPlan → self-close
//   3. fsx 는 process.WaitForExit (timeout 120s) 만 대기 — log4net flush + WPF 정상 shutdown
//   4. ds2.log 의 ToolCall enter 라인 추출 + 같은 시간대 (~ms) 의 thread id 다양성 분석

#r "nuget: Newtonsoft.Json, 13.0.3"

open System
open System.Diagnostics
open System.IO

let scriptDir = __SOURCE_DIRECTORY__
let repoRoot = Path.GetFullPath(Path.Combine(scriptDir, "..", "..", "..", ".."))
let promakerExe = Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "bin", "Debug", "net9.0-windows", "Promaker.exe")
let logDir = Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "bin", "Debug", "net9.0-windows", "logs")

// S2 시나리오 (4 system 자발 묶음 = parallel tool_use 보장)
let prompt = "현재 NewProject 에 'Sys1', 'Sys2', 'Sys3', 'Sys4' 4 개 active system 을 추가하라. 의존 관계 없음. 사용자에게 추가 질문 없이 즉시 진행."

if not (File.Exists(promakerExe)) then
    eprintfn "Promaker.exe 없음: %s" promakerExe
    exit 1

// 측정 시작 timestamp — log 분석 시 cut-off
let startTime = DateTime.Now
printfn "=== Pass 2 spike 측정 시작 — %s ===" (startTime.ToString("yyyy-MM-dd HH:mm:ss"))
printfn "Promaker spawn ..."

let psi = ProcessStartInfo(promakerExe)
psi.WorkingDirectory <- Path.GetDirectoryName(promakerExe)
psi.UseShellExecute <- true
psi.ArgumentList.Add("--autostart-llm")
psi.ArgumentList.Add("--measure-prompt")
psi.ArgumentList.Add(prompt)
psi.ArgumentList.Add("--measure-then-exit")
let proc = Process.Start(psi)
if proc = null then failwithf "Promaker spawn 실패: %s" promakerExe

let stopwatch = Stopwatch.StartNew()
let timeoutMs = 120_000
let exited = proc.WaitForExit(timeoutMs)
stopwatch.Stop()

if not exited then
    eprintfn "Promaker timeout (%dms 초과) — 강제 kill" timeoutMs
    try proc.Kill(true); proc.WaitForExit(5000) |> ignore with _ -> ()
else
    printfn "Promaker self-close 완료 — wall=%dms" stopwatch.ElapsedMilliseconds

// 약간 대기 — log4net 의 마지막 flush
System.Threading.Thread.Sleep(500)

// ds2.log<오늘날짜> 분석
let today = DateTime.Now.ToString("yyyyMMdd")
let logFile = Path.Combine(logDir, sprintf "ds2.log%s" today)
if not (File.Exists(logFile)) then
    eprintfn "log 파일 없음: %s" logFile
    exit 1

printfn ""
printfn "=== ds2.log 분석 — %s ===" (Path.GetFileName(logFile))
printfn ""

// 라인 형식: "yyyy-MM-dd HH:mm:ss.fff [LEVEL] Logger — Message"
let lineRegex =
    System.Text.RegularExpressions.Regex(
        @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(\w+)\s*\] (\w+) [—\-] (.*)$")

type LogLine = { Ts: DateTime; Level: string; Logger: string; Body: string }

let lines =
    File.ReadAllLines(logFile, Text.Encoding.UTF8)
    |> Array.choose (fun raw ->
        let m = lineRegex.Match(raw)
        if m.Success then
            let ts = DateTime.ParseExact(m.Groups.[1].Value, "yyyy-MM-dd HH:mm:ss.fff", null)
            if ts >= startTime then
                Some { Ts = ts; Level = m.Groups.[2].Value.Trim(); Logger = m.Groups.[3].Value; Body = m.Groups.[4].Value }
            else None
        else None)

printfn "측정 시작 후 로그 라인 = %d" lines.Length

// ToolCall enter 라인 — "<toolName> enter seq=<n> t=<threadId> nanos=<ts>"
let enterRegex = System.Text.RegularExpressions.Regex(@"^(\w+) enter seq=(\d+) t=(\d+) nanos=(\d+)$")
let okRegex = System.Text.RegularExpressions.Regex(@"^(\w+) ok seq=(\d+) entryT=(\d+) exitT=(\d+) elapsedMs=(\d+) ")

let enters =
    lines
    |> Array.filter (fun l -> l.Logger = "ToolCall" && l.Body.Contains("enter"))
    |> Array.choose (fun l ->
        let m = enterRegex.Match(l.Body)
        if m.Success then
            Some (l.Ts, m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value, int64 m.Groups.[4].Value)
        else None)

let oks =
    lines
    |> Array.filter (fun l -> l.Logger = "ToolCall" && l.Body.Contains("ok seq="))
    |> Array.choose (fun l ->
        let m = okRegex.Match(l.Body)
        if m.Success then
            Some (l.Ts, m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value, int m.Groups.[4].Value, int m.Groups.[5].Value)
        else None)

printfn ""
printfn "=== ToolCall enter 라인 (%d개) ===" enters.Length
printfn "Time(HH:mm:ss.fff)  Tool                                            seq  threadId  nanos"
for (ts, tool, seq, tid, nanos) in enters do
    printfn "%s  %-46s  %3d  %8d  %d" (ts.ToString("HH:mm:ss.fff")) tool seq tid nanos

printfn ""
printfn "=== ToolCall ok 라인 (%d개) ===" oks.Length
printfn "Time(HH:mm:ss.fff)  Tool                                            seq  entryT  exitT  elapsedMs"
for (ts, tool, seq, entryT, exitT, elapsed) in oks do
    printfn "%s  %-46s  %3d  %6d  %5d  %9d" (ts.ToString("HH:mm:ss.fff")) tool seq entryT exitT elapsed

// 동시성 분석 — enter 의 timestamp 차이 + thread id 다양성
printfn ""
printfn "=== 동시성 분석 ==="
if enters.Length < 2 then
    printfn "enter 라인 < 2 개 — 동시성 판정 불가"
else
    let entriesSorted = enters |> Array.sortBy (fun (_, _, seq, _, _) -> seq)
    let threadIds = entriesSorted |> Array.map (fun (_, _, _, t, _) -> t) |> Array.distinct
    printfn "고유 thread id 수 = %d" threadIds.Length
    printfn "thread ids: %s" (String.concat ", " (threadIds |> Array.map string))

    // 인접 enter 의 시간 차이 (ms)
    printfn ""
    printfn "인접 enter 시간 차 (ms) — 0~10ms 면 동시 진입 (concurrent dispatch), >100ms 면 SDK 가 직렬화"
    for i in 1 .. entriesSorted.Length - 1 do
        let (t1, tool1, seq1, _, _) = entriesSorted.[i - 1]
        let (t2, tool2, seq2, _, _) = entriesSorted.[i]
        let deltaMs = (t2 - t1).TotalMilliseconds
        printfn "  seq %d -> %d (%s -> %s): %.0fms" seq1 seq2 tool1 tool2 deltaMs

// Authoring 라인 (실제 mutation 적용 여부)
let authorings =
    lines
    |> Array.filter (fun l -> l.Logger = "Authoring" && l.Body.Contains("Executed"))
printfn ""
printfn "=== Authoring 'Executed' 라인 (%d개) ===" authorings.Length
for l in authorings do
    printfn "  %s  %s" (l.Ts.ToString("HH:mm:ss.fff")) l.Body
