module Ds2.Store.Editor.Tests.StoreQueryOwnerTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module HierarchyQueryTests =

    [<Fact>]
    let ``StoreHierarchyQueries finds api defs by name from passive systems`` () =
        let store = createStore ()
        let project = addProject store "Project"
        let passiveSystem = addSystem store "PassiveSystem" project.Id false
        let expected = addApiDef store "DeviceApi" passiveSystem.Id
        addApiDef store "OtherApi" passiveSystem.Id |> ignore

        let matches = StoreHierarchyQueries.findApiDefs store "Device"

        let matchItem = Assert.Single(matches)
        Assert.Equal(expected.Id, matchItem.ApiDefId)
        Assert.Equal(passiveSystem.Id, matchItem.SystemId)

module CallConditionQueryTests =

    [<Fact>]
    let ``ConditionQueries returns condition types for target call`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store

        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true, None)

        let targetCall =
            Queries.callsOf work.Id store
            |> List.last

        store.AddCallCondition(targetCall.Id, ConditionType.ComAux)

        let conditionTypes = ConditionQueries.getCallConditionTypes store targetCall.Id

        Assert.Equal<ConditionType list>([ ConditionType.ComAux ], conditionTypes)

    [<Fact>]
    let ``ConditionQueries finds calls referencing api call id`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store

        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true, None)

        let calls = Queries.callsOf work.Id store
        let sourceCall = calls[0]
        let targetCall = calls[1]
        let sourceApiCall = sourceCall.ApiCalls[0]

        store.AddCallCondition(targetCall.Id, ConditionType.ComAux)
        let conditionId =
            store.Calls[targetCall.Id].Conditions
            |> Seq.head
            |> fun condition -> condition.Id

        store.AddApiCallsToConditionBatch(targetCall.Id, conditionId, seq { sourceApiCall.Id }) |> ignore

        let callRefs = ConditionQueries.findCallsByApiCallId store sourceApiCall.Id

        Assert.Contains(struct(sourceCall.Id, sourceCall.Name), callRefs)
        Assert.Contains(struct(targetCall.Id, targetCall.Name), callRefs)

module SystemClosureQueryTests =

    [<Fact>]
    let ``systemClosureOf includes referenced device systems and excludes unrelated ones`` () =
        let store = createStore ()
        let project = addProject store "Project"
        let system1 = addSystem store "System1" project.Id true
        let system2 = addSystem store "System2" project.Id true
        let device = addSystem store "Device" project.Id false
        let flow1 = addFlow store "Flow1" system1.Id
        let work1 = addWork store "Work1" flow1.Id
        let flow2 = addFlow store "Flow2" system2.Id
        addWork store "Work2" flow2.Id |> ignore
        let apiDef = addApiDef store "ADV" device.Id
        store.AddCallWithLinkedApiDefs(work1.Id, "Device", "ADV", [ apiDef.Id ]) |> ignore

        // System1 폐포 = 자신 + ApiCall→ApiDef 로 참조하는 device. 무관한 System2 는 제외.
        let closure1 = Queries.systemClosureOf system1.Id store
        Assert.True(closure1.Contains system1.Id)
        Assert.True(closure1.Contains device.Id)
        Assert.False(closure1.Contains system2.Id)

        // 참조가 없는 System2 폐포 = 자기 자신뿐.
        Assert.Equal<Set<System.Guid>>(Set.ofList [ system2.Id ], Queries.systemClosureOf system2.Id store)

module PlcAddressQueryTests =

    /// Work 에 device call 하나 만들고 ApiCall 의 Out/In 주소를 지정.
    let private addCallWithTags (store: DsStore) (workId: System.Guid) (device: string) (api: string) (outAddr: string) (inAddr: string) =
        let callId =
            store.AddCallWithMultipleDevicesResolved(
                EntityKind.Work, workId, workId, device, api, [ device ], true, None)
        let apiCall = store.Calls.[callId].ApiCalls.[0]
        apiCall.OutTag <- Some (IOTag("Out", outAddr, ""))
        apiCall.InTag  <- Some (IOTag("In",  inAddr,  ""))
        callId

    [<Fact>]
    let ``plcAddressesOfSystem returns only addresses owned by that system`` () =
        let store = createStore ()
        let project = addProject store "Project"
        let system1 = addSystem store "System1" project.Id true
        let system2 = addSystem store "System2" project.Id true
        let flow1 = addFlow store "Flow1" system1.Id
        let flow2 = addFlow store "Flow2" system2.Id
        let work1 = addWork store "Work1" flow1.Id
        let work2 = addWork store "Work2" flow2.Id
        addCallWithTags store work1.Id "Cyl_1" "ADV" "%QX0.0.1" "%IX0.0.1" |> ignore
        addCallWithTags store work2.Id "Cyl_2" "ADV" "%QX0.1.1" "%IX0.1.1" |> ignore

        Assert.Equal<string list>([ "%QX0.0.1"; "%IX0.0.1" ], Queries.plcAddressesOfSystem system1.Id store)
        Assert.Equal<string list>([ "%QX0.1.1"; "%IX0.1.1" ], Queries.plcAddressesOfSystem system2.Id store)

    [<Fact>]
    let ``plcAddressesOfSystem dedups repeated addresses and skips empty ones`` () =
        let store = createStore ()
        let _, system, _, work = setupBasicHierarchy store
        addCallWithTags store work.Id "Cyl_1" "ADV" "%QX0.0.1" "%IX0.0.1" |> ignore
        // 같은 Out 주소 재사용 + In 주소 비움 → dedup 되고 빈 값은 제외.
        addCallWithTags store work.Id "Cyl_2" "ADV" "%QX0.0.1" "" |> ignore

        Assert.Equal<string list>(
            [ "%QX0.0.1"; "%IX0.0.1" ],
            Queries.plcAddressesOfSystem system.Id store)
