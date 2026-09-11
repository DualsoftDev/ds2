module Ds2.Store.Editor.Tests.BodyLineTests

open System
open Xunit
open Xunit.Abstractions
open Ds2.Core
open Ds2.Core.Store
open Ds2.CSV
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core

// 차체 라인 ST01~ST10 — 스테이션마다 다른 차체가 동시에 올라가므로 Flow 10개.
// 체인은 Flow 경계를 넘어 끝까지 이어져야 한다(스테이션 간 이송 = 파이프라인).
type BodyLine(out: ITestOutputHelper) =

    let csv =
        String.concat "\n" [
            "FLOW,WORK,CALL"
            "ST01_플로어투입,플로어패널투입,라인컨베이어.이송=6S>라인컨베이어.정지=300MS>플로어클램프LH.전진=800MS;라인컨베이어.정지>플로어클램프RH.전진=800MS"
            "ST02_크로스멤버조립,크로스멤버조립,전방멤버로봇.취출=6S>전방멤버로봇.안착=6S>전방멤버클램프.전진=600MS;후방멤버로봇.취출=6S>후방멤버로봇.안착=6S>후방멤버클램프.전진=600MS"
            "ST03_언더바디용접,언더바디용접,전방용접슬라이드.전진=1S>전방용접건.가압=500MS>전방용접전원.ON=1S>전방용접전원.OFF=100MS>전방용접건.개방=500MS>전방용접슬라이드.후진=1S>전방멤버클램프.후진=600MS;후방용접슬라이드.전진=1S>후방용접건.가압=500MS>후방용접전원.ON=1S>후방용접전원.OFF=100MS>후방용접건.개방=500MS>후방용접슬라이드.후진=1S>후방멤버클램프.후진=600MS"
            "ST04_사이드패널조립,사이드패널조립,사이드로봇LH.취출=7S>사이드로봇LH.안착=7S>사이드클램프LH.전진=800MS;사이드로봇RH.취출=7S>사이드로봇RH.안착=7S>사이드클램프RH.전진=800MS"
            "ST05_프레이밍용접,프레이밍용접,프레이밍용접슬라이드LH.전진=2S>프레이밍용접건LH.가압=500MS>프레이밍용접전원LH.ON=1S>프레이밍용접전원LH.OFF=100MS>프레이밍용접건LH.개방=500MS>프레이밍용접슬라이드LH.후진=2S;프레이밍용접슬라이드RH.전진=2S>프레이밍용접건RH.가압=500MS>프레이밍용접전원RH.ON=1S>프레이밍용접전원RH.OFF=100MS>프레이밍용접건RH.개방=500MS>프레이밍용접슬라이드RH.후진=2S"
            "ST06_루프패널조립,루프패널조립,루프로봇.취출=7S>루프로봇.안착=7S>루프클램프LH.전진=600MS;루프로봇.안착>루프클램프RH.전진=600MS"
            "ST07_루프용접,루프용접,루프용접슬라이드LH.전진=1.5S>루프용접건LH.가압=500MS>루프용접전원LH.ON=1S>루프용접전원LH.OFF=100MS>루프용접건LH.개방=500MS>루프용접슬라이드LH.후진=1.5S;루프용접슬라이드RH.전진=1.5S>루프용접건RH.가압=500MS>루프용접전원RH.ON=1S>루프용접전원RH.OFF=100MS>루프용접건RH.개방=500MS>루프용접슬라이드RH.후진=1.5S"
            "ST08_사이드보강용접,사이드보강용접,사이드보강슬라이드LH.전진=1.5S>사이드보강용접건LH.가압=500MS>사이드보강용접전원LH.ON=1S>사이드보강용접전원LH.OFF=100MS>사이드보강용접건LH.개방=500MS>사이드보강슬라이드LH.후진=1.5S;사이드보강슬라이드RH.전진=1.5S>사이드보강용접건RH.가압=500MS>사이드보강용접전원RH.ON=1S>사이드보강용접전원RH.OFF=100MS>사이드보강용접건RH.개방=500MS>사이드보강슬라이드RH.후진=1.5S"
            "ST09_하부보강용접,하부보강용접,하부용접리프트.상승=2S>하부용접건LH.가압=500MS>하부용접전원LH.ON=1S>하부용접전원LH.OFF=100MS>하부용접건LH.개방=500MS>하부용접리프트.하강=2S;하부용접리프트.상승>하부용접건RH.가압=500MS>하부용접전원RH.ON=1S>하부용접전원RH.OFF=100MS>하부용접건RH.개방=500MS>하부용접리프트.하강"
            "ST10_차체반출,차체반출,플로어클램프LH.후진=800MS>라인컨베이어.이송>라인컨베이어.정지;플로어클램프RH.후진=800MS>라인컨베이어.이송;사이드클램프LH.후진=800MS>라인컨베이어.이송;사이드클램프RH.후진=800MS>라인컨베이어.이송;루프클램프LH.후진=600MS>라인컨베이어.이송;루프클램프RH.후진=600MS>라인컨베이어.이송" ]

    let load () =
        match CsvImporter.parseBasicContent csv with
        | Error e -> failwith (String.concat "\n" e)
        | Ok doc ->
            match CsvImporter.loadBasicProjectWith true doc "차체라인" "BodyLine" with
            | Error e -> failwith (String.concat "\n" e)
            | Ok s -> doc, s

    let acts (store: DsStore) =
        store.Works.Values |> Seq.filter (fun w -> not (w.Name.Contains "_Flow.")) |> List.ofSeq

    [<Fact>]
    member _.``ST01~ST10 이 끊기지 않고 한 줄로 이어진다`` () =
        let _, store = load ()
        let works = acts store
        let ids = works |> List.map (fun w -> w.Id) |> Set.ofList
        let chain =
            store.ArrowWorks.Values
            |> Seq.filter (fun a -> ids.Contains a.SourceId && ids.Contains a.TargetId
                                    && a.ArrowType = ArrowType.StartReset)
            |> Seq.map (fun a -> a.SourceId, a.TargetId)
            |> List.ofSeq
        // Start + 10 Work + Clear = 12 노드, StartReset 체인 11개
        Assert.Equal(12, List.length works)
        Assert.Equal(11, List.length chain)
        // 체인을 Start 부터 따라가 끝까지 12개 노드를 모두 지나는지 확인
        let nextOf = dict chain
        let nameOf id = works |> List.find (fun w -> w.Id = id) |> fun w -> w.Name
        let start = works |> List.find (fun w -> w.TokenRole = TokenRole.Source)
        let rec walk id acc =
            match nextOf.TryGetValue id with
            | true, nxt -> walk nxt (nameOf nxt :: acc)
            | false, _ -> List.rev acc
        let path = nameOf start.Id :: walk start.Id []
        out.WriteLine(String.concat "\n  → " path)
        Assert.Equal(12, List.length path)
        Assert.Contains("Start", List.head path)
        Assert.Contains("Clear", List.last path)

    [<Fact>]
    member _.``Start 와 Clear 는 양 끝에 하나씩만 생긴다`` () =
        let works = acts (snd (load ()))
        Assert.Single(works |> List.filter (fun w -> w.LocalName = "Start")) |> ignore
        Assert.Single(works |> List.filter (fun w -> w.LocalName = "Clear")) |> ignore
        Assert.Equal("ST01_플로어투입", (works |> List.find (fun w -> w.LocalName = "Start")).FlowPrefix)
        Assert.Equal("ST10_차체반출", (works |> List.find (fun w -> w.LocalName = "Clear")).FlowPrefix)

    [<Fact>]
    member _.``Flow 10개 · 동작시간이 모두 반영된다`` () =
        let doc, store = load ()
        Assert.Equal(10, doc.Works |> List.map (fun w -> w.FlowName) |> List.distinct |> List.length)
        let ms (s: string) =
            store.Works.Values
            |> Seq.filter (fun w -> w.Name.EndsWith(s: string))
            |> Seq.tryPick (fun w -> w.Duration |> Option.map (fun d -> d.TotalMilliseconds))
        Assert.Equal(Some 6000.0, ms "라인컨베이어_Flow.이송")
        Assert.Equal(Some 7000.0, ms "사이드로봇LH_Flow.취출")
        Assert.Equal(Some 1500.0, ms "루프용접슬라이드LH_Flow.전진")

    // CSV 기본 3열 모델은 주소가 아예 없다. 시뮬레이션은 실 I/O 없이 돌아야 하며
    // ("Simulation = Real→Virtual"), hot path 에서 예외를 던지면 안 된다.
    [<Fact>]
    member _.``주소 없는 모델도 예외 없이 시뮬레이션된다`` () =
        let _, store = load ()
        // 전제 확인: 모든 ApiCall 에 In/OutTag 가 없고 ApiDef 는 Normal 이다.
        let apiCalls = store.Calls.Values |> Seq.collect (fun c -> c.ApiCalls) |> List.ofSeq
        Assert.NotEmpty apiCalls
        Assert.True(apiCalls |> List.forall (fun ac -> ac.OutTag.IsNone && ac.InTag.IsNone))
        // V1/V2 를 만족하지 못하는 상태여야 이 테스트가 의미가 있다.
        let v1Issues =
            apiCalls
            |> List.choose (fun ac ->
                ac.ApiDefId
                |> Option.bind (fun id -> Queries.getApiDef id store)
                |> Option.bind (fun def -> V10Validation.validateApiCallV1 def ac))
        Assert.NotEmpty v1Issues
        // 그럼에도 dispatch 는 예외 없이 None 을 돌려줘야 한다.
        for ac in apiCalls do
            match ac.ApiDefId |> Option.bind (fun id -> Queries.getApiDef id store) with
            | Some def ->
                Assert.True((RuntimeSemantics.tryEmitOutput def ac).IsNone)
                Assert.True((RuntimeSemantics.tryCompletionTrigger def ac).IsNone)
            | None -> ()

    [<Fact>]
    member _.``주소 없는 모델로 엔진을 돌려도 사이클이 완주한다`` () =
        let _, store = load ()
        let index = SimIndex.build store 10
        use engine = new EventDrivenEngine(index, RuntimeMode.Simulation) :> ISimulationEngine
        engine.SpeedMultiplier <- 1.0
        engine.Start()
        let acts =
            store.Works.Values |> Seq.filter (fun w -> not (w.Name.Contains "_Flow.")) |> List.ofSeq
        let start = acts |> List.find (fun w -> w.TokenRole = TokenRole.Source)
        let clear = acts |> List.find (fun w -> w.TokenRole = TokenRole.Sink)
        engine.StepWithSourcePriming(start.Id, true) |> ignore
        let mutable n = 0
        while n < 4000 && engine.GetWorkState(clear.Id) <> Some Status4.Finish do
            engine.AdvanceSimulationTo(engine.CurrentTimeMs + 50L)
            engine.Step() |> ignore
            n <- n + 1
        let st = engine.GetWorkState(clear.Id)
        engine.Stop()
        let msg = sprintf "주소 없는 모델이 완주하지 못했습니다. Clear=%A" st
        Assert.True((st = Some Status4.Finish), msg)
