module Ds2.SymbolImport.Tests.ModelGeneratorTests

open Ds2.Core
open Ds2.SymbolImport
open Xunit

let private entry name addr direction =
    { Address = addr; Name = name; Direction = direction; Comment = ""; Vendor = Mitsubishi }

[<Fact>]
let ``Controller(active) + Device(passive) System 생성`` () =
    let entries = [
        entry "P100_Feed_Cyl1_ADV" "Y10" SymbolDirection.Output
        entry "P100_Feed_Cyl1_ADV_LMT" "X10" SymbolDirection.Input
    ]
    let batch = Mapper.map entries
    let plans = ModelGenerator.generate batch
    let controller = plans |> List.find (fun p -> p.IsActive)
    let device = plans |> List.find (fun p -> not p.IsActive && p.Name = "Cyl1")
    Assert.Equal("Controller", controller.Name)
    Assert.Equal(1, controller.Flows.Length)
    Assert.Equal("P100", controller.Flows.[0].Name)
    Assert.Equal("Feed", controller.Flows.[0].Works.[0].Name)
    let callNames = controller.Flows.[0].Works.[0].Calls |> List.map (fun c -> c.Name) |> Set.ofList
    Assert.Contains("Cyl1.ADV", callNames)
    Assert.Contains("Cyl1.ADV_LMT", callNames)
    Assert.NotEmpty(device.ApiDefs)

[<Fact>]
let ``동일 (Device, Api) 의 input + output 심볼 1 Call 로 묶임`` () =
    let entries = [
        entry "Flow_Work_Cyl_ADV" "Y10" SymbolDirection.Output
        entry "Flow_Work_Cyl_ADV" "X10" SymbolDirection.Input
    ]
    let batch = Mapper.map entries
    let plans = ModelGenerator.generate batch
    let controller = plans |> List.find (fun p -> p.IsActive)
    let call = controller.Flows.[0].Works.[0].Calls |> List.head
    Assert.True(call.InTag.IsSome)
    Assert.True(call.OutTag.IsSome)
    Assert.Equal("X10", call.InTag.Value.Address)
    Assert.Equal("Y10", call.OutTag.Value.Address)

[<Fact>]
let ``v10 ActionType — 심볼명 _PB → Latched (Action.set)`` () =
    let entries = [ entry "Flow_HMI_EmergencyStop_PB" "X0" SymbolDirection.Input ]
    let batch = Mapper.map entries
    let plans = ModelGenerator.generate batch
    let device = plans |> List.find (fun p -> not p.IsActive)
    let apiDef = device.ApiDefs |> List.find (fun a -> a.Name = "PB")
    Assert.Equal(ActionType.Real (Latched, None), apiDef.ActionType)

[<Fact>]
let ``v10 SensingType — 심볼명 _LS → OneShot (Sensing.edge)`` () =
    let entries = [ entry "Flow_Conv_Sensor_LS" "X10" SymbolDirection.Input ]
    let batch = Mapper.map entries
    let plans = ModelGenerator.generate batch
    let device = plans |> List.find (fun p -> not p.IsActive)
    let apiDef = device.ApiDefs |> List.find (fun a -> a.Name = "LS")
    Assert.Equal(SensingType.Real (OneShot, None), apiDef.SensingType)

[<Fact>]
let ``default ActionType/SensingType = Real(Level, None) (normal)`` () =
    let entries = [ entry "Flow_Work_Cyl_ADV" "Y0" SymbolDirection.Output ]
    let batch = Mapper.map entries
    let plans = ModelGenerator.generate batch
    let device = plans |> List.find (fun p -> not p.IsActive)
    let apiDef = device.ApiDefs |> List.head
    Assert.Equal(ActionType.Real (Level, None), apiDef.ActionType)
    Assert.Equal(SensingType.Real (Level, None), apiDef.SensingType)
