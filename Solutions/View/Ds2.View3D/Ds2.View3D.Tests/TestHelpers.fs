module Ds2.View3D.Tests.TestHelpers

open System
open Ds2.Core
open Ds2.Store

/// 테스트용 간단한 DsStore 생성
let createTestStore() =
    let store = DsStore()

    // Project 생성
    let project = Project("TestProject")
    store.Projects.Add(project.Id, project)

    // Flow 생성
    let flow1 = Flow("Flow1", project.Id)
    let flow2 = Flow("Flow2", project.Id)
    store.Flows.Add(flow1.Id, flow1)
    store.Flows.Add(flow2.Id, flow2)

    // System 생성
    let robot1 = DsSystem("Robot_RB01")
    let robot1Props = SimulationSystemProperties()
    robot1Props.SystemType <- Some "Robot"
    robot1.SetSimulationProperties(robot1Props)
    store.Systems.Add(robot1.Id, robot1)
    project.ActiveSystemIds.Add(robot1.Id)

    let conveyor1 = DsSystem("Conveyor_CV01")
    let conveyor1Props = SimulationSystemProperties()
    conveyor1Props.SystemType <- Some "Conveyor"
    conveyor1.SetSimulationProperties(conveyor1Props)
    store.Systems.Add(conveyor1.Id, conveyor1)
    project.ActiveSystemIds.Add(conveyor1.Id)

    let cylinder1 = DsSystem("Cylinder_CY01")
    let cylinder1Props = SimulationSystemProperties()
    cylinder1Props.SystemType <- Some "Unit"
    cylinder1.SetSimulationProperties(cylinder1Props)
    store.Systems.Add(cylinder1.Id, cylinder1)
    project.PassiveSystemIds.Add(cylinder1.Id)

    let unknown1 = DsSystem("Unknown_UK01")
    let unknown1Props = SimulationSystemProperties()
    unknown1Props.SystemType <- Some "UNKNOWN_DEVICE"
    unknown1.SetSimulationProperties(unknown1Props)
    store.Systems.Add(unknown1.Id, unknown1)
    project.PassiveSystemIds.Add(unknown1.Id)

    // ApiDef 생성
    let apiDef1 = ApiDef("Pick", robot1.Id)
    store.ApiDefs.Add(apiDef1.Id, apiDef1)

    let apiDef2 = ApiDef("Place", robot1.Id)
    store.ApiDefs.Add(apiDef2.Id, apiDef2)

    let apiDef3 = ApiDef("Move", conveyor1.Id)
    store.ApiDefs.Add(apiDef3.Id, apiDef3)

    let apiDef4 = ApiDef("Extend", cylinder1.Id)
    store.ApiDefs.Add(apiDef4.Id, apiDef4)

    // Work 생성
    let work1 = Work("Flow1", "Work1", flow1.Id)
    store.Works.Add(work1.Id, work1)

    let work2 = Work("Flow1", "Work2", flow1.Id)
    store.Works.Add(work2.Id, work2)

    let work3 = Work("Flow2", "Work1", flow2.Id)
    store.Works.Add(work3.Id, work3)

    // Call 생성 (ApiCall → ApiDef → System 체인)
    let call1 = Call("", "", work1.Id)
    let apiCall1 = ApiCall("")
    apiCall1.ApiDefId <- Some apiDef1.Id
    apiCall1.OriginFlowId <- Some flow1.Id
    call1.ApiCalls.Add(apiCall1)
    store.Calls.Add(call1.Id, call1)

    let call2 = Call("", "", work2.Id)
    let apiCall2 = ApiCall("")
    apiCall2.ApiDefId <- Some apiDef3.Id
    apiCall2.OriginFlowId <- Some flow1.Id
    call2.ApiCalls.Add(apiCall2)
    store.Calls.Add(call2.Id, call2)

    let call3 = Call("", "", work3.Id)
    let apiCall3 = ApiCall("")
    apiCall3.ApiDefId <- Some apiDef1.Id
    apiCall3.OriginFlowId <- Some flow2.Id
    call3.ApiCalls.Add(apiCall3)
    store.Calls.Add(call3.Id, call3)

    let call4 = Call("", "", work3.Id)
    let apiCall4 = ApiCall("")
    apiCall4.ApiDefId <- Some apiDef4.Id
    apiCall4.OriginFlowId <- Some flow2.Id
    call4.ApiCalls.Add(apiCall4)
    store.Calls.Add(call4.Id, call4)

    (store, project.Id, robot1.Id, conveyor1.Id, cylinder1.Id, unknown1.Id)

/// 간단한 assertion 헬퍼
let shouldEqual expected actual =
    if expected <> actual then
        failwithf "Expected: %A\nActual: %A" expected actual

let shouldNotBeEmpty (list: 'a list) =
    if list.IsEmpty then
        failwith "List should not be empty"

let shouldContain (item: 'a) (list: 'a list) =
    if not (List.contains item list) then
        failwithf "List should contain: %A" item
