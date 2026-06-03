module Ds2.Store.Editor.Tests.DsStoreRenameDeviceTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// =============================================================================
// 디바이스 / Action 이름 일괄 변경 (cascade rename) — RenameDevice.fs 검증.
//
// §3.1 핵심: store.ApiCalls(=allApiCalls) 에는 Call 본체 ApiCall 만 등록되고,
//   Call.Conditions / Work.Conditions 트리 내 ApiCall 은 같은 Id 의 *독립 인스턴스* 다.
//   따라서 본체만 바뀌고 Condition 이 안 바뀌면 회귀(②가 그 가드).
//
// 픽스처/헬퍼 재활용: TestHelpers (createStore/addProject/addSystem/addFlow/addWork/addApiDef),
//   AddCallWithLinkedApiDefs (Call+본체 ApiCall, ApiDefId 링크 — V10ApiDefMatrixTests 패턴),
//   AddConditionWithApiCalls / AddWorkConditionWithApiCalls (조건 내 ApiCall = DeepCopy 독립
//   인스턴스, 같은 Id — DsStore.ConditionTests 패턴).
// =============================================================================

/// Active Work 1개 + Passive(device) System 1개 + 그 device 의 ApiDef 1개를
/// 참조하는 Call(본체 ApiCall 포함) 1개. 디바이스명/Action명 둘 다 인자로 받는다.
/// 반환: project, deviceSystem, apiDef, call, bodyApiCall.
let private setupDeviceCall (store: DsStore) (deviceName: string) (apiName: string) =
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let flow = addFlow store "F" activeSystem.Id
    let work = addWork store "W" flow.Id
    let deviceSystem = addSystem store deviceName project.Id false
    let apiDef = addApiDef store apiName deviceSystem.Id
    // Call.DevicesAlias = deviceName, Call.ApiName = apiName, 본체 ApiCall.Name = "deviceName.apiName".
    let callId = store.AddCallWithLinkedApiDefs(work.Id, deviceName, apiName, [ apiDef.Id ])
    let call = store.Calls.[callId]
    let bodyApiCall = call.ApiCalls |> Seq.head
    project, deviceSystem, apiDef, call, bodyApiCall


// ① 본체 ApiCall 뒷부분 교체 ──────────────────────────────────────────────
[<Fact>]
let ``RenameDeviceBatch Action 변경은 본체 ApiCall 뒷부분과 소유 Call ApiName 을 교체한다`` () =
    let store = createStore ()
    let _, device, apiDef, call, bodyApiCall = setupDeviceCall store "Robot231a" "LOAD"

    Assert.Equal("Robot231a.LOAD", bodyApiCall.Name)
    Assert.Equal("LOAD", call.ApiName)

    let changed = store.RenameDeviceBatch(device.Id, None, [ apiDef.Id, "GRAB" ])

    Assert.True(changed > 0)
    Assert.Equal("GRAB", store.ApiDefs.[apiDef.Id].Name)
    // 본체 ApiCall.Name 뒷부분 교체 (앞부분 디바이스명 유지).
    let body = store.Calls.[call.Id].ApiCalls |> Seq.head
    Assert.Equal("Robot231a.GRAB", body.Name)
    // 소유 Call.ApiName 교체 (DevicesAlias 유지).
    Assert.Equal("GRAB", store.Calls.[call.Id].ApiName)
    Assert.Equal("Robot231a", store.Calls.[call.Id].DevicesAlias)


// ② Condition 내 ApiCall 교체 (2경로 핵심 — 회귀 가드) ────────────────────
[<Fact>]
let ``RenameDeviceBatch Action 변경은 Call Condition 내 독립 ApiCall 인스턴스도 교체한다`` () =
    let store = createStore ()
    let _, device, apiDef, call, bodyApiCall = setupDeviceCall store "Robot231a" "LOAD"

    // 본체 ApiCall 을 Call.Conditions 트리에 삽입 → DeepCopy + 같은 Id 의 독립 인스턴스(§3.1).
    store.AddConditionWithApiCalls(call.Id, ConditionType.AutoAux, [ bodyApiCall.Id ]) |> ignore
    let condApiCallBefore = store.Calls.[call.Id].Conditions.[0].ApiCalls.[0]
    Assert.Equal(bodyApiCall.Id, condApiCallBefore.Id)               // 같은 Id (독립 인스턴스)
    Assert.True(store.ApiCalls.ContainsKey(condApiCallBefore.Id))    // 본체 Id 는 dict 에 존재
    Assert.False(Object.ReferenceEquals(condApiCallBefore, store.ApiCalls.[condApiCallBefore.Id])) // 그러나 별개 객체
    Assert.Equal("Robot231a.LOAD", condApiCallBefore.Name)

    store.RenameDeviceBatch(device.Id, None, [ apiDef.Id, "GRAB" ]) |> ignore

    // 본체 ApiCall 교체 확인.
    Assert.Equal("Robot231a.GRAB", (store.Calls.[call.Id].ApiCalls |> Seq.head).Name)
    // ★ Condition 내 독립 ApiCall 인스턴스도 교체되어야 함 (안 바뀌면 회귀 실패).
    let condApiCallAfter = store.Calls.[call.Id].Conditions.[0].ApiCalls.[0]
    Assert.Equal("Robot231a.GRAB", condApiCallAfter.Name)


