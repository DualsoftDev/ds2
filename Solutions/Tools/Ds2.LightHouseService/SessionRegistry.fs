namespace Ds2.LightHouseService

open System
open System.Collections.Concurrent
open System.IO
open System.Threading

/// ATTACH 시점 진단 DU (todo-lighthouse-kb-server.md s3-r0 D-S3-4).
///
/// `IndexerVersionGateResult` (upload 시점) 와 분리 — attach 는 session lazy open 시점에 발생,
/// gate 는 client 의 index.db 가 host 호환 범위에 있는지 검증 (upload 시점).
[<NoComparison; NoEquality; RequireQualifiedAccess>]
type AttachDiagnostic =
    | Ok
    | DbMissing      of collectionId: string * dbPath: string
    | OpenFailure    of collectionId: string * reason: string
    | SchemaMismatch of collectionId: string * reason: string


/// `POST /sessions` 응답 SSOT — endpoint 가 JSON 으로 그대로 직렬화.
///
/// AcceptedCollectionIds = registry 에 존재 + 색인됨 (index.db 존재). 본 셋만 token 의 active 셋.
/// UnknownIds = registry 미존재 (Promaker 가 LlmConfig 에서 제거 trigger — Q4 lazy sync).
/// UnindexableIds = registry 존재하나 index.db 미존재 / 손상 (재시도 가능 — LlmConfig 유지).
[<NoComparison; NoEquality>]
type SessionCreateResult = {
    Token: string
    AcceptedCollectionIds: string array
    UnknownIds: string array
    UnindexableIds: string array
}


/// session 의 in-memory state (§3.8). 본 phase 의 SSOT.
///
/// 동시성:
/// - `SyncRoot` lock 안에서 `Kb` open / Dispose / `CollectionIds` 변경 / Searcher 호출 직렬화.
/// - SQLite WAL + ro mode 라 multi-reader 자연 가능하지만, Kb.Dispose 와 read 의 race 회피를 위해 단순 lock 채택.
/// - 한 session = LLM 한 chat 의 sequential turn 가정 (§3.8 L1) 이라 lock contention 미미.
///
/// lazy ATTACH (D-S3-1):
/// - `Kb = None` = 아직 open 안 함 (또는 OnDeleted / OnPayloadSwapped / sweep 후 폐기).
/// - 첫 MCP call 시 `SessionKb.attach` 가 `KnowledgeBase.openCollections(activePaths)` 호출 + 캐싱.
/// - subsequent call = 재사용.
[<NoComparison; NoEquality>]
type SessionState = {
    Token: string
    /// active 셋. delete/swap 시 mutation. 본 array 순서 = ATTACH alias kb0..kbN-1 순서.
    mutable CollectionIds: string array
    /// lazy ATTACH 된 KnowledgeBase facade (D-S3-1). None = 미 open / 폐기됨.
    mutable Kb: Ds2.LightHouse.KnowledgeBase option
    mutable LastUsedAt: DateTime
    /// `X-User-Identity` 헤더 값 — audit log 박제.
    UserIdentity: string
    /// per-session 직렬화 lock. Kb open / Dispose / search 호출 보호.
    SyncRoot: obj
}


/// session lookup 결과 — MCP 호출 진입 시점 분기.
[<NoComparison; NoEquality; RequireQualifiedAccess>]
type SessionLookup =
    | NotFound
    | Active of SessionState


/// collection id → KnowledgeBase 의 activePaths (collection 디렉토리 절대경로) 변환.
///
/// SessionRegistry 가 Registry / Storage / ZipImport 의존을 직접 갖지 않도록 함수 type 으로 주입.
/// Phase S3 default = `AttachmentResolver.fromRegistry storageRoot` (Registry.fs + Storage.fs + ZipImport.collectionDirName 조립).
[<NoComparison; NoEquality>]
type ResolveResult = {
    AcceptedIds: string array
    Paths: string array
    UnknownIds: string array
    UnindexableIds: string array
}

