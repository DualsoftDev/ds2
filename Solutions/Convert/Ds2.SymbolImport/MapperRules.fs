namespace Ds2.SymbolImport

open Ds2.SymbolImport.Matching

/// <summary>SymbolEntry ↔ dsev2 Variable / MappingSet 어댑터.
/// dsev2 의 InputMatching/DeviceGrouping 을 ds2 측 type 으로 연결한다.</summary>
module MapperRules =

    /// SymbolEntry.Direction → dsev2 IODirection.
    /// Memory/UnknownDir 은 *Input* 으로 처리 (dsev2 가 2-state — Output/Input 만).
    /// case 명 (Output/Input) 이 양쪽 DU 에 모두 있어 fully qualified 필요.
    let symbolDirectionToIO (dir: SymbolDirection) : IODirection =
        match dir with
        | SymbolDirection.Output -> IODirection.Output
        | SymbolDirection.Input
        | SymbolDirection.Memory
        | SymbolDirection.UnknownDir -> IODirection.Input

    /// SymbolEntry → dsev2 Variable.
    let toVariable (entry: SymbolEntry) : Variable =
        { LogicalName = entry.Name
          PhysicalAddress = entry.Address
          Direction = symbolDirectionToIO entry.Direction }

    /// MappingConfig.ApiKeywordDto → dsev2 ApiKeywordMapping.
    let private toApiKeywordMapping (api: MappingConfig.ApiKeywordDto) : ApiKeywordMapping =
        { OutputKeywords = List.ofArray (if isNull api.OutputKeywords then [||] else api.OutputKeywords)
          InputKeywords  = List.ofArray (if isNull api.InputKeywords  then [||] else api.InputKeywords) }

    /// MappingConfig.MappingSetDto → dsev2 MappingSet.
    let toMappingSet (dto: MappingConfig.MappingSetDto) : MappingSet =
        let apisMap =
            (if isNull dto.Apis then [||] else dto.Apis)
            |> Array.map (fun api -> api.Name, toApiKeywordMapping api)
            |> Map.ofArray
        { Name = dto.Name
          DeviceKeywords = List.ofArray (if isNull dto.DeviceKeywords then [||] else dto.DeviceKeywords)
          Apis = apisMap
          OutputAddressPatterns = List.ofArray (if isNull dto.OutputAddressPatterns then [||] else dto.OutputAddressPatterns)
          InputAddressPatterns  = List.ofArray (if isNull dto.InputAddressPatterns  then [||] else dto.InputAddressPatterns) }

    /// MappingConfig 의 Common.MappingSets 전체 변환.
    let mappingSetsFromConfig (config: MappingConfig.InputMatchingConfigDto) : MappingSet list =
        let common =
            if isNull (box config.Common) || isNull config.Common.MappingSets then [||]
            else config.Common.MappingSets
        common |> Array.map toMappingSet |> List.ofArray
