// Pass 1.5 측정 자동화 — F# script
//
// 사용법:
//   dotnet fsi doc/run-pass15.fsx              // smoke (1 trial × 2 scenario × 2 mode = 4 trial)
//   dotnet fsi doc/run-pass15.fsx -- -n 5      // 5 trial × 2 scenario × 2 mode = 20 trial
//   dotnet fsi doc/run-pass15.fsx -- -only treatment -n 3
//
// 사전 조건:
//   - claude CLI ≥ 2.1.x 설치
//   - Promaker.exe 빌드 완료 (Apps/Promaker/Promaker/bin/Debug/net9.0-windows/)
//   - LLM consent 사전 grant (이전 Promaker UI 사용 시 자동 저장됨)
//
// 흐름 (per trial):
//   1. Promaker.exe spawn — McpHostService 자동 시작 + mcp config json 작성
//   2. mcp config path 폴링 (30s timeout)
//   3. claude --print --mcp-config <path> --append-system-prompt <text> --strict-mcp-config
//      --output-format stream-json --allowed-tools <list> <prompt>
//   4. stdout stream-json 라인 파싱 → message id 그룹핑 → tool_use 카운트
//   5. Promaker.exe kill → 다음 trial

#r "nuget: Newtonsoft.Json, 13.0.3"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open Newtonsoft.Json.Linq

let argv = Environment.GetCommandLineArgs()
let getOpt (name: string) (defVal: string) =
    let i = argv |> Array.tryFindIndex (fun a -> a = name)
    match i with
    | Some idx when idx + 1 < argv.Length -> argv.[idx + 1]
    | _ -> defVal
let hasOpt (name: string) = argv |> Array.contains name

let trialPerScenario = getOpt "-n" "1" |> int
let onlyMode = getOpt "-only" ""

// ============================================================
// 경로 설정
// ============================================================
let scriptDir = __SOURCE_DIRECTORY__
let repoRoot = Path.GetFullPath(Path.Combine(scriptDir, "..", "..", "..", ".."))
let promakerExe = Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "bin", "Debug", "net9.0-windows", "Promaker.exe")
let systemPromptRel = "Apps/Promaker/Promaker/LlmAgent/Prompts/1.SystemPrompt.md"
let mcpConfigDir = Path.Combine(Path.GetTempPath(), "Promaker")

let modes = [
    "baseline",  "dac8ef1"   // batching 절 ❌
    "treatment", "5e80af3"   // batching 절 ✅
]

// 결정적 spec — clarification 없이 1 turn 진행 가능
type Scenario = { Id: string; Name: string; Prompt: string }
let scenarios = [
    {
        Id = "S1"
        Name = "실린더 chain heavy"
        Prompt = "현재 NewProject 의 NewSystem 안에 'CylFlow' 라는 Flow 를 만들고, 그 flow 안에 'Adv' 와 'Ret' Work 를 만들어라. Adv → Ret Start arrow. 추가로 'CylDevice' 라는 passive system 을 만들고 그 안에 'Advance' / 'Retract' 두 ApiDef 를 만들어라. 마지막으로 Adv work 안에 'CylDevice.Advance' Call, Ret work 안에 'CylDevice.Retract' Call 을 추가하라. 사용자에게 추가 질문 없이 즉시 진행."
    }
    {
        Id = "S2"
        Name = "독립 4 system"
        Prompt = "현재 NewProject 에 'Sys1', 'Sys2', 'Sys3', 'Sys4' 4 개 active system 을 추가하라. 의존 관계 없음. 사용자에게 추가 질문 없이 즉시 진행."
    }
]

let allowedTools =
    [| "mcp__promaker__list_systems"
       "mcp__promaker__describe_system"
       "mcp__promaker__describe_subtree"
       "mcp__promaker__find_by_name"
       "mcp__promaker__validate_model"
       "mcp__promaker__add_system"
       "mcp__promaker__add_flow"
       "mcp__promaker__add_work"
       "mcp__promaker__add_call"
       "mcp__promaker__add_api_def"
       "mcp__promaker__add_arrow" |]
    |> String.concat ","   // claude CLI 의 --allowed-tools 가 variadic — comma-separated single token 으로 prompt 와 분리

