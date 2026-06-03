/// VLINE — 합성 ground-truth 모델.
/// 인과 검출 알고리즘의 P/R/F1 측정 용도.
namespace Ds2.Reverse.Bench

open Ds2.Reverse.Core

module VLine =

    type GroundTruthArrow = {
        Src: string
        Tgt: string
        Kind: string    // "Start" | "Group"
    }

    /// 정답 인과 — Promaker minimal representation (transitive 제외 fan-out 모두 명시).
    let groundTruth : GroundTruthArrow list = [
        { Src = "F1.PRE.START"; Tgt = "F1.PRE.DONE";  Kind = "Start" }
        { Src = "F1.PRE.DONE";  Tgt = "F1.S1.START"; Kind = "Start" }
        { Src = "F1.S1.START";  Tgt = "F1.S1.DONE";  Kind = "Start" }
        { Src = "F1.S1.DONE";   Tgt = "F1.S2A.START"; Kind = "Start" }
        { Src = "F1.S1.DONE";   Tgt = "F1.S2B.START"; Kind = "Start" }
        { Src = "F1.S2A.START"; Tgt = "F1.S2B.START"; Kind = "Group" }
        { Src = "F1.S2A.START"; Tgt = "F1.S2A.DONE";  Kind = "Start" }
        { Src = "F1.S2B.START"; Tgt = "F1.S2B.DONE";  Kind = "Start" }
        { Src = "F1.S2A.DONE";  Tgt = "F1.S3.START";  Kind = "Start" }
        { Src = "F1.S2B.DONE";  Tgt = "F1.S3.START";  Kind = "Start" }
        { Src = "F1.S3.START";  Tgt = "F1.S3.DONE";   Kind = "Start" }
    ]

    /// 의도된 spurious arrows — 알고리즘이 drop 해야 함.
    let spuriousArrows : GroundTruthArrow list = [
        { Src = "F1.S1.DONE";   Tgt = "F1.Y1.SHADOW"; Kind = "Start" }   // 큰 CV
        { Src = "F1.S1.START";  Tgt = "F1.S2A.START"; Kind = "Start" }   // transitive shortcut
        { Src = "F1.PRE.START"; Tgt = "F1.S1.START";  Kind = "Start" }   // transitive shortcut
        { Src = "F1.X1.PING";   Tgt = "F1.S1.START";  Kind = "Start" }   // 랜덤
        { Src = "F1.X1.PING";   Tgt = "F1.S3.DONE";   Kind = "Start" }   // 랜덤
    ]

    /// 모든 call 이름 (capture 시 등장).
    let allCalls = [
        "F1.PRE.START"; "F1.PRE.DONE"
        "F1.S1.START"; "F1.S1.DONE"
        "F1.S2A.START"; "F1.S2A.DONE"
        "F1.S2B.START"; "F1.S2B.DONE"
        "F1.S3.START"; "F1.S3.DONE"
        "F1.Y1.SHADOW"; "F1.X1.PING"
    ]

    /// arrows_minimal — Tier-1 후보 (정답 + 의도된 spurious).
    let candidateArrows : ArrowCandidate list =
        [ for a in (groundTruth @ spuriousArrows) ->
            let toShort (s: string) =
                match s.IndexOf '.' with
                | -1 -> s
                | i -> s.Substring(i + 1)
            let kindStr = if a.Kind = "Group" then "group" else "trigger"
            { Src = toShort a.Src; Tgt = toShort a.Tgt; DeclaredKind = kindStr } ]

    /// FlowCalls 매핑 — 한 flow F1 안에 모든 call.
    let flowCalls : Map<string, (string * string) list> =
        Map [
            "F1", allCalls |> List.map (fun n -> n, "")
        ]
