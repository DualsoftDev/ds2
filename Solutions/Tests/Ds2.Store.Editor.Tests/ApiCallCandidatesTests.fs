module Ds2.Store.Editor.Tests.ApiCallCandidatesTests

open System
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers
open Xunit

let private setupCallWithApi (store: Ds2.Core.Store.DsStore) flowId workName callName devName apiDefId =
    let work = addWork store workName flowId
    let callId = store.AddCallWithLinkedApiDefs(work.Id, devName, callName, [ apiDefId ])
    work, callId

[<Fact>]
let ``no call returns empty`` () =
    let store = createStore ()
    let candidates = ApiCallCandidates.collect store (Guid.NewGuid())
    Assert.Empty(candidates)

[<Fact>]
let ``call alone yields its own ApiCalls under '현재 Call'`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let flow = addFlow store "F" activeSystem.Id

    let deviceSystem = addSystem store "Dev" project.Id false
    let deviceFlow = addFlow store "DF" deviceSystem.Id
    let _ = addWork store "ADV" deviceFlow.Id
    let apiDef = addApiDef store "ADV" deviceSystem.Id

    let _, callId = setupCallWithApi store flow.Id "W1" "ADV" "Dev" apiDef.Id

    let candidates = ApiCallCandidates.collect store callId
    Assert.Equal(1, candidates.Length)
    Assert.Equal("Dev.ADV", candidates.[0].Name)
    Assert.Equal("현재 Call", candidates.[0].GroupLabel)

[<Fact>]
let ``other calls in same flow appear under 'Flow 내' group`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let flow = addFlow store "F" activeSystem.Id

    let deviceSystem = addSystem store "Dev" project.Id false
    let deviceFlow = addFlow store "DF" deviceSystem.Id
    let _ = addWork store "ADV" deviceFlow.Id
    let _ = addWork store "RET" deviceFlow.Id
    let apiAdv = addApiDef store "ADV" deviceSystem.Id
    let apiRet = addApiDef store "RET" deviceSystem.Id

    let _, callA = setupCallWithApi store flow.Id "W1" "ADV" "Dev" apiAdv.Id
    let _, _ = setupCallWithApi store flow.Id "W2" "RET" "Dev" apiRet.Id

    let candidates = ApiCallCandidates.collect store callA
    Assert.Equal(2, candidates.Length)
    let current = candidates |> List.find (fun c -> c.GroupLabel = "현재 Call")
    let flowOther = candidates |> List.find (fun c -> c.GroupLabel = "Flow 내")
    Assert.Equal("Dev.ADV", current.Name)
    Assert.Contains("Dev.RET", flowOther.Name)

[<Fact>]
let ``calls in different flow are not included`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let flow1 = addFlow store "F1" activeSystem.Id
    let flow2 = addFlow store "F2" activeSystem.Id

    let deviceSystem = addSystem store "Dev" project.Id false
    let deviceFlow = addFlow store "DF" deviceSystem.Id
    let _ = addWork store "ADV" deviceFlow.Id
    let _ = addWork store "RET" deviceFlow.Id
    let apiAdv = addApiDef store "ADV" deviceSystem.Id
    let apiRet = addApiDef store "RET" deviceSystem.Id

    let _, callA = setupCallWithApi store flow1.Id "W1" "ADV" "Dev" apiAdv.Id
    let _, _ = setupCallWithApi store flow2.Id "W2" "RET" "Dev" apiRet.Id

    let candidates = ApiCallCandidates.collect store callA
    // 다른 Flow 의 Call 은 제외 → '현재 Call' 1개만
    Assert.Equal(1, candidates.Length)
    Assert.Equal("현재 Call", candidates.[0].GroupLabel)
