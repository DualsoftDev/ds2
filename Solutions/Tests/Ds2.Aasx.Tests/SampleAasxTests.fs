module Ds2.Aasx.Tests.SampleAasxTests

open System
open System.IO
open AasCore.Aas3_1
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Aasx
open Ds2.Backend.Plc

[<Fact>]
let ``checked-in XGT AID sample imports and builds Agent gateway plan`` () =
    let path =
        Path.GetFullPath(
            Path.Combine(
                __SOURCE_DIRECTORY__,
                "..",
                "..",
                "..",
                "samples",
                "aas-opcua-xgt",
                "DualSoft_XGT_AID_demo.aasx"))
    Assert.True(File.Exists path, $"sample AASX not found: {path}")

    let store = DsStore()
    AasxImporter.importIntoStoreOrRaise store path
    let project = store.Projects.Values |> Seq.exactlyOne
    Assert.Equal(Guid.Parse "2a01c9e9-4d89-4e40-9be1-9dc489ec4d01", project.Id)

    let aid = project.AssetInterfaces |> Option.get
    let system = store.Systems.Values |> Seq.exactlyOne
    let logging = system.GetLoggingProperties() |> Option.get
    Assert.Equal(4, logging.SignalPolicies.Count)
    let temperaturePolicy =
        logging.SignalPolicies
        |> Seq.find (fun policy -> policy.SignalId.Value = "demo.xgt.motor-temperature")
    Assert.Equal(AcquisitionMode.ChangeOfValue, temperaturePolicy.AcquisitionMode)
    Assert.Equal(Some 250, temperaturePolicy.SamplingIntervalMs)
    Assert.Equal(Some 0.5, temperaturePolicy.DeadbandAbsolute)
    Assert.Equal("P90D", temperaturePolicy.Retention)

    let gateway = AidXgtGatewayConfig.buildForProject(store, project, aid)
    Assert.True(gateway.Success, String.Join("; ", gateway.Errors))
    Assert.Equal(4, gateway.Signals.Length)
    let scanInterval = gateway.Config.Connections.Head.ScanInterval |> Option.get
    Assert.Equal(100.0, scanInterval.TotalMilliseconds)

    let signals =
        gateway.Signals
        |> Seq.map (fun signal -> signal.SignalId, signal.Address, signal.ValueType)
        |> Set.ofSeq
    Assert.Contains(("demo.xgt.cycle-running", "%MX100", "boolean"), signals)
    Assert.Contains(("demo.xgt.part-count", "%DD100", "int"), signals)
    Assert.Contains(("demo.xgt.motor-temperature", "%DW110", "double"), signals)
    Assert.Contains(("demo.xgt.energy-kwh", "%DL120", "double"), signals)

[<Fact>]
let ``SequenceLogging production export-import preserves system signal policies`` () =
    let source =
        Path.GetFullPath(
            Path.Combine(
                __SOURCE_DIRECTORY__,
                "..", "..", "..", "samples", "aas-opcua-xgt", "DualSoft_XGT_AID_demo.aasx"))
    let output = Path.Combine(Path.GetTempPath(), $"ds2-policy-{Guid.NewGuid():N}.aasx")
    try
        let sourceStore = DsStore()
        AasxImporter.importIntoStoreOrRaise sourceStore source
        AasxExporter.exportFromStoreOrRaise sourceStore output "urn:dualsoft:test:" false false

        let environment = AasxFileIO.readEnvironment output |> Option.get
        let loggingSubmodel =
            environment.Submodels
            |> Seq.find (fun submodel -> submodel.IdShort = "SequenceLogging")
        let systemProperties =
            loggingSubmodel.SubmodelElements
            |> Seq.pick (function
                | :? SubmodelElementCollection as collection when collection.IdShort = "SystemProperties" -> Some collection
                | _ -> None)
        let systemCollection =
            systemProperties.Value
            |> Seq.pick (function :? SubmodelElementCollection as collection -> Some collection | _ -> None)
        let policyCollection =
            systemCollection.Value
            |> Seq.pick (function
                | :? SubmodelElementCollection as collection when collection.IdShort = "SignalPoliciesCollection" -> Some collection
                | _ -> None)
        Assert.Equal(AasxSemantics.SignalPoliciesCollectionSemanticId, policyCollection.SemanticId.Keys.[0].Value)
        Assert.False(
            systemCollection.Value
            |> Seq.exists (fun element -> element.IdShort = "SignalPolicies"),
            "legacy JSON SignalPolicies property must not be emitted")

        let restoredStore = DsStore()
        AasxImporter.importIntoStoreOrRaise restoredStore output
        let logging =
            restoredStore.Systems.Values
            |> Seq.exactlyOne
            |> fun system -> system.GetLoggingProperties()
            |> Option.get
        Assert.Equal(4, logging.SignalPolicies.Count)
        let expected = set [
            "demo.xgt.cycle-running"
            "demo.xgt.part-count"
            "demo.xgt.motor-temperature"
            "demo.xgt.energy-kwh"
        ]
        let actual = logging.SignalPolicies |> Seq.map (fun policy -> policy.SignalId.Value) |> Set.ofSeq
        Assert.True((actual = expected), $"unexpected signal policy set: {actual}")
    finally
        if File.Exists output then File.Delete output
