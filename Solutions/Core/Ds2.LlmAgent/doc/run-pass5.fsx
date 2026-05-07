// Pass 5 final 측정 — chain heavy 시나리오 (실린더) N회 반복 + 통계 + gate 판정
//
// 정량 gate (todo): 평균 turn ≤ 5 AND 평균 wall ≤ 18s
//
// 흐름:
//   1. for trial in 1..N:
//      - Promaker spawn `--autostart-llm --measure-prompt <S1 prompt, unique Cyl{N}> --measure-then-exit`
//      - WaitForExit (timeout 180s)
//      - ds2.log 의 trial 시작 ~ 종료 cut-off 분석
//      - turnCount / wallSec / toolUseCount / authoring count 추출
//   2. 통계 (avg / min / max / stddev) + gate 판정
//   3. markdown 결과 저장 (`doc/pass5-results-<timestamp>.md`)
//
// 사용:
//   dotnet fsi doc/run-pass5.fsx                                    # default 5 trial, fresh store
//   dotnet fsi doc/run-pass5.fsx 3                                  # 3 trial, fresh store
//   dotnet fsi doc/run-pass5.fsx 5 path\to\default-project.ds2      # default project 환경 측정
//
// default project 환경 측정 시: GUI 로 Promaker 열어 NewProject 생성 + 저장 (.ds2). 그 path 인자.
// 매 trial 마다 같은 .ds2 load 라 누적 system 명 충돌 회피 위해 prompt 의 Cyl{trialId} unique 화 활용.

#r "nuget: Newtonsoft.Json, 13.0.3"

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

let argv = fsi.CommandLineArgs
let nTrials =
    if argv.Length >= 2 then
        match Int32.TryParse(argv.[1]) with true, n when n > 0 && n <= 20 -> n | _ -> 5
    else 5
let loadFilePath =
    if argv.Length >= 3 && File.Exists(argv.[2]) then Some argv.[2]
    else None

let scriptDir = __SOURCE_DIRECTORY__
let repoRoot = Path.GetFullPath(Path.Combine(scriptDir, "..", "..", "..", ".."))
let promakerExe = Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "bin", "Debug", "net9.0-windows", "Promaker.exe")
let logDir = Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "bin", "Debug", "net9.0-windows", "logs")
let timeoutMs = 180_000

// S1 chain heavy 시나리오 — Cyl{trialId} 로 unique 화 (이전 trial 의 누적 영향 최소화)
let trialId =
    // unique suffix — 동일 fsx 호출 안에서는 1..N, 여러 호출 사이에는 timestamp prefix
    let stamp = DateTime.Now.ToString("HHmmss")
    fun (i: int) -> sprintf "%s%d" stamp i

let promptFor trial =
    let id = trialId trial
    sprintf "현재 NewProject 에 'Cyl%s' 라는 실린더 시스템 1개를 만들어줘. 동일 시스템 안에 ApiDef 2개 (ADV, RET) + Flow 1개 ('Run') + Work 2개 (Adv: Cyl%s.ADV 호출, Ret: Cyl%s.RET 호출) + Arrow 1개 (Adv→Ret, Start). 사용자에게 추가 질문 없이 즉시 진행." id id id

if not (File.Exists(promakerExe)) then
    eprintfn "Promaker.exe 없음: %s" promakerExe
    eprintfn "먼저 빌드: dotnet build Apps/Promaker/Promaker.sln -c Debug"
    exit 1

let lineRegex =
    Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(\w+)\s*\] (\w+) [—\-] (.*)$")
let enterRegex = Regex(@"^(\w+) enter seq=(\d+) t=(\d+) nanos=(\d+)$")
let okRegex = Regex(@"^(\w+) ok seq=(\d+) entryT=(\d+) exitT=(\d+) elapsedMs=(\d+) ")

type LogLine = { Ts: DateTime; Level: string; Logger: string; Body: string }
type TrialResult = {
    Trial: int
    Wall: TimeSpan
    Exited: bool
    EnterCount: int
    OkCount: int
    AuthoringCount: int
    AssistantTurnCount: int
    UserTurnCount: int
    HasResultLine: bool
}

let parseLogs (logFile: string) (startTime: DateTime) =
    if not (File.Exists(logFile)) then [||]
    else
        File.ReadAllLines(logFile, Text.Encoding.UTF8)
        |> Array.choose (fun raw ->
            let m = lineRegex.Match(raw)
            if m.Success then
                let ts = DateTime.ParseExact(m.Groups.[1].Value, "yyyy-MM-dd HH:mm:ss.fff", null)
                if ts >= startTime then
                    Some { Ts = ts; Level = m.Groups.[2].Value.Trim(); Logger = m.Groups.[3].Value; Body = m.Groups.[4].Value }
                else None
            else None)