// ②-b Work.Conditions 경로도 동일하게 교체 (디바이스명 변경, 앞부분) ──────
[<Fact>]
let ``RenameDeviceBatch 디바이스명 변경은 Work Condition 내 독립 ApiCall 앞부분도 교체한다`` () =
    let store = createStore ()
    let _, device, _, call, bodyApiCall = setupDeviceCall store "Robot231a" "LOAD"
    // 소유 Work 를 찾아 그 Conditions 에 본체 ApiCall 삽입 (독립 인스턴스).
    let work = store.Works.Values |> Seq.find (fun w -> w.LocalName = "W")
    store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipAction, [ bodyApiCall.Id ]) |> ignore
    Assert.Equal("Robot231a.LOAD", store.Works.[work.Id].Conditions.[0].ApiCalls.[0].Name)

    store.RenameDeviceBatch(device.Id, Some "Cobot999", []) |> ignore

    // 본체 + Work Condition 내 ApiCall 앞부분(디바이스명) 교체.
    Assert.Equal("Cobot999.LOAD", (store.Calls.[call.Id].ApiCalls |> Seq.head).Name)
    Assert.Equal("Cobot999.LOAD", store.Works.[work.Id].Conditions.[0].ApiCalls.[0].Name)


// ③ multi-device DevicesAlias 조건부 (다른 디바이스 대표명 오염 없음) ──────
[<Fact>]
let ``RenameDeviceBatch 디바이스명 변경은 옛 디바이스명과 일치하는 Call alias 만 교체한다`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let flow = addFlow store "F" activeSystem.Id
    let work = addWork store "W" flow.Id

    // 디바이스 2개 — 각자 ApiDef 보유.
    let dev1 = addSystem store "Dev1" project.Id false
    let dev2 = addSystem store "Dev2" project.Id false
    let apiDef1 = addApiDef store "ADV" dev1.Id
    let apiDef2 = addApiDef store "MOVE" dev2.Id
    let call1Id = store.AddCallWithLinkedApiDefs(work.Id, "Dev1", "ADV", [ apiDef1.Id ])
    let call2Id = store.AddCallWithLinkedApiDefs(work.Id, "Dev2", "MOVE", [ apiDef2.Id ])

    // Dev1 만 rename.
    store.RenameDeviceBatch(dev1.Id, Some "Dev1New", []) |> ignore

    // Dev1 의 Call: DevicesAlias + 본체 ApiCall 앞부분 교체.
    Assert.Equal("Dev1New", store.Calls.[call1Id].DevicesAlias)
    Assert.Equal("Dev1New.ADV", (store.Calls.[call1Id].ApiCalls |> Seq.head).Name)
    Assert.Equal("Dev1New", store.Systems.[dev1.Id].Name)
    // Dev2 의 Call: 오염 없음 (대표명/ApiCall 유지).
    Assert.Equal("Dev2", store.Calls.[call2Id].DevicesAlias)
    Assert.Equal("Dev2.MOVE", (store.Calls.[call2Id].ApiCalls |> Seq.head).Name)
    Assert.Equal("Dev2", store.Systems.[dev2.Id].Name)


// ④ Tx=Rx 동일 Work distinct (1회만 교체, 예외/중복 없음) ──────────────────
[<Fact>]
let ``RenameDeviceBatch Action 변경은 Tx=Rx 동일 Work LocalName 을 1회만 교체한다`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let activeFlow = addFlow store "F" activeSystem.Id
    let activeWork = addWork store "W" activeFlow.Id

    let device = addSystem store "Robot" project.Id false
    let deviceFlow = addFlow store "DeviceFlow" device.Id
    // 내부 Work.LocalName = "ADV" (Action 명과 동일) — Tx, Rx 둘 다 이 Work 를 가리킴.
    let deviceWork = addWork store "ADV" deviceFlow.Id
    let apiDef = addApiDef store "ADV" device.Id
    apiDef.TxGuid <- Some deviceWork.Id
    apiDef.RxGuid <- Some deviceWork.Id

    store.AddCallWithLinkedApiDefs(activeWork.Id, "Robot", "ADV", [ apiDef.Id ]) |> ignore

    // preview 의 Works 가 distinct (Tx=Rx 동일 Work → 1개 항목).
    let preview = store.CollectRenameImpact(device.Id, None, [ apiDef.Id, "MOVE" ])
    Assert.Equal(1, preview.Works.Length)

    // 실제 적용 — 예외 없이 1회 교체.
    let changed = store.RenameDeviceBatch(device.Id, None, [ apiDef.Id, "MOVE" ])
    Assert.True(changed > 0)
    Assert.Equal("MOVE", store.Works.[deviceWork.Id].LocalName)


