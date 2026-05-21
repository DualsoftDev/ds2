module Ds2.SymbolImport.Tests.ModelGeneratorTests

open Ds2.Core
open Ds2.SymbolImport
open Ds2.SymbolImport.Matching
open Xunit

let private entry name addr direction =
    { Address = addr; Name = name; Direction = direction; Comment = ""; Vendor = Mitsubishi }

/// Mapper batch 직접 조립 — config / vendor 의존성 회피, ModelGenerator 단독 검증.
let private mappingPair (deviceName: string) (apiName: string) (outEntry: SymbolEntry option) (inEntries: SymbolEntry list) : Mapper.Mapping =
    { OutputEntry = outEntry
      InputEntries = inEntries
      FlowName = "F1"
      WorkName = "W1"
      DeviceName = deviceName
      ApiName = apiName
      Strategy = MatchingStrategy.MappingSet
      Confidence = 1.0 }

let private batch (mappings: Mapper.Mapping list) : Mapper.MappingBatch =
    { Mapped = mappings; Unmatched = [] }

let private extractCalls (plans: ModelGenerator.SystemPlan list) : ModelGenerator.CallPlan list =
    plans
    |> List.filter (fun p -> p.IsActive)
    |> List.collect (fun p -> p.Flows |> List.collect (fun f -> f.Works |> List.collect (fun w -> w.Calls)))

// ── Placeholder fill 회귀 가드 ─────────────────────────────────────────
// 이전 (None 박제) 흐름은 AASX 에 <value>null</value> 저장 → DSPilot V10 V1/V2 위반.
// fix: OutputEntry/InputEntries 가 없으면 "(unset-OUT)" / "(unset-IN)" placeholder IOTag 부여.
// 운영 검증 패턴 (DSPilot fix_aasx_v10.py 후처리와 동일 효과를 ModelGenerator 단계에서 내재화).

[<Fact>]
let ``ModelGenerator — OutputEntry=None ⇒ OutTag = Some placeholder (address="")`` () =
    let plans = ModelGenerator.generate (batch [
        mappingPair "Lamp1" "Red" None [entry "Lamp1_Feedback" "X10" SymbolDirection.Input]
    ])
    let calls = extractCalls plans
    let call = List.exactlyOne calls
    Assert.True(call.OutTag.IsSome)
    Assert.Equal(ModelGenerator.PlaceholderOutName, call.OutTag.Value.Name)
    Assert.Equal("", call.OutTag.Value.Address)

[<Fact>]
let ``ModelGenerator — InputEntries=[] ⇒ InTag = Some placeholder (address="")`` () =
    let plans = ModelGenerator.generate (batch [
        mappingPair "Tower1" "Buzzer" (Some (entry "Tower1_Buzzer" "Y100" SymbolDirection.Output)) []
    ])
    let calls = extractCalls plans
    let call = List.exactlyOne calls
    Assert.True(call.InTag.IsSome)
    Assert.Equal(ModelGenerator.PlaceholderInName, call.InTag.Value.Name)
    Assert.Equal("", call.InTag.Value.Address)

[<Fact>]
let ``ModelGenerator — OutputEntry+InputEntries 양쪽 있으면 placeholder 아님 (실 주소 유지)`` () =
    let plans = ModelGenerator.generate (batch [
        mappingPair "Cyl1" "ADV"
            (Some (entry "Cyl1_O_ADV" "Y10" SymbolDirection.Output))
            [entry "Cyl1_I_ADV" "X10" SymbolDirection.Input]
    ])
    let call = List.exactlyOne (extractCalls plans)
    Assert.True(call.OutTag.IsSome && call.InTag.IsSome)
    Assert.Equal("Y10", call.OutTag.Value.Address)
    Assert.Equal("X10", call.InTag.Value.Address)
    Assert.NotEqual<string>(ModelGenerator.PlaceholderOutName, call.OutTag.Value.Name)
    Assert.NotEqual<string>(ModelGenerator.PlaceholderInName, call.InTag.Value.Name)

[<Fact>]
let ``ModelGenerator — 모든 CallPlan 의 OutTag/InTag 가 Some (None 박제 회귀)`` () =
    // 출력만 / 입력만 / 양쪽 / 양쪽 None — 4가지 케이스 혼합.
    let plans = ModelGenerator.generate (batch [
        mappingPair "A" "Api1" (Some (entry "ao" "Y0" SymbolDirection.Output)) [entry "ai" "X0" SymbolDirection.Input]
        mappingPair "B" "Api2" (Some (entry "bo" "Y1" SymbolDirection.Output)) []
        mappingPair "C" "Api3" None [entry "ci" "X2" SymbolDirection.Input]
        mappingPair "D" "Api4" None []
    ])
    let calls = extractCalls plans
    Assert.Equal(4, calls.Length)
    for c in calls do
        Assert.True(c.OutTag.IsSome, sprintf "%s OutTag None" c.Name)
        Assert.True(c.InTag.IsSome,  sprintf "%s InTag None"  c.Name)

[<Fact>]
let ``ModelGenerator — ApiDef SensingType 은 placeholder 와 무관하게 Real 유지 (V4 회귀 방지)`` () =
    // placeholder 도입이 SensingType 을 Virtual 로 바꾸지 않아야 — Virtual 은 Work.Duration 의존 (V4) → 더 큰 부작용.
    let plans = ModelGenerator.generate (batch [
        mappingPair "Lamp" "Red" (Some (entry "lo" "Y10" SymbolDirection.Output)) []
    ])
    let devicePlans = plans |> List.filter (fun p -> not p.IsActive)
    for plan in devicePlans do
        for apiDef in plan.ApiDefs do
            match apiDef.ActionType with
            | ActionType.Real _ -> ()
            | _ -> Assert.Fail(sprintf "ApiDef '%s' ActionType 이 Real 아님 (%A)" apiDef.Name apiDef.ActionType)
            match apiDef.SensingType with
            | SensingType.Real _ -> ()
            | _ -> Assert.Fail(sprintf "ApiDef '%s' SensingType 이 Real 아님 (%A)" apiDef.Name apiDef.SensingType)
