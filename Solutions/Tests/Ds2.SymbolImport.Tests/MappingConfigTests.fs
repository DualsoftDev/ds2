module Ds2.SymbolImport.Tests.MappingConfigTests

open System
open System.IO
open System.Text.Json
open Xunit
open Ds2.SymbolImport

module private ConfigPath =
    let rec findRepoRoot (dir: DirectoryInfo) : DirectoryInfo option =
        if isNull (box dir) then None
        else
            let configPath = Path.Combine(dir.FullName, "Solutions", "Convert", "Ds2.SymbolImport", "input-matching-config.json")
            if File.Exists configPath then Some dir
            else findRepoRoot dir.Parent

    let sourceConfig () =
        match findRepoRoot (DirectoryInfo(AppContext.BaseDirectory)) with
        | Some root -> Path.Combine(root.FullName, "Solutions", "Convert", "Ds2.SymbolImport", "input-matching-config.json")
        | None -> failwith "repo root with SymbolImport config not found"

[<Fact>]
let ``loadDefault — 실 input-matching-config.json 로드 성공 (Common + 4 vendor + SymmetryRules)`` () =
    let cfg = MappingConfig.loadDefault ()
    Assert.NotNull(cfg.Common)
    Assert.NotNull(cfg.Common.MappingSets)
    Assert.NotEmpty(cfg.Common.MappingSets)
    // 4 vendor: LS / AB / Mitsubishi / Siemens
    Assert.NotNull(cfg.Vendors)
    Assert.Equal(4, cfg.Vendors.Count)
    Assert.True(cfg.Vendors.ContainsKey "LS")
    Assert.True(cfg.Vendors.ContainsKey "AB")
    Assert.True(cfg.Vendors.ContainsKey "Mitsubishi")
    Assert.True(cfg.Vendors.ContainsKey "Siemens")
    Assert.NotEmpty(cfg.SymmetryRules)
    Assert.NotEmpty(cfg.ExplicitMappings)

[<Fact>]
let ``loadDefault — MappingSet 의 Device 키워드 + API 정의 로드`` () =
    let cfg = MappingConfig.loadDefault ()
    let firstSet = cfg.Common.MappingSets.[0]
    Assert.False(System.String.IsNullOrEmpty firstSet.Name)
    Assert.NotEmpty(firstSet.DeviceKeywords)
    Assert.NotEmpty(firstSet.Apis)
    let firstApi = firstSet.Apis.[0]
    Assert.False(System.String.IsNullOrEmpty firstApi.Name)

[<Fact>]
let ``loadFromString — 최소 JSON 로드`` () =
    let json = """
    {
      "Common": { "MappingSets": [
        { "Name": "test", "DeviceKeywords": ["*Foo*"],
          "Apis": [{ "Name": "ADV", "OutputKeywords": ["O"], "InputKeywords": ["I"] }],
          "OutputAddressPatterns": ["%Q"], "InputAddressPatterns": ["%I"] }
      ] },
      "Vendors": {},
      "SymmetryRules": [],
      "ExplicitMappings": [],
      "FilterExclusions": { "Description": "", "DeviceKeywords": [], "ApiKeywords": [], "FlowKeywords": [] },
      "FlowInclusions": { "Description": "", "Flows": [] },
      "ApiNaming": {},
      "WorkNaming": {},
      "NodeConnectionRules": {},
      "DeviceNaming": {},
      "DisplayNaming": {}
    }
    """
    let cfg = MappingConfig.loadFromString json
    Assert.Single(cfg.Common.MappingSets) |> ignore
    Assert.Equal("test", cfg.Common.MappingSets.[0].Name)
    Assert.Single(cfg.Common.MappingSets.[0].Apis) |> ignore
    Assert.Equal("ADV", cfg.Common.MappingSets.[0].Apis.[0].Name)

[<Fact>]
let ``loadFromFile — 존재하지 않는 path → FileNotFoundException`` () =
    Assert.Throws<System.IO.FileNotFoundException>(fun () ->
        MappingConfig.loadFromFile "C:/non-existent-path/no-such-config.json" |> ignore)

[<Fact>]
let ``loadDefault — FilterExclusions Description 비어있지 않음 (현장 데이터 확인)`` () =
    let cfg = MappingConfig.loadDefault ()
    Assert.NotNull(cfg.FilterExclusions)
    Assert.NotNull(cfg.FilterExclusions.DeviceKeywords)
    Assert.NotNull(cfg.FilterExclusions.ApiKeywords)

[<Fact>]
let ``loadDefault — UserTagRules 이상 태그 자동 생성 규칙 로드`` () =
    let cfg = MappingConfig.loadDefault ()
    Assert.NotNull(cfg.UserTagRules)
    Assert.True(cfg.UserTagRules.Enabled.HasValue && cfg.UserTagRules.Enabled.Value)
    Assert.NotEmpty(cfg.UserTagRules.Rules)
    let first = cfg.UserTagRules.Rules.[0]
    Assert.Equal("Error", first.LogLevel)
    Assert.Equal("Bit", first.ValueType)
    Assert.Contains("*ERR*", first.NamePatterns)

[<Fact>]
let ``source config — ExplicitMappings에는 패턴형 SymmetryRule 키가 섞이지 않음`` () =
    let json = File.ReadAllText(ConfigPath.sourceConfig ())
    use doc = JsonDocument.Parse(json)
    let explicitMappings = doc.RootElement.GetProperty("ExplicitMappings").EnumerateArray()

    let malformed =
        explicitMappings
        |> Seq.filter (fun item ->
            let mutable outputPattern = Unchecked.defaultof<JsonElement>
            let mutable inputPattern = Unchecked.defaultof<JsonElement>
            item.TryGetProperty("OutputPattern", &outputPattern)
            || item.TryGetProperty("InputPattern", &inputPattern))
        |> Seq.length

    Assert.Equal(0, malformed)
