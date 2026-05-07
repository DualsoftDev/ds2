namespace Ds2.Runtime.Report

open System
open Ds2.Core
open Ds2.Runtime.Report.Model

module KpiAggregatorHelpers =

    // 상태 코드 (StateSegment.State)
    let [<Literal>] StateGoing = "G"
    let [<Literal>] StateFinish = "F"
    let [<Literal>] StateHoming = "H"
    let [<Literal>] StateReady = "R"

    let secondsOf (segs: StateSegment list) (state: string) : float =
        segs |> List.filter (fun s -> s.State = state) |> List.sumBy (fun s -> s.DurationSeconds)

    let cycleDurations (entry: ReportEntry) : float list =
        entry.Segments
        |> List.filter (fun s -> s.State = StateGoing)
        |> List.map (fun s -> s.DurationSeconds)

    let finishCount (entry: ReportEntry) : int =
        entry.Segments |> List.filter (fun s -> s.State = StateFinish) |> List.length

    let idleGapBetweenCyclesOf (entry: ReportEntry) : float =
        let segs = entry.Segments |> List.toArray
        let mutable acc = 0.0
        for i in 0 .. segs.Length - 1 do
            if segs.[i].State = StateFinish then
                match segs.[i].EndTime with
                | Some fEnd ->
                    let mutable j = i + 1
                    let mutable found = false
                    while not found && j < segs.Length do
                        if segs.[j].State = StateGoing then found <- true
                        else j <- j + 1

                    if found then
                        let gap = (segs.[j].StartTime - fEnd).TotalSeconds
                        if gap > 0.0 then
                            acc <- acc + gap
                | None -> ()
        acc

    let mean xs =
        match xs with
        | [] -> 0.0
        | _ -> List.average xs

    let stdDev (xs: float list) (avg: float) =
        match xs with
        | [] | [ _ ] -> 0.0
        | _ ->
            let n = float (List.length xs)
            let variance = xs |> List.sumBy (fun x -> (x - avg) ** 2.0) |> fun s -> s / (n - 1.0)
            sqrt variance

    let tCritical95 = [|
        12.706; 4.303; 3.182; 2.776; 2.571; 2.447; 2.365; 2.306; 2.262; 2.228
        2.201; 2.179; 2.160; 2.145; 2.131; 2.120; 2.110; 2.101; 2.093; 2.086
        2.080; 2.074; 2.069; 2.064; 2.060; 2.056; 2.052; 2.048; 2.045; 2.042
    |]

    let tCriticalAt95 (df: int) : float =
        if df < 1 then 12.706
        elif df <= 30 then tCritical95.[df - 1]
        else 1.96

    let confidenceInterval95 (avg: float) (stD: float) (n: int) : float * float =
        if n < 2 then
            (avg, avg)
        else
            let df = n - 1
            let tCrit = tCriticalAt95 df
            let sem = stD / sqrt (float n)
            let margin = tCrit * sem
            (avg - margin, avg + margin)

    let skewness (xs: float list) (avg: float) (stD: float) : float =
        match xs with
        | [] | [ _ ] | [ _; _ ] -> 0.0
        | _ when stD <= 0.0 -> 0.0
        | _ ->
            let n = float (List.length xs)
            let m3 = xs |> List.sumBy (fun x -> (x - avg) ** 3.0) |> fun s -> s / n
            m3 / (stD ** 3.0)

    let excessKurtosis (xs: float list) (avg: float) (stD: float) : float =
        match xs with
        | [] | [ _ ] | [ _; _ ] | [ _; _; _ ] -> 0.0
        | _ when stD <= 0.0 -> 0.0
        | _ ->
            let n = float (List.length xs)
            let m4 = xs |> List.sumBy (fun x -> (x - avg) ** 4.0) |> fun s -> s / n
            (m4 / (stD ** 4.0)) - 3.0

    let isNormalDAgostinoK2 (xs: float list) (avg: float) (stD: float) : bool =
        let n = List.length xs
        if n < 8 then false
        elif stD <= 0.0 then false
        else
            let s = skewness xs avg stD
            let k = excessKurtosis xs avg stD
            let nf = float n
            let zs = s * sqrt (nf / 6.0)
            let zk = k * sqrt (nf / 24.0)
            let k2 = zs * zs + zk * zk
            k2 < 5.991

    let bottleneckSeverityOf (utilizationFraction: float) : BottleneckSeverity =
        if utilizationFraction >= 0.95 then CriticalBottleneck
        elif utilizationFraction >= 0.90 then MajorBottleneck
        elif utilizationFraction >= 0.80 then ModerateBottleneck
        elif utilizationFraction >= 0.70 then MinorBottleneck
        else NoBottleneck
