module Ds2.SymbolImport.Tests.MapperRulesTests

open Ds2.SymbolImport
open Xunit

let private entry name direction =
    { Address = "X0"; Name = name; Direction = direction; Comment = ""; Vendor = Mitsubishi }

[<Fact>]
let ``4 segments → Flow / Work / Device / Api`` () =
    let e = entry "P100_Feeder_Cyl1_ADV" SymbolDirection.Output
    let m = (MapperRules.mapEntry e).Value
    Assert.Equal("P100", m.FlowName)
    Assert.Equal("Feeder", m.WorkName)
    Assert.Equal("Cyl1", m.DeviceName)
    Assert.Equal("ADV", m.ApiName)
    Assert.False(m.IsAmbiguous)

[<Fact>]
let ``3 segments → Flow / Work=Device / Api`` () =
    let e = entry "Flow_Cyl_ADV" SymbolDirection.Output
    let m = (MapperRules.mapEntry e).Value
    Assert.Equal("Flow", m.FlowName)
    Assert.Equal("Cyl", m.WorkName)
    Assert.Equal("Cyl", m.DeviceName)
    Assert.Equal("ADV", m.ApiName)

[<Fact>]
let ``2 segments → Flow / Device=Flow / Api`` () =
    let e = entry "FEEDER_START" SymbolDirection.Output
    let m = (MapperRules.mapEntry e).Value
    Assert.Equal("FEEDER", m.FlowName)
    Assert.Equal("FEEDER", m.WorkName)
    Assert.Equal("FEEDER", m.DeviceName)
    Assert.Equal("START", m.ApiName)

[<Fact>]
let ``1 segment → Default Flow/Work/Device + ambiguous`` () =
    let e = entry "EMERGENCY" SymbolDirection.Input
    let m = (MapperRules.mapEntry e).Value
    Assert.Equal("Default", m.FlowName)
    Assert.Equal("EMERGENCY", m.ApiName)
    Assert.True(m.IsAmbiguous)

[<Fact>]
let ``5+ segments — 마지막 segment 들 _ join 으로 Api`` () =
    let e = entry "P100_Run_Cyl1_ADV_LMT" SymbolDirection.Input
    let m = (MapperRules.mapEntry e).Value
    Assert.Equal("P100", m.FlowName)
    Assert.Equal("Run", m.WorkName)
    Assert.Equal("Cyl1", m.DeviceName)
    Assert.Equal("ADV_LMT", m.ApiName)

[<Fact>]
let ``빈 이름은 매칭 실패`` () =
    let e = entry "" SymbolDirection.Input
    Assert.True((MapperRules.mapEntry e).IsNone)

[<Fact>]
let ``mapAll — mapped + unmatched 분리`` () =
    let entries = [
        entry "A_B_C_D" SymbolDirection.Input
        entry "" SymbolDirection.Input
        entry "X_Y" SymbolDirection.Output
    ]
    let mapped, unmatched = MapperRules.mapAll entries
    Assert.Equal(2, mapped.Length)
    Assert.Equal(1, unmatched.Length)
