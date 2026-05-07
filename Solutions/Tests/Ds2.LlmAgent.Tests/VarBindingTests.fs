module VarBindingTests

open System
open Xunit
open Ds2.LlmAgent

/// Pass 3 (c) — Variable binding 회귀 테스트.
/// `ToolOperations.sanitizeVarName` / `resolveGuidOrVar` / `registerVar` +
/// `ImportPlanBuilder` 의 `VarCache` / `CascadeFailureFlag` / `SignalCascadeFailure`.
///
/// LLM tool 측 (`ModelTools.AddSystem` 등) 의 cascade `BATCH_ABORTED` 흐름은 C# 구현이라
/// 본 묶음에서 직접 검증하지 않음 — F# layer 의 build block 단위 동작만 보장.

let private isValid (s: string) = s = ""

// ─── sanitizeVarName ──────────────────────────────────────────────────────

[<Fact>]
let ``ASCII 영문 변수명은 valid`` () =
    Assert.True(isValid (ToolOperations.sanitizeVarName "cyl"))

[<Fact>]
let ``밑줄 시작 변수명은 valid`` () =
    Assert.True(isValid (ToolOperations.sanitizeVarName "_x"))

[<Fact>]
let ``영숫자 + 밑줄 mix 는 valid`` () =
    Assert.True(isValid (ToolOperations.sanitizeVarName "X_1_a"))

[<Fact>]
let ``32자 경계는 valid`` () =
    let exactMax = String('a', ToolOperations.VarNameMaxLength)
    Assert.True(isValid (ToolOperations.sanitizeVarName exactMax))

[<Fact>]
let ``빈 문자열은 invalid`` () =
    let result = ToolOperations.sanitizeVarName ""
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("비어있습니다", result)

[<Fact>]
let ``33자 (cap 초과) 는 invalid + 길이 메시지`` () =
    let tooLong = String('a', ToolOperations.VarNameMaxLength + 1)
    let result = ToolOperations.sanitizeVarName tooLong
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("길이", result)

[<Fact>]
let ``첫 글자 숫자는 invalid + codepoint`` () =
    let result = ToolOperations.sanitizeVarName "1abc"
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("U+0031", result)  // '1' = U+0031

[<Fact>]
let ``첫 글자 dash 는 invalid`` () =
    let result = ToolOperations.sanitizeVarName "-abc"
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("U+002D", result)  // '-' = U+002D

[<Fact>]
let ``중간 dash 는 invalid + codepoint`` () =
    let result = ToolOperations.sanitizeVarName "ab-cd"
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("U+002D", result)

[<Fact>]
let ``공백 포함 변수명은 invalid`` () =
    let result = ToolOperations.sanitizeVarName "ab cd"
    Assert.StartsWith("VALIDATION_ERROR:", result)

[<Fact>]
let ``'$' 자체가 변수명 내부에 오면 invalid (sanitize 시 raw 검사 — 호출자가 'prefix 제거 후' 전달함을 가정)`` () =
    // sanitizeVarName 은 raw varname 만 받음. resolveGuidOrVar 가 '$' prefix 를 떼고 호출.
    let result = ToolOperations.sanitizeVarName "$x"
    Assert.StartsWith("VALIDATION_ERROR:", result)

// ─── resolveGuidOrVar ─────────────────────────────────────────────────────

let private newPlan () = ImportPlanBuilder()

[<Fact>]
let ``빈 string resolveGuidOrVar = invalidOp`` () =
    let plan = newPlan()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.resolveGuidOrVar plan "" "systemId" |> ignore)
    Assert.Contains("VALIDATION_ERROR", ex.Message)
    Assert.Contains("systemId", ex.Message)

[<Fact>]
let ``whitespace-only resolveGuidOrVar = invalidOp`` () =
    let plan = newPlan()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.resolveGuidOrVar plan "   " "flowId" |> ignore)
    Assert.Contains("VALIDATION_ERROR", ex.Message)

[<Fact>]
let ``정상 GUID 문자열은 그대로 Guid 반환`` () =
    let plan = newPlan()
    let g = Guid.NewGuid()
    let resolved = ToolOperations.resolveGuidOrVar plan (g.ToString("D")) "systemId"
    Assert.Equal(g, resolved)

[<Fact>]
let ``잘못된 GUID 문자열 (var 도 아님) = invalidOp`` () =
    let plan = newPlan()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.resolveGuidOrVar plan "not-a-guid" "systemId" |> ignore)
    Assert.Contains("GUID 또는", ex.Message)

[<Fact>]
let ``'$' 단독 = invalidOp (varName empty 후 sanitize fail)`` () =
    let plan = newPlan()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.resolveGuidOrVar plan "$" "systemId" |> ignore)
    Assert.Contains("VALIDATION_ERROR", ex.Message)

[<Fact>]
let ``'$<undefined>' = invalidOp (cache miss 메시지)`` () =
    let plan = newPlan()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.resolveGuidOrVar plan "$cyl" "systemId" |> ignore)
    Assert.Contains("$cyl", ex.Message)
    Assert.Contains("정의되지 않았습니다", ex.Message)