let runOne trial =
    printfn "=== Trial %d/%d ===" trial nTrials
    let prompt = promptFor trial
    let startTime = DateTime.Now
    let psi = ProcessStartInfo(promakerExe)
    psi.WorkingDirectory <- Path.GetDirectoryName(promakerExe)
    psi.UseShellExecute <- true
    psi.ArgumentList.Add("--autostart-llm")
    psi.ArgumentList.Add("--measure-prompt")
    psi.ArgumentList.Add(prompt)
    psi.ArgumentList.Add("--measure-then-exit")
    // default project 환경 측정 — App.StartupFilePath 가 첫 positional arg 로 .ds2 load.
    match loadFilePath with
    | Some p -> psi.ArgumentList.Add(p)
    | None -> ()
    let proc = Process.Start(psi)
    let sw = Stopwatch.StartNew()
    let exited = proc.WaitForExit(timeoutMs)
    sw.Stop()
    if not exited then
        eprintfn "  timeout (%dms) — kill" timeoutMs
        try proc.Kill(true); proc.WaitForExit(5000) |> ignore with _ -> ()
    System.Threading.Thread.Sleep(800)  // log4net flush

    let today = DateTime.Now.ToString("yyyyMMdd")
    let logFile = Path.Combine(logDir, sprintf "ds2.log%s" today)
    let lines = parseLogs logFile startTime
    let enters = lines |> Array.filter (fun l -> l.Logger = "ToolCall" && enterRegex.IsMatch(l.Body))
    let oks = lines |> Array.filter (fun l -> l.Logger = "ToolCall" && okRegex.IsMatch(l.Body))
    let authorings = lines |> Array.filter (fun l -> l.Logger = "Authoring" && l.Body.Contains("Executed"))
    let rawAssistant =
        lines |> Array.filter (fun l ->
            l.Logger = "RawStream"
            && (l.Body.Contains("\"type\":\"assistant\"") || l.Body.Contains("\"role\":\"assistant\"")))
    let rawUser =
        lines |> Array.filter (fun l ->
            l.Logger = "RawStream"
            && (l.Body.Contains("\"type\":\"user\"") || l.Body.Contains("\"role\":\"user\"")))
    let hasResult =
        lines |> Array.exists (fun l ->
            l.Logger = "RawStream" && l.Body.Contains("\"type\":\"result\""))
    printfn "  wall=%.1fs exited=%b enter=%d ok=%d authoring=%d asst=%d result=%b"
        sw.Elapsed.TotalSeconds exited enters.Length oks.Length authorings.Length rawAssistant.Length hasResult
    { Trial = trial; Wall = sw.Elapsed; Exited = exited
      EnterCount = enters.Length; OkCount = oks.Length; AuthoringCount = authorings.Length
      AssistantTurnCount = rawAssistant.Length; UserTurnCount = rawUser.Length
      HasResultLine = hasResult }

let overallStart = DateTime.Now
printfn "=== Pass 5 final 측정 시작 — %s, n=%d ===" (overallStart.ToString("yyyy-MM-dd HH:mm:ss")) nTrials
printfn "Promaker: %s" promakerExe
match loadFilePath with
| Some p -> printfn "Load .ds2 : %s" p
| None -> printfn "Load .ds2 : (none — fresh store)"
printfn ""

let results = [| for i in 1 .. nTrials -> runOne i |]

let avg (xs: float[]) = if xs.Length = 0 then 0.0 else Array.average xs
let stddev (xs: float[]) =
    if xs.Length < 2 then 0.0
    else
        let m = avg xs
        Math.Sqrt(xs |> Array.map (fun x -> (x - m) ** 2.0) |> Array.average)

let walls = results |> Array.map (fun r -> r.Wall.TotalSeconds)
let turns = results |> Array.map (fun r -> float r.AssistantTurnCount)
let toolUses = results |> Array.map (fun r -> float r.OkCount)
let exitedCount = results |> Array.filter (fun r -> r.Exited) |> Array.length
let resultLineCount = results |> Array.filter (fun r -> r.HasResultLine) |> Array.length

printfn ""
printfn "=== 통계 (n=%d) ===" nTrials
printfn "Self-close OK   : %d/%d" exitedCount nTrials
printfn "stop_reason 도달: %d/%d" resultLineCount nTrials
printfn "Wall(s)        : avg=%.1f min=%.1f max=%.1f std=%.1f" (avg walls) (Array.min walls) (Array.max walls) (stddev walls)
printfn "AsstTurn       : avg=%.1f min=%.0f max=%.0f std=%.1f" (avg turns) (Array.min turns) (Array.max turns) (stddev turns)
printfn "ToolUse(ok)    : avg=%.1f min=%.0f max=%.0f std=%.1f" (avg toolUses) (Array.min toolUses) (Array.max toolUses) (stddev toolUses)

