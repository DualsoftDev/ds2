namespace Ds2.Core.Kpi

open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.StandardSubmodels

/// KPI 자동생성 결과 통계.
type KpiGenerationStats = {
    /// 순회한 KPI 타깃 총 수
    Walked: int
    /// AID 에 새로 추가된 인터랙션 수
    AidAdded: int
    /// AID 에 이미 존재 (skip)
    AidExisted: int
    /// OperationalData 에 새로 추가된 아이템 수
    OpDataAdded: int
    OpDataExisted: int
    /// AIMC 에 새로 추가된 매핑 수 (aid+opdata 모두 확인된 후에만)
    AimcAdded: int
    AimcExisted: int
    /// 충돌 감지 (semanticId 일치 · idShort 불일치) — 사용자 SME 보호 위해 skip
    Conflicts: int
    /// Provenance §C — 사용자 tombstone 으로 재생성 skip 된 IdShort 개수
    Suppressed: int
} with
    static member empty = {
        Walked = 0; AidAdded = 0; AidExisted = 0
        OpDataAdded = 0; OpDataExisted = 0
        AimcAdded = 0; AimcExisted = 0
        Conflicts = 0
        Suppressed = 0
    }


/// Convention-Driven KPI 자동 생성 오케스트레이터.
///
/// **AIMC 생성 시점 규칙 (per-KPI 삼중 트랜잭션)**:
///   AID interaction 과 OperationalData item 이 둘 다 확인된 (Added 또는 Existed) KPI 에 한해서만
///   AIMC Mapping ensure. 한쪽이라도 Conflict 면 그 KPI 는 AIMC skip.
///
/// **Idempotency**: SmeDedup 3-tuple guard (SubmodelId, SemanticId, IdShort) 로 재실행 안전.
[<RequireQualifiedAccess>]
module SequenceKpiGenerator =

    let private appendTargets (project: Project) (targets: KpiTarget list) : KpiGenerationStats =
        // KPI 대상이 확인된 뒤에만 3개 서브모델을 생성한다.
        // Passive-only/빈 구형 프로젝트를 export할 때 빈 Phase 0 모델이 생기는 것을 방지한다.
        let aid =
            match project.AssetInterfaces with
            | Some a -> a
            | None ->
                let fresh = AssetInterfacesDescription()
                project.AssetInterfaces <- Some fresh
                fresh
        let od =
            match project.OperationalDataDef with
            | Some o -> o
            | None ->
                let fresh = OperationalData()
                project.OperationalDataDef <- Some fresh
                fresh
        let aimc =
            match project.AssetInterfacesMapping with
            | Some m -> m
            | None ->
                let fresh = AssetInterfacesMappingConfiguration()
                project.AssetInterfacesMapping <- Some fresh
                fresh

        // KPI 배치 처리 (인덱스 1회 구축 → O(n) — 대량 태그 스케일링).
        let mutable stats = { KpiGenerationStats.empty with Walked = targets.Length }

        let aidStates = KpiAidAppender.ensureMany aid targets
        let opStates  = KpiOperationalDataAppender.ensureMany od targets

        // AIMC 는 AID + OpData 둘 다 conflict/suppressed 아닐 때만 대상에 포함.
        let aimcTargets =
            List.zip3 targets aidStates opStates
            |> List.choose (fun (t, a, o) ->
                match a, o with
                | (Added | Existed), (Added | Existed) -> Some t
                | _ -> None)
        let aimcStates = KpiAimcAppender.ensureMany aimc aimcTargets

        let bumpAid s =
            match s with
            | Added      -> stats <- { stats with AidAdded    = stats.AidAdded + 1 }
            | Existed    -> stats <- { stats with AidExisted  = stats.AidExisted + 1 }
            | Conflict   -> stats <- { stats with Conflicts   = stats.Conflicts + 1 }
            | Suppressed -> stats <- { stats with Suppressed  = stats.Suppressed + 1 }
        let bumpOp s =
            match s with
            | Added      -> stats <- { stats with OpDataAdded   = stats.OpDataAdded + 1 }
            | Existed    -> stats <- { stats with OpDataExisted = stats.OpDataExisted + 1 }
            | Conflict   -> stats <- { stats with Conflicts     = stats.Conflicts + 1 }
            | Suppressed -> stats <- { stats with Suppressed    = stats.Suppressed + 1 }
        let bumpAimc s =
            match s with
            | Added      -> stats <- { stats with AimcAdded   = stats.AimcAdded + 1 }
            | Existed    -> stats <- { stats with AimcExisted = stats.AimcExisted + 1 }
            | Conflict   -> stats <- { stats with Conflicts   = stats.Conflicts + 1 }
            | Suppressed -> stats <- { stats with Suppressed  = stats.Suppressed + 1 }

        for s in aidStates  do bumpAid s
        for s in opStates   do bumpOp s
        for s in aimcStates do bumpAimc s

        stats

    /// 주어진 Project 의 AID/AIMC/OperationalData 를 in-place mutation 으로 append.
    /// Active System에서 KPI 대상이 발견된 경우에만 비어 있는 서브모델을 생성한다.
    /// 반환: 통계 (몇 개 add, 몇 개 existed, 몇 개 conflict).
    let appendForProject (store: DsStore) (project: Project) : KpiGenerationStats =
        let targets = KpiWalker.walk store project
        match targets with
        | [] -> KpiGenerationStats.empty
        | _ -> appendTargets project targets
