module Ds2.CSV.Tests.StandardCsvTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.CSV
open Ds2.TestKit.ModelBuilder

// 표준 CSV(11열) 내보내기 → 가져오기.
// 예전 라운드트립 검사는 Flow·Work·디바이스 개수만 세어서, 여러 Active System 이 하나로 합쳐지고
// Reference Call 이 일반 Call(유령 디바이스·원본의 두 번째 ApiCall)로 돌아오는 것을 못 잡았다.
// 그래서 개수가 아니라 이름 경로로 펼친 구조 전체를 비교한다.

let private header11 = "Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress,ActiveSystem,ReferenceOf"

let private setTags (store: DsStore) (callId: Guid) (inAddress: string) (outAddress: string) =
    let call = store.Calls.[callId]
    let apiCall = call.ApiCalls.[0]
    apiCall.InTag <- Some (IOTag($"{call.DevicesAlias}_{call.ApiName}_IN", inAddress, ""))
    apiCall.OutTag <- Some (IOTag($"{call.DevicesAlias}_{call.ApiName}_OUT", outAddress, ""))

/// Active System 둘이 같은 이름의 Main.Load 와 Cyl.ADV 를 가진다.
/// Reference 는 같은 Work(LineA Load, LineB Load)와 다른 Work(LineA Unload)에 둔다.
let private buildModel () =
    let store = createStore ()
    let project = addProject store "P"
    let lineA = addSystem store "LineA" project.Id true
    let lineB = addSystem store "LineB" project.Id true

    let cyl = addSystem store "Cyl" project.Id false
    let gripper = addSystem store "Gripper" project.Id false
    let cylB = addSystem store "CylB" project.Id false
    let cylAdv = addApiDef store "ADV" cyl.Id
    let cylRet = addApiDef store "RET" cyl.Id
    let gripperClose = addApiDef store "CLOSE" gripper.Id
    let cylBAdv = addApiDef store "ADV" cylB.Id

    let mainA = addFlow store "Main" lineA.Id
    let loadA = addWork store "Load" mainA.Id
    let unloadA = addWork store "Unload" mainA.Id
    let advA = addCallWithLinkedApiDefs store loadA.Id "Cyl" "ADV" [ cylAdv.Id ]
    let retA = addCallWithLinkedApiDefs store loadA.Id "Cyl" "RET" [ cylRet.Id ]
    addReferenceCall store advA loadA.Id |> ignore
    let closeA = addCallWithLinkedApiDefs store unloadA.Id "Gripper" "CLOSE" [ gripperClose.Id ]
    addReferenceCall store advA unloadA.Id |> ignore

    let mainB = addFlow store "Main" lineB.Id
    let loadB = addWork store "Load" mainB.Id
    let advB = addCallWithLinkedApiDefs store loadB.Id "Cyl" "ADV" [ cylBAdv.Id ]
    addReferenceCall store advB loadB.Id |> ignore

    setTags store advA "%IX0.0" "%QX0.0"
    setTags store retA "%IX0.1" "%QX0.1"
    setTags store closeA "%IX0.2" "%QX0.2"
    setTags store advB "%IX1.0" "%QX1.0"
    store, project

/// 모델을 이름 경로 줄로 펼친다 — Guid 는 가져올 때마다 새로 생기므로 비교할 수 없다.
/// 일반 Call 은 ApiCall 마다 (디바이스 System.ApiDef, 태그)를, Reference 는 가리키는 원본의 경로를 적는다.
let private describe (store: DsStore) (project: Project) : string list =
    let callPath (call: Call) =
        let work = store.Works.[call.ParentId]
        let flow = store.Flows.[work.ParentId]
        $"{store.Systems.[flow.ParentId].Name}/{flow.Name}/{work.LocalName}/{call.Name}"
    let tagText (tag: IOTag option) =
        tag |> Option.map (fun t -> $"{t.Name}@{t.Address}") |> Option.defaultValue "-"
    let apiCallText (apiCall: ApiCall) =
        let target =
            match apiCall.ApiDefId with
            | Some id ->
                let apiDef = store.ApiDefs.[id]
                $"{store.Systems.[apiDef.ParentId].Name}.{apiDef.Name}"
            | None -> "(ApiDef 없음)"
        $"{target} in={tagText apiCall.InTag} out={tagText apiCall.OutTag}"
    let callText (call: Call) =
        match call.ReferenceOf with
        | Some originalId -> $"{callPath call} -> {callPath store.Calls.[originalId]}"
        | None -> $"""{callPath call} [{call.ApiCalls |> Seq.map apiCallText |> Seq.sort |> String.concat " | "}]"""
    let active = Queries.activeSystemsOf project.Id store
    let names (items: string list) = items |> String.concat ","
    [ yield "Active: " + names (active |> List.map (fun s -> s.Name))
      for device in Queries.passiveSystemsOf project.Id store |> List.sortBy (fun s -> s.Name) do
          yield $"Device {device.Name}: " + names (Queries.apiDefsOf device.Id store |> List.map (fun d -> d.Name) |> List.sort)
      for system in active do
          for flow in Queries.flowsOf system.Id store do
              yield $"{system.Name}/{flow.Name}: " + names (Queries.worksOf flow.Id store |> List.map (fun w -> w.LocalName))
      yield!
          [ for system in active do
              for flow in Queries.flowsOf system.Id store do
                  for work in Queries.worksOf flow.Id store do
                      for call in Queries.callsOf work.Id store do
                          yield callText call ]
          |> List.sort ]

