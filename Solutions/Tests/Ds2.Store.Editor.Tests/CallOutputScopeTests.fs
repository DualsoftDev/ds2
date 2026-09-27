module Ds2.Store.Editor.Tests.CallOutputScopeTests

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

type private Fixture = {
    Store: DsStore
    Run: Work
    Spare: Work
    Device: DsSystem
    DeviceFlow: Flow
    Tx: Work
    Api: ApiDef
}

let private createFixture () =
    let store = createStore ()
    let project = addProject store "P"
    let cell = addSystem store "Cell" project.Id true
    let flow = addFlow store "F" cell.Id
    let run = addWork store "Run" flow.Id
    let spare = addWork store "Spare" flow.Id
    store.UpdateWorkTokenRole(run.Id, TokenRole.Source ||| TokenRole.Sink)
    let device = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "Motion" device.Id
    let tx = addWork store "Move" deviceFlow.Id
    tx.Duration <- Some (TimeSpan.FromMilliseconds 50.)
    let api = addApiDef store "Move" device.Id
    api.TxGuid <- Some tx.Id
    api.RxGuid <- Some tx.Id
    { Store = store; Run = run; Spare = spare; Device = device
      DeviceFlow = deviceFlow; Tx = tx; Api = api }

let private addEmptyCall (fixture: Fixture) (work: Work) alias =
    fixture.Store.AddCallWithLinkedApiDefs(work.Id, alias, "Invoke", [])

// The same editor operation used by the property panel creates distinct binding IDs.
// AddCallWithLinkedApiDefs alone would deliberately share an existing ApiCall.
let private addBinding (fixture: Fixture) callId (api: ApiDef) address value input =
    fixture.Store.AddApiCallFromPanel(
        callId, api.Id, "OUT", address, "IN", input,
        ValueSpecTypeIndex.Int16, string value, ValueSpecTypeIndex.Bool, "true")

let private addApiForTx (fixture: Fixture) name (tx: Work) =
    let api = addApiDef fixture.Store name fixture.Device.Id
    api.TxGuid <- Some tx.Id
    api.RxGuid <- Some tx.Id
    api

let private assertWrites expected (actual: ResizeArray<string * string>) =
    Assert.Equal<(string * string) list>(List.sort expected, actual |> Seq.toList |> List.sort)

let private withControlEngine (fixture: Fixture) check =
    let writes = ResizeArray<string * string>()
    let goingWorks = ResizeArray<Guid>()
    let output address value = writes.Add(address, value)
    use engine = new EventDrivenEngine(SimIndex.build fixture.Store 10, RuntimeMode.Control, Some output) :> ISimulationEngine
    engine.WorkStateChanged.Add(fun e ->
        if e.NewState = Status4.Going then goingWorks.Add(e.WorkGuid))
    engine.ApplyInitialStates()
    fixture.Store.ApiCalls.Values
    |> Seq.choose (fun ac -> ac.InTag |> Option.map (fun tag -> tag.Address))
    |> Seq.distinct
    |> Seq.iter (fun address -> Assert.True(engine.InjectIOValueByAddress(address, "false")))
    engine.AdvanceSimulationTo 0L
    engine.BeginStepBatch(Guid.Empty, true) |> ignore
    try check engine writes goingWorks
    finally engine.EndStep()

[<Theory>]
[<InlineData("D1")>]
[<InlineData("D0")>]
let ``Control excludes an inactive distinct binding to the same API`` (inactiveAddress: string) =
    let f = createFixture ()
    let active = addEmptyCall f f.Run "A"
    let inactive = addEmptyCall f f.Spare "B"
    let activeBinding = addBinding f active f.Api "D0" 1 "X0"
    let inactiveBinding = addBinding f inactive f.Api inactiveAddress 2 "X1"
    Assert.NotEqual(activeBinding, inactiveBinding)
    withControlEngine f (fun engine writes _ ->
        engine.AdvanceSimulationTo 1L
        assertWrites [ "D0", "1" ] writes
        Assert.Equal(Some Status4.Going, engine.GetCallState active)
        Assert.Equal(Some Status4.Ready, engine.GetCallState inactive)
        Assert.Equal(Some Status4.Ready, engine.GetWorkState f.Spare.Id))

[<Fact>]
let ``Control excludes an inactive different API pointing at the same Tx`` () =
    let f = createFixture ()
    let otherApi = addApiForTx f "OtherCommand" f.Tx
    let active = addEmptyCall f f.Run "A"
    let inactive = addEmptyCall f f.Spare "B"
    addBinding f active f.Api "D0" 1 "X0" |> ignore
    addBinding f inactive otherApi "D1" 2 "X1" |> ignore
    withControlEngine f (fun engine writes _ ->
        assertWrites [ "D0", "1" ] writes
        Assert.Equal(Some Status4.Ready, engine.GetCallState inactive))

