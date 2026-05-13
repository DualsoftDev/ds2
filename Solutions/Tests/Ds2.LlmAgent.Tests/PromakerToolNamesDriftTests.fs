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
let ``현재 tool 풀세트 lock-in = 6종 set equality (doc-level 4 + read 2)`` () =
    let csNames = File.ReadAllText promakerToolNamesPath
    let listed = extractPromakerToolNames csNames
    // Phase 5 (op-layer 15종 일소) + Phase 6 (read 4종 일소 — list_projects / list_systems / describe_system /
    // describe_subtree 를 export_model_doc 의 path?/depth? 인자로 흡수, find_by_name 출력 path 격상,
    // validate_model 의 scope path 화). 남는 풀세트 = doc-level 4 (apply_model_doc / validate_model_doc /
    // export_model_doc / json_to_yaml) + read 2 (find_by_name / validate_model). yaml_to_json LLM 비노출 유지.
    //
    // Phase 6 closure #5 v4 격상 (count → set equality): 동일 size 라도 1종 swap (예: find_by_name 제거
    // 및 새 도구 1종 등장) 시 silent drift 회귀 차단. 신규 도구 추가 / 일소 시 본 expectedSet 도 sync.
    let expectedSet =
        Set.ofList [
            "apply_model_doc"; "validate_model_doc"; "export_model_doc"; "json_to_yaml"
            "find_by_name"; "validate_model"
        ]
    Assert.Equal<Set<string>>(expectedSet, listed)

/// `[McpServerTool, Description("...")]` 의 string literal 안에 Phase 5/6 cleanup 으로 일소된 op-layer +
/// read 어휘가 잔재하면 fail. 도구 이름 자체는 PromakerToolNames 와 Sync 가 위에서 검증되지만,
/// description 본문이 LLM 의 mental model 을 직접 형성하므로 stale 어휘가 회귀 fence 대상.
///
/// Phase 6 closure #5 v4 격상: 일소된 read-4종 (`list_projects` / `list_systems` / `describe_system` /
/// `describe_subtree`) 도 추가. drift 가 description 본문에 stale 도구 권유 어휘로 등장하지 않도록.
let private opLayerStaleTokens =
    [
        // Phase 5 — op-layer 15종
        "apply_operations"; "add_project"; "add_active_system"; "add_passive_system"
        "add_flow"; "add_work"; "add_call"; "add_api_def"; "add_arrow"
        "add_cylinder"; "add_clamp"; "add_robot"; "add_device"
        "remove_entity"; "rename_entity"
        // Phase 6 — read 4종 (export_model_doc path?/depth? 로 흡수)
        "list_projects"; "list_systems"; "describe_system"; "describe_subtree"
    ]

[<Fact>]
let ``[McpServerTool] description literal 에 op-layer 어휘 잔재 없음`` () =
    let csModel = File.ReadAllText modelToolsPath
    // [McpServerTool, Description("...")] 안 string literal 만 추출. multi-line / verbatim @"..." 변형 모두 cover.
    // group 1 = verbatim (@"...") / group 2 = normal ("...").
    let pattern = @"\[McpServerTool\b[\s\S]*?Description\(\s*(?:@""((?:[^""]|"""")*)""|""((?:\\.|[^""\\])*)"")"
    let matches = Regex.Matches(csModel, pattern)
    Assert.NotEmpty(matches |> Seq.cast<Match>)
    let mutable violations = []
    for m in matches |> Seq.cast<Match> do
        let body =
            if m.Groups.[1].Success then m.Groups.[1].Value.Replace("\"\"", "\"")
            else m.Groups.[2].Value
        for tok in opLayerStaleTokens do
            // word boundary 강제 — `apply_model_doc` 안 부분문자열 `apply_` 등 false positive 회피.
            if Regex.IsMatch(body, sprintf @"\b%s\b" (Regex.Escape tok)) then
                violations <- (tok, body.Substring(0, min 80 body.Length)) :: violations
    Assert.True(
        List.isEmpty violations,
        sprintf "op-layer 어휘 잔재 (Phase 5 cleanup 위반): %A" violations)

[<Fact>]
let ``snake_case 변환 단위 동작 — 풀세트 6종 + ApiDef 케이스`` () =
    Assert.Equal("apply_model_doc", toSnakeCase "ApplyModelDoc")
    Assert.Equal("validate_model_doc", toSnakeCase "ValidateModelDoc")
    Assert.Equal("export_model_doc", toSnakeCase "ExportModelDoc")
    Assert.Equal("json_to_yaml", toSnakeCase "JsonToYaml")
    Assert.Equal("find_by_name", toSnakeCase "FindByName")
    Assert.Equal("validate_model", toSnakeCase "ValidateModel")
    // ApiDef 케이스 검증 — Phase 6 의 path 깊이 ↔ EntityKind 매핑에서 ApiDef vs Flow ambiguity 해소 시 사용.
    Assert.Equal("add_api_def", toSnakeCase "AddApiDef")
