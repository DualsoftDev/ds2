module Ds2.Core.Tests.CollectionPolicyTests

open Ds2.Core
open Xunit

// Phase 0 — SignalPolicy validator + ISO-8601 duration parser.
// SignalPolicy 는 별도 CollectionPolicy SM 이 아니라
// SequenceLogging SM (LoggingSystemProperties.SignalPolicies) 로 흡수됨.

let private policy signalId retention : SignalPolicy = {
    SignalId = SignalId.create signalId
    AcquisitionMode = ChangeOfValue
    SamplingIntervalMs = Some 500
    PublishingIntervalMs = Some 1000
    DeadbandAbsolute = Some 5.0
    DeadbandPercent = None
    EngineeringRangeLow = None
    EngineeringRangeHigh = None
    QueueSize = Some 10
    Retention = retention
}

[<Fact>]
let ``ISO-8601 duration accepts P90D`` () =
    Assert.True(Iso8601Duration.isValid "P90D")

[<Fact>]
let ``ISO-8601 duration accepts P1Y2M3D`` () =
    Assert.True(Iso8601Duration.isValid "P1Y2M3D")

[<Fact>]
let ``ISO-8601 duration accepts PT1H30M`` () =
    Assert.True(Iso8601Duration.isValid "PT1H30M")

[<Fact>]
let ``ISO-8601 duration accepts P1DT12H`` () =
    Assert.True(Iso8601Duration.isValid "P1DT12H")

[<Fact>]
let ``ISO-8601 duration accepts P2W`` () =
    Assert.True(Iso8601Duration.isValid "P2W")

[<Fact>]
let ``ISO-8601 duration rejects empty and P`` () =
    Assert.False(Iso8601Duration.isValid "")
    Assert.False(Iso8601Duration.isValid "P")

[<Fact>]
let ``ISO-8601 duration rejects bare number`` () =
    Assert.False(Iso8601Duration.isValid "90D")
    Assert.False(Iso8601Duration.isValid "P90")

[<Fact>]
let ``ISO-8601 duration rejects out-of-order components`` () =
    Assert.False(Iso8601Duration.isValid "P1D2M")

[<Fact>]
let ``SignalPolicy validate accepts well-formed policy`` () =
    let p = policy "line1.cnc01.spindle-speed" "P90D"
    match SignalPolicy.validate p with
    | Ok () -> ()
    | Error msg -> Assert.Fail(sprintf "expected Ok, got %s" msg)

[<Fact>]
let ``SignalPolicy validate rejects empty SignalId`` () =
    let p = { policy "line1.cnc01.speed" "P90D" with SignalId = SignalId.empty }
    match SignalPolicy.validate p with
    | Error _ -> ()
    | Ok () -> Assert.Fail "expected Error for empty SignalId"

[<Fact>]
let ``SignalPolicy validate rejects invalid retention`` () =
    let p = policy "line1.cnc01.speed" "not-a-duration"
    match SignalPolicy.validate p with
    | Error _ -> ()
    | Ok () -> Assert.Fail "expected Error for bad retention"

[<Fact>]
let ``SignalPolicy validate rejects non-positive intervals`` () =
    let p = { policy "line1.cnc01.speed" "P90D" with SamplingIntervalMs = Some 0 }
    Assert.True(match SignalPolicy.validate p with Error _ -> true | _ -> false)
    let p2 = { policy "line1.cnc01.speed" "P90D" with QueueSize = Some -1 }
    Assert.True(match SignalPolicy.validate p2 with Error _ -> true | _ -> false)

[<Fact>]
let ``SignalPolicy validate enforces sampled interval and deadband contract`` () =
    let sampled = {
        policy "line1.cnc01.speed" "P90D" with
            AcquisitionMode = Sampled
            SamplingIntervalMs = None
    }
    Assert.True(match SignalPolicy.validate sampled with Error _ -> true | _ -> false)
    let bothDeadbands = {
        policy "line1.cnc01.speed" "P90D" with
            DeadbandAbsolute = Some 1.0
            DeadbandPercent = Some 5.0
    }
    Assert.True(match SignalPolicy.validate bothDeadbands with Error _ -> true | _ -> false)
    let invalidPercent = {
        policy "line1.cnc01.speed" "P90D" with
            DeadbandAbsolute = None
            DeadbandPercent = Some 101.0
    }
    Assert.True(match SignalPolicy.validate invalidPercent with Error _ -> true | _ -> false)

[<Fact>]
let ``percent deadband requires a valid engineering range`` () =
    let missingRange = {
        policy "line1.cnc01.speed" "P90D" with
            DeadbandAbsolute = None
            DeadbandPercent = Some 2.5
    }
    Assert.True(match SignalPolicy.validate missingRange with Error _ -> true | _ -> false)

    let invalidRange = {
        missingRange with
            EngineeringRangeLow = Some 100.0
            EngineeringRangeHigh = Some 100.0
    }
    Assert.True(match SignalPolicy.validate invalidRange with Error _ -> true | _ -> false)

    let validRange = {
        missingRange with
            EngineeringRangeLow = Some -50.0
            EngineeringRangeHigh = Some 150.0
    }
    Assert.True(match SignalPolicy.validate validRange with Ok () -> true | _ -> false)

[<Fact>]
let ``LoggingSystemProperties carries SignalPolicies`` () =
    let props = LoggingSystemProperties()
    // 기존 필드 무영향
    Assert.True(props.EnableAutoLogging)
    // 신규 필드: 초기 비어 있음
    Assert.Equal(0, props.SignalPolicies.Count)
    // 정책 하나 추가 가능
    props.SignalPolicies.Add(policy "line1.cnc01.spindle-speed" "P90D")
    Assert.Equal(1, props.SignalPolicies.Count)
    Assert.Equal("line1.cnc01.spindle-speed", props.SignalPolicies.[0].SignalId.Value)
