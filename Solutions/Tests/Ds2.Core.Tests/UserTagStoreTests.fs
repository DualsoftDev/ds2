module Ds2.Core.Tests.UserTagStoreTests

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Xunit

/// Project 1 + active System 1 인 store. Core 테스트라 편집기(Ds2.Editor) 없이 엔티티를 직접 넣는다.
let private storeWithSystem (systemName: string) =
    let store = DsStore()
    let project = Project("P")
    let system = DsSystem(systemName)
    project.ActiveSystemIds.Add system.Id
    store.Projects.[project.Id] <- project
    store.Systems.[system.Id] <- system
    store, system

let private entry name level addr vt op mv : struct (string * string * string * string * string * string) =
    struct (name, level, addr, vt, op, mv)

/// 서버 측 교체(DSPilot UserTag 적용)와 전체 읽기가 편집기 없이 돈다 — 교체는 기존 항목을 지우고, 빈 이름은 건너뛰며,
/// 빈 matchOp 는 값 타입 기본 연산으로 채워지고, 행에는 소속 System 이 붙는다.
[<Fact>]
let ``ReplaceUserTags rewrites a System's tags and GetAllUserTagsForProject reads them back with ownership`` () =
    let store, system = storeWithSystem "Press"
    let before = store.Revision

    let written =
        store.ReplaceUserTags(
            system.Id,
            List<_>([ entry "Old" "Error" "%IX0.0" "Bit" "" "" ]))
    Assert.Equal(1, written)

    let replaced =
        store.ReplaceUserTags(
            system.Id,
            List<_>([
                entry "Ready" "Error" "%IX0.1" "Bit" "" ""
                entry "" "Error" "%IX0.9" "Bit" "" ""              // 빈 이름은 기록하지 않는다
                entry "Count" "Error" "%MW10" "Word" "GreaterThan" "5"
            ]))
    Assert.Equal(2, replaced)
    Assert.True(store.Revision > before, "모델이 바뀌었으니 revision 이 올라야 한다")

    let rows = store.GetAllUserTagsForProject()
    Assert.Equal(2, rows.Length)
    Assert.All(rows, fun row ->
        Assert.Equal(system.Id, row.SystemId)
        Assert.Equal("Press", row.SystemName))
    Assert.Equal<string list>([ "Ready"; "Count" ], rows |> List.map _.Name)
    Assert.Equal<int list>([ 0; 1 ], rows |> List.map _.Index)
    Assert.Equal("%MW10", rows.[1].TagAddress)
    Assert.Equal("Word", rows.[1].ValueType)
    Assert.Equal("5", rows.[1].MatchValue)
    // 빈 matchOp 는 Bit 의 기본 연산으로 채워진다 — 저장된 문자열에도 그대로 실린다.
    Assert.Equal(
        LoggingHelpers.UserTagHelpers.matchOpToString (LoggingHelpers.UserTagHelpers.defaultMatchOpFor PlcValueType.Bit),
        rows.[0].MatchOp)

/// 해석에 실패한 저장 문자열은 건너뛰되 뒤 항목의 저장 인덱스는 보존한다 — 편집기가 그 인덱스로 수정·삭제한다.
[<Fact>]
let ``unparseable stored entries are skipped without shifting the indices of later tags`` () =
    let store, system = storeWithSystem "Weld"
    let props = UserTagStore.ensureLoggingProps system
    props.UserTags.Add(LoggingHelpers.UserTagHelpers.format (UserTagStore.buildTag "A" "Error" "%IX0.0" "Bit" "" ""))
    props.UserTags.Add("garbage-without-separators")
    props.UserTags.Add(LoggingHelpers.UserTagHelpers.format (UserTagStore.buildTag "B" "Error" "%IX0.1" "Bit" "" ""))

    let parsed = UserTagStore.parsedTagsOf system
    Assert.Equal<int list>([ 0; 2 ], parsed |> List.map fst)
    Assert.Equal<string list>([ "A"; "B" ], parsed |> List.map (fun (_, tag) -> tag.Name))

    let rows = store.GetAllUserTagsForProject()
    Assert.Equal<int list>([ 0; 2 ], rows |> List.map _.Index)

/// 모르는 System 은 조용히 무시하지 않는다 — 서버가 잘못된 id 로 모델을 "성공" 처리하면 태그가 사라진 채 export 된다.
[<Fact>]
let ``ReplaceUserTags on an unknown System throws instead of silently succeeding`` () =
    let store, _ = storeWithSystem "Any"
    Assert.Throws<KeyNotFoundException>(fun () ->
        store.ReplaceUserTags(Guid.NewGuid(), List<_>([ entry "X" "Error" "%IX0.0" "Bit" "" "" ])) |> ignore)
    |> ignore
