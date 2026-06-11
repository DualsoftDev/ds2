module DeviceDurationQueryTests

// tryGetDeviceDurationMs — critical path + 디바이스 자원(mutex) 직렬화 하한.
// Call arrow 없이 병렬로 보이는 Call 들도 같은 디바이스 자원(Reset 계열 인터락으로
// 묶인 Device Work 그룹)을 쓰면 엔진이 직렬 실행하므로 duration 합이 하한이 된다.

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

let private addDeviceWork (store: DsStore) name flowId durationMs =
    let work = addWork store name flowId
    work.Duration <- Some(TimeSpan.FromMilliseconds(float (durationMs: int)))
    work

let private addDeviceApiDef (store: DsStore) name systemId (rxWork: Work) =
    let apiDef = addApiDef store name systemId
    apiDef.TxGuid <- Some rxWork.Id
    apiDef.RxGuid <- Some rxWork.Id
    apiDef

[<Fact>]
let ``tryGetDeviceDurationMs serializes parallel calls sharing one device resource`` () =
    let store = createStore ()
    let project, _, _, work = setupBasicHierarchy store

    // 디바이스: ADV ↔ RET (ResetReset 인터락 = 같은 실린더, 동시 실행 불가)
    let deviceSystem = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
    let adv = addDeviceWork store "ADV" deviceFlow.Id 500
    let ret = addDeviceWork store "RET" deviceFlow.Id 500
    store.ConnectSelectionInOrder([ adv.Id; ret.Id ], ArrowType.ResetReset) |> ignore

    let advDef = addDeviceApiDef store "ADV" deviceSystem.Id adv
    let retDef = addDeviceApiDef store "RET" deviceSystem.Id ret

    // Call arrow 없음 — critical path 는 병렬(max 500)로 보지만 실제는 mutex 직렬 1000
    store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ advDef.Id ]) |> ignore
    store.AddCallWithLinkedApiDefs(work.Id, "Device", "RET", [ retDef.Id ]) |> ignore

    Assert.Equal(Some 1000, Queries.tryGetDeviceDurationMs work.Id store)

[<Fact>]
let ``tryGetDeviceDurationMs keeps parallel max for independent device resources`` () =
    let store = createStore ()
    let project, _, _, work = setupBasicHierarchy store

    // 인터락 없는 독립 디바이스 Work 두 개 — 병렬 가능, max = 500
    let deviceSystem = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
    let w1 = addDeviceWork store "Op1" deviceFlow.Id 500
    let w2 = addDeviceWork store "Op2" deviceFlow.Id 500

    let def1 = addDeviceApiDef store "Op1" deviceSystem.Id w1
    let def2 = addDeviceApiDef store "Op2" deviceSystem.Id w2

    store.AddCallWithLinkedApiDefs(work.Id, "Device", "Op1", [ def1.Id ]) |> ignore
    store.AddCallWithLinkedApiDefs(work.Id, "Device", "Op2", [ def2.Id ]) |> ignore

    Assert.Equal(Some 500, Queries.tryGetDeviceDurationMs work.Id store)

[<Fact>]
let ``tryGetDeviceDurationMs critical path dominates when longer than resource floor`` () =
    let store = createStore ()
    let project, _, _, work = setupBasicHierarchy store

    // 디바이스 A(ADV↔RET 인터락, 각 500) + 독립 디바이스 B(2000)
    let deviceSystem = addSystem store "Device" project.Id false
    let deviceFlow = addFlow store "DeviceFlow" deviceSystem.Id
    let adv = addDeviceWork store "ADV" deviceFlow.Id 500
    let ret = addDeviceWork store "RET" deviceFlow.Id 500
    let slow = addDeviceWork store "Slow" deviceFlow.Id 2000
    store.ConnectSelectionInOrder([ adv.Id; ret.Id ], ArrowType.ResetReset) |> ignore

    let advDef = addDeviceApiDef store "ADV" deviceSystem.Id adv
    let retDef = addDeviceApiDef store "RET" deviceSystem.Id ret
    let slowDef = addDeviceApiDef store "Slow" deviceSystem.Id slow

    let c1 = store.AddCallWithLinkedApiDefs(work.Id, "Device", "ADV", [ advDef.Id ])
    let c2 = store.AddCallWithLinkedApiDefs(work.Id, "Device", "RET", [ retDef.Id ])
    let c3 = store.AddCallWithLinkedApiDefs(work.Id, "Device", "Slow", [ slowDef.Id ])

    // Call 직렬 체인: ADV → RET → Slow = 3000 (critical path) > 자원 하한(ADV+RET=1000)
    store.ConnectSelectionInOrder([ c1; c2; c3 ], ArrowType.Start) |> ignore

    Assert.Equal(Some 3000, Queries.tryGetDeviceDurationMs work.Id store)
