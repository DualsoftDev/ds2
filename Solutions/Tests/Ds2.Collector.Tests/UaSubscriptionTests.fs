module Ds2.Collector.Tests.UaSubscriptionTests

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

let private options = {
    Enabled = true
    EndpointUrl = "opc.tcp://localhost:62541/Ds2/OpcUa/Server"
    DataRoot = "."
    UseSecurity = false
    AutoAcceptUntrustedCertificates = false
    UseCertificateIdentity = false
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
let ``percent deadband falls back safely when UA EURange is unavailable`` () =
    let settings =
        UaSubscription.monitoredItemSettings options
            (policy AcquisitionMode.ChangeOfValue (Some 250) None None (Some 5.0) None)
    Assert.Equal(uint32 DeadbandType.None, settings.DeadbandType)
    Assert.Equal(0.0, settings.DeadbandValue)
