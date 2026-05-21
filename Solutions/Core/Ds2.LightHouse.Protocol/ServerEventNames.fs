namespace Ds2.LightHouse.Protocol

/// **K4 Protocol SSOT (A2)** — SSE event name 단일 박제.
///
/// **본 module 박제 이전** = server F# `Ds2.LightHouseService.ServerEventNames` (`EventBus.fs`) +
/// client C# `Promaker.Knowledge.ServerEventNames` (`ServerEventNames.cs`) 양분. K4 통합으로
/// 단일 SSOT — 새 event 추가 시 본 module 1회 박제만으로 양측 컴파일러 강제.
///
/// 신규 event 추가 시 본 module 의 `[<Literal>]` 1줄만 박제 — server `ServerEvent` factory / client
/// subscriber 분기 caller 가 동일 SSOT 참조.
module ServerEventNames =

    [<Literal>]
    let CollectionAdded = "collection-added"

    [<Literal>]
    let CollectionUpdated = "collection-updated"

    [<Literal>]
    let CollectionDeleted = "collection-deleted"

    [<Literal>]
    let Keepalive = "keepalive"

    /// **D-S7-2c (s6-r32)** — client → server publish (Phase 2 vision caption 진행률, `POST /events/caption-progress`)
    /// → server fan-out.
    [<Literal>]
    let CaptionProgress = "caption-progress"

    /// **D-S7-5 (s6-r60)** — resumable upload PATCH 진행률. CollectionId 필드 = uploadId (wire 재사용).
    [<Literal>]
    let UploadProgress = "upload-progress"
