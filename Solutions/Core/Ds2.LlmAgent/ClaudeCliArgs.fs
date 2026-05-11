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
    /// `--system-prompt-file` 본문. None 이면 인자 미전달 (default Claude Code system prompt 그대로 사용).
    /// 호출자가 임시 파일로 저장 후 path 만 buildWith 의 systemPromptFile 인자로 전달.
    /// round-trip 최적화 — `--append-system-prompt-file` (default 위 누적) 에서 `--system-prompt-file`
    /// (default 치환) 로 전환. CLI default system prompt 수십 KB 토큰 절감.
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

    /// 옵션 + sessionId + (선택) systemPromptFile path → CLI 인자 list.
    /// process spawn 이전 단계 검증 + golden test 용.
    ///
    /// **prompt 본문은 인자가 아닌 stdin 으로 전달** (Windows CreateProcess 32K 한도 회피, Ev2.Oracle
    /// claude_cli.py 패턴). 따라서 본 함수는 prompt 본문을 받지 않으며 `-p` 만 노출한다.
    /// **SystemPrompt 본문**도 동일 이유로 임시 파일에 저장 후 path 만 `--system-prompt-file` 로 전달.
    /// 호출자 (ClaudeCliProvider) 가 파일 생성 / 정리 책임을 갖고 path 만 본 함수에 넘긴다.
    /// `systemPromptFile` 가 None 이면 `--system-prompt-file` 미전달 — options.SystemPrompt 가
    /// Some 이어도 path 가 None 이면 미전달 (호출자가 의도적으로 비활성화한 케이스).
    /// commit-6b 추가 — `useStreamJsonInput` true 시 `--input-format stream-json` 추가.
    /// 첨부 (이미지/PDF) 가 있는 turn 만 stream-json input 으로 전환하고, 그 외 turn 은 기존 raw text stdin 경로 유지.
    let buildWith (options: ClaudeCliOptions) (sessionId: string option) (systemPromptFile: string option) (useStreamJsonInput: bool) : string list =
        [
            yield "-p"
            yield "--output-format"
            yield "stream-json"
            yield "--verbose"
            if useStreamJsonInput then
                yield "--input-format"
                yield "stream-json"
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
            // round-trip 최적화 — Claude Code 의 builtin tool (Task, Bash, Edit, Read, Glob, Grep, Write,
            // WebFetch, WebSearch, ToolSearch 등 29개) 을 model context 의 tool schema 에서 완전 제외.
            // Promaker chat 은 mcp__promaker__* 만 사용 → builtin 은 toolsList noise + 토큰 비용. `--tools ""`
            // 가 빈 화이트리스트로 builtin 전체 비활성화 (출처: code.claude.com/docs cli-reference).
            yield "--tools"
            yield ""
            // round-trip 최적화 — 사용자 ~/.claude 환경의 slash commands + skills 자동 등록을 차단.
            // Promaker chat 은 user 와의 자연어 chat → mcp tool 호출 흐름이라 slash command / skill 무관.
            // CLI default 는 init 메시지의 slash_commands(80+) / skills(50+) / agents(7) / plugins(4) 를
            // 모두 model system context 에 노출하여 cache_creation 토큰 30K+ 추가 발생 의심.
            yield "--disable-slash-commands"
            // round-trip 최적화 — `--settings '{"disableAllHooks":true}'` 가 OAuth 와 양립하면서
            // 사용자 ~/.claude 의 SessionStart hook (superpowers / mem0 등) 을 완전 차단. 별도 PoC
            // 검증 (`echo hi | claude -p --settings '{"disableAllHooks":true}'`): OAuth 인증 통과 +
            // hook_started/hook_response stream message 사라짐 + system prompt 42,755 → 40,486 토큰
            // (~5% 절감). RawStream 의 superpowers 'using-superpowers' skill `additionalContext` 주입도
            // 본 옵션으로 차단됨. F# ProcessStartInfo.ArgumentList.Add 가 JSON body 를 별개 argv 로
            // 전달하므로 quote escape 불필요.
            yield "--settings"
            yield """{"disableAllHooks":true}"""
            // **회귀 가드 — `--bare` / `--setting-sources ""` 를 다시 추가하지 말 것**.
            // 두 옵션 모두 user OAuth credentials 까지 차단되어 CLI 가 `error: authentication_failed`
            // ("Not logged in · Please run /login") 로 exit 1. credentials 가 ~/.claude/settings.json
            // 또는 그 source 와 결합되어 있음. hook 차단 목적은 위 `--settings` 가 안전하게 대체.
            // round-trip 최적화 — `--append-system-prompt-file` 은 CLI default system prompt (수십 KB)
            // 위에 우리 본문을 추가. `--system-prompt-file` 은 default 를 우리 본문으로 **완전 치환** →
            // CLI default prompt 토큰 절감. trade-off: 응답 형식/톤 가이드, working dir / git status / 사용자
            // CLAUDE.md 등 default 만 제공하던 정보는 손실. Promaker chat 은 mcp tool 호출 중심이라 영향
            // 적음 — 응답 형식 회귀 모니터링 필요.
            match systemPromptFile with
            | Some path ->
                yield "--system-prompt-file"
                yield path
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

    /// 첨부 없을 때 사용 — 기존 호출자 호환.
    let build (options: ClaudeCliOptions) (sessionId: string option) (systemPromptFile: string option) : string list =
        buildWith options sessionId systemPromptFile false

    let quoteArg (s: string) : string =
        if s.Contains(' ') || s.Contains('"') then
            "\"" + s.Replace("\"", "\\\"") + "\""
        else s

    /// 인자 list → 단일 commandline string (공백 구분, 공백/quote 포함 인자는 escape). 로그/검증 용도.
    let formatArgs (args: string list) : string =
        args |> List.map quoteArg |> String.concat " "
