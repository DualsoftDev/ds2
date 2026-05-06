namespace Ds2.LlmAgent

open System.Collections.Generic
open Ds2.Core.Store

/// LLM 1 turn 동안의 mutation tool 호출을 누적하는 buffer.
///
/// 결정 7 (d): mutation tool handler 는 `ImportPlanOperation` 만 누적하고 turn 종료 시점에
/// `store.ApplyImportPlan(label, plan)` 1회 호출 → 1 undo step.
///
/// Phase 1b-c 에서는 골격만 추가, mutation tool 자체는 phase 1c 부터.
///
/// **Thread 안전성 노트**: 본 buffer 는 turn 단위 인스턴스. 여러 mutation tool handler 가
/// 같은 buffer 에 동시 Add 를 시도할 수 있으므로 dispatcher marshalling 이후에만 사용해야 함.
/// (= 결정 8 의 `IUiDispatcher.InvokeAsync` 안에서만 호출)
type ImportPlanBuilder() =
    let ops = List<ImportPlanOperation>()

    /// Operation 1개 누적.
    member _.Add(op: ImportPlanOperation) = ops.Add(op)

    /// 현재까지 누적된 operations 의 immutable snapshot.
    member _.Build() : ImportPlan =
        { Operations = ops |> Seq.toList }

    member _.Count = ops.Count
    member _.IsEmpty = ops.Count = 0

    /// Turn 폐기 (e.g. validation 실패 fail-safe).
    member _.Clear() = ops.Clear()