[<NoComparison; NoEquality>]
type AttachmentResolver = {
    /// 주어진 collection id 목록 → (acceptedIds, paths, unknownIds, unindexableIds).
    /// paths.Length = acceptedIds.Length, 같은 index 가 같은 collection.
    Resolve: string array -> ResolveResult
}


/// `AttachmentResolver` 의 default 구현 — Registry (registry.json) + Storage 의 collection 디렉토리 조립.
[<RequireQualifiedAccess>]
module AttachmentResolver =

    /// collection id + DisplayName → collection 디렉토리 절대 경로 (Phase S2 의 `Collections\<guid>-<sanitized>\`).
    let collectionPath (storageRoot: string) (id: string) (displayName: string) : string =
        let dirName = ZipImport.collectionDirName id displayName
        Path.Combine(Storage.collectionsDir storageRoot, dirName)

    /// `<collection-root>/.lighthouse-kb/index.db` 존재 여부 — unindexable 판별 SSOT.
    /// review IM-6 (2/7 reviewer): SqliteStore 직접 참조 우회 제거 → KnowledgeBase facade delegation.
    let isIndexed (collectionRoot: string) : bool =
        Ds2.LightHouse.KnowledgeBase.isIndexed collectionRoot

    /// Registry 기반 default resolver. SessionRegistry 의 생성자 인자.
    let fromRegistry (storageRoot: string) : AttachmentResolver =
        {
            Resolve = fun (requestedIds: string array) ->
                let snapshot = Registry.listSnapshot storageRoot
                let byId =
                    snapshot
                    |> Array.map (fun e -> e.Id, e)
                    |> Map.ofArray

                let accepted = ResizeArray<string>()
                let paths = ResizeArray<string>()
                let unknown = ResizeArray<string>()
                let unindexable = ResizeArray<string>()

                for id in requestedIds do
                    match Map.tryFind id byId with
                    | None -> unknown.Add id
                    | Some entry ->
                        let p = collectionPath storageRoot entry.Id entry.DisplayName
                        if isIndexed p then
                            accepted.Add id
                            paths.Add p
                        else
                            unindexable.Add id

                {
                    AcceptedIds = accepted.ToArray()
                    Paths = paths.ToArray()
                    UnknownIds = unknown.ToArray()
                    UnindexableIds = unindexable.ToArray()
                }
        }


/// session 의 lazy KB 관리 — `SyncRoot` lock 안에서만 호출 의무.
///
/// `attach` 는 SessionState 의 CollectionIds 를 매번 resolver 로 path 변환 (D-S3-3 의 swap 회복 정합).
/// `dispose` 는 Kb 가 있으면 Dispose 후 `Kb <- None`. 다음 attach 가 lazy re-open.
[<RequireQualifiedAccess>]
module SessionKb =

    /// `SyncRoot` lock 안에서 호출. 첫 호출 시 KB open + 캐시. 이미 있으면 재사용.
    /// CollectionIds 가 빈 셋이어도 valid KB (empty active 셋, KnowledgeBase 가 empty 결과 반환).
    let attach (resolver: AttachmentResolver) (s: SessionState) : Ds2.LightHouse.KnowledgeBase =
        match s.Kb with
        | Some kb -> kb
        | None ->
            let resolved = resolver.Resolve s.CollectionIds
            // CollectionIds 가 OnDeleted 이후 줄어들었다면 resolved.AcceptedIds 도 자동 좁아짐.
            // resolve 시점에 storage 외부 변경 (예: 디렉토리 직접 삭제) 으로 unindexable / unknown 새로 발생할 수 있음.
            // 그 경우 본 호출자 (MCP) 는 empty / partial 결과로 동작. CollectionIds 자체는 갱신 안 함
            // (다음 lifecycle 이벤트 OnDeleted 가 와야 SessionState 정합 회복).
            let kb = Ds2.LightHouse.KnowledgeBase.openCollections resolved.Paths
            s.Kb <- Some kb
            kb

    /// `SyncRoot` lock 안에서 호출. KB 폐기 + Kb <- None. 다음 attach 가 lazy re-open.
    let dispose (s: SessionState) =
        match s.Kb with
        | None -> ()
        | Some kb ->
            try kb.Dispose () with ex ->
                Log.service.Warn(sprintf "SessionKb.dispose: KB Dispose 실패 token=%s ex=%s" s.Token ex.Message)
        s.Kb <- None


