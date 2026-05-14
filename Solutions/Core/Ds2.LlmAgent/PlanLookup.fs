namespace Ds2.LlmAgent.Internal

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
