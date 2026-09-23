module Ds2.Aasx.Tests.AasxProjectCacheTests

open System.Collections.Generic
open Xunit
open Ds2.Core.Store
open Ds2.Aasx
open Ds2.Editor

// 원본 AASX 데이터(썸네일·비-DS 서브모델) 는 import 때 캐시에 얹히고 저장 때 다시 꺼내 쓴다.
// 캐시를 Project **인스턴스**로 키잉하면 undo 가 엔티티를 스냅샷으로 통째 교체하는 순간
// (StoreAuthoring.trackMutate 의 `dict.[id] <- oldSnapshot`) 조용히 고아가 되고, 다음 저장에서
// 그 데이터가 경고 없이 빠진다 — 병합 실패는 예외로 저장을 중단시키지만 캐시 미스는
// "보존할 것 없음" 으로 읽히기 때문이다.

let private makeEntries () =
    let entries = Dictionary<string, byte[]>()
    entries.["thumbnail.png"] <- [| 1uy; 2uy; 3uy |]
    entries

[<Fact>]
let ``Cache survives the Project instance swap that undo performs`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let projectBefore = store.Projects.[projectId]

    let env = obj ()
    AasxProjectCache.set store projectBefore env (makeEntries ())

    // System 추가는 Project 를 TrackMutate 한다 → undo 가 Project 인스턴스를 스냅샷으로 되돌린다.
    store.AddSystem("S", projectId, true) |> ignore
    store.Undo()

    let projectAfter = store.Projects.[projectId]
    Assert.NotSame(projectBefore, projectAfter)   // 이 전제가 깨지면 테스트가 아무것도 안 지킨다

    match AasxProjectCache.tryGetEntries store projectAfter with
    | Some entries -> Assert.True(entries.ContainsKey "thumbnail.png")
    | None -> failwith "undo 후 원본 ZIP 엔트리를 잃었다 — 저장 시 썸네일·서브모델이 조용히 빠진다"

    match AasxProjectCache.tryGetEnvironment store projectAfter with
    | Some cached -> Assert.Same(env, cached)
    | None -> failwith "undo 후 원본 Environment 를 잃었다"

[<Fact>]
let ``updateEnvironment reaches the entry after the instance swap`` () =
    let store = DsStore()
    let projectId = store.AddProject("P")
    let entries = makeEntries ()
    AasxProjectCache.set store store.Projects.[projectId] (obj ()) entries

    store.AddSystem("S", projectId, true) |> ignore
    store.Undo()

    let projectAfter = store.Projects.[projectId]
    let merged = obj ()
    AasxProjectCache.updateEnvironment store projectAfter merged

    Assert.Equal(Some merged, AasxProjectCache.tryGetEnvironment store projectAfter)
    // ZIP 엔트리는 최초 import 때 값을 그대로 유지한다.
    match AasxProjectCache.tryGetEntries store projectAfter with
    | Some kept -> Assert.Same(entries, kept)
    | None -> failwith "updateEnvironment 가 ZIP 엔트리를 날렸다"

[<Fact>]
let ``Different stores do not see each other's entries`` () =
    let storeA = DsStore()
    let projectId = storeA.AddProject("P")
    AasxProjectCache.set storeA storeA.Projects.[projectId] (obj ()) (makeEntries ())

    let storeB = DsStore()
    let otherId = storeB.AddProject("P")

    Assert.True((AasxProjectCache.tryGetEntries storeB storeB.Projects.[otherId]).IsNone)
