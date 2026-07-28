module Ds2.Aasx.Tests.KpiFixtures

open System
open Ds2.Core
open Ds2.Core.Store

/// KPI 통합 테스트용 초소형 시퀀스 프로젝트.
///
/// 모양:
///   Project "KpiPilot"
///   └── ActiveSystem "Line1"
///       ├── Flow "MainFlow"
///       │   ├── Work "MainFlow.PickUp"
///       │   │   ├── Call "Robot.Pick"     (InTag=inFoo, OutTag=outBar)
///       │   │   └── Call "Robot.Place"
///       │   └── Work "MainFlow.Return"
///       └── ArrowWork PickUp → Return
///
/// KPI 예상 개수 (규약 기반):
///   System (Line1)              — 6 metrics (OEE/Avail/Perf/Q/MTBF/MTTR)
///   Work × 2 (PickUp, Return)   — 생성 제외
///   Call × 2 (Pick, Place)      — 생성 제외
///   ArrowWork × 1               — 2 metrics
///   UserTag × 2 (inFoo, outBar) — 각 1 metric = 2
///   총계: 10 targets
let buildSmallProject () : DsStore * Project =
    let store = DsStore()

    let project = Project("KpiPilot")
    store.Projects.[project.Id] <- project

    // Active System — Line1
    let sys = DsSystem("Line1")
    store.Systems.[sys.Id] <- sys
    project.ActiveSystemIds.Add(sys.Id)

    // Flow
    let flow = Flow("MainFlow", sys.Id)
    store.Flows.[flow.Id] <- flow

    // Works
    let work1 = Work("MainFlow", "PickUp", flow.Id)
    let work2 = Work("MainFlow", "Return", flow.Id)
    store.Works.[work1.Id] <- work1
    store.Works.[work2.Id] <- work2

    // Calls (Work1 안)
    let call1 = Call("Robot", "Pick", work1.Id)
    let call2 = Call("Robot", "Place", work1.Id)
    store.Calls.[call1.Id] <- call1
    store.Calls.[call2.Id] <- call2

    // ApiCalls with InTag / OutTag — UserTag KPI 대상
    let ac1 = ApiCall("Robot.Pick")
    ac1.InTag  <- Some (IOTag("inFoo",  "line1.robot.pick.in",  ""))
    ac1.OutTag <- Some (IOTag("outBar", "line1.robot.pick.out", ""))
    call1.ApiCalls.Add(ac1)

    // ArrowBetweenWorks (PickUp → Return)
    let arrow = ArrowBetweenWorks(sys.Id, work1.Id, work2.Id, ArrowType.Start)
    store.ArrowWorks.[arrow.Id] <- arrow

    store, project
