module Ds2.Store.Editor.Tests.RuntimeSnapshotBindingTests

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Engine.Passive
open Ds2.Runtime.IO
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

type private Fixture = {
    Store: DsStore
    Work: Work
    Tx: Work
    Api: ApiDef
}

let private createFixture () =
    let store = createStore ()
    let project, _, _, work = setupBasicHierarchy store
    let device = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "DeviceFlow" device.Id
    let tx = addWork store "Move" deviceFlow.Id
    tx.Duration <- Some (TimeSpan.FromMilliseconds 40.)
    let api = addApiDef store "Move" device.Id
    api.TxGuid <- Some tx.Id
    api.RxGuid <- Some tx.Id
    { Store = store; Work = work; Tx = tx; Api = api }

let private addBinding fixture alias outType outValue input inType inValue =
    let callId = fixture.Store.AddCallWithLinkedApiDefs(fixture.Work.Id, alias, "Invoke", [])
    let bindingId =
        fixture.Store.AddApiCallFromPanel(
            callId, fixture.Api.Id, "OUT", "D0", "IN", input,
            outType, outValue, inType, inValue)
    callId, bindingId

let private snapshot (values: (string * string) list) : IReadOnlyDictionary<string, string> =
    let result = Dictionary<string, string>()
    for address, value in values do result.[address] <- value
    result

let private effectsFor mode fixture values =
    let index = SimIndex.build fixture.Store 10
    let session = RuntimeModeSession(index, SignalIOMap.build fixture.Store, mode)
    session.ResolveHubSnapshotEffects(snapshot values)

let private writes (effects: RuntimeHubEffect array) =
    effects
    |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.WriteTag)
    |> Array.map (fun effect -> effect.DelayMs, effect.Address, effect.Value)
    |> Array.toList
    |> List.sort

let private starts (effects: RuntimeHubEffect array) =
    effects
    |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.ForceWorkStateIfReady)
    |> Array.map (fun effect -> effect.WorkGuid, effect.State)
    |> Array.toList

let private controlState fixture values =
    let effects = effectsFor RuntimeMode.Control fixture values
    let states = effects |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.ForceWorkState)
    let state = Assert.Single(states)
    Assert.Equal(fixture.Tx.Id, state.WorkGuid)
    Assert.Empty(writes effects)
    state.State

[<Fact>]
let ``VirtualPlant late join replays numeric active output and echoes its numeric input`` () =
    let f = createFixture ()
    addBinding f "A" ValueSpecTypeIndex.Int16 "2" "X0" ValueSpecTypeIndex.Int16 "9" |> ignore

    let effects = effectsFor RuntimeMode.VirtualPlant f [ "D0", "2" ]

    Assert.Equal<(int * string * string) list>([ 40, "X0", "9" ], writes effects)
    Assert.Equal<(Guid * Status4) list>([ f.Tx.Id, Status4.Going ], starts effects)

[<Fact>]
let ``VirtualPlant late join treats false as active when the output spec is false`` () =
    let f = createFixture ()
    addBinding f "A" ValueSpecTypeIndex.Bool "false" "X0" ValueSpecTypeIndex.Bool "true" |> ignore

    let effects = effectsFor RuntimeMode.VirtualPlant f [ "D0", "false" ]

    Assert.Equal<(int * string * string) list>([ 40, "X0", "True" ], writes effects)
    Assert.Equal<(Guid * Status4) list>([ f.Tx.Id, Status4.Going ], starts effects)

[<Fact>]
let ``VirtualPlant late join accepts serialized typed Bool true output`` () =
    let f = createFixture ()
    addBinding f "A" ValueSpecTypeIndex.Bool "true" "X0" ValueSpecTypeIndex.Bool "true" |> ignore

    let effects = effectsFor RuntimeMode.VirtualPlant f [ "D0", "True" ]

    Assert.Equal<(int * string * string) list>([ 40, "X0", "True" ], writes effects)
    Assert.Equal<(Guid * Status4) list>([ f.Tx.Id, Status4.Going ], starts effects)

[<Fact>]
let ``VirtualPlant late join ignores missing empty null and whitespace snapshot values`` () =
    let f = createFixture ()
    addBinding f "A" ValueSpecTypeIndex.Bool "false" "X0" ValueSpecTypeIndex.Bool "true" |> ignore

    for values in [ []; [ "D0", "" ]; [ "D0", null ]; [ "D0", " " ] ] do
        Assert.Empty(effectsFor RuntimeMode.VirtualPlant f values)

[<Fact>]
let ``VirtualPlant late join replays valid inactive values as input resets`` () =
    let f = createFixture ()
    addBinding f "A" ValueSpecTypeIndex.Int16 "2" "X0" ValueSpecTypeIndex.Int16 "9" |> ignore

    let effects = effectsFor RuntimeMode.VirtualPlant f [ "D0", "0" ]

    Assert.Empty(starts effects)
    Assert.Equal<(int * string * string) list>([ 0, "X0", "0" ], writes effects)

[<Fact>]
let ``VirtualPlant late join matches a later numeric binding instead of the first address mapping`` () =
    let f = createFixture ()
    addBinding f "First" ValueSpecTypeIndex.Int16 "1" "X0" ValueSpecTypeIndex.Int16 "8" |> ignore
    addBinding f "Second" ValueSpecTypeIndex.Int16 "2" "X1" ValueSpecTypeIndex.Int16 "9" |> ignore

    let effects = effectsFor RuntimeMode.VirtualPlant f [ "D0", "2" ]
    let delayedWrites = writes effects |> List.filter (fun (delay, _, _) -> delay > 0)

    Assert.Equal<(int * string * string) list>([ 40, "X1", "9" ], delayedWrites)
    Assert.Equal<(Guid * Status4) list>([ f.Tx.Id, Status4.Going ], starts effects)

