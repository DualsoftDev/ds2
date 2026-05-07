namespace Ds2.LlmAgent

open System
open System.Diagnostics

/// Claude CLI provider 옵션.
type ClaudeCliOptions = {
    /// `claude` 실행 파일 경로. None 이면 PATH 에서 탐색.
    ExecutablePath: string option
    /// `--mcp-config <path>` 인자.
    McpConfigPath: string option
    /// `--permission-mode` 값. None 이면 인자 미전달 (default).
    PermissionMode: string option
    /// `--model` 값. None 이면 인자 미전달.
    Model: string option
    /// `--append-system-prompt` 본문. None 이면 인자 미전달 (default Claude Code system prompt 그대로 사용).
    SystemPrompt: string option
    /// `--strict-mcp-config` — true 면 `--mcp-config` 외 server 자동 로드 차단 (settings.json 의
    /// 사용자 mcp-server 가 우리 turn 에 끼어들지 않도록). default false (CLI default 동작).
    StrictMcpConfig: bool
    /// `--allowed-tools` 화이트리스트. None 또는 빈 array 면 미전달 (전체 허용). 형식 = `mcp__<server>__<tool>`.
    /// 반복 인자로 전달 (`--allowed-tools T1 --allowed-tools T2 ...`) — 공백 escape / 구분자 변경에 무관하고
    /// CLI 측 호환성 ↑ (단일 인자 + 공백 구분 / 콤마 구분 모두 회피).
    AllowedTools: string array option
    /// stream backpressure 채널 capacity. 기본 256.
    ChannelCapacity: int
    /// Process spawn 직후 호출 callback (1d-5 — Job Object attach 등 lifecycle hook).
    /// None 이면 미호출. 예외 발생 시 fail-fast (CLAUDE.md 예외 정책).
    OnProcessStarted: Action<Process> option
} with
    static member Default = {
        ExecutablePath = None
        McpConfigPath = None
        PermissionMode = None
        Model = None
        SystemPrompt = None
        StrictMcpConfig = false
        AllowedTools = None
        ChannelCapacity = 256
        OnProcessStarted = None
    }

/// `claude` CLI 인자 list 빌더. ClaudeCliProvider 외부에서 단위 검증 가능하도록 module-level 노출.
[<RequireQualifiedAccess>]
module ClaudeCliArgs =

    /// 옵션 + sessionId + prompt → CLI 인자 list. process spawn 이전 단계 검증 + golden test 용.
    let build (options: ClaudeCliOptions) (sessionId: string option) (prompt: string) : string list =
        [
            yield "-p"
            yield prompt
            yield "--output-format"
            yield "stream-json"
            yield "--verbose"
            match sessionId with
            | Some sid ->
                yield "--resume"
                yield sid
            | None -> ()
            match options.McpConfigPath with
            | Some path ->
                yield "--mcp-config"
                yield path
            | None -> ()
            match options.PermissionMode with
            | Some mode ->
                yield "--permission-mode"
                yield mode
            | None -> ()
            match options.Model with
            | Some m ->
                yield "--model"
                yield m
            | None -> ()
            match options.SystemPrompt with
            | Some sp ->
                yield "--append-system-prompt"
                yield sp
            | None -> ()
            if options.StrictMcpConfig then
                yield "--strict-mcp-config"
            match options.AllowedTools with
            | Some tools when tools.Length > 0 ->
                for t in tools do
                    yield "--allowed-tools"
                    yield t
            | _ -> ()
        ]

    let quoteArg (s: string) : string =
        if s.Contains(' ') || s.Contains('"') then
            "\"" + s.Replace("\"", "\\\"") + "\""
        else s

    /// 인자 list → 단일 commandline string (공백 구분, 공백/quote 포함 인자는 escape). 로그/검증 용도.
    let formatArgs (args: string list) : string =
        args |> List.map quoteArg |> String.concat " "
