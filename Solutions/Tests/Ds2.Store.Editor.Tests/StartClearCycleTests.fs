module Ds2.Store.Editor.Tests.StartClearCycleTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.CSV
open Ds2.Store.Editor.Tests.TestHelpers
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core

// Start/Clear 자동 추가 모델이 "Start 를 연속으로 눌러도 계속 흘러가는지" 를 실제 엔진으로 검증한다.
// 배선 의미(SimIndex/Build.fs):
//   A →StartReset→ B : B 는 A 가 끝나면 시작 + B 가 A 를 리셋(역방향)
//   A →Reset→ B      : A 가 B 를 리셋(정방향)
// 따라서 마지막 Work ⇄ Clear 는 상호 리셋이 되어 매 사이클 Clear 가 되살아나야 한다.

let private csvOf rows = String.concat "\n" ("FLOW,WORK,CALL" :: rows)

let private buildStore () =
    let content =
        csvOf [ "투입,W1,실린더.전진=30MS>실린더.후진=30MS"
                "가공,W2,드릴.시작=30MS>드릴.정지=30MS" ]
    match CsvImporter.parseBasicContent content with
    | Error e -> failwith (String.concat "\n" e)
    | Ok doc ->
        match CsvImporter.loadBasicProjectWith true doc "P" "S" with
        | Error e -> failwith (String.concat "\n" e)
        | Ok store -> store

let private activeWork (store: DsStore) (localName: string) =
    store.Works.Values
    |> Seq.filter (fun w -> not (w.Name.Contains "_Flow."))
    |> Seq.find (fun w -> w.LocalName = localName)

[<Fact>]
let ``Start 는 TokenSource Clear 는 TokenSink 로 등록된다`` () =
    let store = buildStore ()
    let index = SimIndex.build store 10
    Assert.Contains((activeWork store "Start").Id, index.TokenSourceGuids)
    Assert.True(index.TokenSinkGuids.Contains((activeWork store "Clear").Id))

[<Fact>]
let ``Start 를 연속으로 눌러도 사이클이 계속 돈다`` () =
    let store = buildStore ()
    let index = SimIndex.build store 10
    use engine = new EventDrivenEngine(index, RuntimeMode.Simulation) :> ISimulationEngine
    engine.SpeedMultiplier <- 1.0
    engine.Start()

    let start = activeWork store "Start"
    let clear = activeWork store "Clear"

    // "Start 누르기" = Source Work 기동. 이후 시계를 진행시키며 체인이 끝날 때까지 편다.
    // (StepWithSourcePriming 은 1스텝만 진행시키므로 펌핑이 없으면 Clear 까지 도달하지 않는다.)
    let pressStartAndRun () =
        engine.StepWithSourcePriming(start.Id, true) |> ignore
        let mutable n = 0
        while n < 200 && engine.GetWorkState(clear.Id) <> Some Status4.Finish do
            engine.AdvanceSimulationTo(engine.CurrentTimeMs + 20L)
            engine.Step() |> ignore
            n <- n + 1
        engine.GetWorkState(clear.Id) = Some Status4.Finish

    for i in 1 .. 5 do
        Assert.True(pressStartAndRun (), $"{i}번째 Start: Clear 가 완료되지 않았습니다. Clear={engine.GetWorkState(clear.Id)}")
        // 다음 기동을 위해 Start 가 Ready 로 복귀해야 한다.
        Assert.True(
            waitUntil 2000 (fun () -> engine.GetWorkState(start.Id) = Some Status4.Ready),
            $"{i}번째 Start 후 Start 가 Ready 로 복귀하지 않았습니다. Start={engine.GetWorkState(start.Id)}")
    engine.Stop()

// ── Flow = 동시작업 제품 단위 ───────────────────────────────────────────────
// Flow 끼리는 독립이어야 한다. Flow 경계를 넘어 StartReset 으로 이으면
// 동시작업 캐파가 1로 줄어든다.
module FlowIndependenceTests =

    let private twoFlowStore auto =
        let content =
            csvOf [ "LH,LH셋팅,LH클램프.전진=800MS"
                    "LH,LH체결,LH런너.체결=5S"
                    "RH,RH셋팅,RH클램프.전진=800MS"
                    "RH,RH체결,RH런너.체결=5S" ]
        match CsvImporter.parseBasicContent content with
        | Error e -> failwith (String.concat "\n" e)
        | Ok doc ->
            match CsvImporter.loadBasicProjectWith auto doc "P" "S" with
            | Error e -> failwith (String.concat "\n" e)
            | Ok s -> s

    let private acts (store: DsStore) =
        store.Works.Values |> Seq.filter (fun w -> not (w.Name.Contains "_Flow.")) |> List.ofSeq

    [<Fact>]
    let ``Flow 경계를 넘는 Work 화살표가 없다`` () =
        let store = twoFlowStore true
        let works = acts store
        let flowOf id = works |> List.tryFind (fun w -> w.Id = id) |> Option.map (fun w -> w.FlowPrefix)
        let crossing =
            store.ArrowWorks.Values
            |> Seq.choose (fun a ->
                match flowOf a.SourceId, flowOf a.TargetId with
                | Some s, Some tt when s <> tt -> Some $"{s} -> {tt}"
                | _ -> None)
            |> List.ofSeq
        let joined = String.concat ", " crossing
        Assert.True(List.isEmpty crossing, $"Flow 경계를 넘는 화살표: {joined}")

    [<Fact>]
    let ``Flow 마다 Start 와 Clear 가 하나씩 붙는다`` () =
        let works = acts (twoFlowStore true)
        for flow in [ "LH"; "RH" ] do
            let inFlow = works |> List.filter (fun w -> w.FlowPrefix = flow)
            Assert.Single(inFlow |> List.filter (fun w -> w.LocalName = "Start")) |> ignore
            Assert.Single(inFlow |> List.filter (fun w -> w.LocalName = "Clear")) |> ignore
        Assert.Equal(8, List.length works)   // (셋팅+체결+Start+Clear) x 2 Flow

    [<Fact>]
    let ``끄면 Flow 마다 체인만 남는다`` () =
        let store = twoFlowStore false
        let works = acts store
        Assert.Equal(4, List.length works)
        // Active Work 사이 화살표만 센다 (Passive 디바이스 Work 의 상호리셋 제외).
        let ids = works |> List.map (fun w -> w.Id) |> Set.ofList
        let activeArrows =
            store.ArrowWorks.Values
            |> Seq.filter (fun a -> ids.Contains a.SourceId && ids.Contains a.TargetId)
            |> Seq.length
        Assert.Equal(2, activeArrows)   // Flow 안 체인 1개씩, Flow 를 넘는 연결 없음
