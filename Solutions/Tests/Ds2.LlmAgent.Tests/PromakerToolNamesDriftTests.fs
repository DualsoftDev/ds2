module PromakerToolNamesDriftTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit

/// 1d-6 후속 — tool allowlist drift 검출.
/// `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` 의 `[McpServerTool]` 메소드 (PascalCase) 와
/// `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` 의 `mcp__promaker__*` 화이트리스트 정합성을
/// 텍스트 파싱으로 검증. drift 시 LLM 측 호출이 조용히 차단되는 회귀를 build-time 에 잡음.
///
/// **fragile 주의**: file path 가 변경되거나 [McpServerTool] attribute 형식이 multiline split 되면
/// regex 가 깨질 수 있음. 실제 drift 가 아닌데 false positive 발생 시 본 test 의 regex 부터 점검.

let private repoRoot = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..") |> Path.GetFullPath

let private modelToolsPath =
    Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "LlmAgent", "Tools", "ModelTools.cs")

let private promakerToolNamesPath =
    Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "LlmAgent", "PromakerToolNames.cs")

/// PascalCase → snake_case. "AddSystem" → "add_system", "AddApiDef" → "add_api_def".
let private toSnakeCase (s: string) =
    let sb = System.Text.StringBuilder()
    for i in 0 .. s.Length - 1 do
        let c = s.[i]
        if i > 0 && Char.IsUpper(c) then sb.Append('_') |> ignore
        sb.Append(Char.ToLower(c)) |> ignore
    sb.ToString()

/// `[McpServerTool ...]` attribute 다음 첫 `public static Task<string> XxxYyy(` 메소드명 추출.
/// multi-line attribute 묶음 (`[McpServerTool, Description("...")]`) 도 매칭.
let private extractMcpServerToolMethods (cs: string) : string Set =
    let pattern = @"\[McpServerTool\b[\s\S]*?public\s+static\s+Task<\s*string\s*>\s+(\w+)\s*\("
    Regex.Matches(cs, pattern)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Set.ofSeq

/// `"mcp__promaker__<snake>"` literal 의 snake 부분 추출.
let private extractPromakerToolNames (cs: string) : string Set =
    let pattern = @"""mcp__promaker__(\w+)"""
    Regex.Matches(cs, pattern)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Set.ofSeq

[<Fact>]
let ``ModelTools.cs / PromakerToolNames.cs 둘 다 존재`` () =
    Assert.True(File.Exists modelToolsPath, sprintf "ModelTools.cs not found: %s" modelToolsPath)
    Assert.True(File.Exists promakerToolNamesPath, sprintf "PromakerToolNames.cs not found: %s" promakerToolNamesPath)

[<Fact>]
let ``[McpServerTool] 메소드와 PromakerToolNames.All 화이트리스트 정합성`` () =
    let csModel = File.ReadAllText modelToolsPath
    let csNames = File.ReadAllText promakerToolNamesPath

    let methodsSnake = extractMcpServerToolMethods csModel |> Set.map toSnakeCase
    let listed = extractPromakerToolNames csNames

    Assert.NotEmpty(methodsSnake)
    Assert.NotEmpty(listed)

    let missingFromList = Set.difference methodsSnake listed
    let staleInList = Set.difference listed methodsSnake

    Assert.True(
        Set.isEmpty missingFromList,
        sprintf "PromakerToolNames.All 누락: %A (ModelTools 에 [McpServerTool] 추가 후 화이트리스트 갱신 필요)" (Set.toList missingFromList))
    Assert.True(
        Set.isEmpty staleInList,
        sprintf "PromakerToolNames.All 잔재: %A (ModelTools 에서 메소드 제거 후 화이트리스트 정리 필요)" (Set.toList staleInList))

[<Fact>]
let ``현재 tool 풀세트 sanity = 15개 (phase 1d 11 + Phase 2 list_projects + remove_entity + rename_entity + Pass 5 add_project)`` () =
    let csNames = File.ReadAllText promakerToolNamesPath
    let listed = extractPromakerToolNames csNames
    // phase 후속 (Flow/Work/Call rename, arrow 단독 remove 등) 추가 시 본 expected 값 갱신 필요
    Assert.Equal(15, listed.Count)

[<Fact>]
let ``snake_case 변환 단위 동작 — Add/Describe/ApiDef 등 핵심 케이스`` () =
    Assert.Equal("add_system", toSnakeCase "AddSystem")
    Assert.Equal("add_api_def", toSnakeCase "AddApiDef")
    Assert.Equal("describe_subtree", toSnakeCase "DescribeSubtree")
    Assert.Equal("list_systems", toSnakeCase "ListSystems")
    Assert.Equal("find_by_name", toSnakeCase "FindByName")
    Assert.Equal("validate_model", toSnakeCase "ValidateModel")
