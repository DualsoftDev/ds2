module Ds2.Runtime.Tests.TokenSkipTests

open System
open System.Text.Json
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.TestKit.ModelBuilder
open Ds2.Runtime.Model
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core

// #8938 — DS2 Text 5 `skip when token.필드 ==|!=|in …`. 시작하는 Work(또는 Call 의 부모 Work)가 쥔 토큰의
// 제품 사양(TokenSpec.Fields — JSON 리터럴 글)으로 판정한다. 전에는 SimIndex 가 token 잎을 통째로 거절했다(DS2_V5_UNSUPPORTED).

let private literal (text: string) = JsonDocument.Parse(text).RootElement.Clone()

/// 투입(Source) > 검사 > 배출(Sink). `onCall` 이면 skip 조건을 검사 안의 Call(D.Run)에, 아니면 검사 Work 에 단다.
let private model (specs: (int * string * (string * string) list * bool) list) (op: string) (values: string list) (onCall: bool) =
    let store = createStore ()
    let project = addProject store "사양분기"
    let system = addSystem store "S" project.Id true
    let flow = addFlow store "F" system.Id
    let input = addWork store "투입" flow.Id
    let check = addWork store "검사" flow.Id
    let output = addWork store "배출" flow.Id
    input.Duration <- Some(TimeSpan.FromMilliseconds 100.0)
    output.Duration <- Some(TimeSpan.FromMilliseconds 50.0)
    input.TokenRole <- TokenRole.Source
    output.TokenRole <- TokenRole.Sink
    addWorkArrow store system.Id input.Id check.Id ArrowType.Start |> ignore
    addWorkArrow store system.Id check.Id output.Id ArrowType.Start |> ignore
    for (id, label, fields, fromInput) in specs do
        project.TokenSpecs.Add({ Id = id; Label = label; Fields = Map.ofList fields; WorkId = (if fromInput then Some input.Id else None) })
    let skip = newCondition ConditionType.SkipAction
    skip.Token <- Some({ Field = "옵션"; Op = op; Kind = "bool"; Values = values |> List.map literal } : TokenPredicate)
    let mutable deviceRun = Guid.Empty
    if onCall then
        let device = addSystem store "D" project.Id false
        let motion = addFlow store "M" device.Id
        let run = addWork store "Run" motion.Id
        run.Duration <- Some(TimeSpan.FromMilliseconds 100.0)
        let api = addApiDef store "Run" device.Id
        api.TxGuid <- Some run.Id
        api.RxGuid <- Some run.Id
        let callId = addCallWithLinkedApiDefs store check.Id "D" "Run" [ api.Id ]
        store.Calls.[callId].Conditions.Add skip
        deviceRun <- run.Id
    else
        check.Duration <- Some(TimeSpan.FromMilliseconds 100.0)
        check.Conditions.Add skip
    store, input, check, output, deviceRun

/// 배출이 끝날 때까지 돌리고, Going 을 거친 Work 의 집합을 돌려준다.
let private run (store: DsStore, input: Work, _check: Work, output: Work, _deviceRun: Guid) =
    let index = SimIndex.build store 10
    let engine = new EventDrivenEngine(index, RuntimeMode.Simulation, None, SimulationExecutionOptions.Explicit) :> ISimulationEngine
    let went = Collections.Generic.HashSet<Guid>()
    engine.WorkStateChanged.Add(fun e -> if e.NewState = Status4.Going then went.Add(e.WorkGuid) |> ignore)
    engine.StartWithHomingPhase() |> ignore
    engine.StepWithSourcePriming(input.Id, true) |> ignore
    let mutable n = 0
    while engine.GetWorkState(output.Id) <> Some Status4.Finish && n < 200 do
        engine.AdvanceSimulationTo(engine.CurrentTimeMs + 10L)
        engine.Step() |> ignore
        n <- n + 1
    Assert.True(engine.GetWorkState(output.Id) = Some Status4.Finish, $"배출이 끝나지 않았습니다. 배출={engine.GetWorkState(output.Id)}")
    went

let private one value = [ 1, "A", [ "옵션", value ], false ]

