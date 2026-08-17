namespace Ds2.Core

open System
open Ds2.Core.Encoding

/// Canonical identifiers shared by the Agent UA model, Collector registry,
/// and AAS TimeSeries access points.
module AssetTelemetryIdentity =

    /// GlobalAssetId used for AID-defined signals that belong to a project.
    let aidProject (projectId: Guid) : GlobalAssetId =
        GlobalAssetId(sprintf "urn:dualsoft:aas:%s" (projectId.ToString("N")))

    /// Stable, URL-safe external identifier for one collected signal series.
    let seriesId (globalAssetId: GlobalAssetId) (signalId: SignalId) : string =
        Base64Url.encode globalAssetId.Value + "." + signalId.Value