[<Fact>]
let ``Control emits each explicit binding once when two APIs share a Tx`` () =
    let f = createFixture ()
    let otherApi = addApiForTx f "OtherCommand" f.Tx
    let active = addEmptyCall f f.Run "A"
    addBinding f active f.Api "D0" 1 "X0" |> ignore
    addBinding f active otherApi "D1" 2 "X1" |> ignore
    withControlEngine f (fun _ writes goingWorks ->
        assertWrites [ "D0", "1"; "D1", "2" ] writes
        Assert.Equal(1, goingWorks |> Seq.filter ((=) f.Tx.Id) |> Seq.length))

[<Fact>]
let ``Control keeps a binding shared with an inactive Call`` () =
    let f = createFixture ()
    let active = addEmptyCall f f.Run "A"
    let binding = addBinding f active f.Api "D0" 1 "X0"
    let inactive = f.Store.AddCallWithLinkedApiDefs(f.Spare.Id, "B", "Invoke", [ f.Api.Id ])
    Assert.Equal(binding, f.Store.Calls.[inactive].ApiCalls.[0].Id)
    Assert.Same(f.Store.ApiCalls.[binding], f.Store.Calls.[inactive].ApiCalls.[0])
    withControlEngine f (fun engine writes _ ->
        assertWrites [ "D0", "1" ] writes
        Assert.Equal(Some Status4.Ready, engine.GetCallState inactive))

[<Fact>]
let ``Control Reference Call selects original bindings without unrelated same-Tx bindings`` () =
    let f = createFixture ()
    let original = addEmptyCall f f.Spare "Original"
    let inactive = addEmptyCall f f.Spare "Unrelated"
    addBinding f original f.Api "D0" 1 "X0" |> ignore
    addBinding f inactive f.Api "D1" 2 "X1" |> ignore
    let reference = f.Store.AddReferenceCallToWork(original, f.Run.Id)
    Assert.Empty(f.Store.Calls.[reference].ApiCalls)
    withControlEngine f (fun engine writes _ ->
        assertWrites [ "D0", "1" ] writes
        Assert.Equal(Some Status4.Going, engine.GetCallState reference)
        Assert.Equal(Some Status4.Ready, engine.GetCallState inactive))

[<Fact>]
let ``Control starts each explicit Tx and emits both selected bindings`` () =
    let f = createFixture ()
    let otherTx = addWork f.Store "OtherMove" f.DeviceFlow.Id
    otherTx.Duration <- Some (TimeSpan.FromMilliseconds 50.)
    let otherApi = addApiForTx f "OtherCommand" otherTx
    let active = addEmptyCall f f.Run "A"
    addBinding f active f.Api "D0" 1 "X0" |> ignore
    addBinding f active otherApi "D1" 2 "X1" |> ignore
    withControlEngine f (fun engine writes goingWorks ->
        assertWrites [ "D0", "1"; "D1", "2" ] writes
        for tx in [ f.Tx; otherTx ] do
            Assert.Equal(Some Status4.Going, engine.GetWorkState tx.Id)
            Assert.Equal(1, goingWorks |> Seq.filter ((=) tx.Id) |> Seq.length))

[<Fact>]
let ``Control completion resets the active Normal output without activating the inactive binding`` () =
    let f = createFixture ()
    let active = addEmptyCall f f.Run "A"
    let inactive = addEmptyCall f f.Spare "B"
    addBinding f active f.Api "D0" 1 "X0" |> ignore
    addBinding f inactive f.Api "D1" 2 "X1" |> ignore
    withControlEngine f (fun engine writes _ ->
        assertWrites [ "D0", "1" ] writes
        Assert.True(engine.InjectIOValueByAddress("X0", "true"))
        engine.AdvanceSimulationTo 10L
        engine.AdvanceSimulationTo 60L
        Assert.Equal<(string * string) list>([ "D0", "1"; "D0", "0" ], Seq.toList writes)
        Assert.Equal(Some Status4.Finish, engine.GetCallState active)
        Assert.Equal(Some Status4.Ready, engine.GetCallState inactive))

type private ExecutionCapture = {
    Context: ApiCallExecutionContext
    Writes: ResizeArray<string * string>
    Scheduled: ResizeArray<int * string * string>
    Starts: ResizeArray<Guid * Status4>
    LatchResets: ResizeArray<Guid * Guid>
    Latches: ResizeArray<Guid * Guid * IOTag>
}

let private executionCapture mode bindings getState =
    let writes = ResizeArray<string * string>()
    let scheduled = ResizeArray<int * string * string>()
    let starts = ResizeArray<Guid * Status4>()
    let resets = ResizeArray<Guid * Guid>()
    let latches = ResizeArray<Guid * Guid * IOTag>()
    let context = {
        RuntimeMode = mode
        GetDeviceState = getState
        GetDeviceName = string
        GetTxOutAddresses = fun _ -> []
        GetApiCallsForCall = fun _ -> bindings
        WriteTag = fun address value -> writes.Add(address, value)
        ScheduleAfter = fun _ -> failwith "Output writes must use the output scheduler"
        ScheduleOutputWrite = scheduled.Add
        ResetPriorLatchesOnDevice = resets.Add
        RegisterLatch = latches.Add
        ForceWorkState = fun workId state -> starts.Add(workId, state)
    }
    { Context = context; Writes = writes; Scheduled = scheduled; Starts = starts
      LatchResets = resets; Latches = latches }

