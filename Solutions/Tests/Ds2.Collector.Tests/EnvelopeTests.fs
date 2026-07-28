module Ds2.Adapter.Common.Tests.EnvelopeTests

open System
open Ds2.Core
open Ds2.Adapter.Common
open Xunit

[<Fact>]
let ``NewSample assigns unique EnvelopeId`` () =
    let e1 = Envelope.NewSample(GlobalAssetId "urn:x", SignalId "line.a.b", DateTimeOffset.UtcNow, ValueDouble 1.0, None, "test-adapter")
    let e2 = Envelope.NewSample(GlobalAssetId "urn:x", SignalId "line.a.b", DateTimeOffset.UtcNow, ValueDouble 2.0, None, "test-adapter")
    Assert.NotEqual<Guid>(e1.EnvelopeId, e2.EnvelopeId)

[<Fact>]
let ``NewEvent has EventPayloadJson set and Kind Event`` () =
    let ts = DateTimeOffset.UtcNow
    let e = Envelope.NewEvent(GlobalAssetId "urn:x", SignalId "line.a.b", ts,
                              "urn:opcfoundation:autoid:OpticalScanEventType",
                              """{"code":"123"}""", "autoid-adapter")
    Assert.Equal(Event, e.Kind)
    Assert.Equal(Some """{"code":"123"}""", e.EventPayloadJson)
    Assert.Equal(Some "urn:opcfoundation:autoid:OpticalScanEventType", e.EventTypeSemanticId)
    Assert.Equal(ts, e.SourceTimestamp)

// Phase 4 · SampleValue 라운드트립 (모든 카리에 variant).
//
// 목표: NewSample 이 각 SampleValue 종류를 loss-less 로 저장·복원.
// 왜: Envelope 은 wire format — value carrier 무결성이 파이프라인 정합성의 SSOT.

[<Fact>]
let ``NewSample preserves ValueDouble roundtrip`` () =
    let src = 3.14159265358979
    let e = Envelope.NewSample(GlobalAssetId "urn:x", SignalId "s", DateTimeOffset.UtcNow, ValueDouble src, Some "m", "adp")
    match e.Value with
    | ValueDouble v -> Assert.Equal(src, v)
    | other         -> Assert.Fail (sprintf "expected ValueDouble, got %A" other)
    Assert.Equal(Some "m", e.Unit)

[<Fact>]
let ``NewSample preserves ValueLong roundtrip`` () =
    let src = 9_999_999_999L
    let e = Envelope.NewSample(GlobalAssetId "urn:x", SignalId "s", DateTimeOffset.UtcNow, ValueLong src, None, "adp")
    match e.Value with
    | ValueLong v -> Assert.Equal(src, v)
    | other       -> Assert.Fail (sprintf "expected ValueLong, got %A" other)

[<Fact>]
let ``NewSample preserves ValueString roundtrip`` () =
    let src = "라인-A · 유닛-1"
    let e = Envelope.NewSample(GlobalAssetId "urn:x", SignalId "s", DateTimeOffset.UtcNow, ValueString src, None, "adp")
    match e.Value with
    | ValueString v -> Assert.Equal(src, v)
    | other         -> Assert.Fail (sprintf "expected ValueString, got %A" other)

[<Fact>]
let ``NewSample preserves ValueBool roundtrip`` () =
    let e = Envelope.NewSample(GlobalAssetId "urn:x", SignalId "s", DateTimeOffset.UtcNow, ValueBool true, None, "adp")
    match e.Value with
    | ValueBool v -> Assert.True v
    | other       -> Assert.Fail (sprintf "expected ValueBool, got %A" other)

[<Fact>]
let ``NewSample carries all metadata fields intact`` () =
    let ts = DateTimeOffset.Parse "2026-07-27T10:00:00.500Z"
    let gaid = GlobalAssetId "urn:factory:line-a"
    let sig' = SignalId "line-a.press-1.pressure"
    let e = Envelope.NewSample(gaid, sig', ts, ValueDouble 42.5, Some "bar", "opcua-adp-01")
    Assert.Equal(Sample, e.Kind)
    Assert.Equal(gaid, e.GlobalAssetId)
    Assert.Equal(sig', e.SignalId)
    Assert.Equal(ts, e.SourceTimestamp)
    Assert.Equal(None, e.ServerTimestamp)
    Assert.Equal(0u, e.StatusCode)
    Assert.Equal(Some "bar", e.Unit)
    Assert.Equal(None, e.SeqNo)
    Assert.Equal("opcua-adp-01", e.Origin)
    Assert.Equal(None, e.EventPayloadJson)
    Assert.Equal(None, e.EventTypeSemanticId)

// F# record 등가성 — 모든 필드가 record 에 캡처되었음을 증명 (구조적 equality).
[<Fact>]
let ``Envelope record equality holds under field-identical reconstruction`` () =
    let ts = DateTimeOffset.Parse "2026-07-27T10:00:00Z"
    let id = Guid.NewGuid()
    let e1 : Envelope = {
        EnvelopeId          = id
        Kind                = Sample
        GlobalAssetId       = GlobalAssetId "urn:x"
        SignalId            = SignalId "s"
        SourceTimestamp     = ts
        ServerTimestamp     = Some (ts.AddMilliseconds 5.0)
        Value               = ValueDouble 1.0
        StatusCode          = 0u
        Unit                = Some "m"
        SeqNo               = Some 42UL
        Origin              = "adp"
        EventPayloadJson    = None
        EventTypeSemanticId = None
    }
    let e2 : Envelope = {
        EnvelopeId          = id
        Kind                = Sample
        GlobalAssetId       = GlobalAssetId "urn:x"
        SignalId            = SignalId "s"
        SourceTimestamp     = ts
        ServerTimestamp     = Some (ts.AddMilliseconds 5.0)
        Value               = ValueDouble 1.0
        StatusCode          = 0u
        Unit                = Some "m"
        SeqNo               = Some 42UL
        Origin              = "adp"
        EventPayloadJson    = None
        EventTypeSemanticId = None
    }
    Assert.Equal(e1, e2)

// EventPayloadJson wire-format roundtrip — 임의 JSON string 이 payload 채널로 loss-less 전달.
[<Fact>]
let ``NewEvent preserves arbitrary EventPayloadJson content byte-for-byte`` () =
    let payload = """{"code":"CNC01-alarm","details":{"nested":true,"korean":"경보"},"ts":"2026-07-27T00:00:00Z"}"""
    let e = Envelope.NewEvent(GlobalAssetId "urn:x", SignalId "s", DateTimeOffset.UtcNow,
                              "urn:type:alarm", payload, "adp")
    Assert.Equal(Some payload, e.EventPayloadJson)
