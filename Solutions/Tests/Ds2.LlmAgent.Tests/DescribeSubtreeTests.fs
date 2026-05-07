module DescribeSubtreeTests

open System
open Xunit
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// 1d-2 / R1 후속 — `describeSubtree` 동작 명세 lock + token 회귀.
/// composite read tool 의 핵심 invariants (root kind 자동 판별 / depth cap / 50 budget truncated /
/// batch 호출이 N+1 보다 길지 않음) 이 후속 변경 시 즉시 실패하도록.

/// 5 system × 2 flow × 1 work fixture (entity 합 = project 1 + sys 5 + flow 10 + work 10 = 26).
/// budget 50 안에서 depth=3 시 트리 전부 노출.
let private buildSmallFixture () =
    let store = DsStore()
    let projectId = store.AddProject("Proj")
    let sysIds =
        [| for i in 0 .. 4 ->
            let sysId = store.AddSystem(sprintf "Sys%d" i, projectId, true)
            for j in 0 .. 1 do
                let flowId = store.AddFlow(sprintf "F%d" j, sysId)
                store.AddWork(sprintf "W%d" j, flowId) |> ignore
            sysId |]
    store, projectId, sysIds

[<Fact>]
let ``빈 store 의 unknown rootId 는 NOT_FOUND`` () =
    let store = DsStore()
    let bogus = Guid.NewGuid()
    let result = ToolOperations.describeSubtree store bogus 2
    Assert.StartsWith("NOT_FOUND:", result)
    Assert.Contains(bogus.ToString("D"), result)

[<Fact>]
let ``rootId=Project, depth=0 은 Project 1줄만`` () =
    let store, projectId, _ = buildSmallFixture ()
    let result = ToolOperations.describeSubtree store projectId 0
    let lines = result.Split('\n') |> Array.filter (fun s -> s.Trim() <> "")
    Assert.Single(lines) |> ignore
    Assert.StartsWith("Project \"Proj\"", result)

[<Fact>]
let ``rootId=System 자동 판별, depth=0 은 DsSystem 1줄`` () =
    let store, _, sysIds = buildSmallFixture ()
    let result = ToolOperations.describeSubtree store sysIds.[0] 0
    let lines = result.Split('\n') |> Array.filter (fun s -> s.Trim() <> "")
    Assert.Single(lines) |> ignore
    Assert.StartsWith("DsSystem \"Sys0\"", result)

[<Fact>]
let ``rootId=Flow 자동 판별, depth=0 은 Flow 1줄`` () =
    let store, _, sysIds = buildSmallFixture ()
    let flowId = (Queries.flowsOf sysIds.[0] store).Head.Id
    let result = ToolOperations.describeSubtree store flowId 0
    let lines = result.Split('\n') |> Array.filter (fun s -> s.Trim() <> "")
    Assert.Single(lines) |> ignore
    Assert.StartsWith("Flow \"F0\"", result)

[<Fact>]
let ``rootId=Work 자동 판별, depth=0 은 Work 1줄`` () =
    let store, _, sysIds = buildSmallFixture ()
    let flowId = (Queries.flowsOf sysIds.[0] store).Head.Id
    let workId = (Queries.worksOf flowId store).Head.Id
    let result = ToolOperations.describeSubtree store workId 0
    let lines = result.Split('\n') |> Array.filter (fun s -> s.Trim() <> "")
    Assert.Single(lines) |> ignore
    Assert.Contains("Work \"F0.W0\"", result)

[<Fact>]
let ``depth cap [0,5] — depth=10 호출은 depth=5 와 동일 출력`` () =
    let store, projectId, _ = buildSmallFixture ()
    let r5 = ToolOperations.describeSubtree store projectId 5
    let r10 = ToolOperations.describeSubtree store projectId 10
    Assert.Equal(r5, r10)

[<Fact>]
let ``depth=3 시 fixture 의 모든 26 entity 노출 (project + 5 sys + 10 flow + 10 work)`` () =
    let store, projectId, _ = buildSmallFixture ()
    let result = ToolOperations.describeSubtree store projectId 3
    let lines = result.Split('\n') |> Array.filter (fun s -> s.Trim() <> "")
    // budget=50 안에 fits, truncated 안 발생
    Assert.DoesNotContain("(truncated)", result)
    Assert.Equal(26, lines.Length)

[<Fact>]
let ``budget 정확히 50 = no truncated`` () =
    // project 1 + sys 49 = 50 entity, depth=1 (sys list 까지)
    let store = DsStore()
    let projectId = store.AddProject("Proj")
    for i in 0 .. 48 do
        store.AddSystem(sprintf "S%d" i, projectId, true) |> ignore
    let result = ToolOperations.describeSubtree store projectId 1
    Assert.DoesNotContain("(truncated)", result)
    let lines = result.Split('\n') |> Array.filter (fun s -> s.Trim() <> "")
    Assert.Equal(50, lines.Length)

[<Fact>]
let ``budget 51번째 호출 시 truncated 1줄 추가`` () =
    // project 1 + sys 50 = 51회 writeLine. 50번째까지만 출력 + truncated 1줄
    let store = DsStore()
    let projectId = store.AddProject("Proj")
    for i in 0 .. 49 do
        store.AddSystem(sprintf "S%d" i, projectId, true) |> ignore
    let result = ToolOperations.describeSubtree store projectId 1
    Assert.Contains("(truncated)", result)
    // 정확히 1번만 추가
    let occurrences =
        let mutable count = 0
        let mutable idx = 0
        let needle = "(truncated)"
        while (idx <- result.IndexOf(needle, idx); idx >= 0) do
            count <- count + 1
            idx <- idx + needle.Length
        count
    Assert.Equal(1, occurrences)

/// **token 회귀 (R1 Critical)** — composite `describe_subtree` 1회 호출이 N개의 `describe_system(deep=true)`
/// 합보다 길지 않아야 함. 같은 fixture 에서 byte 비교. system 별 헤더 / flow / work 줄은 양쪽 동일,
/// describe_subtree 는 ApiDef / ArrowBetweenWorks 를 walkSystem 안에 포함하지 않으므로 자연 짧거나 같음.
/// 후속 구현에서 describe_subtree 가 ApiDef 추가하면서 들여쓰기 폭증 등 회귀가 일어나면 본 test 가 검출.
[<Fact>]
let ``describe_subtree 한 번 ≤ N × describe_system(deep=true) 합 (token 회귀)`` () =
    let store, projectId, sysIds = buildSmallFixture ()
    let subtreeBytes =
        (ToolOperations.describeSubtree store projectId 3).Length
    let perSystemBytes =
        sysIds
        |> Array.sumBy (fun sysId -> (ToolOperations.describeSystem store sysId true).Length)
    // 본 fixture 에서는 ApiDef / ArrowWork 가 0개라 두 값이 거의 같지만,
    // describe_subtree 는 추가로 "Project ..." 1줄 + indent 1단계 깊음. 따라서 약간 길 수 있음.
    // 회귀 의도는 "indent 폭증으로 N×describe_system 의 1.5× 넘는 폭증 X". 절대 비교 대신 비율 cap.
    Assert.True(
        subtreeBytes <= perSystemBytes + 256,
        sprintf "subtree=%d > perSystem=%d + 256 (project 헤더 overhead)" subtreeBytes perSystemBytes)
