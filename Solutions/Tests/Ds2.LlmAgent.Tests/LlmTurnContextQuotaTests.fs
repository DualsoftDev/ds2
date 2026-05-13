module LlmTurnContextQuotaTests

open System
open Xunit
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// D8 quota cascade 산식 / 사전 reject 회귀.
///
/// `ToolOperations.cascadeOpCount` SSOT 산식 + helper 진입점 (`queueAddRobot`/`queueAddDevice`) 의
/// `MutationQuotaSync = 2000` 사전 reject 분기 검증. 본 test 는 F# helper 의 build block 만 검증
/// (Phase 5 cleanup 이후 호출 site = `ModelProtocol` doc-level dispatcher).

let private newStoreWithProject () =
    let store = DsStore()
    store.AddProject("M1") |> ignore
    store

// ─── cascadeOpCount 산식 (M3 SSOT) ──────────────────────────────────────────

[<Theory>]
[<InlineData(2, "none", 7)>]      // 3+2*2
[<InlineData(2, "chain", 8)>]     // 3+4+1 (cylinder/clamp 표준)
[<InlineData(4, "none", 11)>]     // 3+2*4 (robot 표준)
[<InlineData(4, "chain", 14)>]    // 3+8+3
[<InlineData(4, "all-pairs", 17)>]// 3+8+6
[<InlineData(8, "all-pairs", 47)>]
[<InlineData(9, "all-pairs", 57)>]
[<InlineData(10, "all-pairs", 68)>]
[<InlineData(12, "all-pairs", 93)>]
[<InlineData(13, "all-pairs", 107)>]
[<InlineData(18, "all-pairs", 192)>]
[<InlineData(19, "all-pairs", 212)>]
[<InlineData(61, "all-pairs", 1955)>]// pass (임계 산수: N=61 = 1955, MutationQuotaSync 미만)
[<InlineData(62, "all-pairs", 2018)>]// reject 임계 (N≥62 부터 MutationQuotaSync 초과)
[<InlineData(10, "chain", 32)>]   // chain N=10 quota 통과 회복 단서
[<InlineData(1, "none", 5)>]      // add_device single apiName
let ``cascadeOpCount 산식 회귀`` (n: int) (opposing: string) (expected: int) =
    Assert.Equal(expected, ToolOperations.cascadeOpCount n opposing)

[<Fact>]
let ``cascadeOpCount 알수없는 opposing = none 산식으로 fallback`` () =
    Assert.Equal(7, ToolOperations.cascadeOpCount 2 "weirdo")

[<Fact>]
let ``MutationQuotaSync literal 가 2000 (LlmTurnContext.cs:24 와 sync)`` () =
    Assert.Equal(2000, ToolOperations.MutationQuotaSync)

// ─── helper 사전 reject ─────────────────────────────────────────────────────

[<Fact>]
let ``robot all-pairs N=61 = 1955 op pass (quota 2000 미만)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let names = [for i in 1..61 -> sprintf "W%d" i]
    let _, ids = ToolOperations.queueAddRobot plan store "RB" names "all-pairs" None
    Assert.Equal(61, ids.Length)
    Assert.Equal(1955, plan.Count)

[<Fact>]
let ``robot all-pairs N=62 = 2018 op reject (quota 2000 초과)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let names = [for i in 1..62 -> sprintf "W%d" i]
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddRobot plan store "RB" names "all-pairs" None |> ignore)
    Assert.Contains("op 수 초과", ex.Message)
    Assert.Contains("2018", ex.Message)
    // SSOT magic 제거 — MutationQuotaSync (= LlmTurnContext.MutationQuota default) 와 sync.
    Assert.Contains(string ToolOperations.MutationQuotaSync, ex.Message)
    // plan 에 부분 적용 안 됨 (사전 reject)
    Assert.Equal(0, plan.Count)

[<Fact>]
let ``robot all-pairs N=63 = 2082 op reject + chain 회복 단서`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let names = [for i in 1..63 -> sprintf "W%d" i]
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddRobot plan store "RB" names "all-pairs" None |> ignore)
    Assert.Contains("chain", ex.Message)

[<Fact>]
let ``add_device all-pairs N=63 = 2082 op reject (helper 시그니처 동등)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let names = [for i in 1..63 -> sprintf "S%d" i]
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddDevice plan store "D" "Robot" names "all-pairs" None |> ignore)
    Assert.Contains("op 수 초과", ex.Message)
    Assert.Equal(0, plan.Count)

[<Fact>]
let ``robot none N=20 = 43 op pass (default opposing 으로 quota 여유)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let names = [for i in 1..20 -> sprintf "W%d" i]
    ToolOperations.queueAddRobot plan store "RB" names "none" None |> ignore
    Assert.Equal(43, plan.Count)
