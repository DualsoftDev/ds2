module Ds2.Store.Editor.Tests.DsStoreAdvancedPanelBatchTests

open System
open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module PanelTimingTests =

    [<Fact>]
    let ``UpdateWorkPeriodMs sets and gets period as int`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        store.UpdateWorkPeriodMs(work.Id, Some 500)
        let result = store.GetWorkPeriodMs(work.Id)
        Assert.True(result.IsSome)
        Assert.Equal(500, result.Value)

    [<Fact>]
    let ``UpdateWorkPeriodMs with None clears period`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        store.UpdateWorkPeriodMs(work.Id, Some 1000)
        store.UpdateWorkPeriodMs(work.Id, None)
        let result = store.GetWorkPeriodMs(work.Id)
        Assert.True(result.IsNone)

    [<Fact>]
    let ``UpdateCallTimeoutMs sets and gets timeout as int`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let callId = store.Calls |> Seq.head |> fun kv -> kv.Key
        store.UpdateCallTimeoutMs(callId, Some 3000)
        let result = store.GetCallTimeoutMs(callId)
        Assert.True(result.IsSome)
        Assert.Equal(3000, result.Value)

    [<Fact>]
    let ``UpdateCallTimeoutMs with None clears timeout`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let callId = store.Calls |> Seq.head |> fun kv -> kv.Key
        store.UpdateCallTimeoutMs(callId, Some 5000)
        store.UpdateCallTimeoutMs(callId, None)
        let result = store.GetCallTimeoutMs(callId)
        Assert.True(result.IsNone)

module PanelTokenRoleTests =

    [<Fact>]
    let ``UpdateWorkTokenRole sets role and supports undo`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        Assert.Equal(TokenRole.None, store.Works[work.Id].TokenRole)

        store.UpdateWorkTokenRole(work.Id, TokenRole.Source)
        Assert.Equal(TokenRole.Source, store.Works[work.Id].TokenRole)

        store.Undo()
        Assert.Equal(TokenRole.None, store.Works[work.Id].TokenRole)

        store.Redo()
        Assert.Equal(TokenRole.Source, store.Works[work.Id].TokenRole)

    [<Fact>]
    let ``UpdateWorkTokenRole changes between all roles`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store

        store.UpdateWorkTokenRole(work.Id, TokenRole.Source)
        Assert.Equal(TokenRole.Source, store.Works[work.Id].TokenRole)

        store.UpdateWorkTokenRole(work.Id, TokenRole.Ignore)
        Assert.Equal(TokenRole.Ignore, store.Works[work.Id].TokenRole)

        store.UpdateWorkTokenRole(work.Id, TokenRole.None)
        Assert.Equal(TokenRole.None, store.Works[work.Id].TokenRole)

    [<Fact>]
    let ``UpdateWorkTokenRole emits one WorkPropsChanged event`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        let mutable count = 0

        store.ObserveEvents().Add(fun evt ->
            match evt with
            | WorkPropsChanged id when id = work.Id -> count <- count + 1
            | _ -> ())

        store.UpdateWorkTokenRole(work.Id, TokenRole.Source)
        Assert.Equal(1, count)

module DsQueryTests =

    [<Fact>]
    let ``flowsOf returns flows under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let _ = store.AddFlow("F2", system.Id)
        let flows = Queries.flowsOf system.Id store
        Assert.Equal(2, flows.Length)

    [<Fact>]
    let ``worksOf returns works under flow`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        let _ = store.AddWork("W2", flow.Id)
        let works = Queries.worksOf flow.Id store
        Assert.Equal(2, works.Length)

    [<Fact>]
    let ``callsOf returns calls under work`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.A"; "Dev.B" ], true, None)
        let calls = Queries.callsOf work.Id store
        Assert.Equal(2, calls.Length)

    [<Fact>]
    let ``trySystemIdOfWork resolves Work → Flow → System`` () =
        let store = createStore ()
        let _, system, _, work = setupBasicHierarchy store
        let result = Queries.trySystemIdOfWork work.Id store
        Assert.Equal(Some system.Id, result)

    [<Fact>]
    let ``tryGetName resolves entity names`` () =
        let store = createStore ()
        let _, system, flow, work = setupBasicHierarchy store
        Assert.Equal(Some "TestSystem", Queries.tryGetName store EntityKind.System system.Id)
        Assert.Equal(Some "TestFlow", Queries.tryGetName store EntityKind.Flow flow.Id)
        Assert.Equal(Some "TestFlow.TestWork", Queries.tryGetName store EntityKind.Work work.Id)
        Assert.Equal(None, Queries.tryGetName store EntityKind.Work (Guid.NewGuid()))

    [<Fact>]
    let ``buttonsOf returns HwButtons under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let btn = HwButton("Btn1", system.Id)
        store.HwButtons.[btn.Id] <- btn
        let buttons = Queries.buttonsOf system.Id store
        Assert.Equal(1, buttons.Length)
        Assert.Equal("Btn1", buttons.[0].Name)

    [<Fact>]
    let ``lampsOf returns HwLamps under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let lamp = HwLamp("Lamp1", system.Id)
        store.HwLamps.[lamp.Id] <- lamp
        let lamps = Queries.lampsOf system.Id store
        Assert.Equal(1, lamps.Length)
        Assert.Equal("Lamp1", lamps.[0].Name)

    [<Fact>]
    let ``conditionsOf returns HwConditions under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let cond = HwCondition("Cond1", system.Id)
        store.HwConditions.[cond.Id] <- cond
        let conditions = Queries.conditionsOf system.Id store
        Assert.Equal(1, conditions.Length)

    [<Fact>]
    let ``actionsOf returns HwActions under system`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let action = HwAction("Act1", system.Id)
        store.HwActions.[action.Id] <- action
        let actions = Queries.actionsOf system.Id store
        Assert.Equal(1, actions.Length)

    [<Fact>]
    let ``arrowWorksOf returns arrows under system`` () =
        let store = createStore ()
        let _, system, flow, work = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([| work.Id; work2.Id |], ArrowType.Start) |> ignore
        let arrows = Queries.arrowWorksOf system.Id store
        Assert.True(arrows.Length >= 1)

    [<Fact>]
    let ``tryFindConditionRec finds nested condition`` () =
        let child = CallCondition()
        let parent = CallCondition()
        parent.Children.Add(child)
        let result = Queries.tryFindConditionRec [parent] child.Id
        Assert.True(result.IsSome)
        Assert.Equal(child.Id, result.Value.Id)

