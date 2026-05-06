module ClaudeCliArgsTests

open Xunit
open Ds2.LlmAgent

/// 1d-6 — 1d-4 B 의 fsx 검증을 영구 회귀로 이전. `--strict-mcp-config` / `--allowed-tools` 반복 인자
/// 형식이 후속 변경 (e.g. CLI 측이 콤마 구분으로 바뀜) 시 즉시 실패하도록.

let private baseOptions = ClaudeCliOptions.Default

let private buildArgs (options: ClaudeCliOptions) (sessionId: string option) (prompt: string) : string list =
    ClaudeCliArgs.build options sessionId prompt

[<Fact>]
let ``Default options 는 strict-mcp-config / allowed-tools 인자 미노출`` () =
    let args = buildArgs baseOptions None "hi"
    Assert.DoesNotContain("--strict-mcp-config", args)
    Assert.DoesNotContain("--allowed-tools", args)

[<Fact>]
let ``StrictMcpConfig=true 시 --strict-mcp-config 단일 토큰 노출`` () =
    let opts = { baseOptions with StrictMcpConfig = true }
    let args = buildArgs opts None "hi"
    Assert.Contains("--strict-mcp-config", args)

[<Fact>]
let ``AllowedTools 빈 array 면 --allowed-tools 미전달`` () =
    let opts = { baseOptions with AllowedTools = Some [||] }
    let args = buildArgs opts None "hi"
    Assert.DoesNotContain("--allowed-tools", args)

[<Fact>]
let ``AllowedTools 11개 시 --allowed-tools 가 11회 반복 (반복 인자 형식)`` () =
    let tools = [| for i in 1..11 -> sprintf "mcp__promaker__t%d" i |]
    let opts = { baseOptions with AllowedTools = Some tools }
    let args = buildArgs opts None "hi"
    let count = args |> List.filter ((=) "--allowed-tools") |> List.length
    Assert.Equal(11, count)
    // 각 tool 이름이 직접 인자로 따라 오는지 확인
    for t in tools do
        Assert.Contains(t, args)

[<Fact>]
let ``AllowedTools 단일 인자/공백 구분 형식이 아닌 반복 인자 형식 보장`` () =
    let opts = { baseOptions with AllowedTools = Some [| "mcp__promaker__a"; "mcp__promaker__b" |] }
    let args = buildArgs opts None "hi"
    // 이전 인자 형식이 'A B' 단일 토큰이거나 'A,B' 콤마구분이면 회귀 실패
    Assert.DoesNotContain("mcp__promaker__a mcp__promaker__b", args)
    Assert.DoesNotContain("mcp__promaker__a,mcp__promaker__b", args)

[<Fact>]
let ``--resume 가 sessionId 직후 토큰으로 등장`` () =
    let args = buildArgs baseOptions (Some "abc-123") "hi"
    let idx = args |> List.findIndex ((=) "--resume")
    Assert.Equal("abc-123", args.[idx + 1])

[<Fact>]
let ``-p prompt --output-format stream-json --verbose 기본 인자 항상 노출`` () =
    let args = buildArgs baseOptions None "hello world"
    Assert.Contains("-p", args)
    Assert.Contains("hello world", args)
    Assert.Contains("--output-format", args)
    Assert.Contains("stream-json", args)
    Assert.Contains("--verbose", args)

[<Fact>]
let ``McpConfigPath / SystemPrompt / PermissionMode / Model Some 시 인자 노출, None 시 미노출`` () =
    let full = {
        baseOptions with
            McpConfigPath = Some "/tmp/mcp.json"
            SystemPrompt = Some "be terse"
            PermissionMode = Some "bypassPermissions"
            Model = Some "claude-sonnet"
    }
    let args = buildArgs full None "hi"
    Assert.Contains("--mcp-config", args)
    Assert.Contains("/tmp/mcp.json", args)
    Assert.Contains("--append-system-prompt", args)
    Assert.Contains("be terse", args)
    Assert.Contains("--permission-mode", args)
    Assert.Contains("bypassPermissions", args)
    Assert.Contains("--model", args)
    Assert.Contains("claude-sonnet", args)

    let none = buildArgs baseOptions None "hi"
    Assert.DoesNotContain("--mcp-config", none)
    Assert.DoesNotContain("--append-system-prompt", none)
    Assert.DoesNotContain("--permission-mode", none)
    Assert.DoesNotContain("--model", none)
