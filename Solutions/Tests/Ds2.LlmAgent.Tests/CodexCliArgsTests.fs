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
let ``Default 는 --json --ephemeral --ignore-user-config 모두 노출`` () =
    let args = buildArgs baseOptions None "hi"
    Assert.Contains("--json", args)
    Assert.Contains("--ephemeral", args)
    Assert.Contains("--ignore-user-config", args)

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
let ``Ephemeral / IgnoreUserConfig false 시 각각 미전달 (사용자 영속 config 사용)`` () =
    let opts = { baseOptions with Ephemeral = false; IgnoreUserConfig = false }
    let args = buildArgs opts None "hi"
    Assert.DoesNotContain("--ephemeral", args)
    Assert.DoesNotContain("--ignore-user-config", args)
