namespace Ds2.SymbolImport

open System

/// <summary>SymbolEntry → DS2 매핑 facade. MapperRules 의 deterministic 결과를
/// *그룹화 + 분류* 형태로 호출자에게 제공.</summary>
module Mapper =

    /// 단일 SymbolEntry 매핑 결과 통합 (matched + unmatched).
    type MappingBatch = {
        Mapped: MapperRules.Mapping list
        Unmatched: SymbolEntry list
    }

    /// SymbolEntry list 매핑 — MapperRules.mapAll 위임 + 그룹화.
    let map (entries: SymbolEntry list) : MappingBatch =
        let mapped, unmatched = MapperRules.mapAll entries
        { Mapped = mapped; Unmatched = unmatched }

    /// 매핑 결과를 Flow 단위로 그룹.
    let groupByFlow (batch: MappingBatch) : Map<string, MapperRules.Mapping list> =
        batch.Mapped
        |> List.groupBy (fun m -> m.FlowName)
        |> Map.ofList

    /// 매핑 결과를 Device 단위로 그룹 (passive System 생성용).
    let groupByDevice (batch: MappingBatch) : Map<string, MapperRules.Mapping list> =
        batch.Mapped
        |> List.groupBy (fun m -> m.DeviceName)
        |> Map.ofList

    /// 매핑 결과를 (Flow * Work) 단위로 그룹 (Work + Call 생성용).
    let groupByFlowWork (batch: MappingBatch) : Map<string * string, MapperRules.Mapping list> =
        batch.Mapped
        |> List.groupBy (fun m -> m.FlowName, m.WorkName)
        |> Map.ofList
