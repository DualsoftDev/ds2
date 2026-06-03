/// Precision / Recall / F1 평가.
namespace Ds2.Reverse.Bench

open Ds2.Core
open Ds2.Core.Store

module Evaluation =

    type Metrics = {
        Truth: int
        Detected: int
        TP: int
        FP: int
        FN: int
        Precision: float
        Recall: float
        F1: float
    }

    type Report = {
        Sequential: Metrics
        Group: Metrics
        Overall: Metrics
        SeqFP: (string * string) list
        SeqFN: (string * string) list
        GrpFP: Set<string> list
        GrpFN: Set<string> list
    }

    let private prf (tp: int) (fp: int) (fn: int) : float * float * float =
        // Vacuous truth — 정답=0, 검출=0 이면 perfect (잘못 검출도, 누락도 없음)
        if tp + fp + fn = 0 then 1.0, 1.0, 1.0
        else
            let p = if tp + fp = 0 then 1.0 else float tp / float (tp + fp)
            let r = if tp + fn = 0 then 1.0 else float tp / float (tp + fn)
            let f1 = if p + r = 0.0 then 0.0 else 2.0 * p * r / (p + r)
            p, r, f1

    let evaluate (truth: VLine.GroundTruthArrow list) (store: DsStore) : Report =
        let norm = Ds2.Reverse.Core.ModelBuilder.normalizeFullName
        let truthSeq =
            truth
            |> List.filter (fun a -> a.Kind <> "Group")
            |> List.map (fun a -> norm a.Src, norm a.Tgt)
            |> Set.ofList
        let truthGrp =
            truth
            |> List.filter (fun a -> a.Kind = "Group")
            |> List.map (fun a -> set [norm a.Src; norm a.Tgt])
            |> Set.ofList

        let callName id =
            match store.Calls.TryGetValue(id: System.Guid) with
            | true, c -> c.Name
            | _ -> "?"

        let detSeq = ResizeArray<string * string>()
        let detGrp = ResizeArray<Set<string>>()
        for KeyValue(_, ac) in store.ArrowCalls do
            let s = callName ac.SourceId
            let t = callName ac.TargetId
            if ac.ArrowType = ArrowType.Group then
                detGrp.Add(set [s; t])
            else
                detSeq.Add(s, t)
        let detSeqSet = Set.ofSeq detSeq
        let detGrpSet = Set.ofSeq detGrp

        let seqTp = Set.intersect truthSeq detSeqSet
        let seqFp = Set.difference detSeqSet truthSeq
        let seqFn = Set.difference truthSeq detSeqSet
        let p, r, f = prf (Set.count seqTp) (Set.count seqFp) (Set.count seqFn)
        let seqM = {
            Truth = Set.count truthSeq; Detected = Set.count detSeqSet
            TP = Set.count seqTp; FP = Set.count seqFp; FN = Set.count seqFn
            Precision = p; Recall = r; F1 = f
        }

        let grpTp = Set.intersect truthGrp detGrpSet
        let grpFp = Set.difference detGrpSet truthGrp
        let grpFn = Set.difference truthGrp detGrpSet
        let p2, r2, f2 = prf (Set.count grpTp) (Set.count grpFp) (Set.count grpFn)
        let grpM = {
            Truth = Set.count truthGrp; Detected = Set.count detGrpSet
            TP = Set.count grpTp; FP = Set.count grpFp; FN = Set.count grpFn
            Precision = p2; Recall = r2; F1 = f2
        }

        let totTp = seqM.TP + grpM.TP
        let totFp = seqM.FP + grpM.FP
        let totFn = seqM.FN + grpM.FN
        let p3, r3, f3 = prf totTp totFp totFn
        let overall = {
            Truth = seqM.Truth + grpM.Truth
            Detected = seqM.Detected + grpM.Detected
            TP = totTp; FP = totFp; FN = totFn
            Precision = p3; Recall = r3; F1 = f3
        }

        { Sequential = seqM; Group = grpM; Overall = overall
          SeqFP = Set.toList seqFp; SeqFN = Set.toList seqFn
          GrpFP = Set.toList grpFp; GrpFN = Set.toList grpFn }
