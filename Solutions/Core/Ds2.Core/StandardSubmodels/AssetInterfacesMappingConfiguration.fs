namespace Ds2.Core.StandardSubmodels

open System.Collections.Generic
open Ds2.Core

/// Asset Interfaces Mapping Configuration (IDTA 02027 v2.0).
///
/// AIMC is **optional**. Per spec §04-D, the raw AID → SQLite/Kafka path is
/// already complete without AIMC — only projections of collected values back
/// into other AAS submodels (typically `OperationalData`) require this
/// declarative mapping.
[<AutoOpen>]
module AssetInterfacesMappingConfigurationTypes =

    /// Value transform applied when copying from source to sink.
    type MappingTransform =
        | Identity
        /// Linear: sink = source × factor + offset
        | LinearScale of factor: float * offset: float
        /// Free-form expression (parser TBD; carried verbatim for now).
        | Expression of source: string

    /// One AID data point mapped to one AAS element sink.
    type AimcMapping = {
        /// Path within AID (`InterfaceOPCUA/InteractionMetadata/SpindleSpeed`).
        SourceAidPath: string
        /// Path within some other submodel (`OperationalData/SpindleSpeed`).
        SinkAasElementPath: string
        Transform: MappingTransform
    }

    /// AAS Submodel "AssetInterfacesMappingConfiguration" — IDTA 02027 v2.0.
    type AssetInterfacesMappingConfiguration() =
        member val IdShort = "AssetInterfacesMappingConfiguration" with get, set
        member val SemanticId : SemanticId =
            SemanticId "https://admin-shell.io/idta/AssetInterfacesMappingConfiguration/2/0/Submodel"
            with get, set
        member val Mappings = ResizeArray<AimcMapping>() with get, set

        /// Provenance §C — Mapping IdShort ("Mapping_%08x") 중 KpiWalker 가 auto-generate 한 것들.
        member val AutoOriginIdShorts = HashSet<string>() with get, set

        /// Provenance §C — 사용자가 삭제한 auto-generated Mapping IdShort (tombstones).
        member val SuppressedAutoIdShorts = HashSet<string>() with get, set

        static member Empty () = AssetInterfacesMappingConfiguration()
