module Ds2.Collector.Tests.UaSubscriptionTests

open System
open Opc.Ua
open Xunit
open Ds2.Adapter.Common
open Ds2.Collector
open Ds2.Core
open Ds2.Core.Encoding

[<Fact>]
let ``asset namespace URI round-trips to global asset id`` () =
    let gaid = "urn:dualsoft:asset:cnc01"
    let uri = "urn:ds:asset:" + Base64Url.encode gaid
    let decoded = UaSubscription.tryGlobalAssetId uri
    Assert.True(decoded.IsSome)
    Assert.Equal(gaid, decoded.Value.Value)

[<Fact>]
let ``non asset namespace is ignored`` () =
    Assert.True((UaSubscription.tryGlobalAssetId "urn:dualsoft:opcua:server").IsNone)

[<Fact>]
let ``UA scalar values map to collector sample union`` () =
    Assert.Equal(ValueBool true, UaSubscription.toSampleValue(box true))
    Assert.Equal(ValueLong 42L, UaSubscription.toSampleValue(box 42))
    Assert.Equal(ValueDouble 12.5, UaSubscription.toSampleValue(box 12.5))
    Assert.Equal(ValueString "ok", UaSubscription.toSampleValue(box "ok"))
    Assert.Equal(ValueNone, UaSubscription.toSampleValue null)

[<Fact>]
let ``sample envelope id is stable for retransmission and changes with value`` () =
    let asset = GlobalAssetId "urn:asset:stable"
    let signal = SignalId "line1.stable.value"
    let timestamp = DateTimeOffset.UtcNow
    let first = UaSubscription.stableSampleEnvelopeId asset signal timestamp 0u (ValueDouble 1.25)
    let replay = UaSubscription.stableSampleEnvelopeId asset signal timestamp 0u (ValueDouble 1.25)
    let changed = UaSubscription.stableSampleEnvelopeId asset signal timestamp 0u (ValueDouble 1.5)
    Assert.Equal(first, replay)
    Assert.NotEqual(first, changed)

let private options = {
    Enabled = true
    EndpointUrl = "opc.tcp://localhost:62541/Ds2/OpcUa/Server"
    DataRoot = "."
    UseSecurity = false
    AutoAcceptUntrustedCertificates = false
    UseCertificateIdentity = false
    PairLocalCertificates = false
    PairedServerCertificateRoot = "."
    PairedServerApplicationUri = "urn:test"
    SamplingIntervalMs = 200
    PublishingIntervalMs = 500
    ReconnectDelayMs = 3000
}

let private policy mode sampling publishing absolute percent queue = Some {
    AcquisitionMode = mode
    SamplingIntervalMs = sampling
    PublishingIntervalMs = publishing
    DeadbandAbsolute = absolute
    DeadbandPercent = percent
    EngineeringRangeLow = percent |> Option.map (fun _ -> 0.0)
    EngineeringRangeHigh = percent |> Option.map (fun _ -> 100.0)
    QueueSize = queue
    Retention = "P90D"
}

[<Fact>]
let ``change-of-value policy configures per-signal sampling deadband and queue`` () =
    let settings =
        UaSubscription.monitoredItemSettings options
            (policy AcquisitionMode.ChangeOfValue (Some 250) (Some 1000) (Some 0.5) None (Some 25))
    Assert.Equal(250, settings.SamplingIntervalMs)
    Assert.Equal(1000, settings.PublishingIntervalMs)
    Assert.Equal(25u, settings.QueueSize)
    Assert.Equal(DataChangeTrigger.StatusValue, settings.Trigger)
    Assert.Equal(uint32 DeadbandType.Absolute, settings.DeadbandType)
    Assert.Equal(0.5, settings.DeadbandValue)

[<Fact>]
let ``sampled and event-driven policies preserve timestamp changes`` () =
    let sampled =
        UaSubscription.monitoredItemSettings options
            (policy AcquisitionMode.Sampled (Some 1000) None None None None)
    Assert.Equal(DataChangeTrigger.StatusValueTimestamp, sampled.Trigger)
    Assert.Equal(1000, sampled.SamplingIntervalMs)

    let eventDriven =
        UaSubscription.monitoredItemSettings options
            (policy AcquisitionMode.EventDriven None None None None None)
    Assert.Equal(DataChangeTrigger.StatusValueTimestamp, eventDriven.Trigger)
    Assert.Equal(0, eventDriven.SamplingIntervalMs)

