module Ds2.Core.Tests.WorkDurationStoreTests

open System
open Ds2.Core
open Ds2.Core.Store
open Xunit

/// Work 만 들어 있는 store. Core 테스트라 편집기(Ds2.Editor) 없이 엔티티를 직접 넣는다.
let private storeWithWorks (names: string list) =
    let store = DsStore()
    let flowId = Guid.NewGuid()
    let works =
        names |> List.map (fun name ->
            let w = Work("F", name, flowId)
            store.Works.[w.Id] <- w
            w)
    store, works

let private ms (v: int) = Nullable<int>(v)
let private none = Nullable<int>()

let private change workId dur mn mx : struct (Guid * Nullable<int> * Nullable<int> * Nullable<int>) =
    struct (workId, dur, mn, mx)

/// 서버 측(DSPilot 실측 보정) 일괄 쓰기가 편집기 없이 돈다 — 세 값이 기록되고, null/0 은 None 이 되며, revision 이 오른다.
[<Fact>]
let ``ApplyWorkDurationRanges writes duration, min and max without the editor`` () =
    let store, works = storeWithWorks [ "W1"; "W2" ]
    let w1, w2 = works.[0], works.[1]
    let before = store.Revision

    let changed =
        store.ApplyWorkDurationRanges([
            change w1.Id (ms 1200) (ms 100) (ms 2200)
            change w2.Id none (ms 200) (ms 0)       // null/0 = 미사용
        ])

    Assert.Equal(2, changed)
    Assert.True(store.Revision > before, "모델이 바뀌었으니 revision 이 올라야 한다")
    Assert.Equal(1200.0, store.Works.[w1.Id].Duration.Value.TotalMilliseconds)
    Assert.Equal(100.0, store.Works.[w1.Id].MinDuration.Value.TotalMilliseconds)
    Assert.Equal(2200.0, store.Works.[w1.Id].MaxDuration.Value.TotalMilliseconds)
    Assert.True(store.Works.[w2.Id].Duration.IsNone)
    Assert.Equal(200.0, store.Works.[w2.Id].MinDuration.Value.TotalMilliseconds)
    Assert.True(store.Works.[w2.Id].MaxDuration.IsNone)

/// 값이 그대로인 항목·없는 Work 는 변경으로 치지 않는다 — DSPilot 이 매 스캔마다 같은 실측을 밀어 넣어도
/// 모델이 더럽혀지거나 revision 이 불필요하게 오르면 안 된다.
[<Fact>]
let ``unchanged values and unknown works are not counted and do not bump revision`` () =
    let store, works = storeWithWorks [ "W1" ]
    let w1 = works.[0]
    store.ApplyWorkDurationRanges([ change w1.Id (ms 500) none none ]) |> ignore
    let before = store.Revision

    let changed =
        store.ApplyWorkDurationRanges([
            change w1.Id (ms 500) none none
            change (Guid.NewGuid()) (ms 700) none none
        ])

    Assert.Equal(0, changed)
    Assert.Equal(before, store.Revision)

/// reference Work 로 들어온 변경은 원본 Work 에 기록된다 — 원본과 ref 가 동시에 오면 한 번만 센다.
[<Fact>]
let ``changes addressed to a reference work land on the original once`` () =
    let store, works = storeWithWorks [ "Orig"; "Ref" ]
    let orig, ref' = works.[0], works.[1]
    ref'.ReferenceOf <- Some orig.Id

    let changed =
        store.ApplyWorkDurationRanges([
            change ref'.Id (ms 900) none none
            change orig.Id (ms 900) none none
        ])

    Assert.Equal(1, changed)
    Assert.Equal(900.0, store.Works.[orig.Id].Duration.Value.TotalMilliseconds)
    Assert.True(store.Works.[ref'.Id].Duration.IsNone)
