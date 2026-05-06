module CodexCliArgsTests

open Xunit
open Ds2.LlmAgent

/// Phase 2 C-1 — `codex exec` CLI 인자 build 영구 회귀.
/// 사전 실증 4 결과 정리: subcommand 형식 (resume), `--json` (Claude 의 stream-json 역할),
/// `--ephemeral` / `--ignore-user-config` 격리, `-c key=value` 반복 config override.

let private baseOptions = CodexCliOptions.Default

let private buildArgs (options: CodexCliOptions) (sessionId: string option) (prompt: string) : string list =
    CodexCliArgs.build options sessionId prompt

[<Fact>]
let ``Default options 의 첫 토큰은 exec subcommand`` () =
    let args = buildArgs baseOptions None "hi"
    Assert.Equal("exec", args.[0])

[<Fact>]
let ``Default 는 --json --ignore-user-config 노출, --ephemeral 미노출 (multi-turn resume 보장)`` () =
    // Ephemeral default 는 false — 그렇지 않으면 두 번째 turn 의 `exec resume <sid>` 가 rollout 부재로 fail.
    // 단일 turn 사용 케이스에서만 명시적으로 true 지정 (todo 후속 session cleanup 정책 참조).
    let args = buildArgs baseOptions None "hi"
    Assert.Contains("--json", args)
    Assert.Contains("--ignore-user-config", args)
    Assert.DoesNotContain("--ephemeral", args)

[<Fact>]
let ``Default 는 prompt 가 마지막 토큰`` () =
    let args = buildArgs baseOptions None "hello world"
    Assert.Equal("hello world", List.last args)

[<Fact>]
let ``sessionId Some 시 exec 다음 resume subcommand 등장`` () =
    let args = buildArgs baseOptions (Some "abc-123") "hi"
    Assert.Equal("exec", args.[0])
    Assert.Equal("resume", args.[1])

[<Fact>]
let ``sessionId 는 prompt 직전 위치 인자`` () =
    let args = buildArgs baseOptions (Some "abc-123") "hi"
    let lastIdx = args.Length - 1
    Assert.Equal("hi", args.[lastIdx])
    Assert.Equal("abc-123", args.[lastIdx - 1])

[<Fact>]
let ``sessionId None 시 resume subcommand 미노출`` () =
    let args = buildArgs baseOptions None "hi"
    Assert.DoesNotContain("resume", args)

[<Fact>]
let ``Cd / Model Some 시 -C / -m 인자 노출, None 시 미노출`` () =
    let opts = { baseOptions with Cd = Some "C:/work"; Model = Some "o3" }
    let args = buildArgs opts None "hi"
    Assert.Contains("-C", args)
    Assert.Contains("C:/work", args)
    Assert.Contains("-m", args)
    Assert.Contains("o3", args)
    let none = buildArgs baseOptions None "hi"
    Assert.DoesNotContain("-C", none)
    Assert.DoesNotContain("-m", none)

[<Fact>]
let ``ConfigOverrides 는 -c key=value 반복 인자 (Claude 의 --allowed-tools 와 같은 패턴)`` () =
    let pairs =
        [|
            "mcp_servers.promaker.url", "\"http://127.0.0.1:5777/\""
            "instructions", "\"be terse\""
        |]
    let opts = { baseOptions with ConfigOverrides = Some pairs }
    let args = buildArgs opts None "hi"
    let cCount = args |> List.filter ((=) "-c") |> List.length
    Assert.Equal(2, cCount)
    Assert.Contains("mcp_servers.promaker.url=\"http://127.0.0.1:5777/\"", args)
    Assert.Contains("instructions=\"be terse\"", args)

[<Fact>]
let ``ConfigOverrides 빈 array 면 -c 인자 미전달`` () =
    let opts = { baseOptions with ConfigOverrides = Some [||] }
    let args = buildArgs opts None "hi"
    Assert.DoesNotContain("-c", args)

