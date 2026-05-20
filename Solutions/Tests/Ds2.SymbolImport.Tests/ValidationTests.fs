module Ds2.SymbolImport.Tests.ValidationTests

open Ds2.SymbolImport
open Xunit

let private entry name addr direction =
    { Address = addr; Name = name; Direction = direction; Comment = ""; Vendor = Mitsubishi }

[<Fact>]
let ``V-S1: unmatched 심볼 있으면 Warning`` () =
    let entries = [ entry "" "X0" SymbolDirection.Input ]
    let batch = Mapper.map entries
    let plans = ModelGenerator.generate batch
    let issues = Validation.validate batch plans
    Assert.Contains(issues, fun i -> i.Code = "V-S1" && i.Severity = Validation.Warning)

[<Fact>]
let ``V-S2: 1-segment 심볼 (ambiguous) 있으면 Info`` () =
    let entries = [ entry "EMERGENCY" "X0" SymbolDirection.Input ]
    let batch = Mapper.map entries
    let plans = ModelGenerator.generate batch
    let issues = Validation.validate batch plans
    Assert.Contains(issues, fun i -> i.Code = "V-S2" && i.Severity = Validation.Info)

[<Fact>]
let ``정상 매핑은 issue 없음 (V-S1/V-S2 부재)`` () =
    let entries = [
        entry "Flow_Work_Cyl_ADV" "Y10" SymbolDirection.Output
        entry "Flow_Work_Cyl_ADV_LMT" "X10" SymbolDirection.Input
    ]
    let batch = Mapper.map entries
    let plans = ModelGenerator.generate batch
    let issues = Validation.validate batch plans
    let codes = issues |> List.map (fun i -> i.Code)
    Assert.DoesNotContain("V-S1", codes)
    Assert.DoesNotContain("V-S2", codes)
