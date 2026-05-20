namespace Ds2.SymbolImport

open System
open Ds2.Core

/// <summary>매핑 결과 → DS2 도메인 엔티티 생성 plan. DsStore mutation 은 호출자가 수행
/// (Ds2.UI.Core/Editor 의존 회피 + WPF 없이 테스트 가능 위해 본 모듈은 *plan 만* 생성).</summary>
module ModelGenerator =

    /// DS2 엔티티 생성 plan — 호출자가 DsStore.AddProject / AddSystem / AddFlow / AddWork / AddCall 등에 위임.
    type SystemPlan = {
        Name: string
        IsActive: bool        // true = controller (Flow 보유), false = passive device (ApiDef 보유)
        Flows: FlowPlan list
        ApiDefs: ApiDefPlan list
    }

    and FlowPlan = {
        Name: string
        Works: WorkPlan list
    }

    and WorkPlan = {
        Name: string
        Calls: CallPlan list
    }

    and CallPlan = {
        Name: string                          // "{DeviceName}.{ApiName}" 형식
        DeviceName: string
        ApiName: string
        InTag: IOTag option                   // PLC Input 심볼 → InTag
        OutTag: IOTag option                  // PLC Output 심볼 → OutTag
    }

    and ApiDefPlan = {
        Name: string                          // Api 이름 (ApiCall.ApiName 과 매칭)
        ActionType: ActionType                // v10 spec — 기본 normal (Real Level None). 룰 기반 dispatch 가능
        SensingType: SensingType
    }

    /// 매핑 결과 → 전체 plan. project 이름은 호출자가 결정.
    /// v10 spec: ApiDef.ActionType / SensingType 은 *심볼명 패턴 기반* 추론 가능.
    ///   심볼명 끝에 "_PB" (push button) → Action.set
    ///   심볼명 끝에 "_LS" / "_LMT" (limit switch) → Sensing.edge
    ///   외엔 normal.
    let private inferActionType (apiName: string) : ActionType =
        let upper = if isNull apiName then "" else apiName.ToUpperInvariant()
        if upper = "PB" || upper.EndsWith("_PB") then ActionType.Real (Latched, None)
        else ActionType.Real (Level, None)

    let private inferSensingType (apiName: string) : SensingType =
        let upper = if isNull apiName then "" else apiName.ToUpperInvariant()
        if upper = "LS" || upper.EndsWith("_LS") || upper = "LMT" || upper.EndsWith("_LMT") then
            SensingType.Real (OneShot, None)
        else SensingType.Real (Level, None)

    /// SymbolEntry → IOTag. 주소 / 이름 / 코멘트 보존.
    let private toIOTag (entry: SymbolEntry) : IOTag =
        IOTag(entry.Name, entry.Address, entry.Comment)

    /// 매핑 batch → SystemPlan list. controller(active) + device(passive) 모두 생성.
    let generate (batch: Mapper.MappingBatch) : SystemPlan list =
        // active controller — 모든 Flow 가 단일 controller 안에 묶임. project 단위 1개.
        // device passive — Device 이름 별로 별도 System.
        let mapped = batch.Mapped

        // controller flows
        let flows =
            mapped
            |> List.groupBy (fun m -> m.FlowName)
            |> List.map (fun (flowName, flowMappings) ->
                let works =
                    flowMappings
                    |> List.groupBy (fun m -> m.WorkName)
                    |> List.map (fun (workName, workMappings) ->
                        // (deviceName, apiName) 단위로 묶어 1 Call 당 in+out IO 쌍.
                        let calls =
                            workMappings
                            |> List.groupBy (fun m -> m.DeviceName, m.ApiName)
                            |> List.map (fun ((deviceName, apiName), entries) ->
                                let inTag =
                                    entries
                                    |> List.tryFind (fun m -> m.Original.Direction = Input)
                                    |> Option.map (fun m -> toIOTag m.Original)
                                let outTag =
                                    entries
                                    |> List.tryFind (fun m -> m.Original.Direction = Output)
                                    |> Option.map (fun m -> toIOTag m.Original)
                                { Name = sprintf "%s.%s" deviceName apiName
                                  DeviceName = deviceName
                                  ApiName = apiName
                                  InTag = inTag
                                  OutTag = outTag })
                        { Name = workName; Calls = calls })
                { Name = flowName; Works = works })

        let controller = {
            Name = "Controller"
            IsActive = true
            Flows = flows
            ApiDefs = []
        }

        // device passive systems
        let devices =
            mapped
            |> List.groupBy (fun m -> m.DeviceName)
            |> List.map (fun (deviceName, deviceMappings) ->
                let apiDefs =
                    deviceMappings
                    |> List.map (fun m -> m.ApiName)
                    |> List.distinct
                    |> List.map (fun apiName ->
                        { Name = apiName
                          ActionType = inferActionType apiName
                          SensingType = inferSensingType apiName })
                { Name = deviceName
                  IsActive = false
                  Flows = []
                  ApiDefs = apiDefs })

        controller :: devices