/// SessionRegistry surface — `ICollectionLifecycleNotifier` 의 Phase S3 구현체.
///
/// Phase S2 의 `LoggingCollectionLifecycleNotifier` 를 swap. Program.fs 에서 DI 등록 변경.
type ISessionRegistry =
    inherit ICollectionLifecycleNotifier
    /// 신규 session 발급. unknown / unindexable 분류 응답.
    /// ATTACH limit (SqliteStore.MaxAttachedDbs) 초과 = `InvalidOperationException` — endpoint 가 400 변환.
    abstract CreateSession: collectionIds: string array * userIdentity: string -> SessionCreateResult
    /// token → SessionState. 본 호출이 `LastUsedAt` 자동 갱신 안 함 — caller (AttachmentTools) 가 acquire/release 시 갱신.
    abstract TryGet: token: string -> SessionLookup
    /// 명시 해제 (DELETE /sessions/{token} 또는 client process exit). KB Dispose 포함.
    abstract Delete: token: string -> bool
    /// idle TTL sweep (SessionSweepService). `lastUsedAt < now - idleAfter` 인 session 제거. 반환 = 제거 개수.
    abstract SweepIdle: now: DateTime * idleAfter: TimeSpan -> int
    /// 현 시점 세션 개수 — 진단/테스트용.
    abstract Count: int
    /// service shutdown 시 호출 (모든 KB Dispose). graceful host stop.
    abstract DisposeAll: unit -> unit


