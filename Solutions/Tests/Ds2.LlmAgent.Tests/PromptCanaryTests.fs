module PromptCanaryTests

open System.IO
open Xunit

/// extend-mcp §5.6 — prompt 문서 첫 줄 canary 회귀 방어 (todo §6.1 protocol).
///
/// canary 가 의도치 않게 제거되면 LLM 측 진단 trigger (`ping all`) 가 동작 불능.
/// release 시점에 canary 제거 결정되면 본 test 도 동시에 skip/제거 (todo §6.4 회수 절차).
///
/// 2026-06 (#192): 1/2/3.{entities,modeling,tooling} 은 `.mdx` 로 rename 되어 system prompt
/// 주입에서 제외됨 (Promaker.csproj 의 `LlmAgent\Prompts\*.md` glob 비매칭 — CLAUDE.md 폴더
/// 안내 참조). rename 시 canary 마커의 pong 식별자도 `.mdx` 로 함께 갱신되었으므로, 재주입
/// 대비 회귀 방어를 위해 `.mdx` 파일과 그 안의 `pong: Prompts/<stem>.mdx` 표기를 검사한다.

let private repoRoot = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..") |> Path.GetFullPath

let private promptsDir =
    Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "LlmAgent", "Prompts")

let private firstLine (file: string) =
    let path = Path.Combine(promptsDir, file)
    Assert.True(File.Exists path, sprintf "prompt file missing: %s" path)
    File.ReadLines(path) |> Seq.head

// 파일 확장자는 .mdx (커밋 5c4fa028 에서 .md → .mdx pure rename — 내용 무변경).
// 단 파일 *내부* canary 문자열의 pong 토큰은 rename 시 그대로 유지되어 'Prompts/<name>.md' 표기 — LLM 진단 trigger 규약 보존.
// 따라서 파일 열기는 .mdx, canary pong 문자열 검증은 파일 내용 그대로 (.md) 유지.

[<Fact>]
let ``1.entities.mdx 첫 줄에 canary pong 표기`` () =
    let line = firstLine "1.entities.mdx"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/1.entities.mdx", line)

[<Fact>]
let ``2.modeling.mdx 첫 줄에 canary pong 표기`` () =
    let line = firstLine "2.modeling.mdx"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/2.modeling.mdx", line)

[<Fact>]
let ``3.tooling.mdx 첫 줄에 canary pong 표기`` () =
    let line = firstLine "3.tooling.mdx"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/3.tooling.mdx", line)

// 4.attachments.md 는 아직 미생성 — attachments 처리 prompt 추가 시 canary 검증도 함께 복원.

[<Fact>]
let ``CLAUDE.md 첫 줄에 canary pong 표기`` () =
    let line = firstLine "CLAUDE.md"
    Assert.Contains("canary:", line)
    Assert.Contains("pong: Prompts/CLAUDE.md", line)
