module Ds2.Store.Editor.Tests.ReferenceCallInteropTests

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

open Ds2.Store.Editor.Tests.ControlVirtualPlantTestHarness

[<Fact>]
let ``Reference Call uses its original binding through the real plant echo path`` () =
    let m = model ()
    let original = call m m.Spare "Original"
    bind m original m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0" |> ignore
    let reference = m.Store.AddReferenceCallToWork(original, m.Run.Id)
    use run = new DualRuntime(m.Store)
    Assert.Empty(run.Control.Store.Calls.[reference].ApiCalls)
    run.Begin()
    assertBeforeEcho run reference m.Tx.Id
    assertFinished run reference m.Tx.Id
    Assert.Equal<(string * string) list>([ "D0", "1"; "D0", "0" ], outputs run)
    assertEcho run "X0"

[<Fact>]
let ``filtered IO map resolves a Reference binding outside the filter and retains referencing ownership`` () =
    let m = model ()
    let original = call m m.Spare "Original"
    let binding = bind m original m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0"
    let projectId = (m.Store.Projects.Values |> Seq.exactlyOne).Id
    let otherSystem = addSystem m.Store "OtherCell" projectId true
    let otherFlow = addFlow m.Store "OtherFlow" otherSystem.Id
    let otherWork = addWork m.Store "ReferenceOwner" otherFlow.Id
    let reference = m.Store.AddReferenceCallToWork(original, otherWork.Id)
    let ioMap = Ds2.Runtime.IO.SignalIOMap.buildFiltered m.Store (Some (Set.singleton reference))
    Assert.Empty(m.Store.Calls.[reference].ApiCalls)
    Assert.False(ioMap.CallToMappings.ContainsKey original)
    let mapping = Assert.Single ioMap.Mappings
    Assert.Equal(binding, mapping.ApiCallGuid)
    Assert.Equal(reference, mapping.CallGuid)
    Assert.Equal(Some otherSystem.Id, mapping.SystemId)
    Assert.Equal(Some m.Tx.Id, mapping.TxWorkGuid)
    Assert.Equal(Some m.Tx.Id, mapping.RxWorkGuid)
    Assert.Equal(("D0", "X0"), (mapping.OutAddress, mapping.InAddress))

[<Fact>]
let ``IO map deduplicates repeated binding IDs within each original and Reference placement`` () =
    let m = model ()
    let original = call m m.Spare "Original"
    let binding = bind m original m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0"
    // The public model collection can contain repeated references (for example in
    // an imported model). Deduplication is per Call, not across separate placements.
    m.Store.Calls.[original].ApiCalls.Add(m.Store.ApiCalls.[binding])
    let reference = m.Store.AddReferenceCallToWork(original, m.Run.Id)
    let ioMap = Ds2.Runtime.IO.SignalIOMap.buildFiltered m.Store (Some (Set.ofList [ original; reference ]))
    Assert.Equal(2, ioMap.Mappings.Length)
    for callId in [ original; reference ] do
        let mapping = Assert.Single ioMap.CallToMappings.[callId]
        Assert.Equal(callId, mapping.CallGuid)
        Assert.Equal(binding, mapping.ApiCallGuid)

[<Theory>]
[<InlineData("NormalAppend")>]
[<InlineData("Pulse")>]
[<InlineData("Latch")>]
let ``Reference completion selects the original Normal extension Pulse and Latch reset policy`` (policy: string) =
    let m = model ()
    m.Api.ActionType <-
        match policy with
        | "NormalAppend" -> ActionType.Normal (Some 20)
        | "Pulse" -> ActionType.Pulse (Some 10)
        | "Latch" -> ActionType.Latch
        | _ -> invalidArg "policy" policy
    let original = call m m.Spare "Original"
    bind m original m.Api "D0" ValueSpecTypeIndex.Int16 "1" "X0" |> ignore
    let reference = m.Store.AddReferenceCallToWork(original, m.Run.Id)
    let index = SimIndex.build m.Store 10
    let ioMap = Ds2.Runtime.IO.SignalIOMap.build m.Store
    let scheduler = Ds2.Runtime.Engine.Scheduler.EventScheduler()
    let stateManager = StateManager(index, 10)
    let writeTag: string -> string -> unit = fun _ _ -> failwith "Policy lookup must not write output"
    let applyTransition: Guid -> Status4 -> unit = fun _ _ -> failwith "Policy lookup must not change state"
    // The composition factory is internal; invoke the actual factory without making
    // production internals public or copying its policy selection into the test.
    let factoryType = typeof<EventDrivenEngine>.Assembly.GetType("Ds2.Runtime.Engine.EventDrivenCompositionContext", true)
    let factory = factoryType.GetMethod(
        "createCallTransitionApplyContext",
        System.Reflection.BindingFlags.Static ||| System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)
    Assert.NotNull factory
    let context =
        factory.Invoke(null, [| box RuntimeMode.Control; box index; box ioMap; box scheduler;
                               box writeTag; box stateManager; box applyTransition |])
        :?> CallTransitionApplyContext
    let expected =
        match policy with
        | "NormalAppend" -> [ ResetAfter ("D0", 20, "0") ]
        | _ -> []
    Assert.Equal<CallOutputReset list>(expected, context.GetCallOutputResets original)
    Assert.Equal<CallOutputReset list>(expected, context.GetCallOutputResets reference)
    // Control uses real Task.Delay for delayed output writes. This test verifies the
    // selected reset policy, not wall-clock delivery or virtual-time scheduling.
    // The Normal(None) test above independently covers the complete dual-engine path.