/// `ISessionRegistry` 의 in-memory 구현 (Phase S3 SSOT, todo-lighthouse-kb-server.md §3.8).
///
/// 책임:
/// - token 발급 (`Guid.NewGuid().ToString("N")` — URL-safe 32-char hex)
/// - per-session ConcurrentDictionary 보관
/// - lifecycle 통합 — `OnDeleted` / `OnPayloadSwapped` 가 모든 session 의 해당 collection 폐기 (D-S3-3)
type SessionRegistry(resolver: AttachmentResolver) =

    let sessions = ConcurrentDictionary<string, SessionState>()

    /// 모든 session 에서 해당 collection 폐기 — D-S3-3 두 단계 모델의 server-side 구현.
    /// 1) activeCollectionIds 에서 제거
    /// 2) Kb 이미 open 이면 Dispose (다음 MCP call 시 lazy re-open)
    let purgeCollectionFromSessions (collectionId: string) =
        for kvp in sessions do
            let s = kvp.Value
            lock s.SyncRoot (fun () ->
                if s.CollectionIds |> Array.contains collectionId then
                    s.CollectionIds <- s.CollectionIds |> Array.filter (fun id -> id <> collectionId)
                    SessionKb.dispose s)

    interface ISessionRegistry with

        member _.CreateSession(collectionIds, userIdentity) =
            let dedup = collectionIds |> Array.distinct
            let resolved = resolver.Resolve dedup

            // ATTACH limit 가드 (Q2 / MA17) — accepted 셋 길이 기준. unknown / unindexable 제외 후 한도 확인.
            // `SqliteStore.MaxAttachedDbs = 10` SSOT 재사용 (parent r10 박제).
            if resolved.AcceptedIds.Length > Ds2.LightHouse.KnowledgeBase.MaxAttachedDbs then
                raise (InvalidOperationException(
                    sprintf "ATTACH 제한 초과 — accepted %d > limit %d (Q2 / MA17). client 가 active 셋 줄여서 재요청 필요."
                        resolved.AcceptedIds.Length Ds2.LightHouse.KnowledgeBase.MaxAttachedDbs))

            let token = Guid.NewGuid().ToString("N")
            let now = DateTime.UtcNow
            let state = {
                Token = token
                CollectionIds = resolved.AcceptedIds
                Kb = None
                LastUsedAt = now
                UserIdentity = userIdentity
                SyncRoot = obj()
            }

            // Guid.NewGuid().ToString("N") collision 가능성 = 사실상 0. 충돌 시 TryAdd false → 재시도 안내.
            if not (sessions.TryAdd(token, state)) then
                raise (InvalidOperationException(sprintf "session token 충돌 — 재시도 필요 (token=%s)" token))

            Log.audit.Info(
                sprintf "session create — token=%s user=%s accepted=%d unknown=%d unindexable=%d"
                    (Log.tokenFingerprint token) (Log.sanitizeForLog userIdentity)
                    resolved.AcceptedIds.Length resolved.UnknownIds.Length resolved.UnindexableIds.Length)

            {
                Token = token
                AcceptedCollectionIds = resolved.AcceptedIds
                UnknownIds = resolved.UnknownIds
                UnindexableIds = resolved.UnindexableIds
            }

        member _.TryGet(token) =
            if String.IsNullOrEmpty token then SessionLookup.NotFound
            else
                match sessions.TryGetValue token with
                | true, s -> SessionLookup.Active s
                | _ -> SessionLookup.NotFound

        member _.Delete(token) =
            match sessions.TryRemove token with
            | true, s ->
                lock s.SyncRoot (fun () -> SessionKb.dispose s)
                Log.audit.Info(sprintf "session delete — token=%s user=%s" (Log.tokenFingerprint token) (Log.sanitizeForLog s.UserIdentity))
                true
            | _ -> false

        member _.SweepIdle(now, idleAfter) =
            let cutoff = now - idleAfter
            let expired =
                sessions
                |> Seq.filter (fun kvp -> kvp.Value.LastUsedAt < cutoff)
                |> Seq.map (fun kvp -> kvp.Key)
                |> Seq.toArray
            let mutable swept = 0
            for token in expired do
                match sessions.TryRemove token with
                | true, s ->
                    lock s.SyncRoot (fun () -> SessionKb.dispose s)
                    Log.audit.Info(sprintf "session sweep — token=%s user=%s lastUsedAt=%O"
                        (Log.tokenFingerprint token) (Log.sanitizeForLog s.UserIdentity) s.LastUsedAt)
                    swept <- swept + 1
                | _ -> ()
            swept

        member _.Count = sessions.Count

        member _.DisposeAll() =
            for kvp in sessions.ToArray() do
                match sessions.TryRemove kvp.Key with
                | true, s ->
                    lock s.SyncRoot (fun () -> SessionKb.dispose s)
                | _ -> ()

    interface ICollectionLifecycleNotifier with
        member _.OnDeleted(collectionId) =
            Log.audit.Info(sprintf "lifecycle: OnDeleted → SessionRegistry purge — id=%s" collectionId)
            purgeCollectionFromSessions collectionId

        member _.OnPayloadSwapped(collectionId) =
            // swap 도 동일 처리 — 다음 MCP call 시 새 path 의 새 index.db 로 lazy re-attach.
            // (CollectionIds 에서 제거 *안 함* — swap 은 같은 id 의 새 payload 라 active 셋 보존.)
            // KB 만 폐기 → 다음 attach 시 같은 paths 로 재 open.
            Log.audit.Info(sprintf "lifecycle: OnPayloadSwapped → SessionRegistry KB 폐기 (lazy re-attach) — id=%s" collectionId)
            for kvp in sessions do
                let s = kvp.Value
                lock s.SyncRoot (fun () ->
                    if s.CollectionIds |> Array.contains collectionId then
                        SessionKb.dispose s)
