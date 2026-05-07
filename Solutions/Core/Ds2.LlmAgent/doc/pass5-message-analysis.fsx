// Pass 5 — message id 그룹핑 분석 (chain pattern 실제 활용도 검증)
//
// 목적: ds2.log 의 RawStream assistant 라인을 message id 별 그룹핑하여
//   - 각 message 안 tool_use block 수
//   - multi tool_use messages (≥2) 비율
//   - max tool_use per message
//   - assignVar / $var 사용 trial 비율
// 산출 → SystemPrompt 의 chain pattern 가이드를 LLM 이 실제로 따르는지 객관 검증.
//
// 사용:
//   dotnet fsi doc/pass5-message-analysis.fsx                      # 가장 최근 30분 ds2.log 분석
//   dotnet fsi doc/pass5-message-analysis.fsx 60                   # 가장 최근 60분 분석
//   dotnet fsi doc/pass5-message-analysis.fsx 30 ds2.log20260507   # 특정 log 파일

#r "nuget: Newtonsoft.Json, 13.0.3"

open System
open System.IO
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq

let argv = fsi.CommandLineArgs
let recentMinutes =
    if argv.Length >= 2 then
        match Int32.TryParse(argv.[1]) with true, n when n > 0 -> n | _ -> 30
    else 30

let scriptDir = __SOURCE_DIRECTORY__
let repoRoot = Path.GetFullPath(Path.Combine(scriptDir, "..", "..", "..", ".."))
let logDir = Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "bin", "Debug", "net9.0-windows", "logs")

let logFile =
    if argv.Length >= 3 then
        let p = argv.[2]
        if File.Exists(p) then p
        elif File.Exists(Path.Combine(logDir, p)) then Path.Combine(logDir, p)
        else
            eprintfn "log 파일 없음: %s" p
            exit 1
    else
        let today = DateTime.Now.ToString("yyyyMMdd")
        Path.Combine(logDir, sprintf "ds2.log%s" today)

if not (File.Exists(logFile)) then
    eprintfn "log 파일 없음: %s" logFile
    exit 1

let cutoff = DateTime.Now.AddMinutes(float -recentMinutes)
printfn "=== Pass 5 message-grouping 분석 ==="
printfn "Log: %s" logFile
printfn "Cutoff: 최근 %d분 (%s ~)" recentMinutes (cutoff.ToString("HH:mm:ss"))
printfn ""

let lineRegex =
    Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(\w+)\s*\] (\w+) [—\-] (.*)$")

type MsgBlock = {
    MsgId: string
    Ts: DateTime
    Type: string                 // "tool_use" | "text" | "thinking"
    ToolName: string option
    InputJson: string
}

let parseAssistant (ts: DateTime) (body: string) : MsgBlock list =
    try
        let json = JObject.Parse(body)
        let typ = json.Value<string>("type")
        if typ <> "assistant" then []
        else
            let msg = json.["message"] :?> JObject
            if isNull msg then []
            else
                let msgId = msg.Value<string>("id")
                let content = msg.["content"] :?> JArray
                if isNull content then []
                else
                    content
                    |> Seq.map (fun blk ->
                        let blkObj = blk :?> JObject
                        let blkType = blkObj.Value<string>("type")
                        let toolName = if blkType = "tool_use" then Some(blkObj.Value<string>("name")) else None
                        let inputJson =
                            if blkType = "tool_use" then
                                match blkObj.["input"] with
                                | null -> ""
                                | x -> x.ToString()
                            else ""
                        { MsgId = (if isNull msgId then "" else msgId)
                          Ts = ts
                          Type = (if isNull blkType then "" else blkType)
                          ToolName = toolName
                          InputJson = inputJson })
                    |> List.ofSeq
    with _ -> []

// 1. 라인 파싱 + cut-off
let blocks =
    File.ReadAllLines(logFile, Text.Encoding.UTF8)
    |> Array.choose (fun raw ->
        let m = lineRegex.Match(raw)
        if m.Success then
            let ts = DateTime.ParseExact(m.Groups.[1].Value, "yyyy-MM-dd HH:mm:ss.fff", null)
            if ts >= cutoff && m.Groups.[3].Value = "RawStream" then
                Some (ts, m.Groups.[4].Value)
            else None
        else None)
    |> Array.collect (fun (ts, body) -> parseAssistant ts body |> List.toArray)

printfn "RawStream assistant blocks: %d" blocks.Length

if blocks.Length = 0 then
    printfn "분석 대상 없음"
    exit 0

// 2. 각 message 가 mcp__promaker__ tool 을 1개 이상 호출했는지 — 분석 대상 message
let mutationToolPrefix = "mcp__promaker__"
let isPromakerTool (name: string option) =
    match name with
    | Some n -> n.StartsWith(mutationToolPrefix)
    | None -> false

