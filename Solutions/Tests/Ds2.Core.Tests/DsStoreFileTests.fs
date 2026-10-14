module Ds2.Core.Tests.DsStoreFileTests

open System
open System.IO
open Xunit
open Ds2.Core
open Ds2.Core.Store

let private tempFile (tag: string) =
    Path.Combine(Path.GetTempPath(), $"dsstore-{tag}-{Guid.NewGuid():N}.json")

let private storeWithProject (name: string) =
    let store = DsStore()
    let project = Project(name)
    store.Projects.[project.Id] <- project
    store, project

/// 역직렬화는 모르는 필드를 버리므로 아무 JSON 객체나 "빈 store" 로 읽혔다. 그러면 편집기는 현재 프로젝트를
/// 빈 모델로 바꾸고 제목에 그 파일명을 달았다 — 사용자는 "로딩이 안 된다" 로 본다.
/// 프로젝트가 없는 파일은 모델 파일이 아니므로 교체 전에 거절하고, 현재 store 는 그대로 둔다.
[<Theory>]
[<InlineData("{}")>]
[<InlineData("{\"foo\": 1, \"items\": []}")>]
let ``a JSON file without projects is rejected before the current store is replaced`` (json: string) =
    let store, project = storeWithProject "Current"
    let revision = store.Revision
    let path = tempFile "no-project"
    try
        File.WriteAllText(path, json)

        let ex = Assert.Throws<InvalidOperationException>(fun () -> store.LoadFromFile path)

        Assert.Contains("프로젝트가 없는", ex.Message)
        Assert.True(store.Projects.ContainsKey project.Id)
        Assert.Equal(revision, store.Revision)
    finally
        if File.Exists path then File.Delete path

/// 대조군 — 거절 조건이 정상 저장본까지 막지 않는다.
[<Fact>]
let ``a saved model file loads back with its project`` () =
    let source, project = storeWithProject "Saved"
    let path = tempFile "roundtrip"
    try
        source.SaveToFile path

        let loaded = DsStore()
        loaded.LoadFromFile path

        Assert.True(loaded.Projects.ContainsKey project.Id)
        Assert.Equal("Saved", loaded.Projects.[project.Id].Name)
    finally
        if File.Exists path then File.Delete path
