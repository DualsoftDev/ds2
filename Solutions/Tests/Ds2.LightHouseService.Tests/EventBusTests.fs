module Ds2.LightHouseService.Tests.EventBusTests

open System
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Xunit
open Ds2.LightHouseService

/// **s6-r50 #5 ⑮ 외부 --review (EventBus unit fact 0 보강)** — 기존 e2e (`EventsSseTests`) 만으로는
/// DropOldest / Unsubscribe lifecycle / fan-out N-subscriber 의 단위 contract 검증 부족. 본 file 이 단위 박제.

/// Subscribe 호출 시 unique Guid + non-null reader 반환 + SubscriberCount += 1.
[<Fact>]
let ``Subscribe — unique Guid + non-null reader + SubscriberCount 증가`` () =
    let bus = EventBus()
    Assert.Equal(0, bus.SubscriberCount)
    let id1, reader1 = bus.Subscribe()
    let id2, reader2 = bus.Subscribe()
    Assert.NotEqual(id1, id2)
    Assert.NotNull(reader1 :> obj)
    Assert.NotNull(reader2 :> obj)
    Assert.Equal(2, bus.SubscriberCount)

/// Unsubscribe 호출 시 SubscriberCount -= 1 + 해당 reader.WaitToReadAsync 가 false 반환 (channel complete).
[<Fact>]
let ``Unsubscribe — SubscriberCount 감소 + reader 가 WaitToReadAsync false (channel complete)`` () =
    let bus = EventBus()
    let id, reader = bus.Subscribe()
    Assert.Equal(1, bus.SubscriberCount)
    bus.Unsubscribe(id)
    Assert.Equal(0, bus.SubscriberCount)
    // channel complete 후 WaitToReadAsync 는 false 반환 (event 없음 + writer.TryComplete 박제).
    let waitTask = reader.WaitToReadAsync().AsTask()
    Assert.True(waitTask.Wait(TimeSpan.FromSeconds 1.0))
    Assert.False(waitTask.Result)

/// Publish fan-out — N subscribers 모두 publish event 박제 받음 (동일 ServerEvent instance).
[<Fact>]
let ``Publish fan-out — 모든 subscribers 가 동일 event 받음 (N=3)`` () =
    let bus = EventBus()
    let _, r1 = bus.Subscribe()
    let _, r2 = bus.Subscribe()
    let _, r3 = bus.Subscribe()
    let evt = ServerEvent.collectionAdded "test-coll-id"
    bus.Publish evt
    let readOne (r: ChannelReader<ServerEvent>) : ServerEvent =
        let t = r.ReadAsync().AsTask()
        Assert.True(t.Wait(TimeSpan.FromSeconds 1.0))
        t.Result
    let e1 = readOne r1
    let e2 = readOne r2
    let e3 = readOne r3
    Assert.Equal<ServerEvent>(evt, e1)
    Assert.Equal<ServerEvent>(evt, e2)
    Assert.Equal<ServerEvent>(evt, e3)

/// DropOldest policy — subscriber capacity (64) 초과 publish 시 oldest event drop.
/// 65 event publish + reader 가 64 event 만 read → 첫 event 가 *2nd* publish (1st 가 drop).
[<Fact>]
let ``DropOldest — capacity (64) 초과 시 oldest event 자연 drop`` () =
    let bus = EventBus()
    let _, reader = bus.Subscribe()
    // publish 65 event — capacity 64 보다 1개 많음.
    for i = 1 to 65 do
        bus.Publish (ServerEvent.collectionAdded (sprintf "coll-%d" i))
    // 모두 read — 첫 event 가 i=2 부터 (i=1 drop).
    let readOne () : ServerEvent =
        let t = reader.ReadAsync().AsTask()
        Assert.True(t.Wait(TimeSpan.FromSeconds 1.0))
        t.Result
    let first = readOne ()
    // ServerEvent record 의 Payload 안 collectionId — payload 의 형태에 의존하지 않고 sequence 정합만 검증.
    // 1st event drop 되었다는 가정 = 64 event read 후 reader 가 빈 channel (block).
    // 본 fact 의 본질: first event 의 ordinal != 1 (drop 확인).
    // ServerEvent 의 raw payload 분해는 SSE wire 박제 정합 의무 — 본 단위 fact 는 *drop 발생* 만 검증.
    Assert.NotEqual<ServerEvent>(first, ServerEvent.collectionAdded "coll-1")  // 1st drop
    // 남은 63 event 모두 read 후 channel 빈 상태.
    for _ = 1 to 63 do readOne () |> ignore
    let waitMore = reader.WaitToReadAsync().AsTask()
    // 추가 publish 없으면 더 read 안 됨 — short timeout 후 false 또는 timeout (channel still open).
    Assert.False(waitMore.Wait(TimeSpan.FromMilliseconds 200.0)) |> ignore  // open + 빈 channel = block

/// Publish 가 dropped (Unsubscribe 된) subscriber 에 silent skip — exception 안 throw.
[<Fact>]
let ``Publish — unsubscribed subscriber 에 silent skip (exception 0)`` () =
    let bus = EventBus()
    let id1, _ = bus.Subscribe()
    let _, r2 = bus.Subscribe()
    bus.Unsubscribe(id1)
    // id1 unsubscribe 후 Publish — id2 만 받음. exception throw 안 함.
    bus.Publish (ServerEvent.collectionAdded "after-unsub")
    let t = r2.ReadAsync().AsTask()
    Assert.True(t.Wait(TimeSpan.FromSeconds 1.0))
