namespace Ds2.SymbolImport

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

/// <summary>
/// dsev2 Ev2.Import.Plc/input-matching-config.json 차용 — System.Text.Json 로더.
/// JSON top-level: Common.MappingSets / Vendors / SymmetryRules / ExplicitMappings /
///                 FilterExclusions / FlowInclusions / ApiNaming / WorkNaming /
///                 NodeConnectionRules / DeviceNaming / DisplayNaming.
/// 향후 단계에서 MatchingUtil / InputMatching / DeviceGrouping 이 본 로더 결과를 소비.
/// </summary>
module MappingConfig =

    // ── DTO (JSON 1:1) ──────────────────────────────────────────────

    [<CLIMutable>]
    type ApiKeywordDto = {
        Name: string
        OutputKeywords: string array
        InputKeywords: string array
    }

    [<CLIMutable>]
    type MappingSetDto = {
        Name: string
        DeviceKeywords: string array
        Apis: ApiKeywordDto array
        OutputAddressPatterns: string array
        InputAddressPatterns: string array
    }

    [<CLIMutable>]
    type CommonDto = {
        MappingSets: MappingSetDto array
    }

    [<CLIMutable>]
    type VendorDto = {
        OutputAddressPatterns: string array
        InputAddressPatterns: string array
        MappingSets: MappingSetDto array
    }

    [<CLIMutable>]
    type SymmetryRuleDto = {
        OutputPattern: string
        InputPattern: string
        Description: string
    }

    [<CLIMutable>]
    type ExplicitMappingDto = {
        OutputKeyword: string
        InputKeyword: string
        Description: string
    }

    [<CLIMutable>]
    type FilterExclusionsDto = {
        Description: string
        DeviceKeywords: string array
        ApiKeywords: string array
        FlowKeywords: string array
    }

    [<CLIMutable>]
    type FlowInclusionsDto = {
        Description: string
        Flows: string array
    }

    [<CLIMutable>]
    type UserTagRuleDto = {
        Name: string
        Enabled: Nullable<bool>
        LogLevel: string
        ValueType: string
        MatchOp: string
        MatchValue: string
        Directions: string array
        AddressPatterns: string array
        NamePatterns: string array
        CommentPatterns: string array
        ExcludeAddressPatterns: string array
        ExcludeNamePatterns: string array
        ExcludeCommentPatterns: string array
    }

    [<CLIMutable>]
    type UserTagRulesDto = {
        Description: string
        Enabled: Nullable<bool>
        Rules: UserTagRuleDto array
    }

    [<CLIMutable>]
    type InputMatchingConfigDto = {
        Common: CommonDto
        Vendors: System.Collections.Generic.Dictionary<string, VendorDto>
        SymmetryRules: SymmetryRuleDto array
        ExplicitMappings: ExplicitMappingDto array
        FilterExclusions: FilterExclusionsDto
        FlowInclusions: FlowInclusionsDto
        UserTagRules: UserTagRulesDto
        // 나머지 (ApiNaming / WorkNaming / NodeConnectionRules / DeviceNaming / DisplayNaming)
        // 는 향후 단계에서 추가 — 현재는 일단 raw JsonElement 로 보존.
        ApiNaming: JsonElement
        WorkNaming: JsonElement
        NodeConnectionRules: JsonElement
        DeviceNaming: JsonElement
        DisplayNaming: JsonElement
    }

    // ── 로더 ────────────────────────────────────────────────────────

    let private jsonOptions =
        let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        opts.Converters.Add(JsonStringEnumConverter())
        opts

    /// JSON 파일 path 로부터 config 로드. 파일 없으면 raise.
    let loadFromFile (path: string) : InputMatchingConfigDto =
        if not (File.Exists path) then
            raise (FileNotFoundException(sprintf "input-matching-config.json not found at %s" path, path))
        let json = File.ReadAllText path
        JsonSerializer.Deserialize<InputMatchingConfigDto>(json, jsonOptions)

    /// 기본 로드 — AppContext.BaseDirectory 의 input-matching-config.json.
    /// Ds2.SymbolImport assembly 옆에 fsproj Content + CopyToOutputDirectory=PreserveNewest 로 배치됨.
    let loadDefault () : InputMatchingConfigDto =
        let path = Path.Combine(AppContext.BaseDirectory, "input-matching-config.json")
        loadFromFile path

    /// JSON 문자열에서 직접 로드 — 테스트용.
    let loadFromString (json: string) : InputMatchingConfigDto =
        JsonSerializer.Deserialize<InputMatchingConfigDto>(json, jsonOptions)
