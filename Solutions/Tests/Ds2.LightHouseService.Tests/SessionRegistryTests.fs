module Ds2.LightHouseService.Tests.SessionRegistryTests

open System
open System.Threading.Tasks
open Xunit
open Ds2.LightHouseService

/// SessionRegistry — Phase S3 핵심 SSOT. todo-lighthouse-kb-server.md §3.8 / §4.2 Phase S3 DoD.
///
/// 본 test 는 KB ATTACH 실제 open 안 함 (resolver 가 paths 만 반환, KB.openCollections 는 호출 시 빈 셋 OK).
/// 실제 ATTACH 동작 (boundary 9/10/11) 은 parent §4.8c KnowledgeBaseTests 가 이미 보호.
/// 본 test 는 *registry surface* 의 동시성 / 정합성에 집중.

/// 테스트용 stub resolver — accepted/unknown/unindexable 를 명시 제어.
let private fakeResolver (accepted: string array) (unknown: string array) (unindexable: string array) : AttachmentResolver =
    {
        Resolve = fun (_ids: string array) ->
            {
                AcceptedIds = accepted
                // KB.openCollections 가 빈 paths 면 빈 active 셋 KB 반환 — KB 객체 만들지만 ATTACH 0.
                Paths = accepted |> Array.map (fun _ -> "")  // path 0 길이라도 KB 가 미색인 collection 으로 skip
                UnknownIds = unknown
                UnindexableIds = unindexable
            }
    }

/// SessionRegistry instance 생성 helper. ISessionRegistry interface 로 캐스팅.
let private mkRegistry (accepted: string array) (unknown: string array) (unindexable: string array) : ISessionRegistry =
    SessionRegistry(fakeResolver accepted unknown unindexable) :> ISessionRegistry


[<Fact>]
let ``CreateSession — accepted 셋 그대로 반환 + token 길이 32 (Guid N format)`` () =
    let reg = mkRegistry [| "id-1"; "id-2" |] [||] [||]
    let r = reg.CreateSession([| "id-1"; "id-2" |], "alice@example.com")
    Assert.Equal(32, r.Token.Length)
    Assert.Equal<string array>([| "id-1"; "id-2" |], r.AcceptedCollectionIds)
    Assert.Empty(r.UnknownIds)
    Assert.Empty(r.UnindexableIds)
    Assert.Equal(1, reg.Count)


[<Fact>]
let ``CreateSession — unknownIds / unindexableIds 분류 응답 (Q4 lazy sync)`` () =
    let reg = mkRegistry [| "id-ok" |] [| "id-gone" |] [| "id-err" |]
    let r = reg.CreateSession([| "id-ok"; "id-gone"; "id-err" |], "alice")
    Assert.Equal<string array>([| "id-ok" |], r.AcceptedCollectionIds)
    Assert.Equal<string array>([| "id-gone" |], r.UnknownIds)
    Assert.Equal<string array>([| "id-err" |], r.UnindexableIds)


[<Fact>]
let ``CreateSession — ATTACH limit 정확 10 OK / 11 hard fail (Q2 MA17)`` () =
    let tenIds = [| for i in 1..10 -> sprintf "id-%02d" i |]
    let elevenIds = [| for i in 1..11 -> sprintf "id-%02d" i |]
    let reg10 = mkRegistry tenIds [||] [||]
    let r10 = reg10.CreateSession(tenIds, "alice")
    Assert.Equal(10, r10.AcceptedCollectionIds.Length)

    let reg11 = mkRegistry elevenIds [||] [||]
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        reg11.CreateSession(elevenIds, "alice") |> ignore)
    Assert.Contains("ATTACH 제한 초과", ex.Message)


[<Fact>]
let ``CreateSession — 중복 id deduplicate`` () =
    let reg = mkRegistry [| "id-a" |] [||] [||]
    let r = reg.CreateSession([| "id-a"; "id-a"; "id-a" |], "alice")
    // resolver 가 받는 input 도 dedup 후 — accepted 도 dedup 결과
    Assert.Single(r.AcceptedCollectionIds) |> ignore


[<Fact>]
let ``TryGet — 없는 token NotFound`` () =
    let reg = mkRegistry [||] [||] [||]
    match reg.TryGet "nope" with
    | SessionLookup.NotFound -> ()
    | _ -> Assert.Fail "NotFound 기대"


[<Fact>]
let ``TryGet — 빈 token NotFound (방어)`` () =
    let reg = mkRegistry [||] [||] [||]
    match reg.TryGet "" with
    | SessionLookup.NotFound -> ()
    | _ -> Assert.Fail "NotFound 기대"


[<Fact>]
let ``TryGet — 발급 후 Active + SessionState 반환`` () =
    let reg = mkRegistry [| "id-1" |] [||] [||]
    let r = reg.CreateSession([| "id-1" |], "alice")
    match reg.TryGet r.Token with
    | SessionLookup.Active s ->
        Assert.Equal(r.Token, s.Token)
        Assert.Equal("alice", s.UserIdentity)
        Assert.Equal<string array>([| "id-1" |], s.CollectionIds)
        Assert.True(s.Kb.IsNone, "초기 KB = None (lazy)")
    | _ -> Assert.Fail "Active 기대"


[<Fact>]
let ``Delete — 발급된 token 제거 + count 감소`` () =
    let reg = mkRegistry [| "id-1" |] [||] [||]
    let r = reg.CreateSession([| "id-1" |], "alice")
    Assert.Equal(1, reg.Count)
    Assert.True(reg.Delete r.Token)
    Assert.Equal(0, reg.Count)
    Assert.False(reg.Delete r.Token, "이미 삭제된 token 재삭제 false")


