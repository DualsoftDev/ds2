module Ds2.Store.Editor.Tests.TestHelpers

open System
open System.Threading
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Core

let createStore () = DsStore()

let addProject (store: DsStore) name =
    let id = store.AddProject(name)
    store.Projects.[id]

let addSystem (store: DsStore) name projectId isActive =
    let id = store.AddSystem(name, projectId, isActive)
    store.Systems.[id]

let addFlow (store: DsStore) name systemId =
    let id = store.AddFlow(name, systemId)
    store.Flows.[id]

let addWork (store: DsStore) localName flowId =
    let id = store.AddWork(localName, flowId)
    store.Works.[id]

let addApiDef (store: DsStore) name systemId =
    let id = store.AddApiDefWithProperties(name, systemId)
    store.ApiDefs.[id]

/// 기본 프로젝트 + 시스템 + 플로우 + 워크 구성
let setupBasicHierarchy (store: DsStore) =
    let project = addProject store "TestProject"
    let system = addSystem store "TestSystem" project.Id true
    let flow = addFlow store "TestFlow" system.Id
    let work = addWork store "TestWork" flow.Id
    project, system, flow, work

let waitUntil (timeoutMs: int) predicate =
    SpinWait.SpinUntil(Func<bool>(predicate), timeoutMs)
