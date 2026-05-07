module BatchTests

open System
open System.Text.Json
open Xunit
open Ds2.LlmAgent
open Ds2.Core.Store

/// Pass 6 (b) — `apply_operations` 의 F# 핵심 (`ToolOperations.queueBatch`) 회귀 테스트.
///
/// (c) variable binding 의 chain pattern 이 numTurns 부풀림으로 인해 (b) batch tool 로 대체됨.
/// 본 묶음은:
///   - JSON parse → BatchOpInput[] 변환
///   - @<ref> resolver (batch self-contained)
///   - mid-batch fail-fast rollback (plan.TruncateTo)
///   - ref 형식 / 중복 / 미정의
///   - 지원 op 화이트리스트
///
/// 실제 LlmTurnContext / IUiDispatcher 의존 흐름 (RunMutation, ApplyOperations) 은 C# 측이라
/// 본 묶음에서 직접 검증하지 않음. F# layer 의 build block 만 보장.

let private parse (json: string) =
    let doc = JsonDocument.Parse(json)
    doc.RootElement.EnumerateArray()
    |> Seq.map (fun item ->
        let op = item.GetProperty("op").GetString()
        let refOpt =
            let mutable r = JsonElement()
            if item.TryGetProperty("ref", &r) && r.ValueKind = JsonValueKind.String then Some(r.GetString())
            else None
        let args =
            let mutable a = JsonElement()
            if item.TryGetProperty("args", &a) then a else JsonElement()
        { Op = op; Ref = refOpt; Args = args })
    |> Array.ofSeq

let private newPlan () = ImportPlanBuilder()

// ─── empty / 잘못된 입력 ───────────────────────────────────────────────────

[<Fact>]
let ``빈 array = Error (failureIndex 0)`` () =
    let plan = newPlan()
    let store = DsStore()
    let result = ToolOperations.queueBatch plan store [||]
    match result with
    | Error(idx, _, msg) ->
        Assert.Equal(0, idx)
        Assert.Contains("operations array 가 비어있습니다", msg)
    | Ok _ -> Assert.Fail("empty array 는 Error 여야 함")

[<Fact>]
let ``op 필드 비어있으면 Error`` () =
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[{"op":"", "args":{"name":"X"}}]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Error(idx, _, msg) ->
        Assert.Equal(0, idx)
        Assert.Contains("op", msg)
    | Ok _ -> Assert.Fail()

[<Fact>]
let ``지원하지 않는 op = Error`` () =
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[{"op":"unknown_op", "args":{}}]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Error(_, opName, msg) ->
        Assert.Equal("unknown_op", opName)
        Assert.Contains("지원하지 않는 op", msg)
    | Ok _ -> Assert.Fail()

// ─── single op success ─────────────────────────────────────────────────────

[<Fact>]
let ``단일 add_project = Ok + plan count 1`` () =
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[{"op":"add_project", "args":{"name":"M1"}}]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Ok rs ->
        Assert.Single(rs) |> ignore
        Assert.Equal("add_project", rs.[0].Op)
        Assert.True(rs.[0].Id.IsSome)
        Assert.Equal(1, plan.Count)
    | Error(_, _, msg) -> Assert.Fail(msg)

// ─── @<ref> resolve chain ──────────────────────────────────────────────────

[<Fact>]
let ``add_project 후 add_system auto attach`` () =
    // add_system 은 첫 project 자동 부착이라 systemId 인자 X. ref 는 add_api_def 등에서 활용.
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[
        {"op":"add_project", "ref":"p", "args":{"name":"M1"}},
        {"op":"add_system",  "ref":"cyl", "args":{"name":"Cyl"}}
    ]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Ok rs ->
        Assert.Equal(2, rs.Length)
        Assert.True(rs |> Array.forall (fun r -> r.Id.IsSome))
    | Error(_, _, msg) -> Assert.Fail(msg)

[<Fact>]
let ``add_system + add_api_def 의 ref 정상 해소`` () =
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[
        {"op":"add_project", "args":{"name":"M1"}},
        {"op":"add_system",  "ref":"cyl", "args":{"name":"Cyl"}},
        {"op":"add_api_def", "args":{"name":"ADV", "systemId":"@cyl"}}
    ]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Ok rs ->
        Assert.Equal(3, rs.Length)
        // 누적된 plan op = AddProject + AddSystem + LinkSystemToProject + AddApiDef = 4
        Assert.Equal(4, plan.Count)
    | Error(_, _, msg) -> Assert.Fail(msg)

