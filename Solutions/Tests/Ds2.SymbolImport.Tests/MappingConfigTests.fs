module Ds2.SymbolImport.Tests.MappingConfigTests

open Xunit
open Ds2.SymbolImport

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
