module Ds2.Store.Editor.Tests.SystemPackageTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// =============================================================================
// SystemPackage — 프로젝트 간 System 트리 복사(폐포 + remap) 코어 테스트.
// 전송층(파일/클립보드)은 "소스 = 별도 DsStore" 계약만 공유하므로,
// 여기서 소스 store 를 직접 만들거나 JSON 왕복시키는 것이 곧 두 전송층의 검증이다.
// =============================================================================

/// 소스: 프로젝트 + Active 시스템 + Flow + Work 2개(화살표 연결) + 디바이스 Call.
let private setupSource () =
    let source = createStore ()
    let project, system, flow, work1 = setupBasicHierarchy source
    let work2 = addWork source "Work2" flow.Id
    source.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
    source.AddCallsWithDevice(project.Id, work1.Id, [ "Dev.Api" ], true, None) |> ignore
    source, project, system, flow, work1

let private importIntoFreshTarget (source: DsStore) (rootId: Guid) =
    let target = createStore ()
    let tProject = addProject target "TargetProject"
    let roots : SystemImportRoot list = [ { Id = rootId; IsActive = true } ]
    let summary = target.ImportSystemsFrom(source, tProject.Id, roots)
    target, tProject, summary

[<Fact>]
let ``ImportSystemsFrom copies system tree with device closure into another store`` () =
    let source, _, system, _, _ = setupSource ()
    let target, tProject, summary = importIntoFreshTarget source system.Id

    Assert.Equal(1, summary.SystemCount)
    Assert.Equal(1, summary.DeviceCount)   // Dev 디바이스 시스템이 폐포로 딸려옴
    Assert.Equal(1, summary.FlowCount)
    Assert.Equal(2, summary.WorkCount)
    Assert.True(summary.CallCount >= 1)

    // Guid 전면 remap — 새 시스템 id 는 원본과 다르고, 대상 프로젝트에 Active 로 등록
    let newSysId = Assert.Single(summary.NewSystemIds)
    Assert.NotEqual<Guid>(system.Id, newSysId)
    Assert.Contains(newSysId, tProject.ActiveSystemIds)
    Assert.Equal(1, tProject.PassiveSystemIds.Count)   // 디바이스는 Passive 로 등록

    // 트리 재구성: Flow → Work 2 → Call 1, 화살표 1 (끝점 remap)
    let newFlow = Assert.Single(Queries.flowsOf newSysId target)
    let newWorks = Queries.worksOf newFlow.Id target
    Assert.Equal(2, newWorks.Length)
    // ArrowBetweenWorks.ParentId = System (arrowWorksOf 규약)
    let arrows = Queries.arrowWorksOf newSysId target
    Assert.Single(arrows) |> ignore
    let arrow = arrows.Head
    Assert.Contains(arrow.SourceId, newWorks |> List.map (fun w -> w.Id))
    Assert.Contains(arrow.TargetId, newWorks |> List.map (fun w -> w.Id))

    // Call 의 ApiCall.ApiDefId 가 대상 store 의 ApiDef 로 resolve 되고, 그 소유는 임포트된 디바이스
    let newCall =
        newWorks
        |> List.collect (fun w -> Queries.callsOf w.Id target)
        |> List.exactlyOne
    Assert.Equal(Status4.Ready, newCall.Status4)   // 런타임 상태 리셋
    let apiCall = Seq.head newCall.ApiCalls
    let apiDefId = apiCall.ApiDefId.Value
    Assert.True(target.ApiDefs.ContainsKey apiDefId)
    let deviceSysId = target.ApiDefs.[apiDefId].ParentId
    Assert.Contains(deviceSysId, tProject.PassiveSystemIds)
    // OriginFlowId 는 새 Flow 로 재지정 (heal 불필요)
    Assert.Equal(Some newFlow.Id, apiCall.OriginFlowId)

    // dangling 불변식 — 경고 0건이어야 정상
    Assert.Empty(summary.Warnings)

[<Fact>]
let ``ImportSystemsFrom into same store renames system on collision`` () =
    let source, project, system, _, _ = setupSource ()
    let roots : SystemImportRoot list = [ { Id = system.Id; IsActive = true } ]
    let summary = source.ImportSystemsFrom(source, project.Id, roots)

    let newSysId = Assert.Single(summary.NewSystemIds)
    Assert.Equal($"{system.Name}_1", source.Systems.[newSysId].Name)
    let rename = summary.Renames |> Seq.find (fun r -> r.Kind = "System" && r.OldName = system.Name)
    Assert.Equal($"{system.Name}_1", rename.NewName)
    // 원본 시스템/트리는 불변
    Assert.Equal(system.Name, source.Systems.[system.Id].Name)
    Assert.Contains(newSysId, project.ActiveSystemIds)

[<Fact>]
let ``BuildSystemPackageStore json roundtrip imports equivalently (clipboard path)`` () =
    let source, _, system, _, _ = setupSource ()

    // 클립보드 전송층과 동일 경로: 부분 store → JSON → 역직렬화 → 임포트
    let pkg = source.BuildSystemPackageStore([ system.Id ])
    Assert.Equal(2, pkg.Systems.Count)   // 루트 + 디바이스
    Assert.Empty(pkg.Projects)           // Project 는 봉투 소유 — payload 에 없음
    let json = Ds2.Serialization.JsonConverter.serialize pkg
    let restored = Ds2.Serialization.JsonConverter.deserialize<DsStore> json

    let target, tProject, summary = importIntoFreshTarget restored system.Id
    Assert.Equal(1, summary.SystemCount)
    Assert.Equal(1, summary.DeviceCount)
    Assert.Empty(summary.Warnings)
    let newSysId = Assert.Single(summary.NewSystemIds)
    let newFlow = Assert.Single(Queries.flowsOf newSysId target)
    Assert.Equal(2, (Queries.worksOf newFlow.Id target).Length)
    Assert.Equal(1, tProject.PassiveSystemIds.Count)

[<Fact>]
let ``ImportSystemsFrom is single undo step`` () =
    let source, _, system, _, _ = setupSource ()
    let target = createStore ()
    let tProject = addProject target "TargetProject"
    let systemCountBefore = target.Systems.Count
    let roots : SystemImportRoot list = [ { Id = system.Id; IsActive = true } ]
    target.ImportSystemsFrom(source, tProject.Id, roots) |> ignore
    Assert.True(target.Systems.Count > systemCountBefore)

    target.Undo()
    Assert.Equal(systemCountBefore, target.Systems.Count)
    // undo 는 백업 클론으로 엔티티를 복원하므로 기존 참조는 stale — store 에서 재조회
    let restoredProject = target.Projects.[tProject.Id]
    Assert.Empty(restoredProject.ActiveSystemIds |> Seq.filter (fun id -> not (target.Systems.ContainsKey id)))