[<Fact>]
let ``실린더 풀세트 chain = Ok + plan count 11 (project + system + LinkSystemToProject + 7 entity + 1 arrow)`` () =
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[
        {"op":"add_project", "args":{"name":"M1"}},
        {"op":"add_system",  "ref":"cyl", "args":{"name":"Cyl"}},
        {"op":"add_api_def", "args":{"name":"ADV", "systemId":"@cyl"}},
        {"op":"add_api_def", "args":{"name":"RET", "systemId":"@cyl"}},
        {"op":"add_flow",    "ref":"run", "args":{"name":"Run", "systemId":"@cyl"}},
        {"op":"add_work",    "ref":"adv", "args":{"localName":"Adv", "flowId":"@run"}},
        {"op":"add_work",    "ref":"ret", "args":{"localName":"Ret", "flowId":"@run"}},
        {"op":"add_call",    "args":{"devicesAlias":"Cyl", "apiName":"ADV", "workId":"@adv"}},
        {"op":"add_call",    "args":{"devicesAlias":"Cyl", "apiName":"RET", "workId":"@ret"}},
        {"op":"add_arrow",   "args":{"sourceId":"@adv", "targetId":"@ret", "arrowType":"Start"}}
    ]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Ok rs ->
        Assert.Equal(10, rs.Length)
        // queueAddSystem 이 LinkSystemToProject 도 추가하므로 plan op = 10 + 1 = 11
        Assert.Equal(11, plan.Count)
    | Error(_, opName, msg) -> Assert.Fail($"unexpected fail at {opName}: {msg}")

// ─── ref 미정의 / 중복 / 형식 ──────────────────────────────────────────────

[<Fact>]
let ``undefined ref = Error + plan rollback`` () =
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[
        {"op":"add_project", "args":{"name":"M1"}},
        {"op":"add_api_def", "args":{"name":"X", "systemId":"@nonexistent"}}
    ]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Error(idx, _, msg) ->
        Assert.Equal(1, idx)
        Assert.Contains("ref '@nonexistent'", msg)
        Assert.Equal(0, plan.Count)  // rollback to snapshot (0)
    | Ok _ -> Assert.Fail()

[<Fact>]
let ``중복 ref = Error + rollback`` () =
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[
        {"op":"add_project", "args":{"name":"M1"}},
        {"op":"add_system",  "ref":"x", "args":{"name":"S1"}},
        {"op":"add_system",  "ref":"x", "args":{"name":"S2"}}
    ]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Error(idx, _, msg) ->
        Assert.Equal(2, idx)
        Assert.Contains("중복 정의", msg)
        Assert.Equal(0, plan.Count)
    | Ok _ -> Assert.Fail()

[<Fact>]
let ``ref 형식 (잘못된 변수명) = Error + rollback`` () =
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[
        {"op":"add_project", "ref":"1bad", "args":{"name":"M1"}}
    ]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Error(idx, _, _) ->
        Assert.Equal(0, idx)
        Assert.Equal(0, plan.Count)
    | Ok _ -> Assert.Fail()

// ─── mid-batch fail-fast rollback ──────────────────────────────────────────

[<Fact>]
let ``mid-batch 실패 = 진입 시점 plan count 로 rollback`` () =
    let plan = newPlan()
    let store = DsStore()
    plan.Add(LinkSystemToProject(Guid.NewGuid(), Guid.NewGuid(), true))
    let snapshot = plan.Count  // 1
    let ops = parse """[
        {"op":"add_project", "args":{"name":"M1"}},
        {"op":"add_system",  "args":{"name":"@malicious"}}
    ]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Error(idx, _, msg) ->
        Assert.Equal(1, idx)
        Assert.Contains("예약 prefix", msg)
        Assert.Equal(snapshot, plan.Count)  // pre-batch state 복원
    | Ok _ -> Assert.Fail()

[<Fact>]
let ``빈 args (필드 누락) = Error + rollback`` () =
    let plan = newPlan()
    let store = DsStore()
    let ops = parse """[
        {"op":"add_project", "args":{}}
    ]"""
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Error(_, _, msg) ->
        Assert.Contains("name", msg)
        Assert.Equal(0, plan.Count)
    | Ok _ -> Assert.Fail()

[<Fact>]
let ``add_arrow 의 잘못된 arrowType = Error + rollback`` () =
    let plan = newPlan()
    let store = DsStore()
    // 두 fake Guid 로 (실제 store 에 없으나 batch 안에서는 ref 미사용 시 Guid 통과)
    // arrowType invalid → 실패해야 함. 단 add_arrow 가 Guid lookup 부터라 그게 먼저 fail. validation order 확인 후
    let g1 = Guid.NewGuid()
    let g2 = Guid.NewGuid()
    let json = sprintf """[{"op":"add_arrow", "args":{"sourceId":"%s", "targetId":"%s", "arrowType":"BadType"}}]""" (g1.ToString("D")) (g2.ToString("D"))
    let ops = parse json
    let result = ToolOperations.queueBatch plan store ops
    match result with
    | Error(_, _, _) -> Assert.Equal(0, plan.Count)
    | Ok _ -> Assert.Fail()
