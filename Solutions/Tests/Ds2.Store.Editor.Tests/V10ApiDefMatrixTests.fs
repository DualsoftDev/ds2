module Ds2.Store.Editor.Tests.V10ApiDefMatrixTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Model

type private ActionCase = {
    Name: string
    Value: ActionType
    ExpectedEffect: string
}

type private SensingCase = {
    Name: string
    Value: SensingType
    ExpectedTrigger: string
}

let private appendMs = 20

let private actionCases = [
    { Name = "normal";     Value = ActionType.Real (Level, None);                     ExpectedEffect = "OutCoil" }
    { Name = "pulse";      Value = ActionType.Real (OneShot, None);                   ExpectedEffect = "EdgePulse" }
    { Name = "set";        Value = ActionType.Real (Latched, None);                   ExpectedEffect = "SetCoil" }
    { Name = "timeAppend"; Value = ActionType.Real (Level, Some (Append appendMs));   ExpectedEffect = "CoilAfterDelay" }
    { Name = "pulseHold";  Value = ActionType.Real (OneShot, Some (Append appendMs)); ExpectedEffect = "EdgePulseHold" }
    { Name = "virt";       Value = ActionType.Virtual None;                           ExpectedEffect = "NoOp" }
    { Name = "virtPlus";   Value = ActionType.Virtual (Some (Append appendMs));       ExpectedEffect = "NoOpAppend" }
]

let private sensingCases = [
    { Name = "normal";     Value = SensingType.Real (Level, None);                     ExpectedTrigger = "WaitInput" }
    { Name = "edge";       Value = SensingType.Real (OneShot, None);                   ExpectedTrigger = "WaitInputEdge" }
    { Name = "latched";    Value = SensingType.Real (Latched, None);                   ExpectedTrigger = "WaitInputLatched" }
    { Name = "debounce";   Value = SensingType.Real (Level, Some (Append appendMs));   ExpectedTrigger = "WaitInputStable" }
    { Name = "edgeStable"; Value = SensingType.Real (OneShot, Some (Append appendMs)); ExpectedTrigger = "WaitInputEdgeStable" }
    { Name = "virt";       Value = SensingType.Virtual None;                           ExpectedTrigger = "WaitPassiveDuration" }
    { Name = "virtPlus";   Value = SensingType.Virtual (Some (Append appendMs));       ExpectedTrigger = "WaitPassiveDurationPlus" }
]

let private isRealAction action =
    match action with
    | ActionType.Real _ -> true
    | ActionType.Virtual _ -> false

let private isRealSensing sensing =
    match sensing with
    | SensingType.Real _ -> true
    | SensingType.Virtual _ -> false

let private outputEffectName effect =
    match effect with
    | RuntimeSemantics.OutCoil _ -> "OutCoil"
    | RuntimeSemantics.CoilAfterDelay (_, ms) ->
        Assert.Equal(appendMs, ms)
        "CoilAfterDelay"
    | RuntimeSemantics.EdgePulse _ -> "EdgePulse"
    | RuntimeSemantics.EdgePulseHold (_, ms) ->
        Assert.Equal(appendMs, ms)
        "EdgePulseHold"
    | RuntimeSemantics.SetCoil _ -> "SetCoil"
    | RuntimeSemantics.NoOp -> "NoOp"
    | RuntimeSemantics.NoOpAppend ms ->
        Assert.Equal(appendMs, ms)
        "NoOpAppend"

let private completionTriggerName trigger =
    match trigger with
    | RuntimeSemantics.WaitInput _ -> "WaitInput"
    | RuntimeSemantics.WaitInputStable (_, ms) ->
        Assert.Equal(appendMs, ms)
        "WaitInputStable"
    | RuntimeSemantics.WaitInputEdge _ -> "WaitInputEdge"
    | RuntimeSemantics.WaitInputEdgeStable (_, ms) ->
        Assert.Equal(appendMs, ms)
        "WaitInputEdgeStable"
    | RuntimeSemantics.WaitInputLatched _ -> "WaitInputLatched"
    | RuntimeSemantics.WaitPassiveDuration _ -> "WaitPassiveDuration"
    | RuntimeSemantics.WaitPassiveDurationPlus (_, ms) ->
        Assert.Equal(appendMs, ms)
        "WaitPassiveDurationPlus"

let private createApiPair action sensing =
    let store = createStore ()
    let project, _, _, activeWork = setupBasicHierarchy store
    let deviceSystem = addSystem store "Device" project.Id false
    let apiDef = addApiDef store "ADV" deviceSystem.Id
    apiDef.ActionType <- action
    apiDef.SensingType <- sensing

    let callId = store.AddCallWithLinkedApiDefs(activeWork.Id, "Device", "ADV", [ apiDef.Id ])
    let apiCall = store.Calls.[callId].ApiCalls |> Seq.head
    apiCall.OutTag <- Some (IOTag("OUT", "Y0", ""))
    apiCall.InTag <- Some (IOTag("IN", "X0", ""))
    apiCall.InputSpec <- ValueSpec.singleBool true
    apiCall.OutputSpec <- ValueSpec.singleBool true
    apiDef, apiCall