// 3. message id 별 그룹핑 — tool_use block 만 카운트
let messageGroups =
    blocks
    |> Array.filter (fun b -> b.Type = "tool_use" && isPromakerTool b.ToolName)
    |> Array.groupBy (fun b -> b.MsgId)
    |> Array.map (fun (msgId, toolBlocks) ->
        let toolNames = toolBlocks |> Array.choose (fun b -> b.ToolName)
        let firstTs = toolBlocks |> Array.map (fun b -> b.Ts) |> Array.min
        let inputs = toolBlocks |> Array.map (fun b -> b.InputJson)
        let usesAssignVar = inputs |> Array.exists (fun i -> i.Contains("\"assignVar\""))
        let usesVarRef =
            inputs |> Array.exists (fun i ->
                Regex.IsMatch(i, @""":\s*""\$[a-zA-Z_][a-zA-Z0-9_]*"""))
        msgId, firstTs, toolNames, usesAssignVar, usesVarRef)
    |> Array.sortBy (fun (_, ts, _, _, _) -> ts)

let nMessages = messageGroups.Length
let multiCount = messageGroups |> Array.filter (fun (_, _, names, _, _) -> names.Length >= 2) |> Array.length
let maxToolUse = messageGroups |> Array.map (fun (_, _, names, _, _) -> names.Length) |> Array.max
let avgToolUse = messageGroups |> Array.averageBy (fun (_, _, names, _, _) -> float names.Length)
let assignVarMsgs = messageGroups |> Array.filter (fun (_, _, _, av, _) -> av) |> Array.length
let varRefMsgs = messageGroups |> Array.filter (fun (_, _, _, _, vr) -> vr) |> Array.length

let multiPct = float multiCount / float nMessages * 100.0
let assignVarPct = float assignVarMsgs / float nMessages * 100.0
let varRefPct = float varRefMsgs / float nMessages * 100.0

printfn ""
printfn "=== mcp__promaker__ 호출 message 통계 ==="
printfn "전체 message 수 (mcp 호출 1+): %d" nMessages
printfn "Multi tool_use messages (≥2): %d  (%.1f%%)" multiCount multiPct
printfn "Max tool_use / message       : %d" maxToolUse
printfn "Avg tool_use / message       : %.2f" avgToolUse
printfn "assignVar 사용 message       : %d  (%.1f%%)" assignVarMsgs assignVarPct
printfn "$<var> 참조 사용 message     : %d  (%.1f%%)" varRefMsgs varRefPct

printfn ""
printfn "=== Tool use 분포 ==="
let bucket = messageGroups |> Array.map (fun (_, _, names, _, _) -> names.Length) |> Array.countBy id |> Array.sortBy fst
for (k, v) in bucket do
    let bar = String.replicate (min 50 (v * 50 / nMessages + 1)) "#"
    printfn "%2d tool/msg : %3d msg  %s" k v bar

printfn ""
printfn "=== Message 별 상세 (최대 30개) ==="
printfn "%-12s %-30s %2s %s %s %s" "Time" "Tools" "N" "AV" "VR" "Tools"
for (msgId, ts, names, av, vr) in messageGroups |> Array.truncate 30 do
    let toolNames = names |> Array.map (fun n -> n.Replace("mcp__promaker__", "")) |> String.concat "+"
    printfn "%s %2d %s %s %s"
        (ts.ToString("HH:mm:ss.fff"))
        names.Length
        (if av then "AV" else "  ")
        (if vr then "VR" else "  ")
        toolNames

printfn ""
printfn "=== 해석 ==="
let idealRatio = 5.0
let ratio = avgToolUse
if ratio >= 5.0 then
    printfn "Avg %.2f tool/msg ≥ 5.0 — chain pattern 활발히 사용 (강한 압축 효과)" ratio
elif ratio >= 3.0 then
    printfn "Avg %.2f tool/msg in [3.0, 5.0) — chain pattern 부분 사용 (중간 압축 효과)" ratio
elif ratio >= 1.5 then
    printfn "Avg %.2f tool/msg in [1.5, 3.0) — chain pattern 약하게 사용 (약한 압축)" ratio
else
    printfn "Avg %.2f tool/msg < 1.5 — chain pattern 거의 미사용 (LLM 이 가이드 안 따름)" ratio

if multiPct >= 50.0 then
    printfn "Multi tool_use msg %.1f%% ≥ 50%% — 절반 이상 message 가 batch" multiPct
elif multiPct >= 25.0 then
    printfn "Multi tool_use msg %.1f%% in [25%%, 50%%) — 일부 message 만 batch" multiPct
else
    printfn "Multi tool_use msg %.1f%% < 25%% — 대부분 single tool_use" multiPct

if assignVarPct >= 30.0 then
    printfn "assignVar %.1f%% ≥ 30%% — variable binding chain 활용" assignVarPct
else
    printfn "assignVar %.1f%% < 30%% — variable binding 거의 미활용" assignVarPct
