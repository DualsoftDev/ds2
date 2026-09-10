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

// ── Flow 경계를 넘는 체인 ───────────────────────────────────────────────────
// Flow 는 제품이 머무는 자리다. 자리 사이는 행 순서대로 이어져야 한다(이송).
module FlowChainTests =

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
    let ``Flow 경계를 넘어 체인이 이어진다`` () =
        let store = twoFlowStore false
        let works = acts store
        let find n = works |> List.find (fun w -> w.LocalName = n)
        let arrow s t =
            store.ArrowWorks.Values
            |> Seq.filter (fun a -> a.SourceId = s && a.TargetId = t && a.ArrowType = ArrowType.StartReset)
            |> Seq.length
        Assert.Equal(1, arrow (find "LH셋팅").Id (find "LH체결").Id)
        Assert.Equal(1, arrow (find "LH체결").Id (find "RH셋팅").Id)   // Flow 경계
        Assert.Equal(1, arrow (find "RH셋팅").Id (find "RH체결").Id)

    [<Fact>]
    let ``Start 와 Clear 는 라인 양 끝에 하나씩만 붙는다`` () =
        let works = acts (twoFlowStore true)
        Assert.Equal(6, List.length works)   // Work 4 + Start + Clear
        let start = works |> List.find (fun w -> w.LocalName = "Start")
        let clear = works |> List.find (fun w -> w.LocalName = "Clear")
        Assert.Equal("LH", start.FlowPrefix)
        Assert.Equal("RH", clear.FlowPrefix)
