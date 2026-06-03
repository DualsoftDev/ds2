/// PLC 래더 로직의 Boolean expression 분석.
///
/// 한 OUT 변수는 여러 LOAD/AND/OR/NOT 의 boolean 함수.
/// 예) `OUT B = LOAD A AND LOAD C` → B 는 A, C 모두 켜져야 → AND 강한 의존
///     `OUT D = LOAD A OR LOAD E` → D 는 A 또는 E → OR 약한 의존
///
/// Multi-level 재귀 추적:
///   B = A AND C, C = X AND Y  →  B = A AND X AND Y (3-input AND)
///
/// Strength 계산:
///   AND-only path → 1.0 (모든 input 필요)
///   OR branch 거치면 → 1/N (한 input 의 기여도 줄어듦)
///   재귀 expand 후 합산
namespace Ds2.Reverse.Core

/// PLC boolean expression.
type LogicExpr =
    /// 단일 변수 (LOAD A)
    | LVar of string
    /// AND 결합 — 모든 child 필요
    | LAnd of LogicExpr list
    /// OR 결합 — 어느 child 든 충분
    | LOr of LogicExpr list
    /// NOT — 강도에 영향 없음
    | LNot of LogicExpr

/// 한 PLC rung — output = expression.
type LogicRung = {
    Output: string
    Expr: LogicExpr
}

module LogicGraph =

    let rec private collectVars (expr: LogicExpr) : Set<string> =
        match expr with
        | LVar v -> Set.singleton v
        | LAnd cs | LOr cs -> cs |> List.map collectVars |> Set.unionMany
        | LNot e -> collectVars e

    /// 변수 정의를 재귀 expand. cycle 방지 + max depth 제한.
    let rec expand (rungs: Map<string, LogicExpr>) (maxDepth: int)
                  (visited: Set<string>) (expr: LogicExpr) : LogicExpr =
        if maxDepth <= 0 then expr
        else
            match expr with
            | LVar v ->
                if Set.contains v visited then LVar v
                else
                    match Map.tryFind v rungs with
                    | Some inner ->
                        expand rungs (maxDepth - 1) (Set.add v visited) inner
                    | None -> LVar v
            | LAnd cs ->
                LAnd (cs |> List.map (expand rungs (maxDepth - 1) visited))
            | LOr cs ->
                LOr (cs |> List.map (expand rungs (maxDepth - 1) visited))
            | LNot e ->
                LNot (expand rungs (maxDepth - 1) visited e)

    /// 중첩된 And/Or 평탄화. 단항 And/Or 는 단순 child 로.
    let rec simplify (expr: LogicExpr) : LogicExpr =
        match expr with
        | LAnd children ->
            let flat =
                children
                |> List.collect (fun c ->
                    match simplify c with
                    | LAnd inner -> inner
                    | other -> [other])
            match flat with
            | [single] -> single
            | _ -> LAnd flat
        | LOr children ->
            let flat =
                children
                |> List.collect (fun c ->
                    match simplify c with
                    | LOr inner -> inner
                    | other -> [other])
            match flat with
            | [single] -> single
            | _ -> LOr flat
        | LNot e -> LNot (simplify e)
        | v -> v

    /// Input 별 강도 계산. AND child 들은 currentStrength 유지, OR child 들은 1/N.
    /// 같은 변수가 여러 경로로 나타나면 최대 강도 채택.
    let rec computeStrengths (currentStrength: float) (expr: LogicExpr)
                             : Map<string, float> =
        let mergeMax (a: Map<string, float>) (b: Map<string, float>) =
            b |> Map.fold (fun acc k v ->
                let existing = Map.tryFind k acc |> Option.defaultValue 0.0
                Map.add k (max v existing) acc) a
        match expr with
        | LVar v -> Map.ofList [v, currentStrength]
        | LNot e -> computeStrengths currentStrength e
        | LAnd cs ->
            cs
            |> List.map (computeStrengths currentStrength)
            |> List.fold mergeMax Map.empty
        | LOr cs when List.isEmpty cs -> Map.empty
        | LOr cs ->
            let n = float (List.length cs)
            cs
            |> List.map (computeStrengths (currentStrength / n))
            |> List.fold mergeMax Map.empty

    /// 한 output 의 모든 input → strength.
    let inputStrengths (rungs: LogicRung list) (maxDepth: int)
                      (output: string) : Map<string, float> =
        let m = rungs |> List.map (fun r -> r.Output, r.Expr) |> Map.ofList
        match Map.tryFind output m with
        | None -> Map.empty
        | Some expr ->
            let expanded = expand m maxDepth Set.empty expr |> simplify
            computeStrengths 1.0 expanded

    /// 전체 rungs 의 (input → output) candidate arrows 추출, strength 와 함께.
    /// strengthThreshold 미만은 제외.
    let extractCandidates (rungs: LogicRung list) (maxDepth: int)
                         (strengthThreshold: float)
                         : (string * string * float) list =
        rungs
        |> List.collect (fun r ->
            let strengths = inputStrengths rungs maxDepth r.Output
            strengths
            |> Map.toList
            |> List.filter (fun (_, s) -> s >= strengthThreshold && s > 0.0)
            |> List.filter (fun (v, _) -> v <> r.Output)   // self-loop 제외
            |> List.map (fun (v, s) -> v, r.Output, s))

    /// AND vs OR 비율 — strength 1.0 (pure AND) 입력 vs OR-influenced 입력.
    let summarize (rungs: LogicRung list) (maxDepth: int) =
        let all =
            rungs
            |> List.collect (fun r ->
                inputStrengths rungs maxDepth r.Output
                |> Map.toList
                |> List.map (fun (v, s) -> r.Output, v, s))
        {| TotalEdges = List.length all
           StrongEdges = all |> List.filter (fun (_, _, s) -> s >= 0.99) |> List.length
           ModerateEdges = all |> List.filter (fun (_, _, s) -> s >= 0.3 && s < 0.99) |> List.length
           WeakEdges = all |> List.filter (fun (_, _, s) -> s < 0.3) |> List.length |}
