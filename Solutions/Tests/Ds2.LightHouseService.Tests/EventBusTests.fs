module Ds2.LightHouseService.Tests.EventBusTests

open System
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Xunit
open Ds2.LightHouseService

/// **s6-r50 #5 ⑮ 외부 --review (EventBus unit fact 0 보강)** — 기존 e2e (`EventsSseTests`) 만으로는
/// DropOldest / Unsubscribe lifecycle / fan-out N-subscriber 의 단위 contract 검증 부족. 본 file 이 단위 박제.
///
/// **R5-M2 (s6-r76, external review backlog)** — per-subscriber 2 channel 분리 박제 후 fact 정합 정정.
/// `Subscribe` return = (Guid, lifecycleReader, progressReader). lifecycle event (collection-added/updated/deleted)
/// 는 lifecycle channel (capacity 32), 나머지 progress event (caption/upload-progress / keepalive) 는 progress
/// channel (capacity 64). 의도 = progress burst 가 lifecycle drop 유발 0 (R5-M2 본질).

/// Subscribe 호출 시 unique Guid + non-null 2 reader 반환 + SubscriberCount += 1.
[<Fact>]
let ``Subscribe — unique Guid + non-null 2 reader + SubscriberCount 증가`` () =
    let bus = EventBus()
    Assert.Equal(0, bus.SubscriberCount)
    let id1, lr1, pr1 = bus.Subscribe()
    let id2, lr2, pr2 = bus.Subscribe()
    Assert.NotEqual(id1, id2)
    Assert.NotNull(lr1 :> obj)
    Assert.NotNull(pr1 :> obj)
    Assert.NotNull(lr2 :> obj)
    Assert.NotNull(pr2 :> obj)
    Assert.Equal(2, bus.SubscriberCount)

/// Unsubscribe 호출 시 SubscriberCount -= 1 + 양 reader 의 WaitToReadAsync 가 false 반환 (둘 다 complete).
[<Fact>]
let ``Unsubscribe — SubscriberCount 감소 + 양 reader 가 WaitToReadAsync false (channel complete)`` () =
    let bus = EventBus()
    let id, lifecycleReader, progressReader = bus.Subscribe()
    Assert.Equal(1, bus.SubscriberCount)
    bus.Unsubscribe(id)
    Assert.Equal(0, bus.SubscriberCount)
    let lcWait = lifecycleReader.WaitToReadAsync().AsTask()
    let prWait = progressReader.WaitToReadAsync().AsTask()
    Assert.True(lcWait.Wait(TimeSpan.FromSeconds 1.0))
    Assert.True(prWait.Wait(TimeSpan.FromSeconds 1.0))
    Assert.False(lcWait.Result)
    Assert.False(prWait.Result)

/// Publish fan-out (lifecycle) — N subscribers 모두 publish event 박제 받음 (동일 ServerEvent instance).
[<Fact>]
let ``Publish fan-out — 모든 subscribers 가 동일 lifecycle event 받음 (N=3)`` () =
    let bus = EventBus()
    let _, lr1, _ = bus.Subscribe()
    let _, lr2, _ = bus.Subscribe()
    let _, lr3, _ = bus.Subscribe()
    let evt = ServerEvent.collectionAdded "test-coll-id"
    bus.Publish evt
    let readOne (r: ChannelReader<ServerEvent>) : ServerEvent =
        let t = r.ReadAsync().AsTask()
        Assert.True(t.Wait(TimeSpan.FromSeconds 1.0))
        t.Result
    let e1 = readOne lr1
    let e2 = readOne lr2
    let e3 = readOne lr3
    Assert.Equal<ServerEvent>(evt, e1)
    Assert.Equal<ServerEvent>(evt, e2)
    Assert.Equal<ServerEvent>(evt, e3)

/// **B16 (s6-r88)** — lifecycle channel = Unbounded → 33 event 모두 drop 0 보장.
[<Fact>]
let ``Lifecycle Unbounded — 33 event publish 시 drop 0 (B16)`` () =
    let bus = EventBus()
    let _, lifecycleReader, _ = bus.Subscribe()
    for i = 1 to 33 do
        bus.Publish (ServerEvent.collectionAdded (sprintf "coll-%d" i))
    let readOne () : ServerEvent =
        let t = lifecycleReader.ReadAsync().AsTask()
        Assert.True(t.Wait(TimeSpan.FromSeconds 1.0))
        t.Result
    let first = readOne ()
    // 1st event = coll-1 (drop 0 보장 — Unbounded 채택). TimestampUtc 는 record 별 시각이라 CollectionId 만 비교.
    Assert.Equal("coll-1", first.CollectionId)
    // 남은 32 event 모두 순서대로 read.
    for i = 2 to 33 do
        let evt = readOne ()
        Assert.Equal(sprintf "coll-%d" i, evt.CollectionId)
    let waitMore = lifecycleReader.WaitToReadAsync().AsTask()
    Assert.False(waitMore.Wait(TimeSpan.FromMilliseconds 200.0)) |> ignore

