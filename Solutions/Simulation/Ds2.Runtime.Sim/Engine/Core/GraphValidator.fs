namespace Ds2.Runtime.Sim.Engine.Core

open System
open Ds2.Core
open Ds2.Store

/// 시뮬레이션 그래프 사전 검증 (순수 함수)
module GraphValidator =

    let private toNameTriple (index: SimIndex) (wg: Guid) =
        match Map.tryFind wg index.WorkSystemName, Map.tryFind wg index.WorkName with
        | Some sn, Some wn -> Some (wg, sn, wn)
        | _ -> None

    /// Source에서 DFS → 각 노드의 정방향 도달 가능 노드 맵 (back edge 제외)
    let private buildForwardReachable (index: SimIndex) : Map<Guid, Set<Guid>> =
        let mutable forwardReachable = Map.empty<Guid, Set<Guid>>
        let mutable visited = Set.empty
        let mutable onStack = Set.empty
        let rec dfs node =
            if not (visited.Contains node) then
                visited <- visited.Add node
                onStack <- onStack.Add node
                let succs = index.WorkTokenSuccessors |> Map.tryFind node |> Option.defaultValue []
                for succ in succs do
                    if not (onStack.Contains succ) then
                        dfs succ
                        let succSet = forwardReachable |> Map.tryFind succ |> Option.defaultValue Set.empty
                        let current = forwardReachable |> Map.tryFind node |> Option.defaultValue Set.empty
                        forwardReachable <- forwardReachable.Add(node, Set.union current (Set.add succ succSet))
                onStack <- onStack.Remove node
        index.TokenSourceGuids |> List.iter dfs
        forwardReachable

    /// Sink이 아닌 Work 중 reset predecessor가 없는 Work 목록 반환
    let findUnresetWorks (index: SimIndex) : (Guid * string * string) list =
        index.AllWorkGuids
        |> List.filter (fun wg ->
            not (index.TokenSinkGuids.Contains(wg))
            && (SimIndex.findOrEmpty wg index.WorkResetPreds |> List.isEmpty))
        |> List.choose (toNameTriple index)

    /// 토큰 경로에서 순환 의존 데드락 후보 감지
    /// startPreds 중 정방향 descendant가 있으면 AND 조건으로 데드락 발생
    let findDeadlockCandidates (index: SimIndex) : (Guid * string * string) list =
        let forwardReachable = buildForwardReachable index
        index.TokenPathGuids
        |> Set.toList
        |> List.filter (fun wg ->
            let preds = SimIndex.findOrEmpty wg index.WorkStartPreds
            let descendants = forwardReachable |> Map.tryFind wg |> Option.defaultValue Set.empty
            preds |> List.exists (fun pred -> descendants.Contains pred))
        |> List.choose (toNameTriple index)

    /// Source 후보 감지 (두 가지 유형)
    /// 1. startPreds가 없는 Work (진입점) — Source 미지정
    /// 2. 데드락 해소용 — 순환 합류점의 정방향 descendant pred (Source로 지정하면 데드락 해소)
    let findSourceCandidates (index: SimIndex) : (Guid * string * string) list =
        let isSource wg =
            index.WorkTokenRole |> Map.tryFind wg
            |> Option.map (fun r -> r.HasFlag(Ds2.Core.TokenRole.Source)) |> Option.defaultValue false
        // Start 화살표 네트워크에 참여하는 Work (실제 Start 화살표가 연결된 Work만)
        let startNetworkGuids =
            index.WorkStartPreds
            |> Map.fold (fun acc key vals ->
                if vals.IsEmpty then acc
                else
                    let acc = Set.add key acc
                    vals |> List.fold (fun a v -> Set.add v a) acc) Set.empty
        // 1. startPreds 없는 Work (Start 화살표 네트워크 참여 Work만)
        let noPredCandidates =
            startNetworkGuids
            |> Set.toList
            |> List.filter (fun wg ->
                let preds = SimIndex.findOrEmpty wg index.WorkStartPreds
                preds.IsEmpty && not (isSource wg))
        // 2. 데드락 해소: 순환 합류점의 정방향 descendant pred
        let forwardReachable = buildForwardReachable index
        let deadlockCandidates =
            index.TokenPathGuids
            |> Set.toList
            |> List.collect (fun wg ->
                let preds = SimIndex.findOrEmpty wg index.WorkStartPreds
                let descendants = forwardReachable |> Map.tryFind wg |> Option.defaultValue Set.empty
                preds |> List.filter (fun pred -> descendants.Contains pred && not (isSource pred)))
        (noPredCandidates @ deadlockCandidates)
        |> List.distinct
        |> List.choose (toNameTriple index)

    /// Source로 지정되었지만 predecessor가 있어 자동 시작 불가한 Work
    /// (predecessor AND 조건을 충족해야 시작 가능 → 순환 시 데드락 위험)
    let findSourcesWithPredecessors (index: SimIndex) : (Guid * string * string) list =
        index.TokenSourceGuids
        |> List.filter (fun wg ->
            let preds = SimIndex.findOrEmpty wg index.WorkStartPreds
            not preds.IsEmpty)
        |> List.choose (toNameTriple index)

    /// Group Arrow로 묶인 Work 중 Ignore 지정이 누락된 그룹 감지
    /// 그룹 N개 Work → (N-1)개는 Ignore여야 함. 비Ignore가 2개 이상이면 경고
    let findGroupWorksWithoutIgnore (index: SimIndex) : (string * (Guid * string * string) list) list =
        let workGroupArrows =
            index.Store.ArrowWorksReadOnly.Values
            |> Seq.filter (fun a -> a.ArrowType = ArrowType.Group)
            |> Seq.toList
        if workGroupArrows.IsEmpty then []
        else
            let sources = workGroupArrows |> List.map (fun a -> a.SourceId)
            let targets = workGroupArrows |> List.map (fun a -> a.TargetId)
            let groupSets = SimIndexGroupExpansion.buildGroupSets sources targets
            groupSets
            |> List.choose (fun groupSet ->
                let members = groupSet |> Set.toList
                let nonIgnoreMembers =
                    members
                    |> List.filter (fun wg ->
                        let role = index.WorkTokenRole |> Map.tryFind wg |> Option.defaultValue TokenRole.None
                        not (role.HasFlag(TokenRole.Ignore)))
                if nonIgnoreMembers.Length >= 2 then
                    let items = members |> List.choose (toNameTriple index)
                    let groupLabel = items |> List.map (fun (_, _, wn) -> wn) |> String.concat ", "
                    Some (groupLabel, items)
                else None)

    /// Group Arrow로 묶인 Work 전체가 Ignore인 그룹 감지 (진행 불가)
    let findGroupWorksAllIgnored (index: SimIndex) : (string * (Guid * string * string) list) list =
        let workGroupArrows =
            index.Store.ArrowWorksReadOnly.Values
            |> Seq.filter (fun a -> a.ArrowType = ArrowType.Group)
            |> Seq.toList
        if workGroupArrows.IsEmpty then []
        else
            let sources = workGroupArrows |> List.map (fun a -> a.SourceId)
            let targets = workGroupArrows |> List.map (fun a -> a.TargetId)
            let groupSets = SimIndexGroupExpansion.buildGroupSets sources targets
            groupSets
            |> List.choose (fun groupSet ->
                let members = groupSet |> Set.toList
                let nonIgnoreMembers =
                    members
                    |> List.filter (fun wg ->
                        let role = index.WorkTokenRole |> Map.tryFind wg |> Option.defaultValue TokenRole.None
                        not (role.HasFlag(TokenRole.Ignore)))
                if nonIgnoreMembers.Length = 0 then
                    let items = members |> List.choose (toNameTriple index)
                    let groupLabel = items |> List.map (fun (_, _, wn) -> wn) |> String.concat ", "
                    Some (groupLabel, items)
                else None)

    /// 하위 호환용 별칭
    let findMissingSources = findSourceCandidates
