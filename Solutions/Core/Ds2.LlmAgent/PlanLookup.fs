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
