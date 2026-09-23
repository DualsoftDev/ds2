module Ds2.Store.Editor.Tests.StoreIndexParityTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// StoreHierarchyIndex 는 Queries 의 전수 스캔을 그대로 대체한다. 대체가 성립하려면 내용뿐
// 아니라 **순서까지** 같아야 한다 — 트리·캔버스의 노드 순서는 사용자에게 그대로 보이고,
// AASX export 에서는 이 순서가 곧 파일 안 요소 순서가 된다. 순서가 틀어지면 모델은 그대로인데
// 저장할 때마다 다른 바이트가 나오는 파일이 된다.

/// System 두 개를 번갈아 채운다 — 부모별로 모으면서 원본 열거 순서를 잃는 구현을 잡기 위함.
let private buildInterleavedModel () =
    let store = createStore ()
    let project = addProject store "P"
    let sysA = addSystem store "A" project.Id true
    let sysB = addSystem store "B" project.Id true

    let flows =
        [ addFlow store "A1" sysA.Id
          addFlow store "B1" sysB.Id
          addFlow store "A2" sysA.Id
          addFlow store "B2" sysB.Id ]

    for flow in flows do
        for i in 1 .. 2 do
            let work = addWork store (sprintf "w%d" i) flow.Id
            store.AddCallsWithDevice(
                project.Id, work.Id, [ sprintf "dev%d.on" i; sprintf "dev%d.off" i ], true, None)

    // Work 화살표(System 소유) 와 Call 화살표(Work 소유) 를 둘 다 만든다.
    for flow in flows do
        let workIds = Queries.worksOf flow.Id store |> List.map (fun w -> w.Id)
        store.ConnectSelectionInOrder(workIds, ArrowType.StartReset) |> ignore
        for workId in workIds do
            let callIds = Queries.callsOf workId store |> List.map (fun c -> c.Id)
            // Call 화살표가 허용하는 종류는 Start / Group 뿐이다(EntityKindRules).
            store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore

    addApiDef store "apiA1" sysA.Id |> ignore
    addApiDef store "apiB1" sysB.Id |> ignore
    addApiDef store "apiA2" sysA.Id |> ignore
    store

[<Fact>]
let ``Hierarchy index matches the full-scan queries, order included`` () =
    let store = buildInterleavedModel ()

    // 축이 비어 있으면 아래 비교가 전부 공허하게 통과한다.
    let counts =
        sprintf "flow=%d work=%d call=%d apiDef=%d arrowWork=%d arrowCall=%d"
            store.FlowsReadOnly.Count store.WorksReadOnly.Count store.CallsReadOnly.Count
            store.ApiDefsReadOnly.Count store.ArrowWorksReadOnly.Count store.ArrowCallsReadOnly.Count
    Assert.True(
        store.FlowsReadOnly.Count > 0 && store.WorksReadOnly.Count > 0
        && store.CallsReadOnly.Count > 0 && store.ApiDefsReadOnly.Count > 0
        && store.ArrowWorksReadOnly.Count > 0 && store.ArrowCallsReadOnly.Count > 0,
        "비교할 축이 비어 있으면 이 테스트는 아무것도 지키지 못한다 — " + counts)

    let index = buildHierarchyIndex store

    for sys in store.SystemsReadOnly.Values do
        Assert.Equal<Flow list>(Queries.flowsOf sys.Id store, index.Flows sys.Id)
        Assert.Equal<ApiDef list>(Queries.apiDefsOf sys.Id store, index.ApiDefs sys.Id)
        Assert.Equal<ArrowBetweenWorks list>(Queries.arrowWorksOf sys.Id store, index.ArrowWorks sys.Id)

    for flow in store.FlowsReadOnly.Values do
        Assert.Equal<Work list>(Queries.worksOf flow.Id store, index.Works flow.Id)

    for work in store.WorksReadOnly.Values do
        Assert.Equal<Call list>(Queries.callsOf work.Id store, index.Calls work.Id)
        Assert.Equal<ArrowBetweenCalls list>(Queries.arrowCallsOf work.Id store, index.ArrowCalls work.Id)

[<Fact>]
let ``Unknown parents yield empty, matching the full-scan queries`` () =
    let store = buildInterleavedModel ()
    let index = buildHierarchyIndex store
    let stranger = System.Guid.NewGuid()

    Assert.Empty(index.Flows stranger)
    Assert.Empty(index.Works stranger)
    Assert.Empty(index.Calls stranger)
    Assert.Empty(index.ApiDefs stranger)
    Assert.Empty(index.ArrowWorks stranger)
    Assert.Empty(index.ArrowCalls stranger)

[<Fact>]
let ``Index is a snapshot — edits made after a axis was read are not visible`` () =
    let store = createStore ()
    let project = addProject store "P"
    let system = addSystem store "S" project.Id true
    let flow = addFlow store "F" system.Id

    let index = buildHierarchyIndex store
    Assert.Empty(index.Works flow.Id)   // Lazy 를 여기서 확정시킨다

    addWork store "w" flow.Id |> ignore

    // 만든 뒤의 변경은 반영되지 않는다. 인덱스를 쥔 채 store 를 고치는 코드는 이 성질 때문에
    // 틀린 답을 얻는다 — 그래서 수명이 한 작업인 지역 인덱스로만 쓴다.
    Assert.Empty(index.Works flow.Id)
    Assert.Single(Queries.worksOf flow.Id store) |> ignore
