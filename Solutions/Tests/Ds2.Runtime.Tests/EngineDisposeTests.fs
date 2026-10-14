module Ds2.Runtime.Tests.EngineDisposeTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.CSV
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core

let private buildStore () =
    match CsvImporter.parseBasicContent "FLOW,WORK,CALL\nF,W,실린더.전진=500MS>실린더.후진=500MS" with
    | Error e -> failwith (String.concat "\n" e)
    | Ok doc ->
        match CsvImporter.loadBasicProject doc "P" "S" with
        | Error e -> failwith (String.concat "\n" e)
        | Ok store -> store

/// 편집기는 STEP 이 Work 시간만큼 기다리는 사이에 엔진을 버릴 수 있다(파일 드롭·새 프로젝트·STOP 후 PLAY).
/// 기다림이 끝나면 STEP 루프는 쥐고 있던 엔진의 EndStep 을 부르는데, 해제된 깨우기 신호를 건드려
/// ObjectDisposedException 으로 앱 전체가 종료됐다. 해제된 엔진에 대한 호출은 조용히 무시돼야 한다.
[<Fact>]
let ``EndStep on a disposed engine does not throw`` () =
    let index = SimIndex.build (buildStore ()) 10
    let engine = new EventDrivenEngine(index, RuntimeMode.Simulation) :> ISimulationEngine
    engine.Start()
    engine.Pause()

    engine.Dispose()

    let ex = Record.Exception(fun () -> engine.EndStep())
    Assert.Null ex