// ⑤ 중복명 거부 (invalidOp) ────────────────────────────────────────────────
[<Fact>]
let ``RenameDeviceBatch 는 Action 변경 후 같은 System 내 이름 충돌 시 invalidOp 를 던진다`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let flow = addFlow store "F" activeSystem.Id
    let work = addWork store "W" flow.Id

    let device = addSystem store "Robot" project.Id false
    let apiDefAdv = addApiDef store "ADV" device.Id
    let apiDefMove = addApiDef store "MOVE" device.Id
    store.AddCallWithLinkedApiDefs(work.Id, "Robot", "MOVE", [ apiDefMove.Id ]) |> ignore

    // "MOVE" → "ADV" : 같은 device System 에 이미 "ADV" ApiDef 존재 → 충돌.
    Assert.Throws<InvalidOperationException>(fun () ->
        store.RenameDeviceBatch(device.Id, None, [ apiDefMove.Id, "ADV" ]) |> ignore)
    |> ignore

    // 충돌 차단 — 트랜잭션 미진입 (이름 미변경).
    Assert.Equal("MOVE", store.ApiDefs.[apiDefMove.Id].Name)
    Assert.Equal("ADV", store.ApiDefs.[apiDefAdv.Id].Name)


// ⑤-b 디바이스명 충돌 거부 (System 유일성) ─────────────────────────────────
[<Fact>]
let ``RenameDeviceBatch 는 디바이스명 변경 후 프로젝트 내 System 이름 충돌 시 invalidOp 를 던진다`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let flow = addFlow store "F" activeSystem.Id
    let work = addWork store "W" flow.Id

    let dev1 = addSystem store "Dev1" project.Id false
    let _dev2 = addSystem store "Dev2" project.Id false
    let apiDef1 = addApiDef store "ADV" dev1.Id
    store.AddCallWithLinkedApiDefs(work.Id, "Dev1", "ADV", [ apiDef1.Id ]) |> ignore

    // Dev1 → "Dev2" : 프로젝트 내 이미 "Dev2" System 존재 → 충돌.
    Assert.Throws<InvalidOperationException>(fun () ->
        store.RenameDeviceBatch(dev1.Id, Some "Dev2", []) |> ignore)
    |> ignore

    Assert.Equal("Dev1", store.Systems.[dev1.Id].Name)


