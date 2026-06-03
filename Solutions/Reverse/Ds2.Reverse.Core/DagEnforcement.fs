/// DAG enforcement — cycle break (Kahn) + Transitive reduction.
namespace Ds2.Reverse.Core

open System.Collections.Generic

module DagEnforcement =

    /// Sequential edge: (src, tgt, score) — Group edge 는 별도 입력.
    type Edge<'N> = 'N * 'N * CausationScore

    /// Kahn's algorithm 으로 topological sort 시도. cycle 발견 시
    /// strength 가 가장 약한 edge 1개 제거 후 재시도. 모든 cycle 해소까지.
    ///
    /// strength = sufficiency + necessity - lag_cv.
    let topoBreakCycle (edges: Edge<'N> list) (nodes: 'N seq) : Edge<'N> list * Edge<'N> list =
        let mutable edgeSet = edges
        let mutable finished = false
        let mutable accepted = []
        while not finished do
            let adj = Dictionary<'N, ResizeArray<'N>>()
            let indeg = Dictionary<'N, int>()
            for n in nodes do
                indeg.[n] <- 0
            for (s, t, _) in edgeSet do
                let lst = match adj.TryGetValue s with
                          | true, v -> v
                          | _ -> let r = ResizeArray() in adj.[s] <- r; r
                lst.Add t
                indeg.[t] <- (match indeg.TryGetValue t with true, v -> v | _ -> 0) + 1
                if not (indeg.ContainsKey s) then indeg.[s] <- 0
            let local = Dictionary(indeg)
            let q = Queue<'N>()
            for KeyValue(n, d) in local do
                if d = 0 then q.Enqueue n
            let order = ResizeArray<'N>()
            while q.Count > 0 do
                let n = q.Dequeue()
                order.Add n
                match adj.TryGetValue n with
                | true, succs ->
                    for m in succs do
                        local.[m] <- local.[m] - 1
                        if local.[m] = 0 then q.Enqueue m
                | _ -> ()
            let totalNodes = Seq.length nodes
            if order.Count = totalNodes then
                accepted <- edgeSet
                finished <- true
            else
                let unresolved =
                    local |> Seq.choose (fun kv -> if kv.Value > 0 then Some kv.Key else None)
                          |> Set.ofSeq
                let cycEdges =
                    edgeSet |> List.mapi (fun i e -> i, e)
                            |> List.filter (fun (_, (s, t, _)) ->
                                Set.contains s unresolved && Set.contains t unresolved)
                if List.isEmpty cycEdges then
                    accepted <- edgeSet
                    finished <- true
                else
                    let weakness (_, _, sco) = sco.Sufficiency + sco.Necessity - sco.LagCv
                    let _, idxOfWeakest, _ =
                        cycEdges
                        |> List.fold (fun (minW, minI, _) (i, e) ->
                            let w = weakness e
                            if i = (cycEdges |> List.head |> fst) || w < minW then (w, i, e)
                            else (minW, minI, e)) (System.Double.MaxValue, -1, Unchecked.defaultof<_>)
                    edgeSet <- edgeSet |> List.mapi (fun i e -> i, e)
                                       |> List.filter (fun (i, _) -> i <> idxOfWeakest)
                                       |> List.map snd
        let removed = edges |> List.filter (fun e -> not (List.contains e accepted))
        accepted, removed

    /// Transitive reduction: A→C 가 A→B→C 경로로 도달 가능하면 A→C 제거.
    /// group_pairs (frozenset 2 노드) 가 주어지면 group edge 도 reach 가능 단계로 간주.
    let transitiveReduction (edges: Edge<'N> list) (groupPairs: Set<Set<'N>>) : Edge<'N> list * Edge<'N> list =
        let adj = Dictionary<'N, HashSet<'N>>()
        for (s, t, _) in edges do
            let h = match adj.TryGetValue s with
                    | true, v -> v
                    | _ -> let r = HashSet() in adj.[s] <- r; r
            h.Add t |> ignore

        let reach (start: 'N) : Set<'N> =
            let seen = HashSet<'N>()
            let stk = Stack<'N>()
            stk.Push start
            while stk.Count > 0 do
                let x = stk.Pop()
                match adj.TryGetValue x with
                | true, succs ->
                    for y in succs do
                        if not (seen.Contains y) then
                            seen.Add y |> ignore
                            stk.Push y
                | _ -> ()
                // group sibling 도 step 1
                for pair in groupPairs do
                    if Set.contains x pair then
                        let sib = pair |> Set.remove x |> Set.toSeq |> Seq.head
                        if not (seen.Contains sib) then
                            seen.Add sib |> ignore
                            stk.Push sib
            seen.Remove start |> ignore
            seen |> Set.ofSeq

        let mutable kept = []
        let mutable removed = []
        for (s, t, _sco) as e in edges do
            let mutable bypass = false
            match adj.TryGetValue s with
            | true, succs ->
                for mid in succs do
                    if not bypass && mid <> t then
                        let r = reach mid
                        if Set.contains t r then bypass <- true
                        if Set.contains (set [mid; t]) groupPairs then bypass <- true
            | _ -> ()
            if bypass then removed <- e :: removed
            else kept <- e :: kept
        List.rev kept, List.rev removed
