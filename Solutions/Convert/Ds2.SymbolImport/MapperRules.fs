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
    /// extraOut/extraIn 패턴은 호출자(=vendor 경로) 가 vendor-level OutputAddressPatterns 등을 inject 할 때만 채움.
    let toMappingSetWithVendorPatterns (extraOut: string array) (extraIn: string array) (dto: MappingConfig.MappingSetDto) : MappingSet =
        let apisMap =
            (if isNull dto.Apis then [||] else dto.Apis)
            |> Array.map (fun api -> api.Name, toApiKeywordMapping api)
            |> Map.ofArray
        let setOut = if isNull dto.OutputAddressPatterns then [||] else dto.OutputAddressPatterns
        let setIn  = if isNull dto.InputAddressPatterns  then [||] else dto.InputAddressPatterns
        let extraOut = if isNull extraOut then [||] else extraOut
        let extraIn  = if isNull extraIn  then [||] else extraIn
        // vendor-level patterns 가 set 자체에 없을 때만 추가 (중복 제거).
        let mergedOut = Array.append setOut extraOut |> Array.distinct
        let mergedIn  = Array.append setIn  extraIn  |> Array.distinct
        { Name = dto.Name
          DeviceKeywords = List.ofArray (if isNull dto.DeviceKeywords then [||] else dto.DeviceKeywords)
          Apis = apisMap
          OutputAddressPatterns = List.ofArray mergedOut
          InputAddressPatterns  = List.ofArray mergedIn }

    /// vendor-level patterns 없이 단순 변환 (Common.MappingSets 용).
    let toMappingSet (dto: MappingConfig.MappingSetDto) : MappingSet =
        toMappingSetWithVendorPatterns [||] [||] dto

    /// Vendor DU → input-matching-config.json 의 vendor key 매핑.
    let vendorConfigKey (vendor: Vendor) : string =
        match vendor with
        | Mitsubishi -> "Mitsubishi"
        | XG5000
        | XGB
        | XGK        -> "LS"
        | AB         -> "AB"

    /// MappingConfig 의 Common.MappingSets + 선택된 vendor 의 MappingSets (vendor-level OutputAddressPatterns 주입).
    /// 매칭 엔진은 vendor 별 디바이스 키워드/Api 키워드/Y·X 주소 prefix 를 mappingSets 안에서 찾기 때문에,
    /// vendor 의 MappingSets 와 그 vendor 가 제공하는 패턴을 합쳐 넘겨야 vendor-specific 매칭이 동작한다.
    let mappingSetsFromConfig (vendor: Vendor) (config: MappingConfig.InputMatchingConfigDto) : MappingSet list =
        let common =
            if isNull (box config.Common) || isNull config.Common.MappingSets then [||]
            else config.Common.MappingSets
        let commonSets = common |> Array.map toMappingSet |> List.ofArray
        let vendorKey = vendorConfigKey vendor
        let vendorSets =
            match config.Vendors with
            | null -> []
            | vendors ->
                let mutable vdto = Unchecked.defaultof<MappingConfig.VendorDto>
                if vendors.TryGetValue(vendorKey, &vdto) && not (isNull (box vdto)) then
                    let sets = if isNull vdto.MappingSets then [||] else vdto.MappingSets
                    sets
                    |> Array.map (toMappingSetWithVendorPatterns vdto.OutputAddressPatterns vdto.InputAddressPatterns)
                    |> List.ofArray
                else
                    []
        commonSets @ vendorSets
