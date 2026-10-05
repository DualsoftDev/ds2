module Ds2.Store.Editor.Tests.ExplicitInitializationTests

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

type private Fixture = {
    Store: DsStore
    Owner: Work
    Resetter: Work
    Device: DsSystem
    DeviceFlow: Flow
}

let private fixture () =
    let store = createStore ()
    let project = addProject store "P"
    let active = addSystem store "Cell" project.Id true
    let flow = addFlow store "F" active.Id
    let owner = addWork store "Run" flow.Id
    let resetter = addWork store "ResetCommand" flow.Id
    let device = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "Motion" device.Id
    { Store = store; Owner = owner; Resetter = resetter; Device = device; DeviceFlow = deviceFlow }

let private operation f name =
    let work = addWork f.Store name f.DeviceFlow.Id
    work.Duration <- Some (TimeSpan.FromMilliseconds 20.)
    let api = addApiDef f.Store name f.Device.Id
    api.TxGuid <- Some work.Id
    api.RxGuid <- Some work.Id
    work, api

let private call f name (api: ApiDef) =
    f.Store.AddCallWithLinkedApiDefs(f.Owner.Id, name, api.Name, [ api.Id ])

let private initial (work: Work) value =
    let props = SimulationWorkProperties()
    props.IsFinished <- value
    work.SetSimulationProperties props

let private addDeviceResetCommand f (target: Work) =
    let clear, clearApi = operation f "Clear"
    f.Store.AddCallWithLinkedApiDefs(f.Resetter.Id, "Device", "Clear", [ clearApi.Id ]) |> ignore
    f.Store.ConnectSelectionInOrder([ clear.Id; target.Id ], ArrowType.Reset) |> ignore

let private engine f options =
    new EventDrivenEngine(SimIndex.build f.Store 10, RuntimeMode.Simulation, None, options)

// Without Start(), no background clock runs. Force only the external command Work
// Going; target rearming remains the responsibility of the modeled Reset edges.
let private advance (sim: ISimulationEngine) durationMs =
    let target = sim.CurrentTimeMs + durationMs
    sim.AdvanceSimulationTo sim.CurrentTimeMs
    while sim.CurrentTimeMs < target do
        sim.AdvanceSimulationTo(min target (sim.CurrentTimeMs + 1L))

[<Theory>]
[<InlineData(0, 1)>]
[<InlineData(1, 0)>]
[<InlineData(77, 0)>]
[<InlineData(0, 77)>]
[<InlineData(-1, -1)>]
let ``unsupported and mixed execution policies reject`` initialization rearming =
    Assert.Throws<ArgumentException>(fun () ->
        SimulationExecutionOptions(enum initialization, enum rearming) |> ignore) |> ignore

[<Theory>]
[<InlineData(1)>]
[<InlineData(2)>]
[<InlineData(3)>]
[<InlineData(99)>]
let ``explicit policies reject non Simulation runtime modes`` mode =
    let f = fixture ()
    Assert.Throws<ArgumentException>(fun () ->
        use rejected = new EventDrivenEngine(SimIndex.build f.Store 10, enum mode, None, SimulationExecutionOptions.Explicit)
        ()) |> ignore

[<Fact>]
let ``existing constructors keep legacy options and null options reject`` () =
    let f = fixture ()
    let index = SimIndex.build f.Store 10
    use two = new EventDrivenEngine(index, RuntimeMode.Simulation)
    use three = new EventDrivenEngine(index, RuntimeMode.Control, None)
    for actual in [ two.ExecutionOptions; three.ExecutionOptions ] do
        Assert.Equal(SimulationInitializationPolicy.LegacyAutoHoming, actual.Initialization)
        Assert.Equal(SimulationRearmingPolicy.LegacyDeviceRecovery, actual.Rearming)
    Assert.Throws<ArgumentNullException>(fun () ->
        use rejected = new EventDrivenEngine(index, RuntimeMode.Simulation, None, Unchecked.defaultof<SimulationExecutionOptions>)
        ()) |> ignore