// ============================================================
// 헬퍼
// ============================================================
let runProcess (exe: string) (args: string seq) (workDir: string) : string * int =
    let psi = ProcessStartInfo(exe)
    psi.WorkingDirectory <- workDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.StandardOutputEncoding <- Text.Encoding.UTF8
    for a in args do psi.ArgumentList.Add(a)
    use p = Process.Start(psi)
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    out, p.ExitCode

let getSystemPromptAt (commit: string) : string =
    let out, exit = runProcess "git" [ "show"; sprintf "%s:%s" commit systemPromptRel ] repoRoot
    if exit <> 0 then failwithf "git show %s:%s 실패" commit systemPromptRel
    out

let killPromakerProcesses () =
    Process.GetProcessesByName("Promaker")
    |> Array.iter (fun p ->
        try p.Kill(true); p.WaitForExit(2000) |> ignore with _ -> ())

let spawnPromaker () : Process * string =
    killPromakerProcesses()
    Thread.Sleep(300)
    let psi = ProcessStartInfo(promakerExe)
    psi.WorkingDirectory <- Path.GetDirectoryName(promakerExe)
    psi.UseShellExecute <- true   // WPF UI startup
    psi.ArgumentList.Add("--autostart-llm")
    psi.ArgumentList.Add("--measure-prompt")
    psi.ArgumentList.Add(prompt)
    psi.ArgumentList.Add("--measure-then-exit")
    let proc = Process.Start(psi)
    if proc = null then failwithf "Promaker spawn 실패: %s" promakerExe

    // mcp config 폴링 (60s) — 첫 실행 = WPF JIT + ChatViewModel ctor 안 McpHostService.StartAsync 추가 시간
    let deadline = DateTime.UtcNow.AddSeconds(60.)
    let mutable configPath = ""
    while configPath = "" && DateTime.UtcNow < deadline do
        if Directory.Exists(mcpConfigDir) then
            let pat = sprintf "mcp-*-%d-*.json" proc.Id
            let candidates = Directory.GetFiles(mcpConfigDir, pat)
            if candidates.Length > 0 then configPath <- candidates.[0]
        if configPath = "" then Thread.Sleep(300)
    if configPath = "" then
        try proc.Kill(true) with _ -> ()
        failwithf "Promaker mcp config 30초 안에 작성 안 됨"
    proc, configPath

// ============================================================
// stream-json 파싱
// ============================================================
type Stat = {
    Mode: string
    Scenario: string
    Trial: int
    WallMs: int64
    TurnCount: int        // assistant message 수 (= round-trip 수)
    MultiToolMsgs: int    // tool_use ≥ 2 인 message 수
    MaxToolUse: int       // 단일 message 의 최대 tool_use
    TotalToolUse: int     // 전체 tool 호출 수 (= 작업량 proxy)
    ExitCode: int
    NumResults: int
    StopReason: string    // end_turn / max_tokens / refusal / ... — 잘림 검출
    NumTurnsCli: int      // claude CLI 가 result 패킷에 보고하는 num_turns
    IsError: bool         // result.is_error
    ResultLen: int        // 마지막 assistant text 길이 (작업 보고 완성도 proxy)
}

