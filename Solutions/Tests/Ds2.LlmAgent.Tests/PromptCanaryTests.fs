module PromptCanaryTests

open System.IO
open Xunit

/// extend-mcp §5.6 — prompt md 첫 줄 canary 회귀 방어 (todo §6.1 protocol).
///
/// canary 가 의도치 않게 제거되면 LLM 측 진단 trigger (`ping all`) 가 동작 불능.
/// release 시점에 canary 제거 결정되면 본 test 도 동시에 skip/제거 (todo §6.4 회수 절차).
///
/// 2026-06 (#192): 1/2/3.{entities,modeling,tooling} 은 `.mdx` 로 rename 되어 system prompt
/// 주입에서 제외됨 (Promaker.csproj 의 `LlmAgent\Prompts\*.md` glob 비매칭 — CLAUDE.md 폴더
/// 안내 참조). 단 canary 마커는 순수 rename 으로 파일 안에 그대로 보존되므로, 재주입 대비
/// 회귀 방어를 위해 `.mdx` 파일을 그대로 검사한다. 파일명은 `.mdx` 라도 canary 내부 pong
/// 식별자는 기존 `pong: Prompts/<stem>.md` 표기를 유지(파일 내용 불변).

let private repoRoot = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..") |> Path.GetFullPath

let private promptsDir =
    Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "LlmAgent", "Prompts")

let private firstLine (file: string) =
    let path = Path.Combine(promptsDir, file)
    Assert.True(File.Exists path, sprintf "prompt file missing: %s" path)
    File.ReadLines(path) |> Seq.head

[<Fact>]
let ``1.entities.md 첫 줄에 canary pong 표기`` () =
    let line = firstLine "1.entities.mdx"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/1.entities.md", line)

[<Fact>]
let ``2.modeling.md 첫 줄에 canary pong 표기`` () =
    let line = firstLine "2.modeling.mdx"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/2.modeling.md", line)

[<Fact>]
let ``3.tooling.md 첫 줄에 canary pong 표기`` () =
    let line = firstLine "3.tooling.mdx"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/3.tooling.md", line)

// 4.attachments.md 는 아직 미생성 — attachments 처리 prompt 추가 시 canary 검증도 함께 복원.

[<Fact>]
let ``CLAUDE.md 첫 줄에 canary pong 표기`` () =
    let line = firstLine "CLAUDE.md"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/CLAUDE.md", line)
