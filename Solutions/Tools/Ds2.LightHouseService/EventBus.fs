namespace Ds2.LightHouseService

open System
open System.Collections.Concurrent
open System.Threading.Channels
open System.Text.Json.Serialization
open Ds2.LightHouse.Protocol

// **A2 (K4 Protocol SSOT 통합, 2026-05-20)** — `ServerEventNames` 양분 박제 폐기.
// 본 file 의 module 이 사라지고 `Ds2.LightHouse.Protocol.ServerEventNames` (5 [<Literal>]) 단일 SSOT 로 routing.
// caller (UploadsEndpoint / EventsEndpoint 등) 는 `open Ds2.LightHouse.Protocol` 후 `ServerEventNames.X` 동일 호출.

/// Phase S7 D-S7-2 — server → client SSE event payload (§3.7 SSE schema 박제, s6-r27 / s6-r32).
///
/// SSOT: `event` 는 enum 화된 한정 값 (`ServerEventNames` module 의 [<Literal>] 5 종 박제).
/// payload 별로 의미 있는 필드만 채워서 발행 (나머지는 None).
/// JSON 직렬화는 `JsonIgnoreCondition.WhenWritingNull` 로 None 필드 자동 생략 — wire 절약.
///
/// 현 발행 event:
/// - `collection-added` — POST /collections 성공 (collectionId 박제)
/// - `collection-updated` — POST /collections/{id}/payload swap 성공 (collectionId 박제)
/// - `collection-deleted` — DELETE /collections/{id} 성공 (collectionId 박제)
/// - `caption-progress` — client → server publish (POST /events/caption-progress, D-S7-2c) → server fan-out.
///   payload = `collectionId` + `progress` (0~100) + 선택 `message`.
/// - `keepalive` — 30s heartbeat (subscriber 가 disconnect 감지)
///
/// 향후 추가 후보:
/// - `session-expired` — SessionRegistry idle TTL backstop
type ServerEvent =
    { [<JsonPropertyName("event")>]
      Event: string
      [<JsonPropertyName("collectionId")>]
      [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
      CollectionId: string
      [<JsonPropertyName("progress")>]
      [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
      Progress: Nullable<int>
      [<JsonPropertyName("message")>]
      [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
      Message: string
      [<JsonPropertyName("timestamp")>]
      TimestampUtc: string }

/// In-memory pub-sub. subscriber 마다 bounded Channel<ServerEvent> 등록 — slow consumer 가
/// publisher 차단 안 함 (DropOldest policy).
///
/// **lifecycle**: 한 SSE request 당 하나의 subscriber. `Subscribe()` 가 reader 반환 + dispose 시
/// `Unsubscribe(id)` 자동 호출. 본 객체는 process-wide singleton (DI).
///
/// **fan-out 안전성**: Publish 가 ConcurrentDictionary.Values snapshot 위에서 TryWrite 비동기 호출.
/// dropped subscriber (e.g. disposed 직전) 는 silent skip — 대신 disconnect 가 다음 `WaitToReadAsync`
/// 에서 false 반환 후 subscribe handler 가 자연 종료.
[<Sealed>]
type EventBus() =

    /// 한 subscriber 의 buffered 한도. caption 진행률 등 high-rate event 도입 대비.
    [<Literal>]
    let SubscriberCapacity = 64

    let subscribers = ConcurrentDictionary<Guid, ChannelWriter<ServerEvent>>()

    /// 새 subscriber 등록 → reader + Guid 반환. dispose 책임 = caller (`Unsubscribe` 호출).
    member _.Subscribe() : Guid * ChannelReader<ServerEvent> =
        let opts = BoundedChannelOptions(SubscriberCapacity)
        opts.FullMode <- BoundedChannelFullMode.DropOldest
        opts.SingleReader <- true
        opts.SingleWriter <- false  // Publish 가 다른 thread 에서 호출 가능
        let channel = Channel.CreateBounded<ServerEvent>(opts)
        let id = Guid.NewGuid()
        subscribers.[id] <- channel.Writer
        id, channel.Reader

    /// subscriber 해제 + channel complete (reader 가 WaitToReadAsync false 반환).
    member _.Unsubscribe(id: Guid) : unit =
        match subscribers.TryRemove id with
        | true, writer -> writer.TryComplete() |> ignore
        | _ -> ()

    /// 모든 active subscribers 에 fan-out. TryWrite 가 DropOldest 적용 — 차단 0.
    /// publisher path (POST/DELETE endpoint) 가 hot — 본 메서드는 lock-free.
    member _.Publish(evt: ServerEvent) : unit =
        for kvp in subscribers do
            kvp.Value.TryWrite evt |> ignore

    /// 현재 subscriber 수 (test / 진단용).
    member _.SubscriberCount : int = subscribers.Count


/// `ServerEvent` factory helper — 신규 event 추가 시 본 module 에 한 줄씩 박제.
[<RequireQualifiedAccess>]
module ServerEvent =

    let private utcNow () = DateTime.UtcNow.ToString("o")

    let collectionAdded (collectionId: string) : ServerEvent =
        { Event = ServerEventNames.CollectionAdded
          CollectionId = collectionId
          Progress = Nullable()
          Message = null
          TimestampUtc = utcNow() }

    let collectionUpdated (collectionId: string) : ServerEvent =
        { Event = ServerEventNames.CollectionUpdated
          CollectionId = collectionId
          Progress = Nullable()
          Message = null
          TimestampUtc = utcNow() }

    let collectionDeleted (collectionId: string) : ServerEvent =
        { Event = ServerEventNames.CollectionDeleted
          CollectionId = collectionId
          Progress = Nullable()
          Message = null
          TimestampUtc = utcNow() }

    let keepalive () : ServerEvent =
        { Event = ServerEventNames.Keepalive
          CollectionId = null
          Progress = Nullable()
          Message = null
          TimestampUtc = utcNow() }

    /// **D-S7-2c (s6-r32)** — client → server publish path (POST /events/caption-progress) 가 본 factory 호출 후 fan-out.
    /// progress 는 0~100 percent (clamp 은 endpoint 책임). message 는 선택 (caption 단계 안내, e.g. "5/120 image").
    let captionProgress (collectionId: string) (progress: int) (message: string) : ServerEvent =
        { Event = ServerEventNames.CaptionProgress
          CollectionId = collectionId
          Progress = Nullable progress
          Message = (if String.IsNullOrEmpty message then null else message)
          TimestampUtc = utcNow() }

    /// **D-S7-5 (s6-r60)** — resumable upload 진행률 (PATCH /uploads/{id} 시점). caller (UploadsEndpoint) 가
    /// 본 factory 호출 후 EventBus.Publish — subscriber 가 같은 user 의 다른 instance / KbManagerDialog
    /// 진행률 수신. collectionId 는 upload 시점에 아직 미존재 → uploadId 박제. CollectionId 필드 재사용
    /// (wire 절약, client 측 분기 = event="upload-progress" 시 collectionId 가 uploadId 의미).
    let uploadProgress (uploadId: string) (progress: int) (message: string) : ServerEvent =
        { Event = ServerEventNames.UploadProgress
          CollectionId = uploadId
          Progress = Nullable progress
          Message = (if String.IsNullOrEmpty message then null else message)
          TimestampUtc = utcNow() }
