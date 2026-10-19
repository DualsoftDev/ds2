module Ds2.CSV.Tests.DeviceCsvTests

open Xunit
open Ds2.Core
open Ds2.CSV
open Ds2.TestKit.ModelBuilder

// Device CSV(#253) — Device·Api 만 선언해 기존 프로젝트에 더하는 형식. 표준 CSV 는 Call 한 줄이 기준이라
// Flow·Work 없이 Device·Api 만 적을 수 없었다.

let private rowsOf (content: string) =
    match DeviceCsv.parse content with
    | Ok rows -> rows
    | Error errors -> failwithf "%A" errors

let private errorsOf (content: string) =
    match DeviceCsv.parse content with
    | Ok rows -> failwithf "expected errors but got %A" rows
    | Error errors -> errors

[<Fact>]
let ``Device CSV 는 종류 글자와 설명을 읽고 빈 종류는 Normal 로 둔다`` () =
    let rows =
        rowsOf (String.concat "\n" [
            "Device,Api,ActionType,SensingType,Description"
            "Cyl,Adv,Pulse(200),Latch(300),전진"
            "Cyl,Ret,,,"
            "Lamp,On,Virtual,Virtual(500),"
        ])

    Assert.Equal(3, rows.Length)
    let adv = rows.[0]
    Assert.Equal(("Cyl", "Adv"), (adv.Device, adv.Api))
    Assert.Equal(ActionType.Pulse (Some 200), adv.ActionType)
    Assert.Equal(SensingType.Latch 300, adv.SensingType)
    Assert.Equal(Some "전진", adv.Description)
    Assert.Equal(ActionType.Normal None, rows.[1].ActionType)
    Assert.Equal(SensingType.Normal None, rows.[1].SensingType)
    Assert.Equal(None, rows.[1].Description)
    Assert.Equal(ActionType.Virtual, rows.[2].ActionType)
    Assert.Equal(SensingType.Virtual 500, rows.[2].SensingType)

[<Fact>]
let ``Device 와 Api 두 열만 있어도 읽는다`` () =
    let rows = rowsOf "Device\tApi\nCyl\tAdv\nCyl\tRet"

    Assert.Equal<string list>([ "Adv"; "Ret" ], rows |> List.map (fun r -> r.Api))

[<Fact>]
let ``잘못된 행은 줄 번호와 함께 모두 알려 준다`` () =
    let errors =
        errorsOf (String.concat "\n" [
            "Device,Api,ActionType,SensingType"
            ",Adv,,"
            "Cyl,Adv,Jump,"
            "Cyl,Ret,,Latch"
            "Cyl,Hold,,"
            "Cyl,Hold,,"
        ])

    Assert.Equal<int list>([ 2; 3; 4; 6 ], errors |> List.map (fun e -> e.LineNumber))
    Assert.Contains("ActionType", errors.[1].Message)
    Assert.Contains("SensingType", errors.[2].Message)
    Assert.Contains("5행", errors.[3].Message)

[<Fact>]
let ``헤더가 Device Api 로 시작하지 않으면 거부한다`` () =
    let errors = errorsOf "Flow,Work,Device\nF,W,Cyl"

    Assert.Equal(1, Assert.Single(errors).LineNumber)

[<Fact>]
let ``내보낸 Device CSV 를 다시 읽으면 Device·Api·종류·설명이 그대로다`` () =
    let store = createStore ()
    let project = addProject store "P"
    addSystem store "Line" project.Id true |> ignore
    let cyl = addSystem store "Cyl" project.Id false
    let adv = addApiDef store "Adv" cyl.Id
    adv.ActionType <- ActionType.Pulse (Some 200)
    adv.SensingType <- SensingType.Latch 300
    adv.Description <- Some "전진, 빠르게"
    addApiDef store "Ret" cyl.Id |> ignore
    let lamp = addSystem store "Lamp" project.Id false
    let on = addApiDef store "On" lamp.Id
    on.ActionType <- ActionType.Virtual
    on.SensingType <- SensingType.Virtual 500

    let rows = rowsOf (DeviceCsv.projectToCsv store project.Id)

    Assert.Equal<(string * string * ActionType * SensingType * string option) list>(
        [ ("Cyl", "Adv", ActionType.Pulse (Some 200), SensingType.Latch 300, Some "전진, 빠르게")
          ("Cyl", "Ret", ActionType.Normal None, SensingType.Normal None, None)
          ("Lamp", "On", ActionType.Virtual, SensingType.Virtual 500, None) ],
        rows |> List.map (fun r -> r.Device, r.Api, r.ActionType, r.SensingType, r.Description))
