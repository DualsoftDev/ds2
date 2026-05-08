module PromptCanaryTests

open System.IO
open Xunit

/// extend-mcp §5.6 신규 3 — 4 prompt md 첫 줄 canary 회귀 방어 (todo §6.1 protocol).
///
/// canary 가 의도치 않게 제거되면 LLM 측 진단 trigger (`ping all`) 가 동작 불능.
/// release 시점에 canary 제거 결정되면 본 test 도 동시에 skip/제거 (todo §6.4 회수 절차).

let private repoRoot = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..") |> Path.GetFullPath

let private promptsDir =
    Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "LlmAgent", "Prompts")

let private firstLine (file: string) =
    let path = Path.Combine(promptsDir, file)
    Assert.True(File.Exists path, sprintf "prompt file missing: %s" path)
    File.ReadLines(path) |> Seq.head

[<Fact>]
let ``1.entities.md 첫 줄에 canary pong 표기`` () =
    let line = firstLine "1.entities.md"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/1.entities.md", line)

[<Fact>]
let ``2.modeling.md 첫 줄에 canary pong 표기`` () =
    let line = firstLine "2.modeling.md"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/2.modeling.md", line)

[<Fact>]
let ``3.tooling.md 첫 줄에 canary pong 표기`` () =
    let line = firstLine "3.tooling.md"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/3.tooling.md", line)

[<Fact>]
let ``CLAUDE.md 첫 줄에 canary pong 표기`` () =
    let line = firstLine "CLAUDE.md"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/CLAUDE.md", line)
