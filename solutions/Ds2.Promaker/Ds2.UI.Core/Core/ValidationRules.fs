namespace Ds2.UI.Core

open Ds2.Core

type ValidationResult =
    | Valid
    | Invalid of string list

// =============================================================================
// ValidationRules - 기본 검증 규칙
// =============================================================================

module ValidationRules =

    let notEmpty (fieldName: string) (value: string) : ValidationResult =
        if System.String.IsNullOrWhiteSpace(value) then
            Invalid [ sprintf "%s cannot be empty" fieldName ]
        else
            Valid

    let validGuid (fieldName: string) (value: System.Guid) : ValidationResult =
        if value = System.Guid.Empty then
            Invalid [ sprintf "%s cannot be empty GUID" fieldName ]
        else
            Valid

    let notEmptyList (fieldName: string) (list: 'a list) : ValidationResult =
        if List.isEmpty list then
            Invalid [ sprintf "%s cannot be empty" fieldName ]
        else
            Valid

    let combine (results: ValidationResult list) : ValidationResult =
        let errors =
            results
            |> List.choose (fun result ->
                match result with
                | Invalid errs -> Some errs
                | Valid -> None)
            |> List.concat
        if List.isEmpty errors then Valid else Invalid errors

// =============================================================================
// ProjectValidation / SystemValidation / FlowValidation / WorkValidation
// =============================================================================

module ProjectValidation =
    open ValidationRules

    let validate (project: Project) : ValidationResult =
        combine [
            validGuid "Project.Id" project.Id
            notEmpty "Project.Name" project.Name
        ]

    let isValid (project: Project) = match validate project with | Valid -> true | Invalid _ -> false
    let getErrors (project: Project) = match validate project with | Valid -> [] | Invalid errors -> errors

module SystemValidation =
    open ValidationRules

    let validate (system: DsSystem) : ValidationResult =
        combine [
            validGuid "System.Id" system.Id
            notEmpty "System.Name" system.Name
        ]

    let isValid (system: DsSystem) = match validate system with | Valid -> true | Invalid _ -> false
    let getErrors (system: DsSystem) = match validate system with | Valid -> [] | Invalid errors -> errors

module FlowValidation =
    open ValidationRules

    let validate (flow: Flow) : ValidationResult =
        combine [
            validGuid "Flow.Id" flow.Id
            notEmpty "Flow.Name" flow.Name
        ]

    let isValid (flow: Flow) = match validate flow with | Valid -> true | Invalid _ -> false

module WorkValidation =
    open ValidationRules

    let validate (work: Work) : ValidationResult =
        combine [
            validGuid "Work.Id" work.Id
            notEmpty "Work.Name" work.Name
        ]

    let validateConnections (work: Work) (store: DsStore) : ValidationResult =
        let workCallIds =
            DsQuery.callsOf work.Id store |> List.map (fun c -> c.Id) |> Set.ofList
        let flowCallIds =
            DsQuery.worksOf work.ParentId store
            |> List.collect (fun w -> DsQuery.callsOf w.Id store)
            |> List.map (fun c -> c.Id)
            |> Set.ofList
        let invalidArrows =
            DsQuery.allArrowCalls store
            |> List.filter (fun arrow ->
                let touchesWork =
                    Set.contains arrow.SourceId workCallIds || Set.contains arrow.TargetId workCallIds
                let withinSameFlow =
                    Set.contains arrow.SourceId flowCallIds && Set.contains arrow.TargetId flowCallIds
                touchesWork && not withinSameFlow)
        if List.isEmpty invalidArrows then Valid
        else Invalid [ sprintf "Work '%s' has call arrows pointing outside its parent Flow" work.Name ]

// =============================================================================
// OneToOneValidation - 1:1 관계 검증
// =============================================================================

module OneToOneValidation =
    open ValidationRules

    let validateFlowParent (flow: Flow) (store: DsStore) : ValidationResult =
        if store.SystemsReadOnly.ContainsKey(flow.ParentId) then Valid
        else Invalid [ sprintf "Flow '%s' references non-existent System GUID: %A" flow.Name flow.ParentId ]

    let validateWorkParent (work: Work) (store: DsStore) : ValidationResult =
        if store.FlowsReadOnly.ContainsKey(work.ParentId) then Valid
        else Invalid [ sprintf "Work '%s' references non-existent Flow GUID: %A" work.Name work.ParentId ]

    let validateArrowWorkParent (arrow: ArrowBetweenWorks) (store: DsStore) : ValidationResult =
        if store.FlowsReadOnly.ContainsKey(arrow.ParentId) then Valid
        else Invalid [ sprintf "ArrowBetweenWorks references non-existent Flow GUID: %A" arrow.ParentId ]

    let validateApiDefParent (apiDef: ApiDef) (store: DsStore) : ValidationResult =
        if store.SystemsReadOnly.ContainsKey(apiDef.ParentId) then Valid
        else Invalid [ sprintf "ApiDef '%s' references non-existent System GUID: %A" apiDef.Name apiDef.ParentId ]

    let validateAllOneToOne (store: DsStore) : ValidationResult =
        ValidationRules.combine [
            yield! store.FlowsReadOnly.Values   |> Seq.map (fun f -> validateFlowParent f store)   |> Seq.toList
            yield! store.WorksReadOnly.Values   |> Seq.map (fun w -> validateWorkParent w store)   |> Seq.toList
            yield! store.ArrowWorksReadOnly.Values |> Seq.map (fun a -> validateArrowWorkParent a store) |> Seq.toList
            yield! store.ApiDefsReadOnly.Values |> Seq.map (fun a -> validateApiDefParent a store) |> Seq.toList
        ]

// =============================================================================
// NameUniquenessValidation - 이름 중복 검증
// =============================================================================

module NameUniquenessValidation =
    open ValidationRules

    // ── 공통 헬퍼 ──────────────────────────────────────────────────────────────

    /// 시스템 범위 내 엔티티 이름 중복 검사
    let private validateSystemScopedNames
        (typeName: string)
        (getItems: System.Guid -> DsStore -> 'a list)
        (getName: 'a -> string)
        (store: DsStore)
        : ValidationResult =
        let errors =
            store.SystemsReadOnly.Values
            |> Seq.collect (fun system ->
                getItems system.Id store
                |> List.groupBy getName
                |> List.filter (fun (_, items) -> List.length items > 1)
                |> List.map (fun (name, _) ->
                    sprintf "Duplicate %s name '%s' in System '%s'" typeName name system.Name))
            |> Seq.toList
        if List.isEmpty errors then Valid else Invalid errors

    // ── 공개 검증 함수 ─────────────────────────────────────────────────────────

    let validateProjectNames (store: DsStore) : ValidationResult =
        let duplicates =
            store.ProjectsReadOnly.Values
            |> Seq.toList
            |> List.groupBy (fun p -> p.Name)
            |> List.filter (fun (_, items) -> List.length items > 1)
            |> List.map fst
        if List.isEmpty duplicates then Valid
        else Invalid (duplicates |> List.map (fun name -> sprintf "Duplicate Project name: '%s'" name))

    let validateSystemNames (store: DsStore) : ValidationResult =
        let errors =
            store.ProjectsReadOnly.Values
            |> Seq.collect (fun project ->
                DsQuery.projectSystemsOf project.Id store
                |> List.groupBy (fun s -> s.Name)
                |> List.filter (fun (_, items) -> List.length items > 1)
                |> List.map (fun (name, _) ->
                    sprintf "Duplicate System name '%s' in Project '%s'" name project.Name))
            |> Seq.toList
        if List.isEmpty errors then Valid else Invalid errors

    let validateFlowNames    store = validateSystemScopedNames EntityTypeNames.Flow      DsQuery.flowsOf       (fun f -> f.Name) store
    let validateApiDefNames  store = validateSystemScopedNames EntityTypeNames.ApiDef    DsQuery.apiDefsOf     (fun a -> a.Name) store
    let validateButtonNames  store = validateSystemScopedNames EntityTypeNames.Button    DsQuery.buttonsOf     (fun b -> b.Name) store
    let validateLampNames    store = validateSystemScopedNames EntityTypeNames.Lamp      DsQuery.lampsOf       (fun l -> l.Name) store
    let validateConditionNames store = validateSystemScopedNames EntityTypeNames.Condition DsQuery.conditionsOf (fun c -> c.Name) store
    let validateActionNames  store = validateSystemScopedNames EntityTypeNames.Action    DsQuery.actionsOf     (fun a -> a.Name) store

    /// ApiCall 이름 중복 검증 (시스템 내 모든 Call 탐색)
    let validateApiCallNames (store: DsStore) : ValidationResult =
        let errors =
            store.SystemsReadOnly.Values
            |> Seq.collect (fun system ->
                let apiCalls =
                    DsQuery.flowsOf system.Id store
                    |> List.collect (fun flow ->
                        DsQuery.worksOf flow.Id store
                        |> List.collect (fun work ->
                            DsQuery.callsOf work.Id store
                            |> List.collect (fun call -> call.ApiCalls |> Seq.toList)))
                    |> List.distinctBy (fun (ac: ApiCall) -> ac.Id)
                apiCalls
                |> List.groupBy (fun (ac: ApiCall) -> ac.Name)
                |> List.filter (fun (_, items) -> List.length items > 1)
                |> List.map (fun (name, _) ->
                    sprintf "Duplicate ApiCall name '%s' in System '%s'" name system.Name))
            |> Seq.toList
        if List.isEmpty errors then Valid else Invalid errors

    let validateAll (store: DsStore) : ValidationResult =
        combine [
            validateProjectNames store
            validateSystemNames store
            validateFlowNames store
            validateApiDefNames store
            validateApiCallNames store
            validateButtonNames store
            validateLampNames store
            validateConditionNames store
            validateActionNames store
        ]

// =============================================================================
// ValidationHelpers - 검증 헬퍼
// =============================================================================

module ValidationHelpers =

    let toResult (validationResult: ValidationResult) : Result<unit, string list> =
        match validationResult with
        | Valid -> Ok ()
        | Invalid errors -> Error errors

    let fromResult (result: Result<unit, string list>) : ValidationResult =
        match result with
        | Ok () -> Valid
        | Error errors -> Invalid errors

    let private combineSeq (validations: ValidationResult seq) =
        validations |> Seq.toList |> ValidationRules.combine

    let private validateRuntimeUniqueNames (store: DsStore) : ValidationResult =
        ValidationRules.combine [
            NameUniquenessValidation.validateProjectNames store
            NameUniquenessValidation.validateSystemNames store
            NameUniquenessValidation.validateApiDefNames store
            NameUniquenessValidation.validateButtonNames store
            NameUniquenessValidation.validateLampNames store
            NameUniquenessValidation.validateConditionNames store
            NameUniquenessValidation.validateActionNames store
        ]

    /// 편집 런타임 스토어 정합성 검증
    let validateStore (store: DsStore) : ValidationResult =
        ValidationRules.combine [
            store.ProjectsReadOnly.Values |> Seq.map ProjectValidation.validate |> combineSeq
            store.SystemsReadOnly.Values  |> Seq.map SystemValidation.validate  |> combineSeq
            store.FlowsReadOnly.Values    |> Seq.map FlowValidation.validate    |> combineSeq
            store.WorksReadOnly.Values    |> Seq.map WorkValidation.validate    |> combineSeq
            store.WorksReadOnly.Values    |> Seq.map (fun w -> WorkValidation.validateConnections w store) |> combineSeq
            OneToOneValidation.validateAllOneToOne store
            validateRuntimeUniqueNames store
        ]

    /// 운영 런타임 엄격 정합성 검증
    let validateStoreStrict (store: DsStore) : ValidationResult =
        ValidationRules.combine [
            validateStore store
            NameUniquenessValidation.validateAll store
        ]
