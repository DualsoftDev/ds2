namespace Ds2.LightHouseService

open System.Collections.Concurrent

/// collection 생명주기 이벤트 (todo-lighthouse-kb-server.md Phase S2/S3 hook).
///
/// Phase S2 의 `DELETE /collections/{id}` / `POST /collections/{id}/payload` 가 active session 에 detach 신호 전달 필요.
/// 그러나 Phase S2 시점에 SessionRegistry 가 아직 없음 → interface 박제 + Phase S2 의 구현은 audit log 만.
/// Phase S3 진입 시 `SessionRegistry` 가 본 interface 구현.
type ICollectionLifecycleNotifier =
    abstract member OnDeleted: collectionId: string -> unit
    abstract member OnPayloadSwapped: collectionId: string -> unit


/// Phase S2 default 구현 — audit log 박제만. Phase S3 진입 시 `SessionRegistry` impl 로 swap.
type LoggingCollectionLifecycleNotifier() =
    interface ICollectionLifecycleNotifier with
        member _.OnDeleted(collectionId) =
            Log.audit.Info(sprintf "lifecycle: collection deleted — id=%s (active session detach 신호 — Phase S3 SessionRegistry 진입 시 실 detach)"
                collectionId)
        member _.OnPayloadSwapped(collectionId) =
            Log.audit.Info(sprintf "lifecycle: collection payload swapped — id=%s (active session 의 다음 MCP call 시점에 lazy re-attach — Phase S3)"
                collectionId)
