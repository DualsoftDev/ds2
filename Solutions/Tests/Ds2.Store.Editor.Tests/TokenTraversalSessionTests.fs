module Ds2.Store.Editor.Tests.TokenTraversalSessionTests

open System
open Ds2.Runtime.Report
open Xunit

let private seedAt = DateTime(2026, 1, 1, 0, 0, 0)
let private at sec = seedAt.AddSeconds(float sec)

[<Fact>]
let ``seed alone keeps token active and snapshot returns incomplete traversal`` () =
    let s = TokenTraversalSession.Session()
    s.RecordSeed(1, "OriginA", "spec", seedAt, "W1")

    let snapshot = s.Snapshot() |> List.ofSeq
    Assert.Equal(1, snapshot.Length)
    let t = snapshot.[0]
    Assert.Equal(1, t.TokenItem)
    Assert.Equal("OriginA", t.OriginName)
    Assert.Equal(None, t.CompleteAt)
    Assert.Empty(t.WorkTimes)

[<Fact>]
let ``linear shift then complete accumulates per-work time and finalizes`` () =
    let s = TokenTraversalSession.Session()
    s.RecordSeed(1, "OriginA", "spec", seedAt, "W1")
    s.RecordShift(1, "W1", "W2", at 3)
    s.RecordComplete(1, "W2", at 5)

    let snapshot = s.Snapshot() |> List.ofSeq
    Assert.Equal(1, snapshot.Length)
    let t = snapshot.[0]
    Assert.Equal(Some(at 5), t.CompleteAt)
    let wtMap = t.WorkTimes |> Map.ofList
    Assert.Equal(3.0, wtMap.["W1"])
    Assert.Equal(2.0, wtMap.["W2"])

[<Fact>]
let ``branch shift produces two active paths, max complete wins`` () =
    let s = TokenTraversalSession.Session()
    s.RecordSeed(1, "OriginA", "spec", seedAt, "W1")
    // 한 토큰이 W1 에서 분기 — 같은 tick 에 두 번 Shift 호출.
    // 첫 번째: W1 path 제거 + W2 추가. 두 번째: (W1 없음) W3 만 추가.
    s.RecordShift(1, "W1", "W2", at 2)
    s.RecordShift(1, "W1", "W3", at 2)
    // 두 branch 가 서로 다른 시각에 complete — max 가 traversal CompleteAt.
    s.RecordComplete(1, "W2", at 4)
    s.RecordComplete(1, "W3", at 7)

    let snapshot = s.Snapshot() |> List.ofSeq
    Assert.Equal(1, snapshot.Length)
    let t = snapshot.[0]
    Assert.Equal(Some(at 7), t.CompleteAt)
    let wtMap = t.WorkTimes |> Map.ofList
    Assert.Equal(2.0, wtMap.["W1"])
    Assert.Equal(2.0, wtMap.["W2"])
    Assert.Equal(5.0, wtMap.["W3"])

[<Fact>]
let ``discard before complete leaves branch counted but marks any-discarded`` () =
    let s = TokenTraversalSession.Session()
    s.RecordSeed(1, "OriginA", "spec", seedAt, "W1")
    s.RecordShift(1, "W1", "W2", at 2)
    s.RecordDiscardOrBlocked(1, "W2", at 5)

    let snapshot = s.Snapshot() |> List.ofSeq
    let t = snapshot.[0]
    // 모든 branch 가 종료됐고 완주가 없으므로 CompleteAt = None.
    Assert.Equal(None, t.CompleteAt)
    let wtMap = t.WorkTimes |> Map.ofList
    Assert.Equal(2.0, wtMap.["W1"])
    Assert.Equal(3.0, wtMap.["W2"])

[<Fact>]
let ``finalize pending forces active traversal into completed list`` () =
    let s = TokenTraversalSession.Session()
    s.RecordSeed(1, "OriginA", "spec", seedAt, "W1")
    s.RecordShift(1, "W1", "W2", at 2)
    s.RecordComplete(1, "W2", at 4)
    // 두 번째 토큰은 진행 중 (활성).
    s.RecordSeed(2, "OriginB", "spec", at 1, "W1")
    s.RecordShift(2, "W1", "W2", at 3)

    s.FinalizePending()

    // 이제 모두 completed 로 옮겨졌어야 함.
    let snapshot = s.Snapshot() |> List.ofSeq
    Assert.Equal(2, snapshot.Length)
    let token2 = snapshot |> List.find (fun t -> t.TokenItem = 2)
    // 완주 branch 없음 → CompleteAt = None
    Assert.Equal(None, token2.CompleteAt)

[<Fact>]
let ``reset clears both active and completed`` () =
    let s = TokenTraversalSession.Session()
    s.RecordSeed(1, "OriginA", "spec", seedAt, "W1")
    s.RecordComplete(1, "W1", at 3)
    s.RecordSeed(2, "OriginB", "spec", at 1, "W1")

    s.Reset()

    Assert.Empty(s.Snapshot())

[<Fact>]
let ``unmatched workName on shift is a no-op`` () =
    let s = TokenTraversalSession.Session()
    s.RecordSeed(1, "OriginA", "spec", seedAt, "W1")
    // 분기 후 두 번째 Shift — source W1 은 이미 제거됐으므로 W3 만 추가.
    s.RecordShift(1, "W1", "W2", at 2)
    s.RecordShift(1, "W1", "W3", at 2)

    // 추가 path 가 정상적으로 잡혀야 함.
    let snapshot = s.Snapshot() |> List.ofSeq
    let t = snapshot.[0]
    Assert.Equal(None, t.CompleteAt)
    // ActivePaths = [W2 at 2; W3 at 2] → completed 아직 아님, work time 누적 = W1:2.
    let wtMap = t.WorkTimes |> Map.ofList
    Assert.Equal(2.0, wtMap.["W1"])

[<Fact>]
let ``snapshot includes both completed and active traversals`` () =
    let s = TokenTraversalSession.Session()
    s.RecordSeed(1, "OriginA", "spec", seedAt, "W1")
    s.RecordComplete(1, "W1", at 3)
    s.RecordSeed(2, "OriginB", "spec", at 1, "W1")

    let snapshot = s.Snapshot() |> List.ofSeq
    Assert.Equal(2, snapshot.Length)
    let completed = snapshot |> List.find (fun t -> t.TokenItem = 1)
    let active = snapshot |> List.find (fun t -> t.TokenItem = 2)
    Assert.Equal(Some(at 3), completed.CompleteAt)
    Assert.Equal(None, active.CompleteAt)
