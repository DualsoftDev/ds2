namespace Ds2.LlmAgent

/// Codex CLI provider 옵션 (skeleton — Phase 2 C-1).
///
/// Claude CLI 와의 핵심 차이 (사전 실증 4 결과):
/// - `--mcp-config <path>` 임시 인자 부재 → `CODEX_HOME` 환경변수 격리 또는 `-c mcp_servers.<name>.url=...` inline override
/// - tool 단위 `--allowed-tools` 부재 → sandbox policy + system prompt 가이드로 대체
/// - session resume = `codex exec resume <sid>` subcommand (인자 형식 ≠ Claude 의 `--resume <sid>` flag)
/// - stream 형식 = `--json` (JSONL, packet 종류는 실증 4 의 C-2 spike 시점에 확정)
///
/// **본 record 는 phase 2 의 점진 구축 단계** — 실제 process spawn / event loop 은 CodexCliProvider.fs (phase 2 후속).
type CodexCliOptions = {
    /// `codex` 실행 파일 경로. None 이면 PATH 에서 탐색.
    ExecutablePath: string option
    /// `-C <dir>` working directory. None 이면 현재 cwd.
    Cd: string option
    /// `-m <model>`. None 이면 인자 미전달 (codex default 모델).
    Model: string option
    /// `--json` JSONL 출력 (= Claude 의 stream-json 역할). default true (provider 가 stream 파싱).
    Json: bool
    /// `--ephemeral` session 영속 X. **default false** — `--ephemeral` 은 thread rollout (= disk save) 안 해서
    /// 다음 turn 의 `exec resume <sid>` 가 "no rollout found" 로 fail. multi-turn 보장 위해 default off.
    /// 단일 turn 사용 (resume 미사용) 케이스에서만 true 명시.
    Ephemeral: bool
    /// `--ignore-user-config` 사용자 ~/.codex/config.toml 무시. default true (인스턴스별 격리).
    IgnoreUserConfig: bool
    /// `--skip-git-repo-check` git repo 외부 실행 허용. default false.
    SkipGitRepoCheck: bool
    /// `--full-auto` low-friction sandbox. default false.
    FullAuto: bool
    /// `--dangerously-bypass-approvals-and-sandbox`. default false. **위험** — full-auto 와 mutually exclusive.
    DangerouslyBypassApprovalsAndSandbox: bool
    /// `-c <key=value>` config override 반복 인자. e.g. `("mcp_servers.promaker.url", "\"http://127.0.0.1:5777/\"")`
    /// MCP HTTP transport 등록은 본 path 로 inline (CODEX_HOME 격리 vs `-c` 중 어느 게 정석인지는 C-2 spike 결과)
    ConfigOverrides: (string * string) array option
    /// `-c experimental_instructions_file=<path>` 으로 codex 의 default system prompt 를 완전 override.
    /// codex docs 의 정식 키 — file path 만 전달, codex 가 그 파일을 읽어 system prompt 로 사용. None 이면
    /// codex default coding agent prompt. build 시 toml literal string `'...'` 으로 인코딩 (Windows backslash
    /// escape 회피, path 안 `'` 미지원). 호출자가 임시 파일 lifecycle 책임.
    ExperimentalInstructionsFile: string option
    /// `CODEX_HOME` 환경변수 — codex 가 sessions / config / log 를 둘 디렉토리. None 이면 codex default
    /// (`~/.codex/`). 인스턴스별 임시 디렉토리로 두면 thread rollout 이 사용자 home 에 누적되지 않고
    /// 워크스페이스와 함께 cleanup. CodexCliArgs 는 인자만 다루므로 build 결과에는 영향 X — Provider
    /// 가 process spawn 시 환경변수로 설정.
    CodexHome: string option
    /// stream backpressure 채널 capacity. ClaudeCli 와 동일 default 256.
    ChannelCapacity: int
} with
    static member Default = {
        ExecutablePath = None
        Cd = None
        Model = None
        Json = true
        Ephemeral = false
        IgnoreUserConfig = true
        SkipGitRepoCheck = false
        FullAuto = false
        DangerouslyBypassApprovalsAndSandbox = false
        ConfigOverrides = None
        ExperimentalInstructionsFile = None
        CodexHome = None
        ChannelCapacity = 256
    }

/// `codex` CLI 인자 list 빌더. CodexCliProvider 외부에서 단위 검증 가능하도록 module-level 노출.
/// Claude 와 인자 형식 (subcommand vs flag) 이 다르므로 ClaudeCliArgs 와 별개 module.
[<RequireQualifiedAccess>]
module CodexCliArgs =

    /// 옵션 + sessionId + prompt → CLI 인자 list.
    /// new session: `codex exec [OPTS] <prompt>`
    /// resume:      `codex exec resume [OPTS] <sid> <prompt>`
    let build (options: CodexCliOptions) (sessionId: string option) (prompt: string) : string list =
        [
            yield "exec"
            match sessionId with
            | Some _ -> yield "resume"
            | None -> ()
            // ─── 공통 boolean flags ──────────────────────────────────────────
            if options.Json then yield "--json"
            if options.Ephemeral then yield "--ephemeral"
            if options.IgnoreUserConfig then yield "--ignore-user-config"
            if options.SkipGitRepoCheck then yield "--skip-git-repo-check"
            // `--full-auto` 는 `codex exec` 만 지원 — `codex exec resume` 은 unknown option (exit code 2).
            // 첫 turn 의 thread context 가 resume 시 이어지므로 resume 에 다시 적용 불필요.
            if options.FullAuto && sessionId.IsNone then yield "--full-auto"
            if options.DangerouslyBypassApprovalsAndSandbox then
                yield "--dangerously-bypass-approvals-and-sandbox"
            // ─── value flags ─────────────────────────────────────────────────
            // `-C / --cd` 도 `codex exec` 만 지원 (codex 0.125 `exec resume --help` 확인).
            // resume 시 codex 가 thread rollout 의 첫 turn cwd 를 이어받음 — 격리 효과 유지 가정.
            match options.Cd, sessionId with
            | Some d, None ->
                yield "-C"
                yield d
            | _ -> ()
            match options.Model with
            | Some m ->
                yield "-m"
                yield m
            | None -> ()
            match options.ConfigOverrides with
            | Some pairs ->
                for (k, v) in pairs do
                    yield "-c"
                    yield $"{k}={v}"
            | None -> ()
            match options.ExperimentalInstructionsFile with
            | Some path ->
                yield "-c"
                // toml literal string `'...'` — backslash 등 escape 무시 (Windows path `C:\...` 안전).
                // path 안에 `'` 가 없는 한 안전 (Windows file 명에 `'` 거의 없음). path 자체가 사용자 정의가 아닌
                // 호출자 (LlmChatViewModel) 가 만든 임시 파일이라 통제 가능.
                yield $"experimental_instructions_file='{path}'"
            | None -> ()
            // ─── 위치 인자 ───────────────────────────────────────────────────
            match sessionId with
            | Some sid -> yield sid
            | None -> ()
            // **INVARIANT**: prompt 는 항상 args 의 마지막 토큰. CodexCliProvider 의 redact 로직이
            // `if i = args.Length - 1 then redact` 로 본 invariant 에 의존 (Claude 의 `--prompt <msg>` flag-pair
            // 구조와 달리 Codex 는 위치 인자라 invariant 가 인자 자체에 박혀있지 않음). 본 줄 위치 변경 시
            // CodexCliProvider 의 redact 로직도 함께 갱신 필요.
            yield prompt
        ]
