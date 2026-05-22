namespace Ds2.Core.Store

open System
open Ds2.Core

/// AASX / Mermaid / CSV 등 *DirectWrite 우회 경로* 가 공유하는 invariant 검증.
/// Core Add* / AddCall* / AddApiDefWithProperties 의 가드와 *같은 룰* 을 import path 에서도 적용.
/// 각 함수는 위반 시 *Some reasonString*, OK 면 None 반환 — 호출자가 skip + log 처리.
module ImportValidator =

    /// Project 단일 entity invariant.
    let validateProject (store: DsStore) (project: Project) : string option =
        if System.String.IsNullOrWhiteSpace project.Name then
            Some "Project 이름이 비어있거나 공백만"
        elif not (Queries.isProjectNameUnique project.Name (Some project.Id) store) then
            Some $"Project 이름 '{project.Name}' 가 이미 존재"
        else None

    /// System 단일 entity invariant.
    /// projectIdOpt: System 이 속한 Project ID (LinkSystemToProject 가 별개 op 라 추적 어려운 경우 None).
    /// None 이면 *전체 store* 에서 중복 검사 — over-restrictive 지만 안전한 fallback.
    let validateSystem (store: DsStore) (projectIdOpt: Guid option) (system: DsSystem) : string option =
        if System.String.IsNullOrWhiteSpace system.Name then
            Some "System 이름이 비어있거나 공백만"
        else
            match projectIdOpt with
            | Some projectId ->
                if not (Queries.isSystemNameUniqueInProject projectId system.Name (Some system.Id) store) then
                    Some $"System 이름 '{system.Name}' 가 같은 Project 안에 이미 존재"
                else None
            | None -> None

    /// Flow 단일 entity invariant — parent System 안 중복 검사.
    let validateFlow (store: DsStore) (flow: Flow) : string option =
        if System.String.IsNullOrWhiteSpace flow.Name then
            Some "Flow 이름이 비어있거나 공백만"
        elif not (Queries.isFlowNameUniqueInSystem flow.ParentId flow.Name (Some flow.Id) store) then
            Some $"Flow 이름 '{flow.Name}' 가 같은 System 안에 이미 존재"
        else None

    /// Work 단일 entity invariant — parent Flow 안 LocalName 중복 검사.
    let validateWork (store: DsStore) (work: Work) : string option =
        if System.String.IsNullOrWhiteSpace work.LocalName then
            Some "Work LocalName 이 비어있거나 공백만"
        elif not (Queries.isLocalNameUniqueInFlow work.ParentId work.LocalName (Some work.Id) store) then
            Some $"Work LocalName '{work.LocalName}' 가 같은 Flow 안에 이미 존재"
        else None

    /// Call 단일 entity invariant — parent Work 안 동명 *원본 Call* 중복 검사.
    /// Reference Call (ReferenceOf.IsSome) 은 검사 skip — 같은 이름이 정상.
    let validateCall (store: DsStore) (call: Call) : string option =
        if System.String.IsNullOrWhiteSpace call.DevicesAlias then
            Some "Call DevicesAlias 가 비어있거나 공백만"
        elif call.DevicesAlias.Contains '.' then
            Some $"Call DevicesAlias '{call.DevicesAlias}' 에 점(.) 포함"
        elif (not (System.String.IsNullOrWhiteSpace call.ApiName)) && call.ApiName.Contains '.' then
            Some $"Call ApiName '{call.ApiName}' 에 점(.) 포함"
        elif call.ReferenceOf.IsSome then
            // Reference Call — 원본 이름 그대로라 중복 검사 skip.
            None
        elif not (Queries.isCallNameUniqueInWork call.ParentId call.Name (Some call.Id) store) then
            Some $"Call 이름 '{call.Name}' 가 같은 Work 안에 이미 존재 (Reference Call 로 만들어야 함)"
        else None

    /// ApiDef 단일 entity invariant — parent System 안 이름 중복 검사.
    let validateApiDef (store: DsStore) (apiDef: ApiDef) : string option =
        if System.String.IsNullOrWhiteSpace apiDef.Name then
            Some "ApiDef 이름이 비어있거나 공백만"
        elif not (Queries.isApiDefNameUniqueInSystem apiDef.ParentId apiDef.Name (Some apiDef.Id) store) then
            Some $"ApiDef 이름 '{apiDef.Name}' 가 같은 System 안에 이미 존재"
        else None

    /// ArrowBetweenWorks 단일 entity invariant — source ≠ target.
    let validateArrowWorks (arrow: ArrowBetweenWorks) : string option =
        if arrow.SourceId = arrow.TargetId then
            Some "ArrowBetweenWorks Source = Target (self-loop)"
        else None

    /// ArrowBetweenCalls 단일 entity invariant — source ≠ target.
    let validateArrowCalls (arrow: ArrowBetweenCalls) : string option =
        if arrow.SourceId = arrow.TargetId then
            Some "ArrowBetweenCalls Source = Target (self-loop)"
        else None
