module internal Ds2.Store.Editor.Tests.ControlVirtualPlantTestHarness

open System
open System.Collections.Generic
open System.IO
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Passive
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

type Model = {
    Store: DsStore
    Run: Work
    Spare: Work
    Device: DsSystem
    Tx: Work
    Api: ApiDef
}

let model () =
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
    { Store = store; Run = run; Spare = spare; Device = device; Tx = tx; Api = api }

let call (m: Model) (owner: Work) name =
    m.Store.AddCallWithLinkedApiDefs(owner.Id, name, "Invoke", [])

let bind (m: Model) callId (api: ApiDef) outAddress outType outValue inAddress =
    m.Store.AddApiCallFromPanel(
        callId, api.Id, "OUT", outAddress, "IN", inAddress,
        outType, outValue, ValueSpecTypeIndex.Bool, "true")

let secondApi (m: Model) =
    let api = addApiDef m.Store "OtherMove" m.Device.Id
    api.TxGuid <- Some m.Tx.Id
    api.RxGuid <- Some m.Tx.Id
    api

// Persistence creates the same model/IDs in two independent object graphs. It also
// exercises the real re-wiring of ApiCall IDs shared by several Calls.
let clonePair (store: DsStore) =
    let path = Path.Combine(Path.GetTempPath(), $"ds2-control-vp-{Guid.NewGuid():N}.sdf")
    try
        store.SaveToFile path
        let control, plant = DsStore(), DsStore()
        control.LoadFromFile path
        plant.LoadFromFile path
        control, plant
    finally
        if File.Exists path then File.Delete path

type Host = {
    Store: DsStore
    Engine: ISimulationEngine
    Session: RuntimeModeSession
    Inference: PassiveInferenceSession
}

type WireValue = {
    At: int64
    Source: string
    Address: string
    Value: string
}

// Only transport and wall-clock waiting are replaced. RuntimeModeSession supplies
// the real Hub effects; PassiveInferenceSession and EventDrivenEngine own states.
// The wire deliberately carries no Call/ApiCall identity and does not coalesce writes.
type DualRuntime(store: DsStore) =
    let controlStore, plantStore = clonePair store
    let pending = SortedDictionary<int64, Queue<unit -> unit>>()
    let writes = ResizeArray<WireValue>()
    let plantTransitions = ResizeArray<Guid * Status4 * int64>()
    let mutable now = 0L
    let mutable hosts: Host list = []

    let enqueue delay action =
        let due = now + int64 delay
        let queue =
            match pending.TryGetValue due with
            | true, queue -> queue
            | _ ->
                let queue = Queue<unit -> unit>()
                pending.Add(due, queue)
                queue
        queue.Enqueue action

    let drainEngine (host: Host) = host.Engine.AdvanceSimulationTo now

    let applyInference (host: Host) actions =
        for (action: PassiveInferenceAction) in actions do
            match action.TargetKind with
            | PassiveInferenceTarget.Work ->
                let mappedDevice =
                    host.Engine.IOMap.TxWorkToOutAddresses.ContainsKey action.TargetGuid
                    || host.Engine.IOMap.RxWorkToInAddresses.ContainsKey action.TargetGuid
                if not mappedDevice && host.Engine.GetWorkState action.TargetGuid <> Some action.State then
                    host.Engine.ForceWorkState(action.TargetGuid, action.State)
            | PassiveInferenceTarget.Call ->
                if host.Engine.GetCallState action.TargetGuid <> Some action.State then
                    host.Engine.ForceCallState(action.TargetGuid, action.State)
            | _ -> failwith "Unknown passive inference target"
        drainEngine host

    let rec send source address value =
        writes.Add { At = now; Source = source; Address = address; Value = value }
        enqueue 0 (fun () ->
            for host in hosts do
                if not (host.Session.ShouldIgnoreHubSource source) then
                    for effect in host.Session.HandleHubTag(address, value, source) do
                        enqueue effect.DelayMs (fun () -> applyEffect host effect))

    and applyEffect (host: Host) (effect: RuntimeHubEffect) =
        match effect.Kind with
        | RuntimeHubEffectKind.Log -> ()
        | RuntimeHubEffectKind.InjectIoByAddress ->
            host.Engine.InjectIOValueByAddress(effect.Address, effect.Value) |> ignore
            drainEngine host
        | RuntimeHubEffectKind.ForceWorkState ->
            host.Engine.ForceWorkState(effect.WorkGuid, effect.State)
            drainEngine host
        | RuntimeHubEffectKind.ForceWorkStateIfGoing ->
            host.Engine.TryForceWorkStateIfGoing(effect.WorkGuid, effect.State)
            drainEngine host
        | RuntimeHubEffectKind.ForceWorkStateIfReady ->
            host.Engine.TryForceWorkStateIfReady(effect.WorkGuid, effect.State)
            drainEngine host
        | RuntimeHubEffectKind.WriteTag -> send host.Session.HubSource effect.Address effect.Value
        | RuntimeHubEffectKind.PassiveObserve ->
            host.Inference.Observe(
                effect.Address, effect.Value,
                Func<Guid, Status4>(fun id -> host.Engine.GetWorkState id |> Option.defaultValue Status4.Ready),
                Func<Guid, Status4>(fun id -> host.Engine.GetCallState id |> Option.defaultValue Status4.Ready), now)
            |> applyInference host
        | RuntimeHubEffectKind.PassiveBaseline -> host.Inference.Baseline(effect.Address, effect.Value)
        | _ -> failwith $"Unknown Hub effect {effect.Kind}"

    let createHost source mode hostStore =
        let callback = if mode = RuntimeMode.Control then Some (send source) else None
        let engine = new EventDrivenEngine(SimIndex.build hostStore 10, mode, callback) :> ISimulationEngine
        { Store = hostStore; Engine = engine
          Session = RuntimeModeSession(engine.Index, engine.IOMap, mode)
          Inference = PassiveInferenceSession(engine.Index, engine.IOMap, mode) }

    let control = createHost "control" RuntimeMode.Control controlStore
    let plant = createHost "virtualplant" RuntimeMode.VirtualPlant plantStore

    let drainWire () =
        let mutable count = 0
        let mutable draining = true
        while draining do
            match pending |> Seq.tryHead with
            | Some bucket when bucket.Key <= now ->
                let action = bucket.Value.Dequeue()
                if bucket.Value.Count = 0 then pending.Remove bucket.Key |> ignore
                action ()
                count <- count + 1
                if count > 10000 then failwith "Hub effects did not settle at the current virtual time"
            | _ -> draining <- false

    do
        hosts <- [ control; plant ]
        plant.Engine.WorkStateChanged.Add(fun e ->
            plantTransitions.Add(e.WorkGuid, e.NewState, plant.Engine.CurrentTimeMs))
        for host in hosts do host.Engine.ApplyInitialStates()
        // Only initial input baselines are injected. Completion inputs are produced
        // exclusively by actual delayed RuntimeHubSession effects after Control OUT.
        store.ApiCalls.Values
        |> Seq.choose (fun ac -> ac.InTag |> Option.map (fun tag -> tag.Address, RuntimeSemantics.resetInputValue ac))
        |> Seq.distinct
        |> Seq.iter (fun (address, value) -> send "plc" address value)
        drainWire ()
        writes.Clear()
        plantTransitions.Clear()

    member _.Control = control
    member _.Plant = plant
    member _.Writes = writes |> Seq.toList
    member _.PlantTransitions = plantTransitions |> Seq.toList

    member _.Begin() =
        control.Engine.BeginStepBatch(Guid.Empty, true) |> ignore
        drainWire ()

    member _.AdvanceTo(target: int64) =
        if target < now then invalidArg "target" "Virtual time cannot move backwards"
        // Millisecond ticks preserve engine timers and delayed Hub effects without
        // Start(), background loops, sleeps or Task.Delay.
        for tick in now + 1L .. target do
            now <- tick
            for host in hosts do drainEngine host
            drainWire ()

    interface IDisposable with
        member _.Dispose() =
            control.Engine.EndStep()
            for host in hosts do host.Engine.Dispose()

