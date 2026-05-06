namespace Ds2.LlmAgent

open System
open Ds2.Core
open Ds2.Core.Store

/// LLM mutation/read tool handler 가 호출하는 F# 측 helper.
///
/// 결정 7 (d): mutation = `ImportPlanBuilder` 에 `ImportPlanOperation` 누적. turn end `ApplyImportPlan` 1회.
/// `Ds2.Core` 의 entity ctor (e.g. `DsSystem`) 가 internal 이라 C# tool method 가 직접 호출 불가 →
/// 본 module 이 F# 측 wrapper.
[<RequireQualifiedAccess>]
module ToolOperations =

    /// add_system mutation tool 의 plan 누적 wrapper.
    ///
    /// **현재 phase 1c 단순화**: 첫 번째 project 에 자동 부착. project 가 0개면 invalidOp.
    /// (phase 1d 에서 projectId 인자 또는 selection 기반 resolve)
    /// 반환: 새로 생성된 system Id (LLM 응답에 포함).
    let queueAddSystem (plan: ImportPlanBuilder) (store: DsStore) (name: string) (isActive: bool) : Guid =
        if String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "System name 이 비어있습니다."
        let project =
            match Queries.allProjects store with
            | [] -> invalidOp "프로젝트가 없습니다. 먼저 프로젝트를 생성하세요."
            | p :: _ -> p
        let sys = DsSystem(name)
        plan.Add(AddSystem sys)
        plan.Add(LinkSystemToProject(project.Id, sys.Id, isActive))
        sys.Id

    /// list_systems read tool. 모든 project 의 active + passive 시스템.
    /// 반환: (Id, Name, IsActive) tuple 의 목록.
    let listSystems (store: DsStore) : (Guid * string * bool) list =
        Queries.allProjects store
        |> List.collect (fun p ->
            let active  = Queries.activeSystemsOf  p.Id store |> List.map (fun s -> s.Id, s.Name, true)
            let passive = Queries.passiveSystemsOf p.Id store |> List.map (fun s -> s.Id, s.Name, false)
            active @ passive)
