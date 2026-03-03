module Ds2.UI.Core.Tests.TestHelpers

open Ds2.Core
open Ds2.UI.Core

let createApi () =
    let store = DsStore.empty()
    let api = EditorApi(store)
    (store, api)

let setupProjectSystemFlow () =
    let store, api = createApi()
    let projId  = api.Nodes.AddProjectAndGetId("P1")
    let sysId   = api.Nodes.AddSystemAndGetId("S1", projId, true)
    let flowId  = api.Nodes.AddFlowAndGetId("F1", sysId)
    let project = store.Projects.[projId]
    let system  = store.Systems.[sysId]
    let flow    = store.Flows.[flowId]
    (store, api, project, system, flow)

// ── 테스트 픽스처 헬퍼 ──────────────────────────────────────────────────────
// EditorApi.Nodes 서브-API를 호출한 뒤 store에서 실제 엔티티를 반환한다.

let addProject (store: DsStore) (api: EditorApi) name =
    let id = api.Nodes.AddProjectAndGetId(name)
    store.Projects.[id]

let addSystem (store: DsStore) (api: EditorApi) name projectId isActive =
    let id = api.Nodes.AddSystemAndGetId(name, projectId, isActive)
    store.Systems.[id]

let addFlow (store: DsStore) (api: EditorApi) name systemId =
    let id = api.Nodes.AddFlowAndGetId(name, systemId)
    store.Flows.[id]

let addWork (store: DsStore) (api: EditorApi) name flowId =
    let id = api.Nodes.AddWorkAndGetId(name, flowId)
    store.Works.[id]

let addCall (store: DsStore) (api: EditorApi) workId devAlias apiName apiDefIds =
    let id = api.Nodes.AddCallWithLinkedApiDefsAndGetId workId devAlias apiName apiDefIds
    store.Calls.[id]

let addApiDef (store: DsStore) (api: EditorApi) name systemId =
    let id = api.Nodes.AddApiDefAndGetId(name, systemId)
    store.ApiDefs.[id]