let outputs (run: DualRuntime) =
    run.Writes
    |> List.filter (fun value -> value.Source = "control")
    |> List.map (fun value -> value.Address, value.Value)

let assertEcho (run: DualRuntime) address =
    let active = run.Writes |> List.filter (fun value -> value.Source = "virtualplant" && value.Address = address && String.Equals(value.Value, "true", StringComparison.OrdinalIgnoreCase))
    Assert.NotEmpty active
    Assert.All(active, fun value -> Assert.Equal(50L, value.At))
    Assert.Contains(run.Writes, fun value -> value.Source = "virtualplant" && value.Address = address && value.Value = "false" && value.At >= 50L)

let assertBeforeEcho (run: DualRuntime) callId txId =
    Assert.Equal(Some Status4.Going, run.Control.Engine.GetCallState callId)
    Assert.Equal(Some Status4.Going, run.Plant.Engine.GetWorkState txId)
    run.AdvanceTo 49L
    Assert.Equal(Some Status4.Going, run.Control.Engine.GetCallState callId)
    Assert.Equal(Some Status4.Going, run.Plant.Engine.GetWorkState txId)
    Assert.DoesNotContain(run.Writes, fun value -> value.Source = "virtualplant" && String.Equals(value.Value, "true", StringComparison.OrdinalIgnoreCase))

let assertFinished (run: DualRuntime) callId txId =
    run.AdvanceTo 100L
    Assert.Equal(Some Status4.Finish, run.Control.Engine.GetCallState callId)
    Assert.Equal(Some Status4.Finish, run.Plant.Engine.GetWorkState txId)
    Assert.Equal(1, run.PlantTransitions |> List.filter (fun (id, state, _) -> id = txId && state = Status4.Going) |> List.length)
    Assert.Contains(run.PlantTransitions, fun (id, state, at) -> id = txId && state = Status4.Finish && at = 50L)