let analyzeStream (lines: string seq) : Stat -> Stat =
    // result/stop_reason/num_turns/is_error/result_len 를 Stat 에 채워 반환할 부분 함수.
    // 사용처: runClaude 안에서 base record 만들고 analyzeStream lines base => 완성된 Stat
    let msgGroups = Dictionary<string, int>()
    let mutable numResults = 0
    let mutable stopReason = ""
    let mutable numTurnsCli = 0
    let mutable isError = false
    let mutable resultLen = 0
    for line in lines do
        if not (String.IsNullOrWhiteSpace(line)) then
            try
                let jo = JObject.Parse(line)
                let typ =
                    match jo.["type"] with
                    | null -> ""
                    | t -> t.Value<string>()
                match typ with
                | "result" ->
                    numResults <- numResults + 1
                    match jo.["stop_reason"] with
                    | null -> ()
                    | t -> stopReason <- t.Value<string>()
                    match jo.["num_turns"] with
                    | null -> ()
                    | t -> numTurnsCli <- t.Value<int>()
                    match jo.["is_error"] with
                    | null -> ()
                    | t -> isError <- t.Value<bool>()
                    match jo.["result"] with
                    | null -> ()
                    | t -> resultLen <- (t.Value<string>() |> Option.ofObj |> Option.defaultValue "").Length
                | "assistant" ->
                    let msg = jo.["message"]
                    if msg <> null then
                        let mid =
                            match msg.["id"] with
                            | null -> ""
                            | t -> t.Value<string>()
                        let content = msg.["content"]
                        let toolUses =
                            match content with
                            | :? JArray as ja ->
                                ja |> Seq.filter (fun b ->
                                    match b.["type"] with
                                    | null -> false
                                    | t -> t.Value<string>() = "tool_use") |> Seq.length
                            | _ -> 0
                        if mid <> "" then
                            msgGroups.[mid] <-
                                (if msgGroups.ContainsKey mid then msgGroups.[mid] else 0) + toolUses
                | _ -> ()
            with _ -> ()
    let multi = msgGroups.Values |> Seq.filter (fun v -> v >= 2) |> Seq.length
    let maxT = if msgGroups.Count = 0 then 0 else msgGroups.Values |> Seq.max
    let total = msgGroups.Values |> Seq.sum
    fun (b: Stat) ->
        { b with
            TurnCount = msgGroups.Count
            MultiToolMsgs = multi
            MaxToolUse = maxT
            TotalToolUse = total
            NumResults = numResults
            StopReason = stopReason
            NumTurnsCli = numTurnsCli
            IsError = isError
            ResultLen = resultLen }

// ============================================================
// claude CLI 호출
// ============================================================
let runClaude (mode: string) (sysPrompt: string) (scenario: Scenario) (trial: int) (mcpConfig: string) : Stat =
    let psi = ProcessStartInfo("claude")
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.StandardOutputEncoding <- Text.Encoding.UTF8
    psi.WorkingDirectory <- repoRoot
    // --bare 는 keychain 차단으로 OAuth subscription 인증 실패 → 제거.
    // 대신 noise 차단은 --no-session-persistence + --strict-mcp-config + --allowed-tools 로 부분 적용.
    let args = [
        "--print"
        "--no-session-persistence"
        "--mcp-config"; mcpConfig
        "--strict-mcp-config"
        "--append-system-prompt"; sysPrompt
        "--allowed-tools"; allowedTools
        "--output-format"; "stream-json"
        "--include-partial-messages"
        "--verbose"
        scenario.Prompt
    ]
    for a in args do psi.ArgumentList.Add(a)

    let lines = ResizeArray<string>()
    let errLines = ResizeArray<string>()
    let stopwatch = Stopwatch.StartNew()
    use proc = Process.Start(psi)
    let outTask =
        Tasks.Task.Run(fun () ->
            let mutable line = proc.StandardOutput.ReadLine()
            while line <> null do
                lines.Add(line)
                line <- proc.StandardOutput.ReadLine())
    let errTask =
        Tasks.Task.Run(fun () ->
            let mutable line = proc.StandardError.ReadLine()
            while line <> null do
                errLines.Add(line)
                line <- proc.StandardError.ReadLine())
    proc.WaitForExit()
    outTask.Wait()
    errTask.Wait()
    stopwatch.Stop()

    if proc.ExitCode <> 0 then
        eprintfn ""
        eprintfn "  [claude exit=%d] stderr (%d lines):" proc.ExitCode errLines.Count
        for l in errLines do eprintfn "    %s" l
        eprintfn "  [claude] stdout (%d lines, last 5):" lines.Count
        for l in (lines |> Seq.toList |> List.rev |> List.truncate 5 |> List.rev) do
            eprintfn "    %s" l
    else
        for l in errLines do eprintfn "  [claude.err] %s" l

    let baseStat = {
        Mode = mode; Scenario = scenario.Id; Trial = trial
        WallMs = stopwatch.ElapsedMilliseconds
        TurnCount = 0; MultiToolMsgs = 0; MaxToolUse = 0; TotalToolUse = 0
        ExitCode = proc.ExitCode; NumResults = 0
        StopReason = ""; NumTurnsCli = 0; IsError = false; ResultLen = 0
    }
    analyzeStream lines baseStat

