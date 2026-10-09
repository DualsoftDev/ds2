namespace Ds2.Core.Store

open System
open System.Runtime.CompilerServices
open Ds2.Core

/// Work 의 Duration / MinDuration / MaxDuration 일괄 쓰기의 정본 — 모델 계층.
///
/// Undo·편집 이벤트는 여기 없다. 편집기(Ds2.Editor)는 planChanges 로 변경 집합을 얻어 트랜잭션·변경 추적으로
/// 감싸고(UpdateWorkDurationRangesBatch), 서버(DSPilot 실측 보정·Min/Max 초기화)는 ApplyWorkDurationRanges 로
/// 그대로 쓴다. 예전에는 이 쓰기가 Ds2.Editor 의 패널 배치 코드에 있어서 서버가 편집기 전체를 참조해야 했다.
///
/// 입력 1건 = (workId, durationMs?, minDurationMs?, maxDurationMs?). null 또는 0 이하 = 미사용(None).
[<RequireQualifiedAccess>]
module WorkDurationStore =

    let private toPeriod (value: Nullable<int>) : TimeSpan option =
        if value.HasValue && value.Value > 0 then Some (TimeSpan.FromMilliseconds(float value.Value)) else None

    /// reference Work 는 원본 Work 로 귀결시키고, 같은 Work 의 중복 항목은 첫 것만 남기며,
    /// 없는 Work 와 현재 값과 똑같은 항목은 버린다. 반환 = 실제로 바뀌는 (원본 WorkId, Duration, Min, Max).
    let planChanges
        (store: DsStore)
        (changes: seq<struct (Guid * Nullable<int> * Nullable<int> * Nullable<int>)>)
        : struct (Guid * TimeSpan option * TimeSpan option * TimeSpan option) list =
        changes
        |> Seq.map (fun struct (workId, durationMs, minDurationMs, maxDurationMs) ->
            let resolvedId = Queries.resolveOriginalWorkId workId store
            struct (resolvedId, toPeriod durationMs, toPeriod minDurationMs, toPeriod maxDurationMs))
        |> Seq.distinctBy (fun struct (workId, _, _, _) -> workId)
        |> Seq.filter (fun struct (workId, duration, minDuration, maxDuration) ->
            match Queries.getWork workId store with
            | Some work ->
                work.Duration <> duration
                || work.MinDuration <> minDuration
                || work.MaxDuration <> maxDuration
            | None -> false)
        |> Seq.toList

    /// 한 Work 에 세 값을 그대로 기록한다.
    let apply (work: Work) (duration: TimeSpan option, minDuration: TimeSpan option, maxDuration: TimeSpan option) =
        work.Duration <- duration
        work.MinDuration <- minDuration
        work.MaxDuration <- maxDuration

/// C# 소비자(DSPilot·Hub)용 DsStore 확장. 편집기의 Undo 가 필요한 조작은 Ds2.Editor 의 UpdateWorkDurationRangesBatch 를 쓴다.
[<Extension>]
type DsStoreWorkDurationExtensions =

    /// 서버 측 일괄 쓰기 — Undo·편집 이벤트 없이 모델만 바꾸고, 하나라도 바뀌었으면 revision 을 올린다.
    /// 반환 = 실제로 바뀐 Work 수(없는 Work·값이 같은 항목은 세지 않는다).
    [<Extension>]
    static member ApplyWorkDurationRanges
        (store: DsStore, changes: seq<struct (Guid * Nullable<int> * Nullable<int> * Nullable<int>)>) : int =
        let plan = WorkDurationStore.planChanges store changes
        for struct (workId, duration, minDuration, maxDuration) in plan do
            WorkDurationStore.apply store.Works.[workId] (duration, minDuration, maxDuration)
        if not plan.IsEmpty then store.BumpRevision()
        plan.Length
