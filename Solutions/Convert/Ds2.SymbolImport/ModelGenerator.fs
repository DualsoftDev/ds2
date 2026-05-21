namespace Ds2.SymbolImport

open System
open Ds2.Core

/// <summary>매핑 결과 → DS2 도메인 엔티티 생성 plan. DsStore mutation 은 호출자가 수행
/// (Ds2.UI.Core/Editor 의존 회피 + WPF 없이 테스트 가능 위해 본 모듈은 *plan 만* 생성).</summary>
module ModelGenerator =

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
        Name: string                          // "{DeviceName}.{ApiName}"
        DeviceName: string
        ApiName: string
        InTag: IOTag option                   // PLC Input 심볼 → InTag (첫 Input)
        OutTag: IOTag option                  // PLC Output 심볼 → OutTag
    }

    and ApiDefPlan = {
        Name: string                          // Api 이름
        ActionType: ActionType                // v10 — 룰 기반 추론. 기본 Real(Level, None)
        SensingType: SensingType
    }

    // ── v10 ActionType/SensingType 추론 (심볼명 패턴) ──
    let private inferActionType (apiName: string) : ActionType =
        let upper = if isNull apiName then "" else apiName.ToUpperInvariant()
        // _PB (push button) → Latched (Action.set 의 SR 회로 의미)
        if upper = "PB" || upper.EndsWith("_PB") then ActionType.Real (Latched, None)
        else ActionType.Real (Level, None)

    let private inferSensingType (apiName: string) : SensingType =
        let upper = if isNull apiName then "" else apiName.ToUpperInvariant()
        // _LS / _LMT (limit switch) → OneShot (Sensing.edge)
        if upper = "LS" || upper.EndsWith("_LS") || upper = "LMT" || upper.EndsWith("_LMT") then
            SensingType.Real (OneShot, None)
        else SensingType.Real (Level, None)

    let private toIOTag (entry: SymbolEntry) : IOTag =
        IOTag(entry.Name, entry.Address, entry.Comment)

    // v10 V1/V2 silence — 매칭이 짝을 못 찾은 ApiCall 에 빈 주소 placeholder 부여.
    // ApiDef.ActionType/SensingType 은 Real 유지 → V4 부작용 없음.
    // 운영 검증된 패턴 (DSPilot fix_aasx_v10.py 가 후처리로 동일하게 치환).
    // 사용자가 UI 에서 실 PLC 주소 보정 시 placeholder address 가 단서.
    [<Literal>]
    let PlaceholderInName = "(unset-IN)"
    [<Literal>]
    let PlaceholderOutName = "(unset-OUT)"
    [<Literal>]
    let private PlaceholderDescription = "v10 placeholder"

    let private placeholderInTag () = IOTag(PlaceholderInName, "", PlaceholderDescription)
    let private placeholderOutTag () = IOTag(PlaceholderOutName, "", PlaceholderDescription)

    /// 매핑 batch → SystemPlan list.
    /// - Controller (active) — 모든 Flow 가 묶임. project 단위 1개.
    /// - Device passive — DeviceName 별 별도 System (ApiDef 보유).
    let generate (batch: Mapper.MappingBatch) : SystemPlan list =
        let mapped = batch.Mapped

        // Controller flows — Flow → Work → Call (1 Mapping = 1 Call).
        let flows =
            mapped
            |> List.groupBy (fun m -> m.FlowName)
            |> List.map (fun (flowName, flowMappings) ->
                let works =
                    flowMappings
                    |> List.groupBy (fun m -> m.WorkName)
                    |> List.map (fun (workName, workMappings) ->
                        let calls =
                            workMappings
                            |> List.map (fun m ->
                                let outTag =
                                    match m.OutputEntry with
                                    | Some e -> toIOTag e
                                    | None   -> placeholderOutTag ()
                                let inTag =
                                    match m.InputEntries |> List.tryHead with
                                    | Some e -> toIOTag e
                                    | None   -> placeholderInTag ()
                                { Name = sprintf "%s.%s" m.DeviceName m.ApiName
                                  DeviceName = m.DeviceName
                                  ApiName = m.ApiName
                                  InTag = Some inTag
                                  OutTag = Some outTag })
                        { Name = workName; Calls = calls })
                { Name = flowName; Works = works })

        let controller = {
            Name = "Controller"
            IsActive = true
            Flows = flows
            ApiDefs = []
        }

        // Device passive systems — DeviceName 별. ApiDef 는 (DeviceName, ApiName) distinct.
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
