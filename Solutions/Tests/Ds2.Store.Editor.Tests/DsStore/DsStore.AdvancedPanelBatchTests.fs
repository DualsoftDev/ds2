module Ds2.Store.Editor.Tests.DsStoreAdvancedPanelBatchTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
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
    let ``arrowWorksOf returns arrows under system`` () =
        let store = createStore ()
        let _, system, flow, work = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([| work.Id; work2.Id |], ArrowType.Start) |> ignore
        let arrows = Queries.arrowWorksOf system.Id store
        Assert.True(arrows.Length >= 1)

    [<Fact>]
    let ``tryFindConditionRec finds nested condition`` () =
        let child = Condition()
        let parent = Condition()
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
    let ``GetAllWorkDurationRows classifies rows by explorer tree ownership`` () =
        let store = createStore ()
        let project = addProject store "P"
        let activeSystem = addSystem store "ControlSystem" project.Id true
        let passiveSystem = addSystem store "DeviceSystem" project.Id false
        let controlFlow = addFlow store "ControlFlow" activeSystem.Id
        let deviceFlow = addFlow store "DeviceFlow" passiveSystem.Id
        let controlWork = addWork store "ControlWork" controlFlow.Id
        let deviceWork = addWork store "DeviceWork" deviceFlow.Id

        store.AddCallsWithDevice(project.Id, controlWork.Id, [ "Dev.Api" ], true, None)
        let controlCall = Queries.callsOf controlWork.Id store |> List.head
        let apiDef = addApiDef store "Api1" passiveSystem.Id
        store.AddApiCallFromPanel(controlCall.Id, apiDef.Id, "", "", "", "", 0, "", 0, "") |> ignore

        let rows = store.GetAllWorkDurationRows()
        let controlRow = rows |> List.find (fun row -> row.WorkId = controlWork.Id)
        let deviceRow = rows |> List.find (fun row -> row.WorkId = deviceWork.Id)

        Assert.False(controlRow.IsDeviceWork)
        Assert.Equal("ControlSystem", controlRow.SystemName)
        Assert.Equal("ControlFlow", controlRow.FlowName)

        Assert.True(deviceRow.IsDeviceWork)
        Assert.Equal("DeviceSystem", deviceRow.SystemName)
        Assert.Equal("DeviceFlow", deviceRow.FlowName)

    [<Fact>]
    let ``UpdateWorkDurationsBatch changes work periods and supports undo`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        let flow = addFlow store "F" system.Id
        let work1 = addWork store "W1" flow.Id
        let work2 = addWork store "W2" flow.Id

        store.UpdateWorkDurationsBatch([ struct(work1.Id, 3000); struct(work2.Id, 7000) ])

        let p1 = store.Works.[work1.Id].Duration
        Assert.True(p1.IsSome)
        Assert.Equal(3000.0, p1.Value.TotalMilliseconds)
        let p2 = store.Works.[work2.Id].Duration
        Assert.True(p2.IsSome)
        Assert.Equal(7000.0, p2.Value.TotalMilliseconds)

        store.Undo()
        // Undo 후 Duration이 None이어야 함
        let w1AfterUndo = store.Works.[work1.Id]
        let w2AfterUndo = store.Works.[work2.Id]
        Assert.True(w1AfterUndo.Duration.IsNone)
        Assert.True(w2AfterUndo.Duration.IsNone)

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

        // Verify Duration is set before saving
        Assert.True(work.Duration.IsSome, "Work should have Duration before save")

        let tmpPath = "C:\\Temp\\test_work_duration.json"
        System.IO.Directory.CreateDirectory("C:\\Temp") |> ignore
        store.SaveToFile(tmpPath)

        let loaded = DsStore()
        loaded.LoadFromFile(tmpPath)
        let loadedWork = loaded.Works.Values |> Seq.head

        // Detailed assertions
        if loadedWork.Duration.IsNone then
            failwith $"Loaded work has no Duration. File saved to: {tmpPath}"

        Assert.Equal(2000.0, loadedWork.Duration.Value.TotalMilliseconds)

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

        Assert.Equal(1200.0, store.Works.[work1.Id].Duration.Value.TotalMilliseconds)
        Assert.Equal(3400.0, store.Works.[work2.Id].Duration.Value.TotalMilliseconds)

        store.Undo()
        // Undo 후 Duration이 None이어야 함
        let w1AfterUndo = store.Works.[work1.Id]
        let w2AfterUndo = store.Works.[work2.Id]
        Assert.True(w1AfterUndo.Duration.IsNone)
        Assert.True(w2AfterUndo.Duration.IsNone)

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

        Assert.Equal(1500.0, store.Calls.[call1.Id].GetSimulationProperties().Value.Timeout.Value.TotalMilliseconds)
        Assert.Equal(2600.0, store.Calls.[call2.Id].GetSimulationProperties().Value.Timeout.Value.TotalMilliseconds)

        store.Undo()
        // Undo 후 SimulationProperties가 None이거나 Timeout이 None이어야 함
        let c1AfterUndo = store.Calls.[call1.Id]
        let c2AfterUndo = store.Calls.[call2.Id]
        Assert.True(c1AfterUndo.GetSimulationProperties().IsNone || c1AfterUndo.GetSimulationProperties().Value.Timeout.IsNone)
        Assert.True(c2AfterUndo.GetSimulationProperties().IsNone || c2AfterUndo.GetSimulationProperties().Value.Timeout.IsNone)

/// Symbol Import Wizard 등 import 경로 — Call 생성 + ApiCall.OutTag/InTag 1 transaction 회귀.
/// 이전 코드 (`AddCallsWithDevice` 만 사용) 는 OutTag/InTag 가 None 으로 저장되어
/// AASX export 시 DSPilot V10 위반 발생. AddCallsWithDeviceAndTags 가 그 회귀의 핵심 수정.
module WizardApplyTests =

    let private tag name addr = IOTag(name, addr, "")
    // F# 에서 IOTag 는 AllowNullLiteral 없으므로 Unchecked.defaultof 로 null 전달 (C# null 통과 시뮬레이션).
    let private nullTag : IOTag = Unchecked.defaultof<IOTag>

    [<Fact>]
    let ``AddCallsWithDeviceAndTags — ApiCall.OutTag/InTag 가 plan 값으로 채워짐`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let entries : System.Collections.Generic.IReadOnlyList<struct (string * IOTag * IOTag)> =
            [|
                struct("Dev.Api1", tag "Out1" "Y0", tag "In1" "X0")
                struct("Dev.Api2", tag "Out2" "Y1", tag "In2" "X1")
            |] :> _
        let applied = store.AddCallsWithDeviceAndTags(project.Id, work.Id, entries, true, None)
        Assert.Equal(2, applied)

        let calls = Queries.callsOf work.Id store
        Assert.Equal(2, calls.Length)
        for call in calls do
            Assert.True(call.ApiCalls.Count > 0, sprintf "Call %s 에 ApiCall 없음" call.Name)
            let apiCall = call.ApiCalls.[0]
            Assert.True(apiCall.OutTag.IsSome, sprintf "ApiCall %s OutTag None" apiCall.Name)
            Assert.True(apiCall.InTag.IsSome, sprintf "ApiCall %s InTag None" apiCall.Name)

    [<Fact>]
    let ``AddCallsWithDeviceAndTags — null IOTag 는 None 으로 저장`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let entries : System.Collections.Generic.IReadOnlyList<struct (string * IOTag * IOTag)> =
            [|
                struct("Dev.Api1", tag "Out1" "Y0", nullTag)  // InTag null
                struct("Dev.Api2", nullTag, tag "In2" "X1")    // OutTag null
            |] :> _
        store.AddCallsWithDeviceAndTags(project.Id, work.Id, entries, true, None) |> ignore

        let calls = Queries.callsOf work.Id store |> List.sortBy (fun c -> c.Name)
        let api1 = calls.[0].ApiCalls.[0]
        let api2 = calls.[1].ApiCalls.[0]
        Assert.True(api1.OutTag.IsSome); Assert.True(api1.InTag.IsNone)
        Assert.True(api2.OutTag.IsNone); Assert.True(api2.InTag.IsSome)

    [<Fact>]
    let ``AddCallsWithDeviceAndTags — 1 transaction Undo (Call+Tag 동시 사라짐)`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let entries : System.Collections.Generic.IReadOnlyList<struct (string * IOTag * IOTag)> =
            [| struct("Dev.Api1", tag "Out1" "Y0", tag "In1" "X0") |] :> _
        store.AddCallsWithDeviceAndTags(project.Id, work.Id, entries, true, None) |> ignore
        Assert.Equal(1, (Queries.callsOf work.Id store).Length)

        store.Undo()
        // 1 transaction 이면 Call 자체가 사라지므로 0건 — Tag 만 따로 사라지는 동작은 회귀.
        Assert.Equal(0, (Queries.callsOf work.Id store).Length)

    [<Fact>]
    let ``AddCallsWithDeviceAndTags — 빈 entries 는 no-op (예외 없음, applied=0)`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        let entries : System.Collections.Generic.IReadOnlyList<struct (string * IOTag * IOTag)> =
            [||] :> _
        let applied = store.AddCallsWithDeviceAndTags(project.Id, work.Id, entries, true, None)
        Assert.Equal(0, applied)
        Assert.Equal(0, (Queries.callsOf work.Id store).Length)

    // V10 V1/V2 통합 회귀 — ModelGenerator 가 빈 페어를 placeholder IOTag 로 채우는 운영 검증된 패턴.
    // 이전 흐름: 짝 없는 ApiCall 의 OutTag/InTag = None → AASX 에 null 박제 → DSPilot V10 V1/V2 위반.
    // 새 흐름: placeholder IOTag (address="") 로 채워서 V10 룰 통과. ApiDef ActionType/SensingType 은 Real 유지 → V4 안 건드림.
    // (참고: DSPilot fix_aasx_v10.py 가 같은 변환을 post-process 로 수행하던 패턴을 ModelGenerator 단계에서 내재화)
    [<Fact>]
    let ``placeholder pattern + 위저드 apply → V10ValidationBatch V1/V2 위반 0건`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        // 출력 전용 (lamp/buzzer 류) 케이스 시뮬레이션 — InTag 자리에 placeholder.
        let placeholderIn = IOTag("(unset-IN)", "", "v10 placeholder")
        let placeholderOut = IOTag("(unset-OUT)", "", "v10 placeholder")
        let entries : System.Collections.Generic.IReadOnlyList<struct (string * IOTag * IOTag)> =
            [|
                struct("Dev.Real",     tag "Out_real" "Y0", tag "In_real" "X0")    // 정상 페어
                struct("Dev.LampOnly", tag "Out_lamp" "Y10", placeholderIn)        // 출력 전용 (Tower.Red 류)
                struct("Dev.SensorOnly", placeholderOut, tag "In_sensor" "X20")    // 입력 전용
            |] :> _
        store.AddCallsWithDeviceAndTags(project.Id, work.Id, entries, true, None) |> ignore

        let issues = Ds2.Core.Store.V10ValidationBatch.validateStore store
        let v1v2 =
            issues
            |> List.filter (fun i -> i.Rule = "V1" || i.Rule = "V2")
        Assert.True(
            v1v2.IsEmpty,
            sprintf "placeholder 채움에도 V1/V2 위반 %d 건: %s"
                v1v2.Length
                (v1v2 |> List.map (fun i -> sprintf "[%s] %s" i.Rule i.Message) |> String.concat "; "))
