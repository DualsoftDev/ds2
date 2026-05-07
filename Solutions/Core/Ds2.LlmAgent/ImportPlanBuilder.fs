namespace Ds2.LlmAgent

open System
open System.Collections.Concurrent
open System.Collections.Generic
open Ds2.Core.Store

/// LLM 1 turn 동안의 mutation tool 호출을 누적하는 buffer.
///
/// 결정 7 (d): mutation tool handler 는 `ImportPlanOperation` 만 누적하고 turn 종료 시점에
/// `store.ApplyImportPlan(label, plan)` 1회 호출 → 1 undo step.
///
/// **Thread 안전성 노트**: 본 buffer 는 turn 단위 인스턴스. 여러 mutation tool handler 가
/// 같은 buffer 에 동시 Add 를 시도할 수 있으므로 dispatcher marshalling 이후에만 사용해야 함.
/// (= 결정 8 의 `IUiDispatcher.InvokeAsync` 안에서만 호출)
///
/// **Pass 3 (c) 추가**: `VarCache` ($<varname> → Guid) + `CascadeFailureFlag` (turn 단위 fail-fast).
/// Pass 2 spike 결과 SDK 자체 직렬 dispatch 확인되어 SemaphoreSlim gate 미도입. `ConcurrentDictionary`
/// 와 `[<VolatileField>]` 는 향후 SDK upgrade / 다른 transport 변경 대비 cheap insurance.
type ImportPlanBuilder() =
    let ops = List<ImportPlanOperation>()
    let varCache = ConcurrentDictionary<string, Guid>()
    [<VolatileField>]
    let mutable cascadeFailed = false

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

    /// Turn 폐기 (e.g. validation 실패 fail-safe). cascade flag 는 건드리지 않음.
    member _.Clear() = ops.Clear()

    /// Pass 3 (c): turn-scoped 변수 cache ($<varname> → Guid).
    /// dispatcher 안 R/W 라 race-free 지만 ConcurrentDictionary 로 방어적.
    member _.VarCache : ConcurrentDictionary<string, Guid> = varCache

    /// Pass 3 (c): turn 안 첫 mutation 실패 시 set. RunMutation 진입 시점 short-circuit 검사용
    /// (volatile — set 시점 dispatcher work thread 와 read 시점 RunMutation 진입 thread 가 다를 수 있음).
    member _.CascadeFailureFlag : bool = cascadeFailed

    /// Pass 3 (c): cascade 진입 — flag set + Plan 비우기 (fail-fast 통일).
    /// turn end 의 ApplyImportPlan 도 빈 plan 이라 호출자 측에서 skip 가능 (IsEmpty 검사).
    member _.SignalCascadeFailure() =
        cascadeFailed <- true
        ops.Clear()
