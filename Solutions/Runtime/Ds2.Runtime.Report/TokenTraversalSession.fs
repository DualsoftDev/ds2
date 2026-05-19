namespace Ds2.Runtime.Report

open System
open System.Collections.Generic

/// <summary>
/// 혼류(Mixed-flow) 환경에서 토큰별 traversal 시간을 추적하는 상태 머신.
/// TokenEvent (Seed/Shift/Complete/Discard/BlockedOnHoming) 를 받아 분기(branch)를
/// 포함한 토큰의 모든 활성 경로를 동시 추적.
///
/// 핵심 의미:
///   - 한 토큰이 N 개 successor 로 분기되면 같은 TokenItem 으로 N 회 Shift 이벤트 emit.
///   - 이를 N 개의 ActivePath 로 표현하여 각 branch 의 시간을 독립 추적.
///   - traversal 종료 시각 = max(완주 branch 들의 CompleteAt) — critical path 시맨틱.
///
/// 시뮬 종료 후 KpiAggregator.buildPerTokenKpis 입력으로 사용.
/// </summary>
module TokenTraversalSession =

    [<NoComparison; NoEquality>]
    type private InProgress = {
        TokenItem: int
        OriginName: string
        SpecLabel: string
        SeedAt: DateTime
        ActivePaths: ResizeArray<string * DateTime>
        WorkTimes: Dictionary<string, float>
        mutable MaxCompleteAt: DateTime option
        mutable AnyDiscarded: bool
    }

    /// 하나의 시뮬레이션 세션 동안 토큰별 traversal 을 추적.
    /// 모든 메서드는 dispatcher thread 에서 호출됨을 가정 (외부 lock 불필요).
    type Session() =
        let active = Dictionary<int, InProgress>()
        let completed = ResizeArray<KpiAggregator.TokenTraversal>()

        /// ActivePaths 에서 workName 과 일치하는 첫 항목을 찾아 G+F 시간 누적 + 제거.
        /// 일치 항목 없으면 무동작 (분기 시 두 번째 이후 Shift 가 이 케이스).
        let removeOnePathAt (t: InProgress) (workName: string) (nowTs: DateTime) =
            let mutable idx = -1
            let mutable i = 0
            while idx < 0 && i < t.ActivePaths.Count do
                let name, _ = t.ActivePaths.[i]
                if String.Equals(name, workName, StringComparison.Ordinal) then idx <- i
                i <- i + 1
            if idx >= 0 then
                let name, arrivalAt = t.ActivePaths.[idx]
                let dur = (nowTs - arrivalAt).TotalSeconds
                if dur > 0.0 then
                    let prev =
                        match t.WorkTimes.TryGetValue(name) with
                        | true, v -> v
                        | _ -> 0.0
                    t.WorkTimes.[name] <- prev + dur
                t.ActivePaths.RemoveAt(idx)

        let makeTraversal (t: InProgress) (completeAt: DateTime option) : KpiAggregator.TokenTraversal =
            let workTimes =
                t.WorkTimes
                |> Seq.map (fun kv -> kv.Key, kv.Value)
                |> List.ofSeq
            {
                TokenItem = t.TokenItem
                OriginName = t.OriginName
                SpecLabel = t.SpecLabel
                SeedAt = t.SeedAt
                CompleteAt = completeAt
                WorkTimes = workTimes
            }

        let finalize (item: int) (t: InProgress) =
            active.Remove(item) |> ignore
            completed.Add(makeTraversal t t.MaxCompleteAt)

        member _.RecordSeed(tokenItem: int, originName: string, specLabel: string, seedAt: DateTime, firstWorkName: string) =
            let paths = ResizeArray<string * DateTime>()
            paths.Add(firstWorkName, seedAt)
            let t = {
                TokenItem = tokenItem
                OriginName = originName
                SpecLabel = specLabel
                SeedAt = seedAt
                ActivePaths = paths
                WorkTimes = Dictionary<string, float>()
                MaxCompleteAt = None
                AnyDiscarded = false
            }
            active.[tokenItem] <- t

        member _.RecordShift(tokenItem: int, sourceWorkName: string, targetWorkName: string, nowTs: DateTime) =
            match active.TryGetValue(tokenItem) with
            | true, t ->
                removeOnePathAt t sourceWorkName nowTs
                t.ActivePaths.Add(targetWorkName, nowTs)
            | _ -> ()

        member _.RecordComplete(tokenItem: int, workName: string, nowTs: DateTime) =
            match active.TryGetValue(tokenItem) with
            | true, t ->
                removeOnePathAt t workName nowTs
                t.MaxCompleteAt <-
                    match t.MaxCompleteAt with
                    | Some prev when prev >= nowTs -> t.MaxCompleteAt
                    | _ -> Some nowTs
                if t.ActivePaths.Count = 0 then finalize tokenItem t
            | _ -> ()

        member _.RecordDiscardOrBlocked(tokenItem: int, workName: string, nowTs: DateTime) =
            match active.TryGetValue(tokenItem) with
            | true, t ->
                removeOnePathAt t workName nowTs
                t.AnyDiscarded <- true
                if t.ActivePaths.Count = 0 then finalize tokenItem t
            | _ -> ()

        /// 시뮬 종료 sweep — 활성 traversal 들을 finalize 하여 completed 로 옮김.
        /// 완주 branch 가 하나라도 있으면 max 시각을 CompleteAt 으로, 없으면 None (미완주) 으로 기록.
        member _.FinalizePending() =
            if active.Count > 0 then
                let pending = ResizeArray(active)
                for kv in pending do
                    finalize kv.Key kv.Value

        member _.Reset() =
            active.Clear()
            completed.Clear()

        /// 진행 중 토큰도 부분집계용으로 함께 반환.
        /// 완주 branch 가 있으면 그 max 시각 사용, 없으면 CompleteAt = None.
        member _.Snapshot() : KpiAggregator.TokenTraversal seq =
            let partials =
                active.Values
                |> Seq.map (fun t -> makeTraversal t t.MaxCompleteAt)
            Seq.append (completed :> seq<_>) partials
