/// Fail 시 seed + 요약을 JSONL 로 저장 → 회귀 재실행에 활용.
/// Note: ScenarioSpec 의 DU 직렬화는 복잡해서 description string 으로 저장.
namespace Ds2.Reverse.Bench

open System
open System.IO

[<CLIMutable>]
type FailureRecord = {
    Seed: int
    Description: string
    F1: float
    Detected: int
    Truth: int
    TimestampUtc: DateTime
}

module FailureRecorder =

    /// Append a failure record as a single TSV line.
    let record (path: string) (rec_: FailureRecord) : unit =
        let dir = Path.GetDirectoryName path
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore
        let line =
            sprintf "%s\t%d\t%.4f\t%d\t%d\t%s\n"
                (rec_.TimestampUtc.ToString("O"))
                rec_.Seed rec_.F1 rec_.Detected rec_.Truth
                rec_.Description
        File.AppendAllText(path, line)

    /// Load all records from TSV.
    let load (path: string) : FailureRecord list =
        if not (File.Exists path) then []
        else
            File.ReadAllLines path
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> Array.choose (fun line ->
                let parts = line.Split('\t')
                if parts.Length < 6 then None
                else
                    try
                        Some {
                            TimestampUtc = DateTime.Parse parts.[0]
                            Seed = Int32.Parse parts.[1]
                            F1 = Double.Parse parts.[2]
                            Detected = Int32.Parse parts.[3]
                            Truth = Int32.Parse parts.[4]
                            Description = parts.[5]
                        }
                    with _ -> None)
            |> Array.toList

    let recordMany (path: string) (records: FailureRecord seq) : unit =
        for r in records do record path r