// ============================================================
// 메인
// ============================================================
printfn ""
printfn "============================================================"
printfn "Pass 1.5 자동 측정 — trial/scenario=%d, only=%s" trialPerScenario (if onlyMode = "" then "(both)" else onlyMode)
printfn "============================================================"
printfn ""

if not (File.Exists(promakerExe)) then
    eprintfn "Promaker.exe 없음: %s — 먼저 빌드하세요." promakerExe
    exit 1

let results = ResizeArray<Stat>()
let activeModes = modes |> List.filter (fun (n, _) -> onlyMode = "" || n = onlyMode)

for (modeName, commit) in activeModes do
    let sysPrompt = getSystemPromptAt commit
    printfn "[mode=%s commit=%s sysPromptLen=%d]" modeName commit sysPrompt.Length
    for s in scenarios do
        for trial in 1..trialPerScenario do
            printf "  %s/%s/#%d — Promaker spawn ... " modeName s.Id trial
            let proc, configPath = spawnPromaker()
            printf "ready (pid=%d, config=%s) ... " proc.Id (Path.GetFileName(configPath))
            try
                let stat = runClaude modeName sysPrompt s trial configPath
                results.Add(stat)
                printfn "wall=%dms turn=%d mt=%d toolUse=%d stop=%s cliT=%d resLen=%d err=%b exit=%d"
                    stat.WallMs stat.TurnCount stat.MultiToolMsgs stat.TotalToolUse
                    stat.StopReason stat.NumTurnsCli stat.ResultLen stat.IsError stat.ExitCode
            finally
                // graceful close — WPF MainWindow.Closing → DisposeLlmChatAsync → log4net flush.
                // 강제 Kill 만 쓰면 RollingFileAppender 의 file handle close 안 되어 ToolCall/Authoring 로그 30초치 손실.
                try
                    if not proc.HasExited then
                        proc.CloseMainWindow() |> ignore
                        if not (proc.WaitForExit(5000)) then
                            proc.Kill(true)
                            proc.WaitForExit(2000) |> ignore
                with _ -> ()

printfn ""
printfn "============================================================"
printfn "결과"
printfn "============================================================"
printfn ""
printfn "Mode      Scenario  Trial  Wall(ms)  Turn  MultiTool  ToolUse  StopReason       ResLen  Err"
for r in results do
    printfn "%-9s %-9s %-6d %8d  %4d  %9d  %7d  %-15s  %6d  %3b"
        r.Mode r.Scenario r.Trial r.WallMs r.TurnCount r.MultiToolMsgs r.TotalToolUse r.StopReason r.ResultLen r.IsError

// 시나리오별 평균
printfn ""
printfn "=== 시나리오별 평균 ==="
let summary =
    results
    |> Seq.groupBy (fun r -> r.Scenario, r.Mode)
    |> Seq.map (fun ((s, m), g) ->
        let g = Seq.toArray g
        let avg f = g |> Array.averageBy f
        s, m, g.Length,
        avg (fun r -> float r.WallMs) / 1000.0,
        avg (fun r -> float r.TurnCount),
        avg (fun r -> float r.MultiToolMsgs),
        avg (fun r -> float r.MaxToolUse),
        avg (fun r -> float r.TotalToolUse))
    |> Seq.sortBy (fun (s, m, _, _, _, _, _, _) -> s, m)
    |> Seq.toArray

printfn "Scenario  Mode      N  Wall(s)  Turn  MultiTool  MaxToolUse  TotalToolUse"
for (s, m, n, w, t, mt, mx, tt) in summary do
    printfn "%-9s %-9s %1d  %7.1f  %4.1f  %9.1f  %10.1f  %12.1f" s m n w t mt mx tt