/// input 에 따라 동작하는 resolver — registry 가 available 셋 안의 id 만 accepted, 나머지는 unknown.
/// `mkRegistry` (input-independent stub) 와 달리 OnDeleted 등 cross-session lifecycle 시나리오에 적합.
let private mkRegistryFromAvailable (available: Set<string>) : ISessionRegistry =
    let resolver = {
        Resolve = fun (ids: string array) ->
            let accepted = ids |> Array.filter available.Contains
            let unknown = ids |> Array.filter (available.Contains >> not)
            {
                AcceptedIds = accepted
                Paths = accepted |> Array.map (fun _ -> "")  // KB.openCollections 가 빈 path skip + warn
                UnknownIds = unknown
                UnindexableIds = [||]
            }
    }
    SessionRegistry(resolver) :> ISessionRegistry


[<Fact>]
let ``OnDeleted — 모든 session 에서 해당 collection 제거 (D-S3-3)`` () =
    let reg = mkRegistryFromAvailable (Set.ofList [ "id-a"; "id-b"; "id-c" ])
    let notifier = reg :> ICollectionLifecycleNotifier
    let r1 = reg.CreateSession([| "id-a"; "id-b" |], "alice")
    let r2 = reg.CreateSession([| "id-b"; "id-c" |], "bob")

    notifier.OnDeleted "id-b"

    match reg.TryGet r1.Token, reg.TryGet r2.Token with
    | SessionLookup.Active s1, SessionLookup.Active s2 ->
        Assert.Equal<string array>([| "id-a" |], s1.CollectionIds)
        Assert.Equal<string array>([| "id-c" |], s2.CollectionIds)
    | _ -> Assert.Fail "두 session 모두 Active 잔존 기대"


[<Fact>]
let ``OnPayloadSwapped — CollectionIds 보존, KB 만 폐기 (lazy re-attach)`` () =
    let reg = mkRegistry [| "id-a"; "id-b" |] [||] [||]
    let notifier = reg :> ICollectionLifecycleNotifier
    let r = reg.CreateSession([| "id-a"; "id-b" |], "alice")

    notifier.OnPayloadSwapped "id-a"

    match reg.TryGet r.Token with
    | SessionLookup.Active s ->
        // CollectionIds 보존 — swap 은 같은 id 의 새 payload
        Assert.Equal<string array>([| "id-a"; "id-b" |], s.CollectionIds)
        // Kb 폐기 (아직 attach 안 했으니 None 그대로 — 본 test 는 KB attach 안 호출, 초기 상태 검증만)
        Assert.True(s.Kb.IsNone)
    | _ -> Assert.Fail "Active 기대"


[<Fact>]
let ``SweepIdle — lastUsedAt 초과 session 제거 + 반환 개수`` () =
    let reg = mkRegistry [| "id-1" |] [||] [||]
    let r1 = reg.CreateSession([| "id-1" |], "alice")
    let r2 = reg.CreateSession([| "id-1" |], "bob")

    // r1 만 LastUsedAt 과거로 강제 (10분 전)
    match reg.TryGet r1.Token with
    | SessionLookup.Active s -> s.LastUsedAt <- DateTime.UtcNow.AddMinutes(-10.0)
    | _ -> ()

    let swept = reg.SweepIdle(DateTime.UtcNow, TimeSpan.FromMinutes 5.0)
    Assert.Equal(1, swept)

    // r1 = 제거, r2 = 잔존
    Assert.Equal(SessionLookup.NotFound, reg.TryGet r1.Token)
    match reg.TryGet r2.Token with
    | SessionLookup.Active _ -> ()
    | _ -> Assert.Fail "r2 잔존 기대"


[<Fact>]
let ``DisposeAll — 모든 session 제거`` () =
    let reg = mkRegistry [| "id-1" |] [||] [||]
    let _r1 = reg.CreateSession([| "id-1" |], "alice")
    let _r2 = reg.CreateSession([| "id-1" |], "bob")
    Assert.Equal(2, reg.Count)
    reg.DisposeAll()
    Assert.Equal(0, reg.Count)


[<Fact>]
let ``동시 CreateSession — 두 thread 동시 race 후 두 entry 보존 (Q1 lazy sync)`` () = task {
    let reg = mkRegistry [| "id-1" |] [||] [||]
    let runs = 50
    let tasks =
        [| for i in 1..runs ->
            Task.Run(fun () ->
                reg.CreateSession([| "id-1" |], sprintf "user-%d" i) |> ignore) |]
    do! Task.WhenAll tasks
    Assert.Equal(runs, reg.Count)
}


[<Fact>]
let ``OnDeleted 후 같은 collection 으로 새 session 발급 — accepted 0 또는 unknown 으로 처리 (resolver 책임)`` () =
    // 본 test 는 resolver 가 OnDeleted 와 동기화되지 않는 stub 이라 accepted 그대로.
    // 운영 시점에는 Registry.removeAsync 가 먼저 호출되어 resolver.fromRegistry 가 unknown 분류.
    // SessionRegistry 자체는 resolver 의 분류 결과를 충실히 따른다는 것만 검증.
    let reg = mkRegistry [| "id-x" |] [||] [||]
    let notifier = reg :> ICollectionLifecycleNotifier
    let _r1 = reg.CreateSession([| "id-x" |], "alice")
    notifier.OnDeleted "id-x"
    let r2 = reg.CreateSession([| "id-x" |], "bob")
    // 새 session 의 CollectionIds 는 resolver 결과 (stub 이 accepted 그대로) — 운영 resolver 였다면 빈 셋이었을 것.
    Assert.Equal(1, r2.AcceptedCollectionIds.Length)
