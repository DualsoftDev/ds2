/// Scenario 빌더 — primitives 조합으로 임의 인과 모델 합성.
namespace Ds2.Reverse.Bench

open System
open Ds2.Reverse.Core

/// 한 시나리오: ground-truth + spurious + 시뮬 패턴.
type Scenario = {
    Name: string
    /// 정답 인과 (검출되어야 함)
    GroundTruth: VLine.GroundTruthArrow list
    /// 의도된 가짜 인과 (drop 되어야 함)
    Spurious: VLine.GroundTruthArrow list
    /// 모든 call 이름 (capture 시 등장)
    AllCalls: string list
    /// 한 사이클의 events 패턴 (rng → offsets).
    /// stateful drift 시나리오는 PatternFactory 를 사용해 fresh state 보장.
    Pattern: Random -> Simulator.CyclePattern
    /// 옵션: cycle index 받는 stateful pattern (drift 등).
    /// 있으면 Pattern 대신 사용.
    PatternCycleAware: (int -> Random -> Simulator.CyclePattern) option
    /// flow 이름 (call 들이 속할 flow)
    Flow: string
    /// 시뮬 cycle 길이 (ms)
    CycleMs: int64
}

module Scenario =

    /// 정답 arrow 만으로 Candidates 생성 — declared kind 자동.
    let candidatesFor (gt: VLine.GroundTruthArrow list) (spurious: VLine.GroundTruthArrow list)
        : ArrowCandidate list =
        let toShort (s: string) =
            match s.IndexOf '.' with
            | -1 -> s
            | i -> s.Substring(i + 1)
        let toCand (a: VLine.GroundTruthArrow) =
            let kind =
                match a.Kind with
                | "Group" -> "group"
                | "Reset" -> "reset"
                | "StartReset" -> "trigger_reset"
                | "ResetReset" | "Mutex" -> "mutex"
                | _ -> "trigger"
            { Src = toShort a.Src; Tgt = toShort a.Tgt; DeclaredKind = kind }
        (gt @ spurious) |> List.map toCand

    /// FlowCalls 매핑 — 단일 flow.
    let flowCalls (flow: string) (calls: string list) : Map<string, (string * string) list> =
        Map.ofList [ flow, calls |> List.map (fun n -> n, "") ]

    /// FlowCalls 매핑 — multi-flow. Call name 의 prefix ("F1.X.Y" → flow="F1") 로 그룹핑.
    /// Phase 6 (multi-flow scenarios) 용.
    let flowCallsAuto (calls: string list) : Map<string, (string * string) list> =
        calls
        |> List.groupBy (fun n ->
            match n.IndexOf '.' with
            | -1 -> n
            | i -> n.Substring(0, i))
        |> List.map (fun (flow, fcalls) ->
            flow, fcalls |> List.map (fun n -> n, ""))
        |> Map.ofList


/// 시나리오 primitives — 한 flow 안에서 짧은 패턴 조합.
module Primitives =

    /// "${flow}.${node}" 풀네임.
    let private full (flow: string) (node: string) = $"{flow}.{node}"

    let private mkArrow (flow: string) (s: string) (t: string) (kind: string) : VLine.GroundTruthArrow =
        { Src = full flow s; Tgt = full flow t; Kind = kind }

    /// 직선 chain — n 노드, n-1 arrow.
    /// 노드 이름: "${baseName}1", "${baseName}2", ...
    /// events: 노드 i 가 (offset0 + i*lag) ms 에 발화.
    let chain (flow: string) (baseName: string) (n: int) (offset0: int64) (lag: int64)
        : VLine.GroundTruthArrow list * (int64 * string) list * string list =
        let nodes = [ for i in 1 .. n -> $"{baseName}{i}" ]
        let arrows =
            [ for i in 0 .. n - 2 -> mkArrow flow nodes.[i] nodes.[i + 1] "Start" ]
        let offsets =
            [ for i in 0 .. n - 1 -> offset0 + int64 i * lag, full flow nodes.[i] ]
        arrows, offsets, (nodes |> List.map (full flow))

    /// Fan-out — 한 source 가 여러 target 트리거. target 들 사이 group 없음 (모두 동시 발화 가능).
    let fanOut (flow: string) (src: string) (targets: string list) (srcT: int64) (lag: int64)
        : VLine.GroundTruthArrow list * (int64 * string) list * string list =
        let arrows = [ for t in targets -> mkArrow flow src t "Start" ]
        let offsets =
            (srcT, full flow src) ::
            [ for t in targets -> srcT + lag, full flow t ]
        let nodes = (src :: targets) |> List.map (full flow)
        arrows, offsets, nodes

    /// Group pair — 두 노드가 거의 동시 발화 (lag ≈ 0).
    let groupPair (flow: string) (a: string) (b: string) (t: int64)
        : VLine.GroundTruthArrow list * (int64 * string) list * string list =
        let arrows = [ mkArrow flow a b "Group" ]
        let offsets = [ t, full flow a; t, full flow b ]
        arrows, offsets, [ full flow a; full flow b ]

    /// Fan-in — 여러 source 가 한 target 으로 합쳐짐.
    let fanIn (flow: string) (sources: string list) (tgt: string) (srcsT: int64) (lag: int64)
        : VLine.GroundTruthArrow list * (int64 * string) list * string list =
        let arrows = [ for s in sources -> mkArrow flow s tgt "Start" ]
        let offsets =
            [ for s in sources -> srcsT, full flow s ] @
            [ srcsT + lag, full flow tgt ]
        let nodes = (sources @ [tgt]) |> List.map (full flow)
        arrows, offsets, nodes

    /// Spurious random call — 인과 없음.
    let spuriousPing (flow: string) (name: string) (rng: Random) (rangeMax: int64)
        : (int64 * string) =
        int64 (rng.Next(0, int rangeMax)), full flow name

    /// Confounded — 외부 timer 에 의한 sequence (큰 jitter, CV 매우 큼).
    let confounded (flow: string) (_srcName: string) (tgtName: string)
        (srcT: int64) (lagMin: int) (lagMax: int) (rng: Random)
        : (int64 * string) =
        let lag = int64 (rng.Next(lagMin, lagMax))
        srcT + lag, full flow tgtName
