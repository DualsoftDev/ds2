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
    let ``CallConditionQueries returns condition types for target call`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store

        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true, None)

        let targetCall =
            Queries.callsOf work.Id store
            |> List.last

        store.AddCallCondition(targetCall.Id, CallConditionType.ComAux)

        let conditionTypes = CallConditionQueries.getCallConditionTypes store targetCall.Id

        Assert.Equal<CallConditionType list>([ CallConditionType.ComAux ], conditionTypes)

    [<Fact>]
    let ``CallConditionQueries finds calls referencing api call id`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store

        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true, None)

        let calls = Queries.callsOf work.Id store
        let sourceCall = calls[0]
        let targetCall = calls[1]
        let sourceApiCall = sourceCall.ApiCalls |> Seq.head

        store.AddCallCondition(targetCall.Id, CallConditionType.ComAux)
        let conditionId =
            store.Calls[targetCall.Id].CallConditions
            |> Seq.head
            |> fun condition -> condition.Id

        store.AddApiCallsToConditionBatch(targetCall.Id, conditionId, [ sourceApiCall.Id ]) |> ignore

        let callRefs = CallConditionQueries.findCallsByApiCallId store sourceApiCall.Id

        Assert.Contains(struct(sourceCall.Id, sourceCall.Name), callRefs)
        Assert.Contains(struct(targetCall.Id, targetCall.Name), callRefs)
