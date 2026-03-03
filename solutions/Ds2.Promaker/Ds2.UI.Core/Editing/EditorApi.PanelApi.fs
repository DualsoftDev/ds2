namespace Ds2.UI.Core

open System
open Ds2.Core

// =============================================================================
// EditorPanelApi — 프로퍼티 패널 읽기/쓰기 진입점
// =============================================================================

type EditorPanelApi
    (store: DsStore,
     exec: ExecFn,
     execOpt: EditorCommand option -> bool,
     applyTryBuild: (bool * EditorCommand option) -> bool) =

    // --- ApiDef Properties ---

    member _.UpdateApiDefProperties
        (apiDefId: Guid, isPush: bool,
         txGuid: Nullable<Guid>, rxGuid: Nullable<Guid>,
         duration: int, description: string) =
        exec(PanelOps.buildUpdateApiDefPropertiesCmd store apiDefId isPush txGuid rxGuid duration description)

    member _.RemoveApiCallFromCall(callId: Guid, apiCallId: Guid) =
        exec(PanelOps.buildRemoveApiCallFromCallCmd store callId apiCallId)

    // --- Work Duration ---

    member _.GetWorkDurationText(workId: Guid) : string =
        PanelOps.getWorkDurationText store workId

    member _.TryUpdateWorkDuration(workId: Guid, durationText: string) : bool =
        applyTryBuild(PanelOps.tryBuildUpdateWorkDurationCmd store workId durationText)

    // --- Call Timeout ---

    member _.GetCallTimeoutText(callId: Guid) : string =
        PanelOps.getCallTimeoutText store callId

    member _.TryUpdateCallTimeout(callId: Guid, msText: string) : bool =
        applyTryBuild(PanelOps.tryBuildUpdateCallTimeoutCmd store callId msText)

    // --- ApiDef / System Query ---

    member _.GetApiDefsForSystem(systemId: Guid) : ApiDefPanelItem list =
        PanelOps.getApiDefsForSystem store systemId

    member _.GetWorksForSystem(systemId: Guid) : WorkDropdownItem list =
        PanelOps.getWorksForSystem store systemId

    member _.GetApiDefParentSystemId(apiDefId: Guid) : Guid option =
        PanelOps.getApiDefParentSystemId store apiDefId

    member _.GetDeviceApiDefOptionsForCall(callId: Guid) : DeviceApiDefOption list =
        PanelOps.getDeviceApiDefOptionsForCall store callId

    // --- ApiCall Panel ---

    member _.GetCallApiCallsForPanel(callId: Guid) : CallApiCallPanelItem list =
        PanelOps.getCallApiCallsForPanel store callId

    member _.GetAllApiCallsForPanel() : CallApiCallPanelItem list =
        PanelOps.getAllApiCallsForPanel store

    member _.AddApiCallFromPanel
        (callId: Guid, apiDefId: Guid, apiCallName: string,
         outputAddress: string, inputAddress: string,
         valueSpecText: string, inputValueSpecText: string)
        : Guid option =
        match PanelOps.buildAddApiCallCmd store callId apiDefId apiCallName outputAddress inputAddress valueSpecText inputValueSpecText with
        | Some (newId, cmd) -> exec cmd; Some newId
        | None -> None

    member _.UpdateApiCallFromPanel
        (callId: Guid, apiCallId: Guid, apiDefId: Guid, apiCallName: string,
         outputAddress: string, inputAddress: string,
         outputTypeIndex: int, valueSpecText: string,
         inputTypeIndex: int, inputValueSpecText: string)
        : bool =
        execOpt(PanelOps.buildUpdateApiCallCmd store callId apiCallId apiDefId apiCallName outputAddress inputAddress outputTypeIndex valueSpecText inputTypeIndex inputValueSpecText)

    // --- Call Conditions ---

    member _.GetCallConditionsForPanel(callId: Guid) : CallConditionPanelItem list =
        PanelOps.getCallConditionsForPanel store callId

    member _.GetCallConditionsForPanelUi(callId: Guid) : UiCallConditionPanelItem list =
        PanelOps.getCallConditionsForPanel store callId
        |> List.map (fun condition ->
            UiCallConditionPanelItem(
                condition.ConditionId,
                UiCallConditionType.ofCore condition.ConditionType,
                condition.IsOR,
                condition.IsRising,
                condition.Items))

    member _.AddCallCondition(callId: Guid, conditionType: CallConditionType) : bool =
        execOpt(PanelOps.buildAddCallConditionCmd store callId conditionType)

    member _.AddCallConditionUi(callId: Guid, conditionType: UiCallConditionType) : bool =
        execOpt(PanelOps.buildAddCallConditionCmd store callId (UiCallConditionType.toCore conditionType))

    member _.RemoveCallCondition(callId: Guid, conditionId: Guid) : bool =
        execOpt(PanelOps.buildRemoveCallConditionCmd store callId conditionId)

    member _.UpdateCallConditionSettings(callId: Guid, conditionId: Guid, isOR: bool, isRising: bool) : bool =
        execOpt(PanelOps.buildUpdateCallConditionSettingsCmd store callId conditionId isOR isRising)

    /// 다중 ApiCall을 조건에 한 번에 추가 (Composite 1건 → Undo 1회). 추가된 개수 반환.
    member _.AddApiCallsToConditionBatch(callId: Guid, conditionId: Guid, sourceApiCallIds: Guid[]) : int =
        match PanelOps.buildAddApiCallsToConditionBatchCmd store callId conditionId (List.ofArray sourceApiCallIds) with
        | Some (cmd, count) -> exec cmd; count
        | None -> 0

    member _.RemoveApiCallFromCondition(callId: Guid, conditionId: Guid, apiCallId: Guid) : bool =
        execOpt(PanelOps.buildRemoveApiCallFromConditionCmd store callId conditionId apiCallId)

    member _.UpdateConditionApiCallOutputSpec(callId: Guid, conditionId: Guid, apiCallId: Guid, newSpecText: string) : bool =
        execOpt(PanelOps.buildUpdateConditionApiCallOutputSpecCmd store callId conditionId apiCallId newSpecText)
