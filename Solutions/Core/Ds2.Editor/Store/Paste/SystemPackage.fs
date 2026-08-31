namespace Ds2.Editor

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

// =============================================================================
// SystemPackage — System/디바이스 트리의 프로젝트 간 복사(폐포 수집 + Guid remap 병합)
//
// 설계: Docs 참조. 본질은 두 가지 —
//   ① 폐포(closure) 수집: 루트 System 하위 트리 전부 + Conditions 내부 ApiCall 재귀
//      + ApiCall.ApiDefId 가 가리키는 디바이스(Passive) System 통째 (fixpoint).
//   ② Guid 전면 remap: 같은 프로젝트 재붙여넣기/다른 프로젝트 병합이 한 코드로 동작.
// 전송층(파일 headless 로드 / 클립보드 JSON 봉투)은 소스가 "별도 DsStore 인스턴스"라는
// 계약만 공유하는 어댑터 — 본 모듈은 소스의 출처를 모른다.
//
// 이름 유일화는 코어 전담 (UI 개명 다이얼로그 없음) — pasteFlowToSystem 의
// nextUniqueName 정책과 동일. 런타임 상태(Status4)는 Ready 로 리셋.
// =============================================================================

/// 임포트 시 발생한 자동 개명 1건 — 결과 요약 표시용.
type SystemImportRename = { Kind: string; OldName: string; NewName: string }

/// 임포트 루트 지정 1건. IsActive=true 면 Project.ActiveSystemIds, false 면 PassiveSystemIds 로 등록.
type SystemImportRoot = { Id: Guid; IsActive: bool }

/// ImportSystemsFrom 결과 요약 — UI 상태줄/결과 다이얼로그용.
type SystemImportSummary = {
    /// 루트 System 들의 새 Id (요청 순서 보존) — 트리 포커스용.
    NewSystemIds: ResizeArray<Guid>
    /// 루트 System 수 (사용자가 고른 것).
    SystemCount: int
    /// 폐포로 딸려온 의존 디바이스(Passive) System 수.
    DeviceCount: int
    FlowCount: int
    WorkCount: int
    CallCount: int
    /// 이름 충돌 자동 개명 내역.
    Renames: ResizeArray<SystemImportRename>
    /// 절단/승격/스킵 경고 — 조용한 손실 방지용으로 반드시 사용자에게 표시할 것.
    Warnings: ResizeArray<string>
}

