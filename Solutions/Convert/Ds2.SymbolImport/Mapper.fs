namespace Ds2.SymbolImport

open Ds2.SymbolImport.Matching

/// <summary>SymbolEntry list → dsev2 InputMatching/DeviceGrouping → ds2 PairedMapping list.
/// 매칭 룰은 input-matching-config.json (dsev2 차용, 2,874줄 현장 자산) 기반.
/// 출력 = (FlowName, WorkName, DeviceName, ApiName, OutputEntry, InputEntries[]).</summary>
module Mapper =

    /// 출력 SymbolEntry 1개 + 입력 SymbolEntry N개 페어 (dsev2 DeviceApiMapping 동등).
    /// 한 ApiDef 에 OutTag (Output) + InTag (첫 Input) 매핑이 이 단위에서 결정됨.
    type Mapping = {
        OutputEntry: SymbolEntry option
        InputEntries: SymbolEntry list
        FlowName: string
        WorkName: string
        DeviceName: string
        ApiName: string
        Strategy: MatchingStrategy
        Confidence: float
    }

    type MappingBatch = {
        Mapped: Mapping list
        /// 어떤 페어에도 들어가지 못한 SymbolEntry.
        Unmatched: SymbolEntry list
    }

    /// dsev2 DeviceApiMapping → ds2 Mapping.
    /// (LogicalName, PhysicalAddress) 쌍으로 lookup — 같은 Name 의 actuator/sensor 페어
    /// (예: X1240 / Y1250 모두 "Laser___ Stopper Up") 가 collision 으로 OutputEntry 가
    /// InputEntries 에도 들어가는 버그 회피.
    let private fromDsev2Mapping (entryByKey: Map<string * string, SymbolEntry>) (dam: DeviceApiMapping) : Mapping =
        let outputEntry =
            entryByKey |> Map.tryFind (dam.OutputVariableName, dam.OutputPhysicalAddress)
        let inputEntries =
            // dam.InputVariableNames / InputPhysicalAddresses 는 같은 길이의 parallel list.
            // List.zip 으로 (Name, Address) 쌍을 만들어 정확히 매칭.
            if dam.InputVariableNames.Length = dam.InputPhysicalAddresses.Length then
                List.zip dam.InputVariableNames dam.InputPhysicalAddresses
                |> List.choose (fun key -> entryByKey |> Map.tryFind key)
            else
                // 길이 mismatch 는 매칭 엔진 invariant 위반 — 명시적 fail 보다는 Name fallback.
                dam.InputVariableNames
                |> List.choose (fun n ->
                    entryByKey
                    |> Map.toList
                    |> List.tryFind (fun ((name, _), _) -> name = n)
                    |> Option.map snd)
        { OutputEntry = outputEntry
          InputEntries = inputEntries
          FlowName = dam.Flow
          WorkName = dam.Work
          DeviceName = dam.DeviceName
          ApiName = dam.Api
          Strategy = dam.MatchingStrategy
          Confidence = dam.MatchingConfidence }

    /// 명시적 config 로 매핑 — vendor / FlowInclusions / FilterExclusions / SymmetryRules 모두 반영.
    /// vendor 인자가 필수 — Common.MappingSets + Vendors.<vendor>.MappingSets (+ vendor OutputAddressPatterns/InputAddressPatterns) 를 합쳐 dsev2 매칭 엔진에 전달.
    let mapWithConfig (vendor: Vendor) (config: MappingConfig.InputMatchingConfigDto) (entries: SymbolEntry list) : MappingBatch =
        let variables = entries |> List.map MapperRules.toVariable
        let mappingSets = MapperRules.mappingSetsFromConfig vendor config
        let dsev2Mappings = DeviceGroupingCore.createDeviceApiMappingsWithMappingSets variables mappingSets

        // (Name, Address) 쌍을 키로 — 같은 Name 의 X/Y 페어가 collision 없이 보존.
        // 같은 (Name, Address) 가 중복되면 마지막 entry 만 — 거의 일어나지 않지만 가능.
        let entryByKey =
            entries |> List.map (fun e -> (e.Name, e.Address), e) |> Map.ofList
        let mapped = dsev2Mappings |> List.map (fromDsev2Mapping entryByKey)

        // 매칭된 SymbolEntry name 집합 → unmatched 추출.
        let matchedNames =
            mapped
            |> List.collect (fun m ->
                let outName = m.OutputEntry |> Option.map (fun e -> e.Name) |> Option.toList
                let inNames = m.InputEntries |> List.map (fun e -> e.Name)
                outName @ inNames)
            |> Set.ofList
        let unmatched = entries |> List.filter (fun e -> not (matchedNames.Contains e.Name))
        { Mapped = mapped; Unmatched = unmatched }

    /// Default config (AppContext.BaseDirectory/input-matching-config.json) 로드 후 vendor 명시 매핑.
    let map (vendor: Vendor) (entries: SymbolEntry list) : MappingBatch =
        let config = MappingConfig.loadDefault ()
        mapWithConfig vendor config entries

    /// Flow 단위 그룹.
    let groupByFlow (batch: MappingBatch) : Map<string, Mapping list> =
        batch.Mapped |> List.groupBy (fun m -> m.FlowName) |> Map.ofList

    /// Device 단위 그룹 (passive System 생성용).
    let groupByDevice (batch: MappingBatch) : Map<string, Mapping list> =
        batch.Mapped |> List.groupBy (fun m -> m.DeviceName) |> Map.ofList

    /// (Flow * Work) 단위 그룹 (Work + Call 생성용).
    let groupByFlowWork (batch: MappingBatch) : Map<string * string, Mapping list> =
        batch.Mapped |> List.groupBy (fun m -> m.FlowName, m.WorkName) |> Map.ofList
