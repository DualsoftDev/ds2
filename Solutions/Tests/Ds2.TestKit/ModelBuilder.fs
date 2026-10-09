/// 편집기(Ds2.Editor) 없이 테스트 모델을 만드는 빌더.
///
/// Editor 의 AddProject/AddSystem/… 은 Undo 트랜잭션·변경 추적·이벤트로 감싼 편집 연산이다. 엔진·수집기·AASX 테스트는
/// 그 포장이 아니라 "store 에 엔티티가 들어 있는 상태" 만 필요하므로, Core 의 DirectWrite 로 바로 넣는다
/// (AASX 임포터가 store 를 채우는 방식과 같다). 반환은 엔티티 자신 — 테스트가 Id 와 속성을 바로 만진다.
module Ds2.TestKit.ModelBuilder

open System
open System.Threading
open Ds2.Core
open Ds2.Core.Store

let createStore () = DsStore()

let addProject (store: DsStore) (name: string) : Project =
    let project = Project(name)
    store.DirectWrite(store.Projects, project)
    project

/// isActive=true 면 Project.ActiveSystemIds, 아니면 PassiveSystemIds(Device 시스템)에 등록한다.
let addSystem (store: DsStore) (name: string) (projectId: Guid) (isActive: bool) : DsSystem =
    let system = DsSystem(name)
    store.DirectWrite(store.Systems, system)
    let project = store.Projects.[projectId]
    (if isActive then project.ActiveSystemIds else project.PassiveSystemIds).Add system.Id
    system

let addFlow (store: DsStore) (name: string) (systemId: Guid) : Flow =
    let flow = Flow(name, systemId)
    store.DirectWrite(store.Flows, flow)
    flow

/// Work 이름은 "Flow.Local" — FlowPrefix 는 소속 Flow 이름이다.
let addWork (store: DsStore) (localName: string) (flowId: Guid) : Work =
    let flow = store.Flows.[flowId]
    let work = Work(flow.Name, localName, flowId)
    store.DirectWrite(store.Works, work)
    work

let addApiDef (store: DsStore) (name: string) (systemId: Guid) : ApiDef =
    let apiDef = ApiDef(name, systemId)
    store.DirectWrite(store.ApiDefs, apiDef)
    apiDef

/// Work 에 Call(devicesAlias.apiName)을 만들고 ApiDef 마다 ApiCall 을 연결한다.
/// 같은 ApiDef 에 이미 연결된 ApiCall 이 있으면 그것을 공유한다(참조 Call) — 편집기와 같은 규칙.
let addCallWithLinkedApiDefs
    (store: DsStore) (workId: Guid) (devicesAlias: string) (apiName: string) (apiDefIds: Guid seq) : Guid =
    let call = Call(devicesAlias, apiName, workId)
    store.DirectWrite(store.Calls, call)
    let originFlowId = Queries.getWork workId store |> Option.map (fun w -> w.ParentId)
    for apiDefId in apiDefIds do
        let existing = store.ApiCalls.Values |> Seq.tryFind (fun ac -> ac.ApiDefId = Some apiDefId)
        let apiCall =
            match existing with
            | Some ac -> Some ac
            | None ->
                Queries.getApiDef apiDefId store
                |> Option.map (fun apiDef ->
                    let ac = ApiCall($"{devicesAlias}.{apiDef.Name}")
                    ac.ApiDefId <- Some apiDefId
                    ac.OriginFlowId <- originFlowId
                    store.DirectWrite(store.ApiCalls, ac)
                    ac)
        apiCall |> Option.iter call.ApiCalls.Add
    call.Id

/// 기본 프로젝트 + active 시스템 + 플로우 + 워크 구성.
let setupBasicHierarchy (store: DsStore) =
    let project = addProject store "TestProject"
    let system = addSystem store "TestSystem" project.Id true
    let flow = addFlow store "TestFlow" system.Id
    let work = addWork store "TestWork" flow.Id
    project, system, flow, work

let waitUntil (timeoutMs: int) predicate =
    SpinWait.SpinUntil(Func<bool>(predicate), timeoutMs)