let private executionBinding action tx address value =
    let api = ApiDef("Move", Guid.NewGuid())
    api.ActionType <- action
    api.TxGuid <- tx
    let binding = ApiCall("Device.Move")
    binding.ApiDefId <- Some api.Id
    binding.OutTag <- Some (IOTag("OUT", address, ""))
    binding.OutputSpec <- Int16Value (Single value)
    api, binding

[<Fact>]
let ``one request deduplicates binding identity without collapsing distinct bindings on one Tx`` () =
    let tx = Guid.NewGuid()
    let first = executionBinding (ActionType.Normal None) (Some tx) "D0" 1s
    let second = executionBinding (ActionType.Normal None) (Some tx) "D1" 2s
    let capture = executionCapture RuntimeMode.Control [ first; first; second ] (fun _ -> Status4.Ready)
    EventDrivenExecution.executeCallGoing capture.Context (Guid.NewGuid())
    assertWrites [ "D0", "1"; "D1", "2" ] capture.Writes
    Assert.Equal<(Guid * Status4) list>([ tx, Status4.Going ], Seq.toList capture.Starts)
    Assert.Equal(2, capture.LatchResets.Count)

[<Fact>]
let ``request selection preserves no-Tx and Finish exclusions while allowing an already Going Tx`` () =
    let finished, going = Guid.NewGuid(), Guid.NewGuid()
    let bindings = [
        executionBinding (ActionType.Normal None) None "NO_TX" 1s
        executionBinding (ActionType.Normal None) (Some finished) "FINISHED" 2s
        executionBinding (ActionType.Normal None) (Some going) "GOING" 3s
    ]
    let capture = executionCapture RuntimeMode.Control bindings (fun id -> if id = finished then Status4.Finish else Status4.Going)
    EventDrivenExecution.executeCallGoing capture.Context (Guid.NewGuid())
    assertWrites [ "GOING", "3" ] capture.Writes
    Assert.Equal<(Guid * Status4) list>([ going, Status4.Going ], Seq.toList capture.Starts)
    Assert.Single(capture.LatchResets) |> ignore

[<Theory>]
[<InlineData(RuntimeMode.Monitoring)>]
[<InlineData(RuntimeMode.VirtualPlant)>]
let ``passive modes do not emit request output or start Tx`` (mode: RuntimeMode) =
    let binding = executionBinding (ActionType.Normal None) (Some (Guid.NewGuid())) "D0" 1s
    let capture = executionCapture mode [ binding ] (fun _ -> Status4.Ready)
    EventDrivenExecution.executeCallGoing capture.Context (Guid.NewGuid())
    Assert.Empty(capture.Writes)
    Assert.Empty(capture.Starts)
    Assert.Empty(capture.LatchResets)

[<Fact>]
let ``Homing uses request bindings restricted to its allowed Tx targets`` () =
    let allowed, excluded = Guid.NewGuid(), Guid.NewGuid()
    let first = executionBinding (ActionType.Normal None) (Some allowed) "D0" 1s
    let second = executionBinding (ActionType.Normal None) (Some allowed) "D1" 2s
    let other = executionBinding (ActionType.Normal None) (Some excluded) "EXCLUDED" 3s
    let capture = executionCapture RuntimeMode.Control [ first; first; second; other ] (fun _ -> Status4.Ready)
    EventDrivenExecution.executeCallHoming capture.Context (Guid.NewGuid()) (Set.singleton allowed)
    assertWrites [ "D0", "1"; "D1", "2" ] capture.Writes
    Assert.Equal<(Guid * Status4) list>([ allowed, Status4.Going ], Seq.toList capture.Starts)

[<Fact>]
let ``selected action policies keep Normal Pulse Latch and Virtual effects`` () =
    let cases = [
        ActionType.Normal None, None, false, true
        ActionType.Normal (Some 25), None, false, true
        ActionType.Pulse None, Some 1, false, true
        ActionType.Pulse (Some 25), Some 25, false, true
        ActionType.Latch, None, true, true
        ActionType.Virtual, None, false, false
    ]
    for action, delay, registersLatch, emits in cases do
        let tx = Guid.NewGuid()
        let binding = executionBinding action (Some tx) "D0" 7s
        let capture = executionCapture RuntimeMode.Control [ binding ] (fun _ -> Status4.Ready)
        EventDrivenExecution.executeCallGoing capture.Context (Guid.NewGuid())
        assertWrites (if emits then [ "D0", "7" ] else []) capture.Writes
        Assert.Equal<(int * string * string) list>(
            (delay |> Option.map (fun ms -> [ ms, "D0", "0" ]) |> Option.defaultValue []),
            Seq.toList capture.Scheduled)
        Assert.Equal((if registersLatch then 1 else 0), capture.Latches.Count)
        // Existing Latch release is a separate effect, including a Virtual counterpart.
        Assert.Single(capture.LatchResets) |> ignore
        Assert.Equal<(Guid * Status4) list>([ tx, Status4.Going ], Seq.toList capture.Starts)
