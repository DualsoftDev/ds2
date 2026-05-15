namespace Ds2.LlmAgent.Internal

// =============================================================================
// `Ds2.LlmAgent.Internal` namespace — assembly 내부 helper 전용 (외부 reviewer #26)
//
// **책임 경계 (정책)**:
//   * 본 namespace 의 모든 module 은 `module internal` 한정 — public API 노출 *금지*.
//   * 두 개 이상의 file-scoped private helper 가 *동일 의도 / 동일 시그니처* 로 중복 정의되는
//     순간이 본 namespace 로 승격 시점. 단일 파일 helper 는 file-scoped private 유지.
//   * Helper 가 Ds2.Core / Ds2.Editor / 다른 어셈블리에서 필요해지면 본 namespace 가 아닌
//     해당 assembly 의 적절한 위치 (e.g. Ds2.Core.Store.Queries) 로 다시 이동. 본 namespace
//     는 *Ds2.LlmAgent assembly 내부 SSOT* 역할만.
//
// **위치 가이드 (후속 internal helper 추가 시)**:
//   * Plan operation lookup → `PlanLookup` (현 module).
//   * Apply 단계 leaf 키 setter / 진단 키 합성 등은 ModelProtocol.fs 안 file-scoped private 유지
//     (한 파일 안 사용) — 본 namespace 로 끌어올리지 *않음*. 후속 분산 발생 시점에 승격 검토.
//   * 신규 module 추가 시 본 namespace docstring 의 위치 가이드 row 도 함께 갱신.
// =============================================================================

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.LlmAgent

/// plan.Operations 안 ImportPlanOperation 패턴 lookup helper (SSOT).
/// Phase 7 #13 (todo §10.2) — `tryFindXxxInPlan` 5종 ToolOperations.fs + ModelProtocol.fs
/// 양쪽 file-scoped private 중복 제거. 둘 모두 본 module 참조로 일원화.
/// internal scope (assembly 내부 한정) — `Ds2.LlmAgent.Internal` namespace 분리.
module internal PlanLookup =

    let tryFindInPlan (plan: ImportPlanBuilder) (picker: ImportPlanOperation -> 'T option) : 'T option =
        plan.Operations |> Seq.tryPick picker

    let tryFindSystem (plan: ImportPlanBuilder) (id: Guid) : DsSystem option =
        tryFindInPlan plan (function AddSystem s when s.Id = id -> Some s | _ -> None)

    let tryFindFlow (plan: ImportPlanBuilder) (id: Guid) : Flow option =
        tryFindInPlan plan (function AddFlow f when f.Id = id -> Some f | _ -> None)

    let tryFindWork (plan: ImportPlanBuilder) (id: Guid) : Work option =
        tryFindInPlan plan (function AddWork w when w.Id = id -> Some w | _ -> None)

    let tryFindApiDef (plan: ImportPlanBuilder) (id: Guid) : ApiDef option =
        tryFindInPlan plan (function AddApiDef d when d.Id = id -> Some d | _ -> None)

    let tryFindCall (plan: ImportPlanBuilder) (id: Guid) : Call option =
        tryFindInPlan plan (function AddCall c when c.Id = id -> Some c | _ -> None)

    let tryFindProject (plan: ImportPlanBuilder) (id: Guid) : Project option =
        tryFindInPlan plan (function AddProject p when p.Id = id -> Some p | _ -> None)

    // ─── name-by-plan lookup (Phase 7 §10.2 #32 — modeling lookup plan/store 합집합) ─────
    //
    // dispatch helper 의 modeling level lookup-first 분기에서 사용. store 검색에서 미발견 시
    // *같은 turn 안 plan 에 add 된 entity* 도 reuse 가능하게 fallback. 현 회귀 시나리오 (collectSystems
    // wire invariant + 2-pass forward-ref) 부재 상태에서도 *방어적 SSOT* 역할 — multi-stage apply
    // 또는 cross-call ApiDef forward-ref via plan-only ApiDef 도입 시 자동 cover.
    //
    // **매칭 키 invariant**:
    //   * System: name 만 (Project 안 unique).
    //   * Flow:   parent systemId + name (System 안 unique).
    //   * Work:   parent flowId + localName (Flow 안 unique).
    //   * ApiDef: parent systemId + name (System 안 unique).
    //   * Call:   parent workId + ApiCalls[0].ApiDefId (Work 안 1:1 PoC scope — `dispatchWork` 의
    //             lookup-first 분기와 동일 invariant).

    let tryFindSystemByName (plan: ImportPlanBuilder) (name: string) : DsSystem option =
        tryFindInPlan plan (function AddSystem s when s.Name = name -> Some s | _ -> None)

    let tryFindFlowByName (plan: ImportPlanBuilder) (systemId: Guid) (name: string) : Flow option =
        tryFindInPlan plan (function
            | AddFlow f when f.ParentId = systemId && f.Name = name -> Some f
            | _ -> None)

    let tryFindWorkByLocalName (plan: ImportPlanBuilder) (flowId: Guid) (localName: string) : Work option =
        tryFindInPlan plan (function
            | AddWork w when w.ParentId = flowId && w.LocalName = localName -> Some w
            | _ -> None)

    /// **future-proof / 대칭 SSOT**: 현 phase 호출처 0건 (외부 review m-1). `dispatchPassiveSystem` 의
    /// modeling reuse 분기가 `entry.ApiDefIds` 누적 메커니즘으로 ApiDef plan-only fallback 을
    /// 이미 cover 하므로 본 helper 직접 호출 불요. 단 5개 lookup helper 의 *분류 대칭성*
    /// (System / Flow / Work / ApiDef / Call) 유지 차원에서 보존 — 후속 phase (cross-system
    /// ApiDef forward-ref via plan-only 또는 multi-stage apply 도입) 시점에 호출 발생 가능.
    let tryFindApiDefByName (plan: ImportPlanBuilder) (systemId: Guid) (name: string) : ApiDef option =
        tryFindInPlan plan (function
            | AddApiDef d when d.ParentId = systemId && d.Name = name -> Some d
            | _ -> None)

    /// `LinkSystemToProject` plan op 의 isActive flag 추적 (Active=true / Passive=false / 부재=None).
    /// 외부 review m-3 산출 — `dispatchActiveSystem` / `dispatchPassiveSystem` 두 곳에서 동일 패턴 중복.
    /// 호출처는 `Option.defaultValue false` 로 fail-safe (LinkSystemToProject 부재 = store-only 신뢰).
    /// **kind 검증 사용 패턴**: Active 검증 = `tryFindSystemLinkKind ... |> Option.defaultValue false`,
    /// Passive 검증 = `tryFindSystemLinkKind ... |> Option.map not |> Option.defaultValue false`.
    let tryFindSystemLinkKind (plan: ImportPlanBuilder) (systemId: Guid) : bool option =
        tryFindInPlan plan (function
            | LinkSystemToProject(_, sysId, isActive) when sysId = systemId -> Some isActive
            | _ -> None)

    /// Call 의 plan-by-name 은 (workId, apiDefId) 조합 — name 이 아닌 ApiDef 참조 매칭.
    /// `dispatchWork` 의 modeling lookup 와 동일 invariant (ApiCalls[0].ApiDefId = apiDefId).
    let tryFindCallByApiDef (plan: ImportPlanBuilder) (workId: Guid) (apiDefId: Guid) : Call option =
        tryFindInPlan plan (function
            | AddCall c when c.ParentId = workId
                              && c.ApiCalls.Count > 0
                              && c.ApiCalls.[0].ApiDefId = Some apiDefId -> Some c
            | _ -> None)