[<Fact>]
let ``percent deadband uses UA percent filter when engineering range is published`` () =
    let settings =
        UaSubscription.monitoredItemSettings options
            (policy AcquisitionMode.ChangeOfValue (Some 250) None None (Some 5.0) None)
    Assert.Equal(uint32 DeadbandType.Percent, settings.DeadbandType)
    Assert.Equal(5.0, settings.DeadbandValue)

[<Fact>]
let ``UA BaseEvent fields decode to durable event envelope`` () =
    let eventId = Guid.NewGuid()
    let sourceTimestamp = DateTime.UtcNow.AddSeconds(-1.0)
    let receiveTimestamp = DateTime.UtcNow
    let fields = VariantCollection([|
        Variant(eventId.ToByteArray())
        Variant(ObjectTypeIds.BaseEventType)
        Variant(NodeId("Asset", 2us))
        Variant("line1.reader.code")
        Variant(sourceTimestamp)
        Variant(receiveTimestamp)
        Variant(LocalizedText """{"eventTypeSemanticId":"urn:autoid:scan","sourceSignalId":"line1.reader.code","payload":{"code":"ABC-123"}}""")
        Variant(500us)
    |])
    let envelope =
        UaSubscription.tryEventEnvelope
            "opcua:test"
            (GlobalAssetId "urn:asset:reader")
            fields
        |> Option.get
    Assert.Equal(eventId, envelope.EnvelopeId)
    Assert.Equal(Event, envelope.Kind)
    Assert.Equal("line1.reader.code", envelope.SignalId.Value)
    Assert.Equal(Some "urn:autoid:scan", envelope.EventTypeSemanticId)
    Assert.Equal(Some """{"code":"ABC-123"}""", envelope.EventPayloadJson)
    Assert.Equal("opcua:test", envelope.Origin)

[<Fact>]
let ``oversized UA event payload is rejected before durable enqueue`` () =
    let payload = new System.String('x', UaSubscription.MaxEventPayloadBytes + 1)
    let fields = VariantCollection([|
        Variant(Guid.NewGuid().ToByteArray())
        Variant(ObjectTypeIds.BaseEventType)
        Variant(NodeId("Asset", 2us))
        Variant("line1.reader.code")
        Variant(DateTime.UtcNow)
        Variant(DateTime.UtcNow)
        Variant(LocalizedText($"{{\"eventTypeSemanticId\":\"urn:autoid:scan\",\"payload\":\"{payload}\"}}"))
        Variant(500us)
    |])

    Assert.True(
        UaSubscription.tryEventEnvelope "opcua:test" (GlobalAssetId "urn:asset:reader") fields
        |> Option.isNone)

[<Fact>]
let ``event filter selects the complete BaseEvent wire contract`` () =
    let filter = UaSubscription.eventFilter()
    Assert.Equal(8, filter.SelectClauses.Count)

[<Fact>]
let ``Collector rejects unsecured remote OPC UA configuration`` () =
    let remote =
        { options with
            EndpointUrl = "opc.tcp://10.0.0.12:62541/Ds2/OpcUa/Server"
            UseSecurity = false
            UseCertificateIdentity = true }
    Assert.Throws<InvalidOperationException>(fun () -> UaSubscriptionOptions.validate remote |> ignore)
    |> ignore

[<Fact>]
let ``Collector accepts secured remote OPC UA with certificate identity`` () =
    let remote =
        { options with
            EndpointUrl = "opc.tcp://10.0.0.12:62541/Ds2/OpcUa/Server"
            UseSecurity = true
            UseCertificateIdentity = true
            PairLocalCertificates = false }
    Assert.Same(remote, UaSubscriptionOptions.validate remote)

[<Fact>]
let ``Collector accepts only modern SignAndEncrypt endpoint descriptions`` () =
    let endpoint mode policy =
        EndpointDescription(SecurityMode = mode, SecurityPolicyUri = policy)
    Assert.True(UaSubscription.isAcceptedEndpointSecurity true
        (endpoint MessageSecurityMode.SignAndEncrypt SecurityPolicies.Basic256Sha256))
    Assert.True(UaSubscription.isAcceptedEndpointSecurity true
        (endpoint MessageSecurityMode.SignAndEncrypt SecurityPolicies.Aes256_Sha256_RsaPss))
    Assert.False(UaSubscription.isAcceptedEndpointSecurity true
        (endpoint MessageSecurityMode.Sign SecurityPolicies.Basic256Sha256))
    Assert.False(UaSubscription.isAcceptedEndpointSecurity true
        (endpoint MessageSecurityMode.SignAndEncrypt SecurityPolicies.Basic256))
    Assert.True(UaSubscription.isAcceptedEndpointSecurity false
        (endpoint MessageSecurityMode.None SecurityPolicies.None))