module internal SystemPackageOps =

    /// 소스 store 에서 수집한 폐포. 엔티티는 소스 인스턴스 참조 그대로 (클론은 import 단계).
    type Closure = {
        RootSystems: DsSystem list
        DeviceSystems: DsSystem list
        Flows: Flow list
        Works: Work list
        Calls: Call list
        ApiDefs: ApiDef list
        ArrowWorks: ArrowBetweenWorks list
        ArrowCalls: ArrowBetweenCalls list
    }

    /// 조건 트리를 재귀로 훑어 ApiDefId 들을 수집 (폐포에서 가장 놓치기 쉬운 경로).
    let rec private collectConditionApiDefIds (acc: HashSet<Guid>) (conditions: seq<Condition>) =
        for c in conditions do
            for ac in c.ApiCalls do
                ac.ApiDefId |> Option.iter (fun id -> acc.Add id |> ignore)
            collectConditionApiDefIds acc c.Children

    let private groupByParent (values: seq<'T> when 'T :> DsChild) =
        let map = Dictionary<Guid, ResizeArray<'T>>()
        for v in values do
            match map.TryGetValue v.ParentId with
            | true, list -> list.Add v
            | _ ->
                let list = ResizeArray<'T>()
                list.Add v
                map.[v.ParentId] <- list
        map

    let private childrenOf (map: Dictionary<Guid, ResizeArray<'T>>) (parentId: Guid) =
        match map.TryGetValue parentId with
        | true, list -> List.ofSeq list
        | _ -> []

    /// 루트 System 집합에서 시작해 디바이스 참조를 fixpoint 까지 확장하며 폐포 수집.
    let collect (source: DsStore) (rootIds: Guid list) : Closure =
        let flowsBySystem = groupByParent source.Flows.Values
        let worksByFlow   = groupByParent source.Works.Values
        let callsByWork   = groupByParent source.Calls.Values
        let apiDefsBySys  = groupByParent source.ApiDefs.Values
        // Arrow 의 ParentId 스코프: Work간 = System / Call간 = Work (Queries.arrowWorksOf/arrowCallsOf 규약)
        let arrowWBySystem = groupByParent source.ArrowWorks.Values
        let arrowCByWork   = groupByParent source.ArrowCalls.Values

        let rootSet = HashSet(rootIds)
        let visited = HashSet<Guid>()
        let queue   = Queue<Guid>(rootIds)

        let rootSystems   = ResizeArray<DsSystem>()
        let deviceSystems = ResizeArray<DsSystem>()
        let flows         = ResizeArray<Flow>()
        let works         = ResizeArray<Work>()
        let calls         = ResizeArray<Call>()
        let apiDefs       = ResizeArray<ApiDef>()
        let arrowWorks    = ResizeArray<ArrowBetweenWorks>()
        let arrowCalls    = ResizeArray<ArrowBetweenCalls>()

        while queue.Count > 0 do
            let sysId = queue.Dequeue()
            if visited.Add sysId then
                match source.Systems.TryGetValue sysId with
                | false, _ -> ()
                | true, sys ->
                    if rootSet.Contains sysId then rootSystems.Add sys else deviceSystems.Add sys
                    apiDefs.AddRange(childrenOf apiDefsBySys sysId)
                    arrowWorks.AddRange(childrenOf arrowWBySystem sysId)

                    let referencedApiDefIds = HashSet<Guid>()
                    for flow in childrenOf flowsBySystem sysId do
                        flows.Add flow
                        for work in childrenOf worksByFlow flow.Id do
                            works.Add work
                            arrowCalls.AddRange(childrenOf arrowCByWork work.Id)
                            collectConditionApiDefIds referencedApiDefIds work.Conditions
                            for call in childrenOf callsByWork work.Id do
                                calls.Add call
                                collectConditionApiDefIds referencedApiDefIds call.Conditions
                                for ac in call.ApiCalls do
                                    ac.ApiDefId |> Option.iter (fun id -> referencedApiDefIds.Add id |> ignore)

                    // 참조된 ApiDef 의 소유 System(디바이스) 을 worklist 로 — fixpoint 확장.
                    for adId in referencedApiDefIds do
                        match source.ApiDefs.TryGetValue adId with
                        | true, ad when not (visited.Contains ad.ParentId) -> queue.Enqueue ad.ParentId
                        | _ -> ()

        { RootSystems   = List.ofSeq rootSystems
          DeviceSystems = List.ofSeq deviceSystems
          Flows         = List.ofSeq flows
          Works         = List.ofSeq works
          Calls         = List.ofSeq calls
          ApiDefs       = List.ofSeq apiDefs
          ArrowWorks    = List.ofSeq arrowWorks
          ArrowCalls    = List.ofSeq arrowCalls }

    /// 조건 트리(클론) 를 제자리 갱신 — Condition/ApiCall Id 재발급 + ApiDefId·OriginFlowId remap.
    /// map 에 없는 ApiDefId 는 원본 유지 (소스 자체가 dangling 이던 경우 — 경고는 사후 스캔이 담당).
    let rec private renewConditions
        (conditions: seq<Condition>) (tryMap: Guid -> Guid option) (originFallback: Guid option) =
        for c in conditions do
            c.Id <- Guid.NewGuid()
            for ac in c.ApiCalls do
                ac.Id <- Guid.NewGuid()
                ac.ApiDefId <- ac.ApiDefId |> Option.map (fun id -> tryMap id |> Option.defaultValue id)
                ac.OriginFlowId <-
                    match ac.OriginFlowId |> Option.bind tryMap with
                    | Some mapped -> Some mapped
                    | None -> originFallback
            renewConditions c.Children tryMap originFallback

    let importInto
        (target: DsStore) (targetProjectId: Guid) (source: DsStore)
        (roots: SystemImportRoot list) : SystemImportSummary =

        match target.Projects.TryGetValue targetProjectId with
        | false, _ -> invalidOp $"Target project not found: {targetProjectId}"
        | true, project ->

        let closure = collect source (roots |> List.map (fun r -> r.Id))
        let renames  = ResizeArray<SystemImportRename>()
        let warnings = ResizeArray<string>()

        let idMap = Dictionary<Guid, Guid>()
        let tryMap (id: Guid) =
            match idMap.TryGetValue id with
            | true, v -> Some v
            | _ -> None
        let mustMap (context: string) (id: Guid) =
            match tryMap id with
            | Some v -> v
            | None -> invalidOp $"SystemPackage remap 누락: {context} → {id}"

        // ── Pass 1: 클론 + 새 Id 발급 + 이름 유일화 (프로젝트 스코프) ──────────
        let existingNames =
            let names = HashSet<string>()
            for sid in Seq.append project.ActiveSystemIds project.PassiveSystemIds do
                match target.Systems.TryGetValue sid with
                | true, s -> names.Add s.Name |> ignore
                | _ -> ()
            names

        let cloneSystem (sys: DsSystem) =
            let clone = sys.DeepCopy()
            idMap.[sys.Id] <- clone.Id
            let unique = Queries.nextUniqueName clone.Name (List.ofSeq existingNames)
            if unique <> clone.Name then
                renames.Add { Kind = "System"; OldName = clone.Name; NewName = unique }
                clone.Name <- unique
            existingNames.Add clone.Name |> ignore
            clone

        let rootClones   = closure.RootSystems   |> List.map (fun s -> s, cloneSystem s)
        let deviceClones = closure.DeviceSystems |> List.map (fun s -> s, cloneSystem s)
        let flowClones   = closure.Flows   |> List.map (fun f -> f, f.DeepCopy())
        let workClones   = closure.Works   |> List.map (fun w -> w, w.DeepCopy())
        let callClones   = closure.Calls   |> List.map (fun c -> c, c.DeepCopy())
        let apiDefClones = closure.ApiDefs |> List.map (fun d -> d, d.DeepCopy())
        for old, clone in flowClones   do idMap.[old.Id] <- clone.Id
        for old, clone in workClones   do idMap.[old.Id] <- clone.Id
        for old, clone in callClones   do idMap.[old.Id] <- clone.Id
        for old, clone in apiDefClones do idMap.[old.Id] <- clone.Id

        // ── Pass 2: 참조 fixup (idMap 완성 후) ─────────────────────────────────
        let remapReferenceOf (kind: string) (name: string) (refOpt: Guid option) =
            match refOpt with
            | None -> None
            | Some id ->
                match tryMap id with
                | Some mapped -> Some mapped
                | None ->
                    // 레퍼런스 원본이 폐포 밖 — 일반 노드로 승격 (조용한 dangling 방지).
                    warnings.Add $"{kind} '{name}': 레퍼런스 원본이 패키지 밖 — 일반 노드로 승격됨"
                    None

        for old, clone in flowClones do
            clone.ParentId <- mustMap $"Flow '{clone.Name}' ParentId" old.ParentId

        for old, clone in workClones do
            clone.ParentId <- mustMap $"Work '{clone.Name}' ParentId" old.ParentId
            clone.Status4 <- Status4.Ready
            clone.ReferenceOf <- remapReferenceOf "Work" clone.Name old.ReferenceOf
            renewConditions clone.Conditions tryMap (tryMap old.ParentId)

        let sourceWorkFlowId =
            let byId = closure.Works |> List.map (fun w -> w.Id, w.ParentId) |> dict
            fun (workId: Guid) ->
                match byId.TryGetValue workId with
                | true, flowId -> Some flowId
                | _ -> None

        for old, clone in callClones do
            clone.ParentId <- mustMap $"Call '{clone.Name}' ParentId" old.ParentId
            clone.Status4 <- Status4.Ready
            clone.ReferenceOf <- remapReferenceOf "Call" clone.Name old.ReferenceOf
            // OriginFlowId 폴백 = 이 Call 이 속한 Work 의 (새) Flow — heal 없이 정확 지정.
            let originFallback = sourceWorkFlowId old.ParentId |> Option.bind tryMap
            for ac in clone.ApiCalls do
                ac.Id <- Guid.NewGuid()
                ac.ApiDefId <- ac.ApiDefId |> Option.map (fun id -> tryMap id |> Option.defaultValue id)
                ac.OriginFlowId <-
                    match ac.OriginFlowId |> Option.bind tryMap with
                    | Some mapped -> Some mapped
                    | None -> originFallback
            renewConditions clone.Conditions tryMap originFallback

        for old, clone in apiDefClones do
            clone.ParentId <- mustMap $"ApiDef '{clone.Name}' ParentId" old.ParentId
            let remapWorkRef (label: string) (refOpt: Guid option) =
                match refOpt with
                | None -> None
                | Some id ->
                    match tryMap id with
                    | Some mapped -> Some mapped
                    | None ->
                        warnings.Add $"ApiDef '{clone.Name}': {label} 대상 Work 가 패키지 밖 — 해제됨"
                        None
            clone.TxGuid <- remapWorkRef "TxGuid" old.TxGuid
            clone.RxGuid <- remapWorkRef "RxGuid" old.RxGuid

        // Arrow: 양끝/부모가 모두 폐포 안이어야 유효 — 아니면 드롭 + 경고 (조용한 절단 금지).
        let remapArrow (kind: string) (old: #DsArrow) (clone: #DsArrow) =
            match tryMap old.ParentId, tryMap old.SourceId, tryMap old.TargetId with
            | Some p, Some s, Some t ->
                clone.ParentId <- p
                clone.SourceId <- s
                clone.TargetId <- t
                true
            | _ ->
                warnings.Add $"{kind} 화살표 1건: 끝점이 패키지 밖 — 제외됨"
                false
        let arrowWClones =
            closure.ArrowWorks |> List.choose (fun a ->
                let clone = a.DeepCopy()
                if remapArrow "Work" a clone then Some clone else None)
        let arrowCClones =
            closure.ArrowCalls |> List.choose (fun a ->
                let clone = a.DeepCopy()
                if remapArrow "Call" a clone then Some clone else None)

        // ── Pass 3: 트랜잭션 1회 = Undo 1스텝 ──────────────────────────────────
        let label = $"Import {closure.RootSystems.Length} System(s) (+{closure.DeviceSystems.Length} device)"
        target.WithTransaction(label, fun () ->
            for old, clone in rootClones do
                target.TrackAdd(target.Systems, clone)
                let isActive = roots |> List.exists (fun r -> r.Id = old.Id && r.IsActive)
                target.TrackMutate(target.Projects, targetProjectId, fun p ->
                    (if isActive then p.ActiveSystemIds else p.PassiveSystemIds).Add clone.Id)
            for _, clone in deviceClones do
                target.TrackAdd(target.Systems, clone)
                target.TrackMutate(target.Projects, targetProjectId, fun p ->
                    p.PassiveSystemIds.Add clone.Id)
            for _, clone in flowClones   do target.TrackAdd(target.Flows, clone)
            for _, clone in workClones   do target.TrackAdd(target.Works, clone)
            for _, clone in callClones do
                target.TrackAdd(target.Calls, clone)
                // 본체 ApiCall 은 store.ApiCalls 사전에도 등록 (조건 내 ApiCall 은 독립 인스턴스 — 미등록 정책).
                for ac in clone.ApiCalls do
                    target.TrackAdd(target.ApiCalls, ac)
            for _, clone in apiDefClones do target.TrackAdd(target.ApiDefs, clone)
            for clone in arrowWClones    do target.TrackAdd(target.ArrowWorks, clone)
            for clone in arrowCClones    do target.TrackAdd(target.ArrowCalls, clone))

        // ── Pass 4: dangling 불변식 검사 — 수집기가 미래 스키마의 참조 필드를 놓치면
        //    조용한 절단이 아니라 여기서 경고로 드러난다.
        let checkRef (ownerKind: string) (ownerName: string) (refKind: string) (exists: bool) =
            if not exists then
                warnings.Add $"{ownerKind} '{ownerName}': {refKind} 참조가 대상 프로젝트에 없음 (dangling)"
        for _, clone in flowClones do
            checkRef "Flow" clone.Name "System" (target.Systems.ContainsKey clone.ParentId)
        for _, clone in workClones do
            checkRef "Work" clone.Name "Flow" (target.Flows.ContainsKey clone.ParentId)
        for _, clone in callClones do
            checkRef "Call" clone.Name "Work" (target.Works.ContainsKey clone.ParentId)
            for ac in clone.ApiCalls do
                match ac.ApiDefId with
                | Some adId -> checkRef "Call" clone.Name "ApiDef" (target.ApiDefs.ContainsKey adId)
                | None -> ()
        for _, clone in apiDefClones do
            checkRef "ApiDef" clone.Name "System" (target.Systems.ContainsKey clone.ParentId)

        target.EmitRefreshAndHistory()

        // 요약 카운트는 루트 System 하위만 — 디바이스 내부 Flow/Work 는 DeviceCount 가 대변
        // (미리보기 "Flow n · Work n · Call n · 디바이스 n" 의 의미와 일치).
        let rootIdSet     = closure.RootSystems |> List.map (fun s -> s.Id) |> HashSet
        let rootFlowIds   = closure.Flows |> List.filter (fun f -> rootIdSet.Contains f.ParentId) |> List.map (fun f -> f.Id) |> HashSet
        let rootWorkIds   = closure.Works |> List.filter (fun w -> rootFlowIds.Contains w.ParentId) |> List.map (fun w -> w.Id) |> HashSet
        let rootCallCount = closure.Calls |> List.filter (fun c -> rootWorkIds.Contains c.ParentId) |> List.length

        { NewSystemIds = ResizeArray(rootClones |> List.map (fun (_, c) -> c.Id))
          SystemCount  = closure.RootSystems.Length
          DeviceCount  = closure.DeviceSystems.Length
          FlowCount    = rootFlowIds.Count
          WorkCount    = rootWorkIds.Count
          CallCount    = rootCallCount
          Renames      = renames
          Warnings     = warnings }

    /// 폐포에 해당하는 엔티티만 담은 부분(pruned) store 생성 — 클립보드/파일 export payload 용.
    /// 엔티티는 소스 인스턴스 참조 그대로 (직렬화는 읽기 전용이므로 클론 불필요).
    /// Project 는 담지 않음 — 루트/활성 정보는 봉투(envelope)가 소유한다.
    let buildPackageStore (source: DsStore) (rootIds: Guid list) : DsStore =
        let closure = collect source rootIds
        let pkg = DsStore()
        for s in closure.RootSystems @ closure.DeviceSystems do pkg.Systems.[s.Id] <- s
        for f in closure.Flows      do pkg.Flows.[f.Id] <- f
        for w in closure.Works      do pkg.Works.[w.Id] <- w
        for c in closure.Calls      do pkg.Calls.[c.Id] <- c
        for d in closure.ApiDefs    do pkg.ApiDefs.[d.Id] <- d
        for a in closure.ArrowWorks do pkg.ArrowWorks.[a.Id] <- a
        for a in closure.ArrowCalls do pkg.ArrowCalls.[a.Id] <- a
        pkg


[<Extension>]
type DsStoreSystemPackageExtensions =

    /// rootIds(시스템) 기준 폐포만 담은 부분 store 를 만든다 — JsonConverter.serialize 로
    /// 직렬화해 클립보드 봉투 payload 로 쓴다 (프로젝트 저장과 동일 직렬화기 = 신규 포맷 없음).
    [<Extension>]
    static member BuildSystemPackageStore(store: DsStore, rootIds: IEnumerable<Guid>) : DsStore =
        SystemPackageOps.buildPackageStore store (List.ofSeq rootIds)

    /// 소스 store(파일 headless 로드본 또는 클립보드 역직렬화본)에서 roots 폐포를
    /// Guid 전면 remap 으로 현재 store 의 targetProjectId 에 병합한다. Undo 1스텝.
    /// 소스와 대상이 같은 store 여도 동작 (자기 복제 — remap 이 항상 새 Id 발급).
    [<Extension>]
    static member ImportSystemsFrom
        (target: DsStore, source: DsStore, targetProjectId: Guid,
         roots: IEnumerable<SystemImportRoot>) : SystemImportSummary =
        SystemPackageOps.importInto target targetProjectId source (List.ofSeq roots)
