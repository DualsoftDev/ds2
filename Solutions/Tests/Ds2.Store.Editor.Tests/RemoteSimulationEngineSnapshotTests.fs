module Ds2.Store.Editor.Tests.RemoteSimulationEngineSnapshotTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Backend.Common
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.Model
open Ds2.Runtime.Remote
open Ds2.Store.Editor.Tests.TestHelpers

let private snapshot workStates callStates currentTimeMs =
    { SessionId = "session"
      ModelHash = "model"
      Generation = 1
      Mode = "Monitoring"
      StatusName = "Running"
      StatusValue = 0
      ClockMs = currentTimeMs
      CurrentTimeMs = currentTimeMs
      NextEventTimeMs = Nullable<int64>()
      WorkStates = workStates
      CallStates = callStates
      FlowStates = [||]
      IOValues = [||]
      HasStartableWork = false
      HasActiveDuration = false
      IsHomingPhase = false
      TimestampUtc = DateTime.UnixEpoch }

let private buildIndexWithCall () =
    let store = createStore ()
    let _, system, _, work = setupBasicHierarchy store
    let apiDef = addApiDef store "Api1" system.Id
    store.AddCallWithLinkedApiDefs(work.Id, "Dev", "Api1", [ apiDef.Id ]) |> ignore
    let call = Queries.callsOf work.Id store |> List.head
    let index = SimIndex.build store 10
    index, work.Id, call.Id

[<Fact>]
let ``remote snapshot diff emits changed work and call states`` () =
    let index, workId, callId = buildIndexWithCall ()
    let previous = SimState.create index.TickMs index.AllWorkGuids index.AllCallGuids index.AllFlowGuids
    let snap =
        snapshot
            [| { Id = string workId; StatusName = string Status4.Going; StatusValue = int Status4.Going } |]
            [| { Id = string callId; StatusName = string Status4.Finish; StatusValue = int Status4.Finish } |]
            1234L

    let workChange = RemoteSnapshotDiff.workChanges index previous snap |> Assert.Single
    let callChange = RemoteSnapshotDiff.callChanges index previous snap |> Assert.Single

    Assert.Equal(workId, workChange.WorkGuid)
    Assert.Equal(Status4.Ready, workChange.PreviousState)
    Assert.Equal(Status4.Going, workChange.NewState)
    Assert.Equal(TimeSpan.FromMilliseconds(1234.0), workChange.Clock)
    Assert.Equal(callId, callChange.CallGuid)
    Assert.Equal(Status4.Ready, callChange.PreviousState)
    Assert.Equal(Status4.Finish, callChange.NewState)

[<Fact>]
let ``remote snapshot diff suppresses unchanged states`` () =
    let index, workId, callId = buildIndexWithCall ()
    let previous =
        SimState.create index.TickMs index.AllWorkGuids index.AllCallGuids index.AllFlowGuids
        |> SimState.setWorkState workId Status4.Going
        |> SimState.setCallState callId Status4.Finish
    let snap =
        snapshot
            [| { Id = string workId; StatusName = string Status4.Going; StatusValue = int Status4.Going } |]
            [| { Id = string callId; StatusName = string Status4.Finish; StatusValue = int Status4.Finish } |]
            5678L

    Assert.Empty(RemoteSnapshotDiff.workChanges index previous snap)
    Assert.Empty(RemoteSnapshotDiff.callChanges index previous snap)