[<Theory>]
[<InlineData("ne", "true", "false", true)>]     // 옵션=false, `!= true` → 건너뜀
[<InlineData("ne", "true", "true", false)>]     // 옵션=true,  `!= true` → 실행
[<InlineData("eq", "true", "true", true)>]      // 옵션=true,  `== true` → 건너뜀
[<InlineData("eq", "true", "false", false)>]    // 옵션=false, `== true` → 실행
[<InlineData("in", "false", "false", true)>]    // 옵션=false, `in {false}` → 건너뜀
let ``Work skip when token — 제품 사양으로 건너뛰거나 실행한다(#8938)`` op value field skipped =
    let ((_, _, check, _, _) as m) = model (one field) op [ value ] false
    let went = run m
    Assert.Equal(not skipped, went.Contains check.Id)

[<Theory>]
[<InlineData("ne", "true", "false", true)>]
[<InlineData("eq", "true", "false", false)>]
let ``Call skip when token — 부모 Work 의 토큰 사양으로 그 Call 만 건너뛴다(#8938)`` op value field skipped =
    let ((_, _, check, _, deviceRun) as m) = model (one field) op [ value ] true
    let went = run m
    Assert.True(went.Contains check.Id, "부모 Work 는 실행된다(Call 만 건너뛴다)")
    Assert.Equal(not skipped, went.Contains deviceRun)

[<Fact>]
let ``사양이 여럿이면 출처 Work 가 맞는 사양(product … from)을 쓴다(#8938)`` () =
    // 사양 1(옵션=true, 출처 없음) · 사양 2(옵션=false, 출처 투입). 투입에서 생긴 토큰은 사양 2 다 → `!= true` 성립 → 건너뜀.
    let ((_, _, check, _, _) as m) = model [ 1, "기본", [ "옵션", "true" ], false; 2, "정밀", [ "옵션", "false" ], true ] "ne" [ "true" ] false
    let went = run m
    Assert.False(went.Contains check.Id, "사양 2(옵션=false)로 건너뛰어야 한다")

[<Fact>]
let ``사양을 정할 수 없으면 token 조건은 거짓 — 건너뛰지 않는다(#8938)`` () =
    // 사양 둘, 둘 다 출처 없음 → 토큰의 사양 미정 → 조건 거짓 → 검사 실행.
    let ((_, _, check, _, _) as m) = model [ 1, "A", [ "옵션", "false" ], false; 2, "B", [ "옵션", "false" ], false ] "ne" [ "true" ] false
    let went = run m
    Assert.True(went.Contains check.Id)

[<Fact>]
let ``token 잎은 Skip 밖에서는 거절한다(#8938)`` () =
    // Work 는 Skip 조건만 색인한다. Call 은 AutoAux·ComAux·Skip 을 모두 색인하므로 Call 의 조건을 AutoAux 로 옮겨 본다.
    let (store, _, _, _, _) = model (one "false") "ne" [ "true" ] true
    let condition = store.Calls.Values |> Seq.collect (fun c -> c.Conditions) |> Seq.exactlyOne
    condition.Type <- Some ConditionType.AutoAux
    let ex = Assert.Throws<InvalidOperationException>(fun () -> SimIndex.build store 10 |> ignore)
    Assert.Contains("Skip 에서만", ex.Message)

[<Fact>]
let ``지령 api 의 제품(Q43)이 출처·단일 사양보다 먼저다(#8938)`` () =
    // 사양 1(옵션=true, 출처 투입) · 사양 2(옵션=false). 투입을 지령하는 api 가 `product 2;` 를 적었다 → 토큰은 사양 2 →
    // `!= true` 성립 → 검사를 건너뛴다. api 제품이 없으면 출처 규칙으로 사양 1 → 실행(아래 대조).
    let build (apiProduct: int option) =
        let ((store, input, _, _, _) as m) = model [ 1, "기본", [ "옵션", "true" ], true; 2, "옵션", [ "옵션", "false" ], false ] "ne" [ "true" ] false
        let system = store.Flows.[input.ParentId].ParentId
        let api = addApiDef store "X" system
        api.TxGuid <- Some input.Id
        api.RxGuid <- Some input.Id
        api.TokenSpecId <- apiProduct
        m
    let ((_, _, check, _, _) as withApi) = build (Some 2)
    Assert.False((run withApi).Contains check.Id, "api 제품 2(옵션=false)로 건너뛰어야 한다")
    let ((_, _, check2, _, _) as withoutApi) = build None
    Assert.True((run withoutApi).Contains check2.Id, "api 제품이 없으면 출처 사양 1(옵션=true) → 실행")