// Δ% (treatment vs baseline)
printfn ""
printfn "=== Delta%% (treatment vs baseline, 음수 = 단축) ==="
let byScenario = summary |> Array.groupBy (fun (s, _, _, _, _, _, _, _) -> s)
printfn "Scenario  Wall_B  Wall_T  Wall_Δ%%  Turn_B  Turn_T  Turn_Δ%%"
for (sname, group) in byScenario do
    let b = group |> Array.tryFind (fun (_, m, _, _, _, _, _, _) -> m = "baseline")
    let t = group |> Array.tryFind (fun (_, m, _, _, _, _, _, _) -> m = "treatment")
    match b, t with
    | Some (_, _, _, wB, tB, _, _, _), Some (_, _, _, wT, tT, _, _, _) ->
        let wDelta = if wB > 0.0 then (wT - wB) / wB * 100.0 else 0.0
        let tDelta = if tB > 0.0 then (tT - tB) / tB * 100.0 else 0.0
        let fmt (v: float) = if v >= 0.0 then sprintf "+%6.1f" v else sprintf "%7.1f" v
        printfn "%-9s %6.1f  %6.1f  %s  %6.1f  %6.1f  %s" sname wB wT (fmt wDelta) tB tT (fmt tDelta)
    | _ -> printfn "%-9s — 한쪽 모드 데이터 부족" sname

// markdown 저장
let reportPath = Path.Combine(scriptDir, sprintf "pass15-results-%s.md" (DateTime.Now.ToString("yyyyMMdd-HHmm")))
use sw = new StreamWriter(reportPath, false, Text.UTF8Encoding(true))
sw.WriteLine("# Pass 1.5 결과 (자동)")
sw.WriteLine()
sw.WriteLine(sprintf "- 측정 시각: %s" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")))
sw.WriteLine(sprintf "- trial/scenario: %d" trialPerScenario)
sw.WriteLine(sprintf "- 활성 mode: %s" (activeModes |> List.map fst |> String.concat ", "))
sw.WriteLine()
sw.WriteLine("## Trial 별")
sw.WriteLine("```")
sw.WriteLine("Mode      Scenario  Trial  Wall(ms)  Turn  MultiTool  ToolUse  StopReason       ResLen  Err")
for r in results do
    sw.WriteLine(sprintf "%-9s %-9s %-6d %8d  %4d  %9d  %7d  %-15s  %6d  %3b"
        r.Mode r.Scenario r.Trial r.WallMs r.TurnCount r.MultiToolMsgs r.TotalToolUse r.StopReason r.ResultLen r.IsError)
sw.WriteLine("```")
sw.WriteLine()
sw.WriteLine("## 시나리오별 평균")
sw.WriteLine("```")
sw.WriteLine("Scenario  Mode      N  Wall(s)  Turn  MultiTool  MaxToolUse  TotalToolUse")
for (s, m, n, w, t, mt, mx, tt) in summary do
    sw.WriteLine(sprintf "%-9s %-9s %1d  %7.1f  %4.1f  %9.1f  %10.1f  %12.1f" s m n w t mt mx tt)
sw.WriteLine("```")
sw.WriteLine()
sw.WriteLine("## Δ% (treatment vs baseline, 음수 = 단축)")
sw.WriteLine("```")
sw.WriteLine("Scenario  Wall_B  Wall_T  Wall_Δ%  Turn_B  Turn_T  Turn_Δ%")
for (sname, group) in byScenario do
    let b = group |> Array.tryFind (fun (_, m, _, _, _, _, _, _) -> m = "baseline")
    let t = group |> Array.tryFind (fun (_, m, _, _, _, _, _, _) -> m = "treatment")
    match b, t with
    | Some (_, _, _, wB, tB, _, _, _), Some (_, _, _, wT, tT, _, _, _) ->
        let wDelta = if wB > 0.0 then (wT - wB) / wB * 100.0 else 0.0
        let tDelta = if tB > 0.0 then (tT - tB) / tB * 100.0 else 0.0
        let fmt (v: float) = if v >= 0.0 then sprintf "+%6.1f" v else sprintf "%7.1f" v
        sw.WriteLine(sprintf "%-9s %6.1f  %6.1f  %s  %6.1f  %6.1f  %s" sname wB wT (fmt wDelta) tB tT (fmt tDelta))
    | _ -> sw.WriteLine(sprintf "%-9s — 한쪽 모드 데이터 부족" sname)
sw.WriteLine("```")
printfn ""
printfn "→ 리포트 저장: %s" reportPath
printfn ""