[<Fact>]
let ``'$<bad-name>' = invalidOp (변수 참조 형식 오류)`` () =
    let plan = newPlan()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.resolveGuidOrVar plan "$1abc" "systemId" |> ignore)
    Assert.Contains("변수 참조 형식 오류", ex.Message)

[<Fact>]
let ``'$<known>' 은 등록된 Guid 반환`` () =
    let plan = newPlan()
    let g = Guid.NewGuid()
    ToolOperations.registerVar plan "cyl" g
    let resolved = ToolOperations.resolveGuidOrVar plan "$cyl" "systemId"
    Assert.Equal(g, resolved)

[<Fact>]
let ``leading/trailing whitespace 는 trim 후 valid GUID 처리`` () =
    let plan = newPlan()
    let g = Guid.NewGuid()
    let resolved = ToolOperations.resolveGuidOrVar plan ("  " + g.ToString("D") + "  ") "systemId"
    Assert.Equal(g, resolved)

[<Fact>]
let ``leading/trailing whitespace + '$ref' 도 trim 후 lookup`` () =
    let plan = newPlan()
    let g = Guid.NewGuid()
    ToolOperations.registerVar plan "cyl" g
    let resolved = ToolOperations.resolveGuidOrVar plan "  $cyl  " "systemId"
    Assert.Equal(g, resolved)

// ─── registerVar ──────────────────────────────────────────────────────────

[<Fact>]
let ``empty assignVar = no-op (cache count 변화 X)`` () =
    let plan = newPlan()
    ToolOperations.registerVar plan "" (Guid.NewGuid())
    Assert.Equal(0, plan.VarCache.Count)

[<Fact>]
let ``null assignVar = no-op`` () =
    let plan = newPlan()
    ToolOperations.registerVar plan null (Guid.NewGuid())
    Assert.Equal(0, plan.VarCache.Count)

[<Fact>]
let ``정상 assignVar 등록 → cache count 증가 + lookup 가능`` () =
    let plan = newPlan()
    let g = Guid.NewGuid()
    ToolOperations.registerVar plan "cyl" g
    Assert.Equal(1, plan.VarCache.Count)
    let mutable v = Guid.Empty
    Assert.True(plan.VarCache.TryGetValue("cyl", &v))
    Assert.Equal(g, v)

[<Fact>]
let ``잘못된 assignVar 형식 = invalidOp (sanitizeVarName 위임)`` () =
    let plan = newPlan()
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.registerVar plan "1bad" (Guid.NewGuid()))
    Assert.Contains("VALIDATION_ERROR", ex.Message)

[<Fact>]
let ``중복 assignVar = invalidOp`` () =
    let plan = newPlan()
    ToolOperations.registerVar plan "cyl" (Guid.NewGuid())
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.registerVar plan "cyl" (Guid.NewGuid()))
    Assert.Contains("이미 정의되어", ex.Message)

[<Fact>]
let ``cap (50) 초과 등록 = invalidOp + cap 메시지`` () =
    let plan = newPlan()
    for i in 1 .. ToolOperations.VarCacheMaxCount do
        ToolOperations.registerVar plan ($"v{i}") (Guid.NewGuid())
    Assert.Equal(ToolOperations.VarCacheMaxCount, plan.VarCache.Count)
    let ex = Assert.Throws<InvalidOperationException>(fun () ->
        ToolOperations.registerVar plan "overflow" (Guid.NewGuid()))
    Assert.Contains("cap", ex.Message)
    Assert.Contains(string ToolOperations.VarCacheMaxCount, ex.Message)

// ─── ImportPlanBuilder cascade ────────────────────────────────────────────

[<Fact>]
let ``초기 ImportPlanBuilder 의 CascadeFailureFlag 는 false`` () =
    let plan = newPlan()
    Assert.False(plan.CascadeFailureFlag)

[<Fact>]
let ``SignalCascadeFailure 후 flag = true 로 set`` () =
    let plan = newPlan()
    plan.SignalCascadeFailure()
    Assert.True(plan.CascadeFailureFlag)

[<Fact>]
let ``SignalCascadeFailure 는 누적된 ops 를 비움 (Plan.Clear 동반)`` () =
    // DsSystem ctor 등이 internal 이라 테스트 어셈블리에서 직접 호출 불가 →
    // 단순 tuple-payload DU case (LinkSystemToProject) 를 sentinel 로 사용해 ops 를 채움.
    let plan = newPlan()
    plan.Add(Ds2.Core.Store.LinkSystemToProject(Guid.NewGuid(), Guid.NewGuid(), true))
    plan.Add(Ds2.Core.Store.LinkSystemToProject(Guid.NewGuid(), Guid.NewGuid(), false))
    Assert.Equal(2, plan.Count)
    plan.SignalCascadeFailure()
    Assert.Equal(0, plan.Count)
    Assert.True(plan.IsEmpty)

[<Fact>]
let ``SignalCascadeFailure 는 VarCache 는 건드리지 않음 (cascade 후 후속 호출은 BATCH_ABORTED 단락이라 영향 없음)`` () =
    let plan = newPlan()
    ToolOperations.registerVar plan "cyl" (Guid.NewGuid())
    plan.SignalCascadeFailure()
    Assert.Equal(1, plan.VarCache.Count)
