module Ds2.SymbolImport.Tests.ValidationTests

open Ds2.SymbolImport
open Ds2.SymbolImport.Matching
open Xunit

let private entry name addr direction =
    { Address = addr; Name = name; Direction = direction; Comment = ""; Vendor = Mitsubishi }

[<Fact>]
let ``V-S1: unmatched 심볼 있으면 Warning`` () =
    // dsev2 매칭은 MappingSets 의 DeviceKeyword 와 매칭되지 않는 심볼을 unmatched 로 분류.
    // "" 또는 어떤 룰에도 매칭 안 되는 이름 → unmatched.
    let entries = [
        entry "" "X0" SymbolDirection.Input
        entry "RANDOM_UNKNOWN_NAME" "X1" SymbolDirection.Input
    ]
    let batch = Mapper.map Mitsubishi entries
    let plans = ModelGenerator.generate batch
    let issues = Validation.validate batch plans
    Assert.Contains(issues, fun i -> i.Code = "V-S1" && i.Severity = Validation.Warning)

[<Fact>]
let ``V-S2: NotMatched 또는 낮은 신뢰도 매핑 있으면 Info`` () =
    // dsev2 측 NotMatched strategy 또는 confidence < 0.5 → V-S2 Info.
    // 매칭은 됐지만 부분 매칭이라 신뢰도 낮은 케이스를 일부러 만들기 어려워, 매핑이 0 건이면 V-S2 도 없음.
    // 신뢰도 의미는 dsev2 InputMatching 결과 그대로 보존.
    let batch : Mapper.MappingBatch = {
        Mapped = [{
            OutputEntry = Some (entry "Out" "Y0" SymbolDirection.Output)
            InputEntries = []
            FlowName = "F"; WorkName = "W"; DeviceName = "D"; ApiName = "A"
            Strategy = MatchingStrategy.NotMatched
            Confidence = 0.0
        }]
        Unmatched = []
    }
    let plans = ModelGenerator.generate batch
    let issues = Validation.validate batch plans
    Assert.Contains(issues, fun i -> i.Code = "V-S2" && i.Severity = Validation.Info)

[<Fact>]
let ``V-S3: InTag/OutTag 모두 누락된 Call 이 있으면 Warning`` () =
    // OutputEntry 와 InputEntries 모두 빈 가짜 Mapping → Call.InTag/OutTag 둘 다 None → V-S3.
    let batch : Mapper.MappingBatch = {
        Mapped = [{
            OutputEntry = None
            InputEntries = []
            FlowName = "F"; WorkName = "W"; DeviceName = "D"; ApiName = "A"
            Strategy = MatchingStrategy.MappingSet
            Confidence = 1.0
        }]
        Unmatched = []
    }
    let plans = ModelGenerator.generate batch
    let issues = Validation.validate batch plans
    Assert.Contains(issues, fun i -> i.Code = "V-S3" && i.Severity = Validation.Warning)

[<Fact>]
let ``V-S4: 같은 (Device, Api) 가 3건 이상이면 Warning`` () =
    // 동일 (DeviceName, ApiName) 페어가 3건 이상 — 중복으로 분류.
    let mkMapping () : Mapper.Mapping = {
        OutputEntry = Some (entry "X" "Y0" SymbolDirection.Output)
        InputEntries = []
        FlowName = "F"; WorkName = "W"; DeviceName = "D"; ApiName = "A"
        Strategy = MatchingStrategy.MappingSet
        Confidence = 1.0
    }
    let batch : Mapper.MappingBatch = {
        Mapped = [ mkMapping(); mkMapping(); mkMapping() ]
        Unmatched = []
    }
    let plans = ModelGenerator.generate batch
    let issues = Validation.validate batch plans
    Assert.Contains(issues, fun i -> i.Code = "V-S4" && i.Severity = Validation.Warning)

[<Fact>]
let ``빈 batch + 빈 plans → issue 0건`` () =
    let batch : Mapper.MappingBatch = { Mapped = []; Unmatched = [] }
    let plans = ModelGenerator.generate batch  // controller 만, Flows 비어있음
    let issues = Validation.validate batch plans
    Assert.Empty(issues)