module BatchTests =

    [<Fact>]
    let ``GetAllWorkDurationRows returns works with period`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "Flow1" system.Id
        let work = addWork store "Work1" flow.Id
        store.UpdateWorkPeriodMs(work.Id, Nullable<int>(5000))

        let rows = store.GetAllWorkDurationRows()
        Assert.Equal(1, rows.Length)
        Assert.Equal(work.Id, rows.[0].WorkId)
        Assert.Equal("Flow1", rows.[0].FlowName)
        Assert.Equal("Work1", rows.[0].WorkName)
        Assert.Equal(5000, rows.[0].PeriodMs)

    [<Fact>]
    let ``UpdateWorkDurationsBatch changes work periods and supports undo`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "F" system.Id
        let work1 = addWork store "W1" flow.Id
        let work2 = addWork store "W2" flow.Id

        store.UpdateWorkDurationsBatch([ struct(work1.Id, 3000); struct(work2.Id, 7000) ])

        let p1 = work1.Properties.Duration
        Assert.True(p1.IsSome)
        Assert.Equal(3000.0, p1.Value.TotalMilliseconds)
        let p2 = work2.Properties.Duration
        Assert.True(p2.IsSome)
        Assert.Equal(7000.0, p2.Value.TotalMilliseconds)

        store.Undo()
        Assert.True(store.Works.[work1.Id].Properties.Duration.IsNone)
        Assert.True(store.Works.[work2.Id].Properties.Duration.IsNone)

    [<Fact>]
    let ``GetAllApiCallIORows returns apiCalls with IO tags`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let activeSystem = addSystem store "A" project.Id true
        let flow = addFlow store "Flow1" activeSystem.Id
        let work = addWork store "Work1" flow.Id
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let call = store.Calls.Values |> Seq.head
        let apiDef = addApiDef store "Api1" system.Id
        let apiCallId = store.AddApiCallFromPanel(call.Id, apiDef.Id, "", "outAddr", "", "inAddr", 0, "", 0, "")

        let rows = store.GetAllApiCallIORows()
        Assert.True(rows.Length >= 1)
        let row = rows |> List.find (fun r -> r.ApiCallId = apiCallId)
        Assert.Equal("Flow1", row.FlowName)
        Assert.Equal("outAddr", row.OutAddress)
        Assert.Equal("inAddr", row.InAddress)

    [<Fact>]
    let ``UpdateApiCallIOTagsBatch changes IO tags and supports undo`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let activeSystem = addSystem store "A" project.Id true
        let flow = addFlow store "F" activeSystem.Id
        let work = addWork store "W" flow.Id
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let call = store.Calls.Values |> Seq.head
        let apiDef = addApiDef store "Api1" system.Id
        let apiCallId = store.AddApiCallFromPanel(call.Id, apiDef.Id, "", "", "", "", 0, "", 0, "")

        store.UpdateApiCallIOTagsBatch([ struct(apiCallId, IOTag("inSym", "newIn", ""), IOTag("outSym", "newOut", "")) ])

        let apiCall = store.ApiCalls.[apiCallId]
        Assert.True(apiCall.InTag.IsSome)
        Assert.Equal("newIn", apiCall.InTag.Value.Address)
        Assert.Equal("inSym", apiCall.InTag.Value.Name)
        Assert.True(apiCall.OutTag.IsSome)
        Assert.Equal("newOut", apiCall.OutTag.Value.Address)
        Assert.Equal("outSym", apiCall.OutTag.Value.Name)

        store.Undo()
        let reverted = store.ApiCalls.[apiCallId]
        Assert.True(reverted.InTag.IsNone)
        Assert.True(reverted.OutTag.IsNone)

    [<Fact>]
    let ``UpdateApiCallIOTagsBatch followed by SaveToFile works`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id false
        let activeSystem = addSystem store "A" project.Id true
        let flow = addFlow store "F" activeSystem.Id
        let work = addWork store "W" flow.Id
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let call = store.Calls.Values |> Seq.head
        let apiDef = addApiDef store "Api1" system.Id
        let apiCallId = store.AddApiCallFromPanel(call.Id, apiDef.Id, "", "", "", "", 0, "", 0, "")

        store.UpdateApiCallIOTagsBatch([ struct(apiCallId, IOTag("InSensor", "192.168.0.1", ""), IOTag("OutActuator", "192.168.0.2", "")) ])

        let tmpPath = System.IO.Path.GetTempFileName()
        try
            store.SaveToFile(tmpPath)
            let loaded = DsStore()
            loaded.LoadFromFile(tmpPath)

            let loadedApiCall = loaded.ApiCalls.[apiCallId]
            Assert.True(loadedApiCall.InTag.IsSome)
            Assert.Equal("192.168.0.1", loadedApiCall.InTag.Value.Address)
            Assert.Equal("InSensor", loadedApiCall.InTag.Value.Name)
            Assert.True(loadedApiCall.OutTag.IsSome)
            Assert.Equal("192.168.0.2", loadedApiCall.OutTag.Value.Address)
            Assert.Equal("OutActuator", loadedApiCall.OutTag.Value.Name)

            let loadedCall = loaded.Calls.Values |> Seq.head
            let callApiCall = loadedCall.ApiCalls |> Seq.find (fun ac -> ac.Id = apiCallId)
            Assert.True(callApiCall.InTag.IsSome, "call.ApiCalls 내부의 InTag이 비어있음 — RewireApiCallReferences 누락")
            Assert.Equal("192.168.0.1", callApiCall.InTag.Value.Address)

            let ioRows = loaded.GetAllApiCallIORows()
            let row = ioRows |> List.find (fun r -> r.ApiCallId = apiCallId)
            Assert.Equal("192.168.0.1", row.InAddress)
            Assert.Equal("InSensor", row.InSymbol)
            Assert.Equal("192.168.0.2", row.OutAddress)
            Assert.Equal("OutActuator", row.OutSymbol)
        finally
            System.IO.File.Delete(tmpPath)

    [<Fact>]
    let ``UpdateWorkDurationsBatch followed by SaveToFile works`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "F" system.Id
        let work = addWork store "W" flow.Id

        store.UpdateWorkDurationsBatch([ struct(work.Id, 2000) ])

        let tmpPath = System.IO.Path.GetTempFileName()
        try
            store.SaveToFile(tmpPath)
            let loaded = DsStore()
            loaded.LoadFromFile(tmpPath)
            let loadedWork = loaded.Works.Values |> Seq.head
            Assert.True(loadedWork.Properties.Duration.IsSome)
            Assert.Equal(2000.0, loadedWork.Properties.Duration.Value.TotalMilliseconds)
        finally
            System.IO.File.Delete(tmpPath)

    [<Fact>]
    let ``UpdateWorkPeriodsBatch updates multiple works and supports undo`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "F" system.Id
        let work1 = addWork store "W1" flow.Id
        let work2 = addWork store "W2" flow.Id

        store.UpdateWorkPeriodsBatch([
            struct(work1.Id, Nullable<int>(1200))
            struct(work2.Id, Nullable<int>(3400))
        ])

        Assert.Equal(1200.0, store.Works.[work1.Id].Properties.Duration.Value.TotalMilliseconds)
        Assert.Equal(3400.0, store.Works.[work2.Id].Properties.Duration.Value.TotalMilliseconds)

        store.Undo()
        Assert.True(store.Works.[work1.Id].Properties.Duration.IsNone)
        Assert.True(store.Works.[work2.Id].Properties.Duration.IsNone)

    [<Fact>]
    let ``UpdateWorkTokenRolesBatch updates multiple works and supports undo`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "F" system.Id
        let work1 = addWork store "W1" flow.Id
        let work2 = addWork store "W2" flow.Id

        store.UpdateWorkTokenRolesBatch([
            struct(work1.Id, TokenRole.Source ||| TokenRole.Ignore)
            struct(work2.Id, TokenRole.Source ||| TokenRole.Ignore)
        ])

        Assert.Equal(TokenRole.Source ||| TokenRole.Ignore, store.Works.[work1.Id].TokenRole)
        Assert.Equal(TokenRole.Source ||| TokenRole.Ignore, store.Works.[work2.Id].TokenRole)

        store.Undo()
        Assert.Equal(TokenRole.None, store.Works.[work1.Id].TokenRole)
        Assert.Equal(TokenRole.None, store.Works.[work2.Id].TokenRole)

    [<Fact>]
    let ``UpdateCallTimeoutsBatch updates multiple calls and supports undo`` () =
        let store = createStore ()
        let project = addProject store "P"
        let _ = addSystem store "Device" project.Id false
        let activeSystem = addSystem store "Active" project.Id true
        let flow = addFlow store "F" activeSystem.Id
        let work1 = addWork store "W1" flow.Id
        let work2 = addWork store "W2" flow.Id

        store.AddCallsWithDevice(project.Id, work1.Id, [ "Dev.Api1" ], true, None)
        store.AddCallsWithDevice(project.Id, work2.Id, [ "Dev.Api2" ], true, None)

        let call1 = Queries.callsOf work1.Id store |> List.head
        let call2 = Queries.callsOf work2.Id store |> List.head

        store.UpdateCallTimeoutsBatch([
            struct(call1.Id, Nullable<int>(1500))
            struct(call2.Id, Nullable<int>(2600))
        ])

        Assert.Equal(1500.0, store.Calls.[call1.Id].Properties.Timeout.Value.TotalMilliseconds)
        Assert.Equal(2600.0, store.Calls.[call2.Id].Properties.Timeout.Value.TotalMilliseconds)

        store.Undo()
        Assert.True(store.Calls.[call1.Id].Properties.Timeout.IsNone)
        Assert.True(store.Calls.[call2.Id].Properties.Timeout.IsNone)
