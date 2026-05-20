namespace Ds2.LightHouseService

open Ds2.LightHouse.Protocol

/// **A2 (K4 Protocol SSOT 통합, 2026-05-20)** — 본 file 의 record `MetaJson` + `MetaJsonSchema` module +
/// `MetaJson` 일반 helper (FileName / SubDir / path / load / save / stampServerFields / jsonOptions) 는
/// 모두 `Ds2.LightHouse.Protocol` 로 이전. server-internal `toRegistryEntry` 변환만 본 file 에 잔류 —
/// `CollectionEntry` / `CollectionAcl` (Registry.fs 의존) 가 server-side 박제라 Protocol 에 둘 수 없음.
///
/// caller (CollectionEndpoints / UploadsEndpoint) 는 `open Ds2.LightHouse.Protocol` 후 `MetaJson.load /
/// save / stampServerFields / path / FileName / SubDir` 동일 호출 + `MetaJsonRegistry.toRegistryEntry` 호출.
[<RequireQualifiedAccess>]
module MetaJsonRegistry =

    /// CollectionEntry 변환 — Registry 에 upsert 할 때 사용.
    /// meta 의 file/byte count 가 0 인 경우 (0-doc collection, MA18) 그대로 보존.
    /// **s6-r66 D-S7-4**: Acl=null default — T1/T2 모드 시 무시. T3 모드는 admin 이 별 endpoint (Phase S7 후속) 또는
    /// registry.json 수동 편집으로 acl 박제. 본 helper 는 upload path 의 default 만 책임.
    let toRegistryEntry (meta: MetaJson) : CollectionEntry =
        { Id = meta.Id
          DisplayName = meta.Title
          IndexerVersion = meta.IndexerVersion
          FileCount = meta.FileCount
          TotalSourceBytes = meta.TotalSourceBytes
          CreatedAt = meta.CreatedAt
          ImportedAt = meta.ImportedAt
          ImportedBy = meta.ImportedBy
          StorageRelPath = meta.StorageRelPath
          Status = "idle"
          ErrorReason = null
          LastImportedAt = meta.ImportedAt
          Acl = Unchecked.defaultof<CollectionAcl> }