type private EngineCase = {
    Engine: ISimulationEngine
    ActiveWorkId: Guid
    CallId: Guid
    ApiCallId: Guid
}

type private IndexCase = {
    Index: SimIndex
    ActiveWorkId: Guid
    CallId: Guid
    ApiCallId: Guid
}

let private createIndexCase action sensing =
    let store = createStore ()
    let project, _, _, activeWork = setupBasicHierarchy store
    activeWork.Duration <- Some (TimeSpan.FromMilliseconds 10.)

    let deviceSystem = addSystem store "Device" project.Id false
    let apiDef = addApiDef store "ADV" deviceSystem.Id
    apiDef.ActionType <- action
    apiDef.SensingType <- sensing

    let callId = store.AddCallWithLinkedApiDefs(activeWork.Id, "Device", "ADV", [ apiDef.Id ])
    let apiCall = store.Calls.[callId].ApiCalls |> Seq.head
    if isRealAction action then
        apiCall.OutTag <- Some (IOTag("OUT", "Y0", ""))
    if isRealSensing sensing then
        apiCall.InTag <- Some (IOTag("IN", "X0", ""))
    apiCall.InputSpec <- ValueSpec.singleBool true
    apiCall.OutputSpec <- ValueSpec.singleBool true

    {
        Index = SimIndex.build store 10
        ActiveWorkId = activeWork.Id
        CallId = callId
        ApiCallId = apiCall.Id
    }

let private createEngineCase includeTxWork action sensing =
    let store = createStore ()
    let project, _, _, activeWork = setupBasicHierarchy store
    activeWork.Duration <- Some (TimeSpan.FromMilliseconds 10.)

    let deviceSystem = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
    let deviceWork = addWork store "ADV" deviceFlow.Id
    deviceWork.Duration <- Some (TimeSpan.FromMilliseconds 10.)

    let apiDef = addApiDef store "ADV" deviceSystem.Id
    apiDef.ActionType <- action
    apiDef.SensingType <- sensing
    if includeTxWork && isRealAction action then
        apiDef.TxGuid <- Some deviceWork.Id

    let callId = store.AddCallWithLinkedApiDefs(activeWork.Id, "Device", "ADV", [ apiDef.Id ])
    let apiCall = store.Calls.[callId].ApiCalls |> Seq.head
    if isRealAction action then
        apiCall.OutTag <- Some (IOTag("OUT", "Y0", ""))
    if isRealSensing sensing then
        apiCall.InTag <- Some (IOTag("IN", "X0", ""))
    apiCall.InputSpec <- ValueSpec.singleBool true
    apiCall.OutputSpec <- ValueSpec.singleBool true

    let index = SimIndex.build store 10
    {
        Engine = new EventDrivenEngine(index, RuntimeMode.Simulation) :> ISimulationEngine
        ActiveWorkId = activeWork.Id
        CallId = callId
        ApiCallId = apiCall.Id
    }

let private drainNow (engine: ISimulationEngine) =
    engine.AdvanceSimulationTo(engine.CurrentTimeMs)

let private ensureCallGoing testCase =
    testCase.Engine.ForceWorkState(testCase.ActiveWorkId, Status4.Going)
    drainNow testCase.Engine
    match testCase.Engine.GetCallState(testCase.CallId) with
    | Some Status4.Going
    | Some Status4.Finish -> ()
    | _ ->
        testCase.Engine.ForceCallState(testCase.CallId, Status4.Going)
        drainNow testCase.Engine

let private completeBySensing testCase sensing =
    match sensing with
    | SensingType.Real (Level, None)
    | SensingType.Real (Latched, None) ->
        testCase.Engine.InjectIOValue(testCase.ApiCallId, "true")
        drainNow testCase.Engine
    | SensingType.Real (Level, Some (Append ms)) ->
        testCase.Engine.InjectIOValue(testCase.ApiCallId, "true")
        drainNow testCase.Engine
        testCase.Engine.AdvanceSimulationTo(testCase.Engine.CurrentTimeMs + int64 ms)
    | SensingType.Real (OneShot, None) ->
        testCase.Engine.InjectIOValue(testCase.ApiCallId, "true")
        drainNow testCase.Engine
    | SensingType.Real (OneShot, Some (Append ms)) ->
        testCase.Engine.InjectIOValue(testCase.ApiCallId, "true")
        drainNow testCase.Engine
        testCase.Engine.AdvanceSimulationTo(testCase.Engine.CurrentTimeMs + int64 ms)
    | SensingType.Virtual None ->
        testCase.Engine.AdvanceSimulationTo(testCase.Engine.CurrentTimeMs + 100L)
    | SensingType.Virtual (Some (Append ms)) ->
        testCase.Engine.AdvanceSimulationTo(testCase.Engine.CurrentTimeMs + int64 (100 + ms))
    | SensingType.Real (Latched, Some (Append _)) ->
        Assert.Fail("Latched + Append is not one of the supported v10 UI cases")

let private outputValue testCase =
    testCase.Engine.State.OutputValues |> Map.tryFind testCase.ApiCallId