[<Fact>]
let ``model only leaves ordered same device operations Ready while legacy infers last Finish`` () =
    let f = fixture ()
    let first, firstApi = operation f "First"
    let last, lastApi = operation f "Last"
    initial first false
    initial last false
    let firstCall = call f "first" firstApi
    let lastCall = call f "last" lastApi
    f.Store.ConnectSelectionInOrder([ firstCall; lastCall ], ArrowType.Start) |> ignore
    use explicitEngine = engine f SimulationExecutionOptions.Explicit
    let sim = explicitEngine :> ISimulationEngine
    sim.ApplyInitialStates()
    Assert.Equal(Some Status4.Ready, sim.GetWorkState first.Id)
    Assert.Equal(Some Status4.Ready, sim.GetWorkState last.Id)
    Assert.Equal(Some Status4.Ready, sim.GetCallState lastCall)
    use legacyEngine = engine f SimulationExecutionOptions.Legacy
    let legacy = legacyEngine :> ISimulationEngine
    legacy.ApplyInitialStates()
    Assert.Equal(Some Status4.Finish, legacy.GetWorkState last.Id)
    Assert.Equal(Some Status4.Ready, legacy.GetWorkState first.Id)

[<Fact>]
let ``model only disables RET name fallback without a positive flag`` () =
    let f = fixture ()
    let ret, api = operation f "RETURN_POSITION"
    call f "return" api |> ignore
    use explicitEngine = engine f SimulationExecutionOptions.Explicit
    let sim = explicitEngine :> ISimulationEngine
    sim.ApplyInitialStates()
    Assert.Equal(Some Status4.Ready, sim.GetWorkState ret.Id)
    use legacyEngine = engine f SimulationExecutionOptions.Legacy
    let legacy = legacyEngine :> ISimulationEngine
    legacy.ApplyInitialStates()
    Assert.Equal(Some Status4.Finish, legacy.GetWorkState ret.Id)

[<Fact>]
let ``explicit startup honors positive flags without executing inferred homing`` () =
    let f = fixture ()
    let first, firstApi = operation f "First"
    let last, lastApi = operation f "Last"
    let firstCall = call f "first" firstApi
    let lastCall = call f "last" lastApi
    f.Store.ConnectSelectionInOrder([ firstCall; lastCall ], ArrowType.Start) |> ignore
    // A real explicit positive flag used to suppress fallback still allowed a separate
    // inferred homing plan to execute Last. The explicit contract must skip both paths.
    initial f.Resetter true
    use explicitEngine = engine f SimulationExecutionOptions.Explicit
    let sim = explicitEngine :> ISimulationEngine
    let going = ResizeArray<Guid>()
    sim.WorkStateChanged.Add(fun e -> if e.NewState = Status4.Going then going.Add e.WorkGuid)
    Assert.False(sim.StartWithHomingPhase())
    sim.Stop()
    Assert.False(sim.IsHomingPhase)
    Assert.Equal(Some Status4.Finish, sim.GetWorkState f.Resetter.Id)
    Assert.Equal(Some Status4.Ready, sim.GetWorkState first.Id)
    Assert.Equal(Some Status4.Ready, sim.GetWorkState last.Id)
    Assert.Equal(Some Status4.Ready, sim.GetCallState lastCall)
    Assert.Empty(going)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``later same target Call requires an ordered explicit reset and a new epoch`` hasReset =
    let f = fixture ()
    let target, api = operation f "Move"
    let firstCall = call f "first" api
    let nextCall = call f "next" api
    if hasReset then
        let clear, clearApi = operation f "Clear"
        let clearCall = call f "clear" clearApi
        f.Store.ConnectSelectionInOrder([ clear.Id; target.Id ], ArrowType.Reset) |> ignore
        // Reset is an explicit operation before the next request. An unrelated late
        // reset Call can be excluded by the existing device race guards.
        f.Store.ConnectSelectionInOrder([ firstCall; clearCall; nextCall ], ArrowType.Start) |> ignore
    else
        f.Store.ConnectSelectionInOrder([ firstCall; nextCall ], ArrowType.Start) |> ignore
    use explicitEngine = engine f SimulationExecutionOptions.Explicit
    let sim = explicitEngine :> ISimulationEngine
    let going = ResizeArray<Guid>()
    sim.WorkStateChanged.Add(fun e -> if e.NewState = Status4.Going then going.Add e.WorkGuid)
    sim.ApplyInitialStates()
    sim.ForceWorkState(f.Owner.Id, Status4.Going)
    advance sim 100L
    Assert.Equal(Some Status4.Finish, sim.GetCallState firstCall)
    Assert.Equal(Some Status4.Finish, sim.GetWorkState target.Id)
    Assert.Equal(Some (if hasReset then Status4.Finish else Status4.Going), sim.GetCallState nextCall)
    let expectedCycles = if hasReset then 2 else 1
    Assert.Equal(expectedCycles, going |> Seq.filter ((=) target.Id) |> Seq.length)
    Assert.Equal(expectedCycles, sim.State.WorkCycleEpoch[target.Id])

