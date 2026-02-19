namespace Ds2.UI.Core

open Ds2.Core

/// <summary>
/// 검증 결과 타입
/// </summary>
type ValidationResult =
    | Valid
    | Invalid of string list  // 에러 메시지 목록

// =============================================================================
// ValidationRules - 기본 검증 규칙
// =============================================================================

/// <summary>
/// 도메인 엔티티 검증 규칙 모듈
///
/// <para>기본적인 필드 검증 규칙과 검증 결과 조합 유틸리티를 제공합니다.</para>
///
/// <example>
/// <code>
/// let result = ValidationRules.notEmpty "Name" project.Name
/// match result with
/// | Valid -> printfn "Valid"
/// | Invalid errors -> errors |> List.iter (printfn "%s")
/// </code>
/// </example>
/// </summary>
module ValidationRules =

    /// <summary>문자열이 비어있지 않은지 확인</summary>
    /// <param name="fieldName">필드 이름</param>
    /// <param name="value">검증할 문자열 값</param>
    /// <returns>유효하면 Valid, 아니면 Invalid</returns>
    let notEmpty (fieldName: string) (value: string) : ValidationResult =
        if System.String.IsNullOrWhiteSpace(value) then
            Invalid [ sprintf "%s cannot be empty" fieldName ]
        else
            Valid

    /// <summary>ID가 유효한 GUID 형식인지 확인 (Guid 타입은 이미 유효함)</summary>
    /// <param name="fieldName">필드 이름</param>
    /// <param name="value">검증할 GUID 값</param>
    /// <returns>유효하면 Valid, 아니면 Invalid</returns>
    let validGuid (fieldName: string) (value: System.Guid) : ValidationResult =
        if value = System.Guid.Empty then
            Invalid [ sprintf "%s cannot be empty GUID" fieldName ]
        else
            Valid

    /// <summary>리스트가 비어있지 않은지 확인</summary>
    /// <param name="fieldName">필드 이름</param>
    /// <param name="list">검증할 리스트</param>
    /// <returns>유효하면 Valid, 아니면 Invalid</returns>
    let notEmptyList (fieldName: string) (list: 'a list) : ValidationResult =
        if List.isEmpty list then
            Invalid [ sprintf "%s cannot be empty" fieldName ]
        else
            Valid

    /// <summary>여러 검증 결과 합치기</summary>
    /// <param name="results">검증 결과 리스트</param>
    /// <returns>모두 유효하면 Valid, 하나라도 실패하면 모든 에러 메시지를 포함한 Invalid</returns>
    let combine (results: ValidationResult list) : ValidationResult =
        let errors =
            results
            |> List.choose (fun result ->
                match result with
                | Invalid errs -> Some errs
                | Valid -> None)
            |> List.concat

        if List.isEmpty errors then
            Valid
        else
            Invalid errors

// =============================================================================
// ProjectValidation - Project 검증
// =============================================================================

/// <summary>
/// Project 엔티티 검증 모듈
/// </summary>
module ProjectValidation =

    open ValidationRules

    /// <summary>Project 검증</summary>
    /// <param name="project">검증할 Project</param>
    /// <returns>검증 결과</returns>
    let validate (project: Project) : ValidationResult =
        combine [
            validGuid "Project.Id" project.Id
            notEmpty "Project.Name" project.Name
        ]

    /// <summary>Project가 유효한지 확인</summary>
    /// <param name="project">검증할 Project</param>
    /// <returns>유효하면 true</returns>
    let isValid (project: Project) : bool =
        match validate project with
        | Valid -> true
        | Invalid _ -> false

    /// <summary>Project 검증 에러 메시지 가져오기</summary>
    /// <param name="project">검증할 Project</param>
    /// <returns>에러 메시지 리스트 (유효하면 빈 리스트)</returns>
    let getErrors (project: Project) : string list =
        match validate project with
        | Valid -> []
        | Invalid errors -> errors

// =============================================================================
// SystemValidation - DsSystem 검증
// =============================================================================

/// <summary>
/// DsSystem 엔티티 검증 모듈
/// </summary>
module SystemValidation =

    open ValidationRules

    /// <summary>DsSystem 검증</summary>
    /// <param name="system">검증할 DsSystem</param>
    /// <returns>검증 결과</returns>
    let validate (system: DsSystem) : ValidationResult =
        combine [
            validGuid "System.Id" system.Id
            notEmpty "System.Name" system.Name
        ]

    /// <summary>DsSystem이 유효한지 확인</summary>
    /// <param name="system">검증할 DsSystem</param>
    /// <returns>유효하면 true</returns>
    let isValid (system: DsSystem) : bool =
        match validate system with
        | Valid -> true
        | Invalid _ -> false

    /// <summary>DsSystem 검증 에러 메시지 가져오기</summary>
    /// <param name="system">검증할 DsSystem</param>
    /// <returns>에러 메시지 리스트</returns>
    let getErrors (system: DsSystem) : string list =
        match validate system with
        | Valid -> []
        | Invalid errors -> errors

// =============================================================================
// FlowValidation - Flow 검증
// =============================================================================

/// <summary>
/// Flow 엔티티 검증 모듈
/// </summary>
module FlowValidation =

    open ValidationRules

    /// <summary>Flow 검증</summary>
    /// <param name="flow">검증할 Flow</param>
    /// <returns>검증 결과</returns>
    let validate (flow: Flow) : ValidationResult =
        combine [
            validGuid "Flow.Id" flow.Id
            notEmpty "Flow.Name" flow.Name
        ]

    /// <summary>Flow가 유효한지 확인</summary>
    /// <param name="flow">검증할 Flow</param>
    /// <returns>유효하면 true</returns>
    let isValid (flow: Flow) : bool =
        match validate flow with
        | Valid -> true
        | Invalid _ -> false

// =============================================================================
// WorkValidation - Work 검증
// =============================================================================

/// <summary>
/// Work 엔티티 검증 모듈
/// </summary>
module WorkValidation =

    open ValidationRules

    /// <summary>Work 검증</summary>
    /// <param name="work">검증할 Work</param>
    /// <returns>검증 결과</returns>
    let validate (work: Work) : ValidationResult =
        combine [
            validGuid "Work.Id" work.Id
            notEmpty "Work.Name" work.Name
        ]

    /// <summary>
    /// Work의 Call들이 Arrow로 올바르게 연결되어 있는지 확인
    /// <para>DsStore 컨텍스트에서 검증해야 함</para>
    /// </summary>
    /// <param name="work">검증할 Work</param>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateConnections (work: Work) (store: DsStore) : ValidationResult =
        let workCallIds =
            DsQuery.callsOf work.Id store
            |> List.map (fun c -> c.Id)
            |> Set.ofList

        let flowCallIds =
            DsQuery.worksOf work.ParentId store
            |> List.collect (fun w -> DsQuery.callsOf w.Id store)
            |> List.map (fun c -> c.Id)
            |> Set.ofList

        let invalidArrows =
            DsQuery.allArrowCalls store
            |> List.filter (fun arrow ->
                let touchesWork =
                    Set.contains arrow.SourceId workCallIds
                    || Set.contains arrow.TargetId workCallIds

                let withinSameFlow =
                    Set.contains arrow.SourceId flowCallIds
                    && Set.contains arrow.TargetId flowCallIds

                touchesWork && not withinSameFlow)

        if List.isEmpty invalidArrows then
            Valid
        else
            Invalid [ sprintf "Work '%s' has call arrows pointing outside its parent Flow" work.Name ]

// =============================================================================
// OneToOneValidation - 1:1 관계 검증
// =============================================================================

/// <summary>
/// 엔티티 간 1:1 관계 검증 모듈
///
/// <para>ParentId 참조 무결성을 검증합니다.</para>
/// </summary>
module OneToOneValidation =

    open ValidationRules

    /// <summary>Flow의 ParentId가 유효한 DsSystem을 참조하는지 확인</summary>
    /// <param name="flow">검증할 Flow</param>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateFlowParent (flow: Flow) (store: DsStore) : ValidationResult =
        if store.SystemsReadOnly.ContainsKey(flow.ParentId) then
            Valid
        else
            Invalid [ sprintf "Flow '%s' references non-existent System GUID: %A" flow.Name flow.ParentId ]

    /// <summary>Work의 ParentId가 유효한 Flow를 참조하는지 확인</summary>
    /// <param name="work">검증할 Work</param>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateWorkParent (work: Work) (store: DsStore) : ValidationResult =
        if store.FlowsReadOnly.ContainsKey(work.ParentId) then
            Valid
        else
            Invalid [ sprintf "Work '%s' references non-existent Flow GUID: %A" work.Name work.ParentId ]

    /// <summary>ArrowBetweenWorks의 ParentId가 유효한지 확인</summary>
    /// <param name="arrow">검증할 ArrowBetweenWorks</param>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateArrowWorkParent (arrow: ArrowBetweenWorks) (store: DsStore) : ValidationResult =
        if store.FlowsReadOnly.ContainsKey(arrow.ParentId) then
            Valid
        else
            Invalid [ sprintf "ArrowBetweenWorks references non-existent Flow GUID: %A" arrow.ParentId ]

    /// <summary>ApiDef의 ParentId가 유효한 DsSystem을 참조하는지 확인</summary>
    /// <param name="apiDef">검증할 ApiDef</param>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateApiDefParent (apiDef: ApiDef) (store: DsStore) : ValidationResult =
        if store.SystemsReadOnly.ContainsKey(apiDef.ParentId) then
            Valid
        else
            Invalid [ sprintf "ApiDef '%s' references non-existent System GUID: %A" apiDef.Name apiDef.ParentId ]

    /// <summary>Store 내의 모든 1:1 관계 검증</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateAllOneToOne (store: DsStore) : ValidationResult =
        let flowValidations =
            store.FlowsReadOnly.Values
            |> Seq.map (fun f -> validateFlowParent f store)
            |> Seq.toList

        let workValidations =
            store.WorksReadOnly.Values
            |> Seq.map (fun w -> validateWorkParent w store)
            |> Seq.toList

        let arrowWorkValidations =
            store.ArrowWorksReadOnly.Values
            |> Seq.map (fun a -> validateArrowWorkParent a store)
            |> Seq.toList

        let apiDefValidations =
            store.ApiDefsReadOnly.Values
            |> Seq.map (fun a -> validateApiDefParent a store)
            |> Seq.toList

        combine [
            yield! flowValidations
            yield! workValidations
            yield! arrowWorkValidations
            yield! apiDefValidations
        ]

// =============================================================================
// NameUniquenessValidation - 이름 중복 검증
// =============================================================================

/// <summary>
/// 엔티티 이름 중복 검증 모듈
///
/// <para>Work와 Call 제외, 시스템이 다르면 중복 아님</para>
/// </summary>
module NameUniquenessValidation =

    open ValidationRules

    /// <summary>Project 이름 중복 검증 (전역)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateProjectNames (store: DsStore) : ValidationResult =
        let projects = store.ProjectsReadOnly.Values |> Seq.toList
        let duplicates =
            projects
            |> List.groupBy (fun p -> p.Name)
            |> List.filter (fun (_, items) -> List.length items > 1)
            |> List.map fst

        if List.isEmpty duplicates then
            Valid
        else
            Invalid (duplicates |> List.map (fun name -> sprintf "Duplicate Project name: '%s'" name))

    /// <summary>System 이름 중복 검증 (같은 Project 내)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateSystemNames (store: DsStore) : ValidationResult =
        let errors =
            store.ProjectsReadOnly.Values
            |> Seq.collect (fun project ->
                let allSystems =
                    DsQuery.projectSystemsOf project.Id store
                let duplicates =
                    allSystems
                    |> List.groupBy (fun s -> s.Name)
                    |> List.filter (fun (_, items) -> List.length items > 1)
                    |> List.map fst
                duplicates |> List.map (fun name ->
                    sprintf "Duplicate System name '%s' in Project '%s'" name project.Name))
            |> Seq.toList

        if List.isEmpty errors then Valid else Invalid errors

    /// <summary>Flow 이름 중복 검증 (같은 System 내)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateFlowNames (store: DsStore) : ValidationResult =
        let errors =
            store.SystemsReadOnly.Values
            |> Seq.collect (fun system ->
                let flows = DsQuery.flowsOf system.Id store
                let duplicates =
                    flows
                    |> List.groupBy (fun f -> f.Name)
                    |> List.filter (fun (_, items) -> List.length items > 1)
                    |> List.map fst
                duplicates |> List.map (fun name ->
                    sprintf "Duplicate Flow name '%s' in System '%s'" name system.Name))
            |> Seq.toList

        if List.isEmpty errors then Valid else Invalid errors

    /// <summary>ApiDef 이름 중복 검증 (같은 System 내)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateApiDefNames (store: DsStore) : ValidationResult =
        let errors =
            store.SystemsReadOnly.Values
            |> Seq.collect (fun system ->
                let apiDefs = DsQuery.apiDefsOf system.Id store
                let duplicates =
                    apiDefs
                    |> List.groupBy (fun a -> a.Name)
                    |> List.filter (fun (_, items) -> List.length items > 1)
                    |> List.map fst
                duplicates |> List.map (fun name ->
                    sprintf "Duplicate ApiDef name '%s' in System '%s'" name system.Name))
            |> Seq.toList

        if List.isEmpty errors then Valid else Invalid errors

    /// <summary>ApiCall 이름 중복 검증 (같은 System 내)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateApiCallNames (store: DsStore) : ValidationResult =
        let errors =
            store.SystemsReadOnly.Values
            |> Seq.collect (fun system ->
                // System의 모든 ApiCall을 가져오기 (Call을 통해)
                let flows = DsQuery.flowsOf system.Id store
                let apiCalls =
                    flows
                    |> List.collect (fun flow ->
                        let works = DsQuery.worksOf flow.Id store
                        works
                        |> List.collect (fun work ->
                            let calls = DsQuery.callsOf work.Id store
                            calls
                            |> List.collect (fun call ->
                                call.ApiCalls |> Seq.toList)))
                    |> List.distinctBy (fun ((ac: ApiCall), _) -> ac.Id)

                let duplicates =
                    apiCalls
                    |> List.groupBy (fun ((ac: ApiCall), _) -> ac.Name)
                    |> List.filter (fun (_, items) -> List.length items > 1)
                    |> List.map fst
                duplicates |> List.map (fun name ->
                    sprintf "Duplicate ApiCall name '%s' in System '%s'" name system.Name))
            |> Seq.toList

        if List.isEmpty errors then Valid else Invalid errors

    /// <summary>Button 이름 중복 검증 (같은 System 내)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateButtonNames (store: DsStore) : ValidationResult =
        let errors =
            store.SystemsReadOnly.Values
            |> Seq.collect (fun system ->
                let buttons = DsQuery.buttonsOf system.Id store
                let duplicates =
                    buttons
                    |> List.groupBy (fun b -> b.Name)
                    |> List.filter (fun (_, items) -> List.length items > 1)
                    |> List.map fst
                duplicates |> List.map (fun name ->
                    sprintf "Duplicate Button name '%s' in System '%s'" name system.Name))
            |> Seq.toList

        if List.isEmpty errors then Valid else Invalid errors

    /// <summary>Lamp 이름 중복 검증 (같은 System 내)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateLampNames (store: DsStore) : ValidationResult =
        let errors =
            store.SystemsReadOnly.Values
            |> Seq.collect (fun system ->
                let lamps = DsQuery.lampsOf system.Id store
                let duplicates =
                    lamps
                    |> List.groupBy (fun l -> l.Name)
                    |> List.filter (fun (_, items) -> List.length items > 1)
                    |> List.map fst
                duplicates |> List.map (fun name ->
                    sprintf "Duplicate Lamp name '%s' in System '%s'" name system.Name))
            |> Seq.toList

        if List.isEmpty errors then Valid else Invalid errors

    /// <summary>Condition 이름 중복 검증 (같은 System 내)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateConditionNames (store: DsStore) : ValidationResult =
        let errors =
            store.SystemsReadOnly.Values
            |> Seq.collect (fun system ->
                let conditions = DsQuery.conditionsOf system.Id store
                let duplicates =
                    conditions
                    |> List.groupBy (fun c -> c.Name)
                    |> List.filter (fun (_, items) -> List.length items > 1)
                    |> List.map fst
                duplicates |> List.map (fun name ->
                    sprintf "Duplicate Condition name '%s' in System '%s'" name system.Name))
            |> Seq.toList

        if List.isEmpty errors then Valid else Invalid errors

    /// <summary>Action 이름 중복 검증 (같은 System 내)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
    let validateActionNames (store: DsStore) : ValidationResult =
        let errors =
            store.SystemsReadOnly.Values
            |> Seq.collect (fun system ->
                let actions = DsQuery.actionsOf system.Id store
                let duplicates =
                    actions
                    |> List.groupBy (fun a -> a.Name)
                    |> List.filter (fun (_, items) -> List.length items > 1)
                    |> List.map fst
                duplicates |> List.map (fun name ->
                    sprintf "Duplicate Action name '%s' in System '%s'" name system.Name))
            |> Seq.toList

        if List.isEmpty errors then Valid else Invalid errors

    /// <summary>전체 이름 중복 검증 (Work와 Call 제외)</summary>
    /// <param name="store">도메인 스토어</param>
    /// <returns>검증 결과</returns>
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

/// <summary>
/// 검증 관련 유틸리티 모듈
/// </summary>
module ValidationHelpers =

    /// <summary>ValidationResult를 Result&lt;unit, string list&gt;로 변환</summary>
    /// <param name="validationResult">변환할 ValidationResult</param>
    /// <returns>Result 타입</returns>
    let toResult (validationResult: ValidationResult) : Result<unit, string list> =
        match validationResult with
        | Valid -> Ok ()
        | Invalid errors -> Error errors

    /// <summary>Result&lt;unit, string list&gt;를 ValidationResult로 변환</summary>
    /// <param name="result">변환할 Result</param>
    /// <returns>ValidationResult 타입</returns>
    let fromResult (result: Result<unit, string list>) : ValidationResult =
        match result with
        | Ok () -> Valid
        | Error errors -> Invalid errors

    let private combineEntityValidations (validations: ValidationResult seq) : ValidationResult =
        validations |> Seq.toList |> ValidationRules.combine

    let private validateProjects (store: DsStore) : ValidationResult =
        store.ProjectsReadOnly.Values
        |> Seq.map ProjectValidation.validate
        |> combineEntityValidations

    let private validateSystems (store: DsStore) : ValidationResult =
        store.SystemsReadOnly.Values
        |> Seq.map SystemValidation.validate
        |> combineEntityValidations

    let private validateFlows (store: DsStore) : ValidationResult =
        store.FlowsReadOnly.Values
        |> Seq.map FlowValidation.validate
        |> combineEntityValidations

    let private validateWorks (store: DsStore) : ValidationResult =
        store.WorksReadOnly.Values
        |> Seq.map WorkValidation.validate
        |> combineEntityValidations

    let private validateWorkConnections (store: DsStore) : ValidationResult =
        store.WorksReadOnly.Values
        |> Seq.map (fun work -> WorkValidation.validateConnections work store)
        |> combineEntityValidations

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

    /// <summary>편집 런타임에서 사용하는 스토어 정합성 검증 집합</summary>
    let validateStore (store: DsStore) : ValidationResult =
        ValidationRules.combine [
            validateProjects store
            validateSystems store
            validateFlows store
            validateWorks store
            validateWorkConnections store
            OneToOneValidation.validateAllOneToOne store
            validateRuntimeUniqueNames store
        ]

    /// <summary>운영 런타임보다 더 강한 정합성 점검이 필요한 경우 사용하는 집합</summary>
    let validateStoreStrict (store: DsStore) : ValidationResult =
        ValidationRules.combine [
            validateStore store
            NameUniquenessValidation.validateAll store
        ]
