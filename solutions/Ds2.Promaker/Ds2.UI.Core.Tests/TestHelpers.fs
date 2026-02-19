module Ds2.UI.Core.Tests.TestHelpers

open Ds2.Core
open Ds2.UI.Core

let createApi () =
    let store = DsStore.empty()
    let api = EditorApi(store)
    (store, api)

let setupProjectSystemFlow () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let flow = api.AddFlow("F1", system.Id)
    (store, api, project, system, flow)
