module LlmTurnContextQuotaTests

open System
open Xunit
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// extend-mcp §5.6 신규 5 — D8 quota cascade 산식 / 사전 reject 회귀.
///
/// `ToolOperations.cascadeOpCount` SSOT 산식 + helper 진입점 (`queueAddRobot`/`queueAddDevice`) 의
/// `MutationQuotaSync = 50` 사전 reject 분기 검증. C# `ModelTools` 의 single-helper / batch 경로
/// 추가 차감 (`IncrementMutationCount(cascadeOpCount-1)`) 은 .NET interop 측 책임 — 본 test 는
/// F# layer 의 build block 만 검증.

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
[<InlineData(8, "all-pairs", 47)>]// pass (rev 5 임계 산수: N=8 = 47)
[<InlineData(9, "all-pairs", 57)>]// reject 임계 (rev 5: N≥9 부터 50 초과)
[<InlineData(10, "all-pairs", 68)>]
[<InlineData(10, "chain", 32)>]   // chain N=10 quota 통과 회복 단서
[<InlineData(1, "none", 5)>]      // add_device single apiName
let ``cascadeOpCount 산식 회귀`` (n: int) (opposing: string) (expected: int) =
    Assert.Equal(expected, ToolOperations.cascadeOpCount n opposing)

[<Fact>]
let ``cascadeOpCount 알수없는 opposing = none 산식으로 fallback`` () =
    Assert.Equal(7, ToolOperations.cascadeOpCount 2 "weirdo")

[<Fact>]
let ``MutationQuotaSync literal 가 50 (LlmTurnContext.cs:24 와 sync)`` () =
    Assert.Equal(50, ToolOperations.MutationQuotaSync)

// ─── helper 사전 reject ─────────────────────────────────────────────────────

[<Fact>]
let ``robot all-pairs N=8 = 47 op pass (quota 50 미만)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let names = [for i in 1..8 -> sprintf "W%d" i]
    let _, ids = ToolOperations.queueAddRobot plan store "RB" names "all-pairs" None
    Assert.Equal(8, ids.Length)
    Assert.Equal(47, plan.Count)

[<Fact>]
let ``robot all-pairs N=9 = 57 op reject (quota 50 초과)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let names = [for i in 1..9 -> sprintf "W%d" i]
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddRobot plan store "RB" names "all-pairs" None |> ignore)
    Assert.Contains("op 수 초과", ex.Message)
    Assert.Contains("57", ex.Message)
    Assert.Contains("50", ex.Message)
    // plan 에 부분 적용 안 됨 (사전 reject)
    Assert.Equal(0, plan.Count)

[<Fact>]
let ``robot all-pairs N=10 = 68 op reject + chain 회복 단서`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let names = [for i in 1..10 -> sprintf "W%d" i]
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.queueAddRobot plan store "RB" names "all-pairs" None |> ignore)
    Assert.Contains("chain", ex.Message)

[<Fact>]
let ``add_device all-pairs N=10 = 68 op reject (helper 시그니처 동등)`` () =
    let store = newStoreWithProject ()
    let plan = ImportPlanBuilder()
    let names = [for i in 1..10 -> sprintf "S%d" i]
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