/// Publish 가 dropped (Unsubscribe 된) subscriber 에 silent skip — exception 안 throw.
[<Fact>]
let ``Publish — unsubscribed subscriber 에 silent skip (exception 0)`` () =
    let bus = EventBus()
    let id1, _, _ = bus.Subscribe()
    let _, lr2, _ = bus.Subscribe()
    bus.Unsubscribe(id1)
    // id1 unsubscribe 후 Publish — id2 만 받음. exception throw 안 함.
    bus.Publish (ServerEvent.collectionAdded "after-unsub")
    let t = lr2.ReadAsync().AsTask()
    Assert.True(t.Wait(TimeSpan.FromSeconds 1.0))

// ─── R5-M2 (s6-r76, external review backlog) — channel 분리 박제 신규 fact ──────────────

/// IsLifecycleEvent — collection-added/updated/deleted 만 true, 나머지 false.
[<Fact>]
let ``IsLifecycleEvent — 분기 정합 (R5-M2)`` () =
    let bus = EventBus()
    Assert.True(bus.IsLifecycleEvent(ServerEvent.collectionAdded "x"))
    Assert.True(bus.IsLifecycleEvent(ServerEvent.collectionUpdated "x"))
    Assert.True(bus.IsLifecycleEvent(ServerEvent.collectionDeleted "x"))
    Assert.False(bus.IsLifecycleEvent(ServerEvent.captionProgress "x" 50 null))
    Assert.False(bus.IsLifecycleEvent(ServerEvent.uploadProgress "x" 50 null))
    Assert.False(bus.IsLifecycleEvent(ServerEvent.keepalive()))

/// Publish 가 evt 분기 — lifecycle → lifecycleReader, progress → progressReader (cross-channel leak 0).
[<Fact>]
let ``Publish — lifecycle 과 progress 가 별 channel 으로 분기 (R5-M2)`` () =
    let bus = EventBus()
    let _, lifecycleReader, progressReader = bus.Subscribe()
    bus.Publish (ServerEvent.collectionAdded "lc-evt")
    bus.Publish (ServerEvent.captionProgress "p-evt" 40 null)
    // lifecycle channel 에는 lc-evt 만, progress channel 에는 p-evt 만 있어야 함.
    let lc = lifecycleReader.ReadAsync().AsTask()
    let pr = progressReader.ReadAsync().AsTask()
    Assert.True(lc.Wait(TimeSpan.FromSeconds 1.0))
    Assert.True(pr.Wait(TimeSpan.FromSeconds 1.0))
    Assert.Equal("lc-evt", lc.Result.CollectionId)
    Assert.Equal("p-evt", pr.Result.CollectionId)
    // cross-channel leak 0 — lifecycle channel 빈 / progress channel 빈.
    let lcMore = lifecycleReader.WaitToReadAsync().AsTask()
    let prMore = progressReader.WaitToReadAsync().AsTask()
    Assert.False(lcMore.Wait(TimeSpan.FromMilliseconds 200.0)) |> ignore
    Assert.False(prMore.Wait(TimeSpan.FromMilliseconds 200.0)) |> ignore

/// progress burst (capacity 64 초과) 시 lifecycle event drop 0 — R5-M2 본질.
[<Fact>]
let ``progress burst — lifecycle event drop 0 (R5-M2 본질)`` () =
    let bus = EventBus()
    let _, lifecycleReader, progressReader = bus.Subscribe()
    // 100 progress event — capacity 64 초과 (oldest progress drop 발생).
    for i = 1 to 100 do
        bus.Publish (ServerEvent.captionProgress (sprintf "p-%d" i) 1 null)
    // 그 사이 lifecycle event 5건 publish.
    for i = 1 to 5 do
        bus.Publish (ServerEvent.collectionAdded (sprintf "lc-%d" i))
    // lifecycle channel 안에 정확히 5건 (drop 0). progress 는 capacity 안 (최대 64).
    let readOne (r: ChannelReader<ServerEvent>) : ServerEvent option =
        let t = r.ReadAsync().AsTask()
        if t.Wait(TimeSpan.FromSeconds 1.0) then Some t.Result else None
    let mutable lifecycleCount = 0
    let mutable lifecycleEvts = []
    for _ = 1 to 5 do
        match readOne lifecycleReader with
        | Some e ->
            lifecycleCount <- lifecycleCount + 1
            lifecycleEvts <- e.CollectionId :: lifecycleEvts
        | None -> ()
    Assert.Equal(5, lifecycleCount)
    Assert.Equal<string list>(
        [ "lc-5"; "lc-4"; "lc-3"; "lc-2"; "lc-1" ],  // 역순 (push order)
        lifecycleEvts)
    // 더 read 안 됨 (lifecycle channel 비었음, drop 0 확인).
    let lcMore = lifecycleReader.WaitToReadAsync().AsTask()
    Assert.False(lcMore.Wait(TimeSpan.FromMilliseconds 200.0)) |> ignore
    // progress 는 capacity 64 까지 buffered. 100 publish 중 마지막 64 + 처음 36 drop. drain 확인.
    let mutable progressCount = 0
    let mutable continueLoop = true
    while continueLoop do
        match readOne progressReader with
        | Some _ -> progressCount <- progressCount + 1
        | None -> continueLoop <- false
    Assert.Equal(64, progressCount)
