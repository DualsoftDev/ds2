/// 시나리오별 F1 결과를 HTML로 출력 — 알고리즘 변화 추적.
module Ds2.Reverse.Tests.ScenarioReportTests

open System.IO
open System.Text
open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Generate scenario report HTML`` () =
    let all =
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all @
        Phase4Models.all @
        Phase5Models.all
    let cfg = CausationConfig.defaults
    let results = all |> List.map (fun s -> BenchRunner.runOne s cfg 20260523 60)

    let sb = StringBuilder()
    sb.AppendLine "<!DOCTYPE html>" |> ignore
    sb.AppendLine "<html lang=\"ko\"><head><meta charset=\"UTF-8\">" |> ignore
    sb.AppendLine "<title>Ds2.Reverse 시나리오별 결과</title>" |> ignore
    sb.AppendLine """<style>
body { font-family: -apple-system, "Segoe UI", "Noto Sans KR", sans-serif;
       max-width: 1100px; margin: 20px auto; padding: 0 16px; }
h1 { color: #0d47a1; }
.summary { background: #e3f2fd; padding: 10px 14px; border-left: 4px solid #1565c0; }
table { width: 100%; border-collapse: collapse; margin: 12px 0; }
th, td { border: 1px solid #ddd; padding: 6px 10px; text-align: left; }
th { background: #f5f5f5; }
.f1-perfect { background: #c8e6c9; }
.f1-good { background: #fff9c4; }
.f1-poor { background: #ffcdd2; }
.dim-R { color: #c62828; } .dim-Q { color: #1565c0; }
.dim-D { color: #6a1b9a; } .dim-P { color: #00838f; }
.dim-V { color: #2e7d32; } .dim-G { color: #ef6c00; }
.dim-Z { color: #455a64; } .dim-K { color: #4e342e; }
.dim-S { color: #ad1457; } .dim-O { color: #1b5e20; }
.dim-T { color: #4527a0; }
</style></head><body>""" |> ignore

    sb.AppendLine "<h1>Ds2.Reverse 시나리오별 결과</h1>" |> ignore
    sb.AppendLine (sprintf "<div class=\"summary\">전체 %d 시나리오, perfect %d, 평균 F1=%.4f</div>"
                    results.Length
                    (results |> List.filter (fun r -> r.F1 >= 0.9999) |> List.length)
                    (results |> List.averageBy (fun r -> r.F1))) |> ignore

    sb.AppendLine "<table>" |> ignore
    sb.AppendLine "<thead><tr><th>시나리오</th><th>차원</th><th>Truth</th><th>Detected</th><th>TP/FP/FN</th><th>P</th><th>R</th><th>F1</th></tr></thead>" |> ignore
    sb.AppendLine "<tbody>" |> ignore

    for r in results do
        let dim = if r.Name.Length > 0 then r.Name.Substring(0, 1).ToUpper() else "?"
        let cls =
            if r.F1 >= 0.9999 then "f1-perfect"
            elif r.F1 >= 0.7 then "f1-good"
            else "f1-poor"
        sb.AppendLine (sprintf """<tr class="%s"><td>%s</td><td class="dim-%s">%s</td><td>%d</td><td>%d</td><td>%d/%d/%d</td><td>%.3f</td><td>%.3f</td><td><b>%.3f</b></td></tr>"""
                        cls r.Name dim dim r.Truth r.Detected r.TP r.FP r.FN
                        r.Precision r.Recall r.F1) |> ignore

    sb.AppendLine "</tbody></table>" |> ignore
    sb.AppendLine "</body></html>" |> ignore

    let outPath =
        Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "ScenarioReport.html")
        |> Path.GetFullPath
    File.WriteAllText(outPath, sb.ToString())
    printfn "Scenario report saved: %s" outPath
    Assert.True (File.Exists outPath)

[<Fact>]
let ``Generate scenario CSV`` () =
    let all =
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all @
        Phase4Models.all @
        Phase5Models.all
    let cfg = CausationConfig.defaults
    let csv = StringBuilder()
    csv.AppendLine "name,dim,truth,detected,tp,fp,fn,precision,recall,f1" |> ignore
    for s in all do
        let r = BenchRunner.runOne s cfg 20260523 60
        let dim = if r.Name.Length > 0 then r.Name.Substring(0, 1).ToUpper() else "?"
        csv.AppendLine (sprintf "%s,%s,%d,%d,%d,%d,%d,%.4f,%.4f,%.4f"
                          r.Name dim r.Truth r.Detected r.TP r.FP r.FN
                          r.Precision r.Recall r.F1) |> ignore
    let outPath =
        Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "ScenarioReport.csv")
        |> Path.GetFullPath
    File.WriteAllText(outPath, csv.ToString())
    printfn "CSV report saved: %s" outPath
    Assert.True (File.Exists outPath)