let avgTurn = avg turns
let avgWall = avg walls
let turnPass = avgTurn <= 5.0
let wallPass = avgWall <= 18.0
let gatePass = turnPass && wallPass

printfn ""
printfn "=== Gate (avg turn ≤ 5 AND avg wall ≤ 18s) ==="
printfn "avg turn = %.1f (limit 5.0) — %s" avgTurn (if turnPass then "PASS" else "FAIL")
printfn "avg wall = %.1fs (limit 18.0s) — %s" avgWall (if wallPass then "PASS" else "FAIL")
printfn "RESULT: %s" (if gatePass then "PASS" else "FAIL")

// markdown 저장
let outFile = Path.Combine(scriptDir, sprintf "pass5-results-%s.md" (overallStart.ToString("yyyyMMdd-HHmm")))
let sb = System.Text.StringBuilder()
sb.AppendLine(sprintf "# Pass 5 final 측정 결과 (%s)" (overallStart.ToString("yyyy-MM-dd HH:mm"))) |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine(sprintf "- 시나리오: S1 chain heavy (실린더) — Cyl{trialId} unique 화") |> ignore
sb.AppendLine(sprintf "- n = %d trials" nTrials) |> ignore
sb.AppendLine("- 코드 상태: HEAD = Pass 4 (SystemPrompt 풀세트 + Negative test) + Pass 5 hot-fix (sendHandler timing race)") |> ignore
sb.AppendLine("- 측정: Promaker self-flow (--autostart-llm --measure-prompt --measure-then-exit)") |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine("## Trial 결과") |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine("| trial | wall(s) | exited | result line | enter | ok | authoring | asstTurn |") |> ignore
sb.AppendLine("|-------|--------:|:------:|:-----------:|------:|---:|----------:|---------:|") |> ignore
for r in results do
    sb.AppendLine(sprintf "| %d | %.1f | %s | %s | %d | %d | %d | %d |"
        r.Trial r.Wall.TotalSeconds (if r.Exited then "✓" else "✗") (if r.HasResultLine then "✓" else "✗")
        r.EnterCount r.OkCount r.AuthoringCount r.AssistantTurnCount) |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine("## 통계") |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine(sprintf "- Self-close OK: %d/%d" exitedCount nTrials) |> ignore
sb.AppendLine(sprintf "- stop_reason 도달: %d/%d" resultLineCount nTrials) |> ignore
sb.AppendLine(sprintf "- Wall(s): avg=%.1f / min=%.1f / max=%.1f / std=%.1f" (avg walls) (Array.min walls) (Array.max walls) (stddev walls)) |> ignore
sb.AppendLine(sprintf "- AsstTurn: avg=%.1f / min=%.0f / max=%.0f / std=%.1f" (avg turns) (Array.min turns) (Array.max turns) (stddev turns)) |> ignore
sb.AppendLine(sprintf "- ToolUse(ok): avg=%.1f / min=%.0f / max=%.0f / std=%.1f" (avg toolUses) (Array.min toolUses) (Array.max toolUses) (stddev toolUses)) |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine("## Gate 판정 (avg turn ≤ 5 AND avg wall ≤ 18s)") |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine(sprintf "- avg turn = %.1f (limit 5.0) — **%s**" avgTurn (if turnPass then "PASS" else "FAIL")) |> ignore
sb.AppendLine(sprintf "- avg wall = %.1fs (limit 18.0s) — **%s**" avgWall (if wallPass then "PASS" else "FAIL")) |> ignore
sb.AppendLine(sprintf "- **결과: %s**" (if gatePass then "✅ PASS" else "❌ FAIL")) |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine("## 비교 (Pass 1.5 baseline 의 S1 결과)") |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine("Pass 1.5 측정 (n=3, 자동화 도구 = run-pass15.fsx, baseline=dac8ef1, treatment=5e80af3 — 둘 다 chain pattern 가이드 / variable binding 미적용):") |> ignore
sb.AppendLine("") |> ignore
sb.AppendLine("- baseline: wall avg 28.1s / asstTurn 4.7 / toolUse 3.7 (작업 누락 의심)") |> ignore
sb.AppendLine("- treatment(5e80af3): wall avg 57.8s / asstTurn 9.3 / toolUse 8.7") |> ignore

File.WriteAllText(outFile, sb.ToString(), Text.Encoding.UTF8)
printfn ""
printfn "결과 저장: %s" outFile