[<Fact>]
let ``FullAuto / DangerouslyBypass / SkipGitRepoCheck false (default) 시 미노출`` () =
    let args = buildArgs baseOptions None "hi"
    Assert.DoesNotContain("--full-auto", args)
    Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args)
    Assert.DoesNotContain("--skip-git-repo-check", args)

[<Fact>]
let ``DangerouslyBypass true 시 long flag 정확히 노출`` () =
    let opts = { baseOptions with DangerouslyBypassApprovalsAndSandbox = true }
    let args = buildArgs opts None "hi"
    Assert.Contains("--dangerously-bypass-approvals-and-sandbox", args)

[<Fact>]
let ``Json false 시 --json 미전달 (raw text 출력 모드)`` () =
    let opts = { baseOptions with Json = false }
    let args = buildArgs opts None "hi"
    Assert.DoesNotContain("--json", args)

[<Fact>]
let ``Ephemeral true override 시 --ephemeral 노출, IgnoreUserConfig false 시 --ignore-user-config 미전달`` () =
    let opts = { baseOptions with Ephemeral = true; IgnoreUserConfig = false }
    let args = buildArgs opts None "hi"
    Assert.Contains("--ephemeral", args)
    Assert.DoesNotContain("--ignore-user-config", args)

[<Fact>]
let ``approval_policy 는 -c config override 로만 전달 (--ask-for-approval flag 미사용)`` () =
    // codex 0.125 의 codex exec 는 `--ask-for-approval` flag 를 거부 (clap parse error, exit code 2).
    // approval_policy 는 ConfigOverrides 의 `-c approval_policy="never"` path 로만 전달해야 함.
    let args = buildArgs baseOptions None "hi"
    Assert.DoesNotContain("--ask-for-approval", args)

[<Fact>]
let ``ExperimentalInstructionsFile None default 면 -c experimental_instructions_file= 미전달`` () =
    let args = buildArgs baseOptions None "hi"
    Assert.False(args |> List.exists (fun a -> a.StartsWith("experimental_instructions_file=")))

[<Fact>]
let ``resume 시 Cd Some 이어도 -C 미전달 (codex exec resume 가 -C 미지원)`` () =
    // codex 0.125 의 `codex exec resume` subcommand 는 `-C / --cd` 옵션 받지 않음 (Hot-fix-6 회귀).
    // 첫 turn 에서 cwd 가 thread rollout 에 기록되어 resume 시 그대로 이어진다는 가정.
    let opts = { baseOptions with Cd = Some "C:/work" }
    let args = buildArgs opts (Some "abc-123") "hi"
    Assert.DoesNotContain("-C", args)
    Assert.DoesNotContain("C:/work", args)

[<Fact>]
let ``resume 시 FullAuto true 이어도 --full-auto 미전달`` () =
    // `--full-auto` 도 `codex exec` 만 지원. resume 에 yield 시 clap parsing fail (exit code 2).
    let opts = { baseOptions with FullAuto = true }
    let args = buildArgs opts (Some "abc-123") "hi"
    Assert.DoesNotContain("--full-auto", args)

[<Fact>]
let ``ExperimentalInstructionsFile Some 시 -c experimental_instructions_file='<path>' 형식 (toml literal string)`` () =
    let path = @"C:\Users\dualk\AppData\Local\Temp\Promaker\codex-instructions-abc.md"
    let opts = { baseOptions with ExperimentalInstructionsFile = Some path }
    let args = buildArgs opts None "hi"
    let cIdx = args |> List.findIndex (fun a -> a.StartsWith("experimental_instructions_file="))
    Assert.Equal("-c", args.[cIdx - 1])
    let value = args.[cIdx]
    // toml literal string '...' — Windows path 의 backslash 가 raw 로 들어감 (escape 없이).
    Assert.Equal(sprintf "experimental_instructions_file='%s'" path, value)