[<Fact>]
let ``VirtualPlant late join preserves distinct matching responses and deduplicates a shared binding`` () =
    let f = createFixture ()
    let _, firstBinding = addBinding f "First" ValueSpecTypeIndex.Int16 "2" "X0" ValueSpecTypeIndex.Int16 "8"
    let sharedCall = f.Store.AddCallWithLinkedApiDefs(f.Work.Id, "Shared", "Invoke", [ f.Api.Id ])
    Assert.Equal(firstBinding, f.Store.Calls.[sharedCall].ApiCalls.[0].Id)
    addBinding f "Second" ValueSpecTypeIndex.Int16 "2" "X1" ValueSpecTypeIndex.Int16 "9" |> ignore

    let effects = effectsFor RuntimeMode.VirtualPlant f [ "D0", "2" ]

    Assert.Equal<(int * string * string) list>([ 40, "X0", "8"; 40, "X1", "9" ], writes effects)
    Assert.Equal<(Guid * Status4) list>([ f.Tx.Id, Status4.Going ], starts effects)

[<Theory>]
[<InlineData("2", "0", Status4.Going)>]
[<InlineData("2", "9", Status4.Finish)>]
[<InlineData("0", "9", Status4.Finish)>]
[<InlineData("0", "0", Status4.Ready)>]
let ``Control bootstrap evaluates numeric output and input specs`` (output: string, input: string, expected: Status4) =
    let f = createFixture ()
    addBinding f "A" ValueSpecTypeIndex.Int16 "2" "X0" ValueSpecTypeIndex.Int16 "9" |> ignore

    Assert.Equal(expected, controlState f [ "D0", output; "X0", input ])

[<Fact>]
let ``Control bootstrap treats false output as active when its spec is false`` () =
    let f = createFixture ()
    addBinding f "A" ValueSpecTypeIndex.Bool "false" "X0" ValueSpecTypeIndex.Bool "true" |> ignore

    Assert.Equal(Status4.Going, controlState f [ "D0", "false"; "X0", "false" ])

[<Fact>]
let ``Control bootstrap treats false input as active when its spec is false`` () =
    let f = createFixture ()
    addBinding f "A" ValueSpecTypeIndex.Bool "true" "X0" ValueSpecTypeIndex.Bool "false" |> ignore

    Assert.Equal(Status4.Finish, controlState f [ "D0", "false"; "X0", "false" ])

[<Fact>]
let ``Control bootstrap retains Ready fallback when snapshot values are unknown`` () =
    let f = createFixture ()
    addBinding f "A" ValueSpecTypeIndex.Bool "false" "X0" ValueSpecTypeIndex.Bool "false" |> ignore

    // Preserve the existing unknown-snapshot fallback; empty data must not match active false specs.
    for values in [ []; [ "D0", ""; "X0", "" ]; [ "D0", null; "X0", null ]; [ "D0", " "; "X0", " " ] ] do
        Assert.Equal(Status4.Ready, controlState f values)

[<Fact>]
let ``Control bootstrap checks all bindings sharing a Tx and infers one device state`` () =
    let f = createFixture ()
    let _, firstBinding = addBinding f "First" ValueSpecTypeIndex.Int16 "1" "X0" ValueSpecTypeIndex.Int16 "8"
    let sharedCall = f.Store.AddCallWithLinkedApiDefs(f.Work.Id, "Shared", "Invoke", [ f.Api.Id ])
    Assert.Equal(firstBinding, f.Store.Calls.[sharedCall].ApiCalls.[0].Id)
    addBinding f "Second" ValueSpecTypeIndex.Int16 "2" "X1" ValueSpecTypeIndex.Int16 "9" |> ignore

    Assert.Equal(Status4.Going, controlState f [ "D0", "2"; "X0", "0"; "X1", "0" ])
    Assert.Equal(Status4.Finish, controlState f [ "D0", "2"; "X0", "0"; "X1", "9" ])

[<Fact>]
let ``Control bootstrap only starts the matching device Work when outputs share an address`` () =
    let f = createFixture ()
    addBinding f "First" ValueSpecTypeIndex.Int16 "1" "X0" ValueSpecTypeIndex.Int16 "8" |> ignore
    let otherTx = addWork f.Store "OtherMove" f.Tx.ParentId
    let otherApi = addApiDef f.Store "OtherMove" f.Api.ParentId
    otherApi.TxGuid <- Some otherTx.Id
    otherApi.RxGuid <- Some otherTx.Id
    let other = { f with Tx = otherTx; Api = otherApi }
    addBinding other "Second" ValueSpecTypeIndex.Int16 "2" "X1" ValueSpecTypeIndex.Int16 "9" |> ignore

    let effects = effectsFor RuntimeMode.Control f [ "D0", "2"; "X0", "0"; "X1", "0" ]
    let states =
        effects
        |> Array.filter (fun effect -> effect.Kind = RuntimeHubEffectKind.ForceWorkState)
        |> Array.map (fun effect -> effect.WorkGuid, effect.State)
        |> Map.ofArray

    Assert.Equal(2, states.Count)
    Assert.Equal(Status4.Ready, states.[f.Tx.Id])
    Assert.Equal(Status4.Going, states.[otherTx.Id])
    Assert.Empty(writes effects)