let private completionState testCase sensing =
    let baseState =
        SimState.create 10 testCase.Index.AllWorkGuids testCase.Index.AllCallGuids testCase.Index.AllFlowGuids
        |> SimState.setWorkState testCase.ActiveWorkId Status4.Going
        |> SimState.setCallState testCase.CallId Status4.Going

    match sensing with
    | SensingType.Real (Level, None)
    | SensingType.Real (Latched, None) ->
        baseState
        |> SimState.setIOValue testCase.ApiCallId "true"
    | SensingType.Real (Level, Some (Append ms)) ->
        baseState
        |> SimState.setIOValue testCase.ApiCallId "true"
        |> fun state -> { state with Clock = TimeSpan.FromMilliseconds(float ms) }
    | SensingType.Real (OneShot, None) ->
        baseState
        |> SimState.snapshotCallInputEpochs testCase.CallId [ testCase.ApiCallId ]
        |> SimState.setIOValue testCase.ApiCallId "true"
    | SensingType.Real (OneShot, Some (Append ms)) ->
        baseState
        |> SimState.snapshotCallInputEpochs testCase.CallId [ testCase.ApiCallId ]
        |> SimState.setIOValue testCase.ApiCallId "true"
        |> fun state -> { state with Clock = TimeSpan.FromMilliseconds(float ms) }
    | SensingType.Virtual None
    | SensingType.Virtual (Some (Append _)) ->
        baseState
        |> SimState.markMinDurationMet testCase.ActiveWorkId
    | SensingType.Real (Latched, Some (Append _)) ->
        Assert.Fail("Latched + Append is not one of the supported v10 UI cases")
        baseState

[<Fact>]
let ``v10 ApiDef 7x7 matrix dispatches emitOutput and completionTrigger`` () =
    let mutable checkedCount = 0

    for action in actionCases do
        for sensing in sensingCases do
            checkedCount <- checkedCount + 1
            let apiDef, apiCall = createApiPair action.Value sensing.Value

            let actualEffect = RuntimeSemantics.emitOutput apiDef apiCall |> outputEffectName
            let actualTrigger = RuntimeSemantics.completionTrigger apiDef apiCall |> completionTriggerName

            Assert.Equal(action.ExpectedEffect, actualEffect)
            Assert.Equal(sensing.ExpectedTrigger, actualTrigger)

    Assert.Equal(49, checkedCount)

[<Fact>]
let ``v10 ApiDef 7x7 matrix builds SimIndex and completion predicate accepts each combination`` () =
    let mutable checkedCount = 0

    for action in actionCases do
        for sensing in sensingCases do
            checkedCount <- checkedCount + 1
            let testCase = createIndexCase action.Value sensing.Value
            let state = completionState testCase sensing.Value
            let actual = WorkConditionChecker.canCompleteCall testCase.Index state testCase.CallId false

            Assert.True(
                actual,
                sprintf "Expected canCompleteCall=true for action=%s sensing=%s"
                    action.Name sensing.Name)

    Assert.Equal(49, checkedCount)

[<Fact>]
let ``v10 Action pulse output resets after one millisecond`` () =
    let testCase = createEngineCase true (ActionType.Real (OneShot, None)) (SensingType.Real (Level, None))
    try
        ensureCallGoing testCase

        Assert.Equal(Some "True", outputValue testCase)
        testCase.Engine.AdvanceSimulationTo(testCase.Engine.CurrentTimeMs + 1L)
        Assert.Equal(Some "false", outputValue testCase)
        Assert.Equal(Some Status4.Going, testCase.Engine.GetCallState(testCase.CallId))
    finally
        testCase.Engine.Stop()

[<Fact>]
let ``v10 Action pulseHold output resets after append window`` () =
    let testCase = createEngineCase true (ActionType.Real (OneShot, Some (Append appendMs))) (SensingType.Real (Level, None))
    try
        ensureCallGoing testCase

        Assert.Equal(Some "True", outputValue testCase)
        testCase.Engine.AdvanceSimulationTo(testCase.Engine.CurrentTimeMs + int64 (appendMs - 1))
        Assert.Equal(Some "True", outputValue testCase)

        testCase.Engine.AdvanceSimulationTo(testCase.Engine.CurrentTimeMs + 1L)
        Assert.Equal(Some "false", outputValue testCase)
        Assert.Equal(Some Status4.Going, testCase.Engine.GetCallState(testCase.CallId))
    finally
        testCase.Engine.Stop()

[<Fact>]
let ``v10 Real Latched sensing completes on active input value`` () =
    let testCase = createEngineCase false (ActionType.Virtual None) (SensingType.Real (Latched, None))
    try
        ensureCallGoing testCase
        Assert.Equal(Some Status4.Going, testCase.Engine.GetCallState(testCase.CallId))

        testCase.Engine.InjectIOValue(testCase.ApiCallId, "true")
        drainNow testCase.Engine

        Assert.Equal(Some Status4.Finish, testCase.Engine.GetCallState(testCase.CallId))
    finally
        testCase.Engine.Stop()