[<Fact>]
let ``modeled parent and device resets rearm the same Call and clear stale snapshots`` () =
    let f = fixture ()
    let target, api = operation f "Move"
    let invoked = call f "invoke" api
    addDeviceResetCommand f target
    f.Store.ConnectSelectionInOrder([ f.Resetter.Id; f.Owner.Id ], ArrowType.Reset) |> ignore
    use explicitEngine = engine f SimulationExecutionOptions.Explicit
    let sim = explicitEngine :> ISimulationEngine
    sim.ForceWorkState(f.Owner.Id, Status4.Going)
    advance sim 100L
    Assert.Equal(Some Status4.Finish, sim.GetCallState invoked)
    Assert.True(sim.State.CallRxEpochSnapshot.ContainsKey invoked)
    Assert.NotEmpty(sim.State.IOValues)
    sim.ForceWorkState(f.Resetter.Id, Status4.Going)
    advance sim 10L
    Assert.Equal(Some Status4.Ready, sim.GetWorkState target.Id)
    Assert.Equal(Some Status4.Ready, sim.GetWorkState f.Owner.Id)
    Assert.Equal(Some Status4.Ready, sim.GetCallState invoked)
    Assert.False(sim.State.CallRxEpochSnapshot.ContainsKey invoked)
    Assert.Empty(sim.State.IOValues)
    sim.ForceWorkState(f.Owner.Id, Status4.Going)
    advance sim 10L
    Assert.Equal(Some Status4.Going, sim.GetCallState invoked)
    advance sim 100L
    Assert.Equal(Some Status4.Finish, sim.GetCallState invoked)
    Assert.Equal(2, sim.State.WorkCycleEpoch[target.Id])

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``mutual Finish recovery is legacy only`` explicitPolicy =
    let f = fixture ()
    let left, _ = operation f "Left"
    let right, _ = operation f "Right"
    f.Store.ConnectSelectionInOrder([ left.Id; right.Id ], ArrowType.ResetReset) |> ignore
    let options = if explicitPolicy then SimulationExecutionOptions.Explicit else SimulationExecutionOptions.Legacy
    use actualEngine = engine f options
    let sim = actualEngine :> ISimulationEngine
    sim.ForceWorkState(left.Id, Status4.Finish)
    advance sim 0L
    sim.ForceWorkState(right.Id, Status4.Finish)
    advance sim 10L
    if explicitPolicy then
        Assert.Equal(Some Status4.Finish, sim.GetWorkState left.Id)
        Assert.Equal(Some Status4.Finish, sim.GetWorkState right.Id)
    else
        let recovered = min left.Id right.Id
        let retained = max left.Id right.Id
        Assert.Equal(Some Status4.Ready, sim.GetWorkState recovered)
        Assert.Equal(Some Status4.Finish, sim.GetWorkState retained)

[<Fact>]
let ``Reset retains explicit policy and reapplies only model flags on repeated starts`` () =
    let f = fixture ()
    let target, api = operation f "RETURN_POSITION"
    call f "return" api |> ignore
    initial f.Resetter true
    use actualEngine = engine f SimulationExecutionOptions.Explicit
    let options = actualEngine.ExecutionOptions
    let sim = actualEngine :> ISimulationEngine
    for _ in 1 .. 3 do
        Assert.False(sim.StartWithHomingPhase())
        sim.Stop()
        Assert.Equal(Some Status4.Finish, sim.GetWorkState f.Resetter.Id)
        Assert.Equal(Some Status4.Ready, sim.GetWorkState target.Id)
        sim.Reset()
        Assert.Equal(Some Status4.Ready, sim.GetWorkState f.Resetter.Id)
        Assert.Same(options, actualEngine.ExecutionOptions)
    Assert.Equal(SimulationInitializationPolicy.ModelOnly, actualEngine.ExecutionOptions.Initialization)
    Assert.Equal(SimulationRearmingPolicy.ResetEdgesOnly, actualEngine.ExecutionOptions.Rearming)