let private load (csv: string) (systemName: string) =
    CsvImporter.parseContent csv
    |> Result.bind (fun document -> CsvImporter.loadProject document "P" systemName)

let private loadOk (csv: string) (systemName: string) =
    match load csv systemName with
    | Error errors -> failwith (String.concat "\n" errors)
    | Ok store -> store, (store.Projects.Values |> Seq.exactlyOne)

[<Fact>]
let ``프로젝트 CSV 를 다시 읽으면 System·Flow 소속·Reference 연결·디바이스가 그대로다`` () =
    let store, project = buildModel ()
    let csv = CsvExporter.projectToCsv store project.Id

    // Reference 행 — 디바이스·API 는 원본 값, 태그·주소는 비우고, ReferenceOf 에 원본 Work.
    Assert.Contains("Main,Unload,Cyl,Cyl,ADV,,,,,LineA,Main.Load", csv)

    let restored, restoredProject = loadOk csv "대화상자System"
    // 모든 행이 ActiveSystem 을 적었으니 대화상자 System 은 생기지 않는다.
    Assert.Equal<string list>(
        [ "LineA"; "LineB" ],
        Queries.activeSystemsOf restoredProject.Id restored |> List.map (fun s -> s.Name))
    Assert.Equal(3, (Queries.passiveSystemsOf restoredProject.Id restored).Length)
    Assert.Equal<string list>(describe store project, describe restored restoredProject)

[<Fact>]
let ``ActiveSystem 이 빈 행만 대화상자 System 으로 간다`` () =
    let csv =
        String.concat "\n" [
            header11
            "Main,Load,Cyl,Cyl,ADV,,%IX0.0,,%QX0.0,,"
            "Main,Load,Cyl,CylB,ADV,,%IX1.0,,%QX1.0,LineB," ]
    let store, project = loadOk csv "Cell"
    // 같은 Main.Load·Cyl.ADV 라도 System 마다 따로 생긴다.
    let callsBySystem =
        Queries.activeSystemsOf project.Id store
        |> List.map (fun system ->
            system.Name,
            [ for flow in Queries.flowsOf system.Id store do
                for work in Queries.worksOf flow.Id store do
                    for call in Queries.callsOf work.Id store do
                        yield $"{flow.Name}.{work.LocalName}/{call.Name}" ])
    Assert.Equal<(string * string list) list>(
        [ "Cell", [ "Main.Load/Cyl.ADV" ]
          "LineB", [ "Main.Load/Cyl.ADV" ] ],
        callsBySystem)

[<Fact>]
let ``ReferenceOf 가 가리키는 Work 에 원본 Call 이 없으면 가져오기를 거부한다`` () =
    // 원본 없는 Reference 를 일반 Call 로 바꾸면 유령 디바이스가 생긴다 — 조용히 넘기지 않고 알린다.
    let csv =
        String.concat "\n" [
            header11
            "Main,Load,Cyl,Cyl,ADV,,%IX0.0,,%QX0.0,LineA,"
            // Load 에 Cyl.RET 원본이 없다.
            "Main,Unload,Cyl,Cyl,RET,,,,,LineA,Main.Load"
            // LineB 에는 Main.Load 가 없다 — LineA 의 같은 이름 Work 를 집으면 안 된다.
            "Main,Unload,Cyl,Cyl,ADV,,,,,LineB,Main.Load" ]
    match load csv "Cell" with
    | Ok _ -> failwith "원본 없는 Reference 가 통과했습니다."
    | Error errors ->
        Assert.Equal(2, errors.Length)
        Assert.Contains(errors, fun e -> e.StartsWith "line 3:" && e.Contains "ReferenceOf 'Main.Load'" && e.Contains "Cyl.RET")
        Assert.Contains(errors, fun e -> e.StartsWith "line 4:" && e.Contains "'LineB'")