// ⑥ Undo 1단계 복원 (System/ApiDef/ApiCall/Call/Work + Condition 전부) ─────
[<Fact>]
let ``RenameDeviceBatch 는 Undo 1회로 본체와 Condition 의 모든 변경을 원복한다`` () =
    let store = createStore ()
    let project = addProject store "P"
    let activeSystem = addSystem store "Active" project.Id true
    let flow = addFlow store "F" activeSystem.Id
    let work = addWork store "W" flow.Id

    let device = addSystem store "Robot231a" project.Id false
    let deviceFlow = addFlow store "DeviceFlow" device.Id
    let deviceWork = addWork store "LOAD" deviceFlow.Id
    let apiDef = addApiDef store "LOAD" device.Id
    apiDef.TxGuid <- Some deviceWork.Id

    let callId = store.AddCallWithLinkedApiDefs(work.Id, "Robot231a", "LOAD", [ apiDef.Id ])
    let call = store.Calls.[callId]
    let bodyApiCall = call.ApiCalls |> Seq.head
    // Condition 내 독립 ApiCall (Call + Work 양쪽).
    store.AddConditionWithApiCalls(call.Id, ConditionType.AutoAux, [ bodyApiCall.Id ]) |> ignore
    store.AddWorkConditionWithApiCalls(work.Id, ConditionType.SkipAction, [ bodyApiCall.Id ]) |> ignore

    // 디바이스명 + Action 동시 변경 (전체 cascade 경로 커버).
    store.RenameDeviceBatch(device.Id, Some "Cobot999", [ apiDef.Id, "GRAB" ]) |> ignore

    // 변경 확인 (sanity).
    Assert.Equal("Cobot999", store.Systems.[device.Id].Name)
    Assert.Equal("GRAB", store.ApiDefs.[apiDef.Id].Name)
    Assert.Equal("GRAB", store.Works.[deviceWork.Id].LocalName)
    Assert.Equal("Cobot999.GRAB", (store.Calls.[callId].ApiCalls |> Seq.head).Name)
    Assert.Equal("Cobot999", store.Calls.[callId].DevicesAlias)
    Assert.Equal("GRAB", store.Calls.[callId].ApiName)
    Assert.Equal("Cobot999.GRAB", store.Calls.[callId].Conditions.[0].ApiCalls.[0].Name)
    Assert.Equal("Cobot999.GRAB", store.Works.[work.Id].Conditions.[0].ApiCalls.[0].Name)

    // ★ Undo 1회 — 모든 변경 원복.
    store.Undo() |> ignore

    Assert.Equal("Robot231a", store.Systems.[device.Id].Name)
    Assert.Equal("LOAD", store.ApiDefs.[apiDef.Id].Name)
    Assert.Equal("LOAD", store.Works.[deviceWork.Id].LocalName)
    Assert.Equal("Robot231a.LOAD", (store.Calls.[callId].ApiCalls |> Seq.head).Name)
    Assert.Equal("Robot231a", store.Calls.[callId].DevicesAlias)
    Assert.Equal("LOAD", store.Calls.[callId].ApiName)
    Assert.Equal("Robot231a.LOAD", store.Calls.[callId].Conditions.[0].ApiCalls.[0].Name)
    Assert.Equal("Robot231a.LOAD", store.Works.[work.Id].Conditions.[0].ApiCalls.[0].Name)


// ⑦ no-op (동일 이름 / 빈 cascade) → 0 반환, 이름 불변 ─────────────────────────
[<Fact>]
let ``RenameDeviceBatch 는 실질 변경이 없으면 0 을 반환하고 이름을 바꾸지 않는다`` () =
    let store = createStore ()
    let _, device, apiDef, _, _ = setupDeviceCall store "Robot231a" "LOAD"

    // 같은 이름으로 rename (실질 변경 0) — preview/apply 둘 다 0.
    Assert.Equal(0, store.CollectRenameImpact(device.Id, Some "Robot231a", [ apiDef.Id, "LOAD" ]).TotalCount)
    let changed = store.RenameDeviceBatch(device.Id, Some "Robot231a", [ apiDef.Id, "LOAD" ])

    Assert.Equal(0, changed)
    Assert.Equal("Robot231a", store.Systems.[device.Id].Name)
    Assert.Equal("LOAD", store.ApiDefs.[apiDef.Id].Name)


// ⑧ splitApiCallName 순수함수 — 첫 '.' 분리 / 점 없음·null → None ───────────────
[<Fact>]
let ``splitApiCallName 은 첫 점 기준 분리하고 점이 없거나 null 이면 None`` () =
    Assert.Equal(Some("Robot231a", "LOAD"), Queries.splitApiCallName "Robot231a.LOAD")
    Assert.Equal(Some("A", "B.C"), Queries.splitApiCallName "A.B.C")   // 첫 점 기준(앞=A)
    Assert.Equal(None, Queries.splitApiCallName "__inverter__")        // dummy/raw 심볼(점 없음)
    Assert.Equal(None, Queries.splitApiCallName null)


// ⑨ device-only rename 은 ApiName(뒷부분)/ApiDef 를 건드리지 않는다 (negative) ───
[<Fact>]
let ``RenameDeviceBatch 디바이스명만 변경 시 ApiName 과 ApiDef 이름은 불변이다`` () =
    let store = createStore ()
    let _, device, apiDef, call, _ = setupDeviceCall store "Robot231a" "LOAD"

    store.RenameDeviceBatch(device.Id, Some "Cobot999", []) |> ignore

    // 앞부분(디바이스명)만 교체, 뒷부분(ApiName)·ApiDef 불변.
    Assert.Equal("Cobot999.LOAD", (store.Calls.[call.Id].ApiCalls |> Seq.head).Name)
    Assert.Equal("LOAD", store.Calls.[call.Id].ApiName)
    Assert.Equal("Cobot999", store.Calls.[call.Id].DevicesAlias)
    Assert.Equal("LOAD", store.ApiDefs.[apiDef.Id].Name)
