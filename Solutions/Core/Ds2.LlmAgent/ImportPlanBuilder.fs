namespace Ds2.LlmAgent

open System.Collections.Generic
open Ds2.Core.Store

/// LLM 1 turn 동안의 mutation tool 호출을 누적하는 buffer.
///
/// 결정 7 (d): mutation tool handler 는 `ImportPlanOperation` 만 누적하고 turn 종료 시점에
/// `store.ApplyImportPlan(label, plan)` 1회 호출 → 1 undo step.
///
/// Phase 5 cleanup 이후 mutation 진입점은 `apply_model_doc` 1종 + (그 안의 `patch:` 키) — `ModelProtocol.fs` 의
/// dispatcher 가 doc 1건당 N op 을 본 builder 에 누적한다. turn-scoped ref/cache state 없음 (doc-level 은 이름
/// 기반 forward-ref 라 self-contained).
///
/// **Thread 안전성**: 본 buffer 는 turn 단위 인스턴스. 여러 mutation tool handler 가
/// 같은 buffer 에 동시 Add 를 시도할 수 있으므로 dispatcher marshalling 이후에만 사용해야 함.
/// (= 결정 8 의 `IUiDispatcher.InvokeAsync` 안에서만 호출)
type ImportPlanBuilder() =
    let ops = List<ImportPlanOperation>()

    /// Operation 1개 누적.
    member _.Add(op: ImportPlanOperation) = ops.Add(op)

    /// 현재까지 누적된 operations 의 immutable snapshot.
    member _.Build() : ImportPlan =
        { Operations = ops |> Seq.toList }

    /// Plan 내 누적된 operation 의 read-only enumeration. tool handler 가 같은 turn 안에서
    /// 추가된 entity (e.g. add_flow 후 add_work 의 parent flow) 를 plan + store 합쳐 검색하기 위함.
    member _.Operations : seq<ImportPlanOperation> = ops :> seq<_>

    member _.Count = ops.Count
    member _.IsEmpty = ops.Count = 0

    /// Turn 폐기 (e.g. validation 실패 fail-safe).
    member _.Clear() = ops.Clear()

    /// fail-fast rollback — 진입 시점 snapshot count 까지 잘라냄.
    /// `ModelProtocol.applyPatch` / `applyDoc` 가 mid-dispatch 실패 시 호출 (self-contained 처리).
    member _.TruncateTo(targetCount: int) =
        if targetCount < 0 || targetCount > ops.Count then
            invalidArg (nameof targetCount) $"TruncateTo: 범위 초과 (target={targetCount}, current={ops.Count})."
        while ops.Count > targetCount do
            ops.RemoveAt(ops.Count - 1)
