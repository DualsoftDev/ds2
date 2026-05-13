module KpiAggregatorTests

open System
open Xunit
open Ds2.Core
open Ds2.Runtime.Report
open Ds2.Runtime.Report.Model

let private mkSegment state startSec endSec =
    {
        State = state
        StateFullName = StateSegment.getFullName state
        StartTime = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(startSec)
        EndTime = Some(DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(endSec))
        DurationSeconds = endSec - startSec
    }

[<Fact>]
let ``buildCycleTimes should reuse finish to next going idle gap consistently`` () =
    let entry =
        {
            Id = Guid.NewGuid().ToString()
            Name = "Work-A"
            Type = "Work"
            SystemId = Guid.NewGuid().ToString()
            ParentWorkId = None
            Segments = [
                mkSegment "G" 0.0 1.0
                mkSegment "F" 1.0 2.0
                mkSegment "R" 2.0 5.0
                mkSegment "G" 5.0 6.0
                mkSegment "F" 6.0 7.0
            ]
            RowIndex = 0
        }

    let report =
        {
            Metadata = {
                StartTime = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                EndTime = DateTime(2026, 1, 1, 0, 0, 7, DateTimeKind.Utc)
                TotalDuration = TimeSpan.FromSeconds(7.0)
                WorkCount = 1
                CallCount = 0
                GeneratedAt = DateTime.UtcNow
            }
            Entries = [ entry ]
        }

    let result = KpiAggregator.buildCycleTimes (fun _ -> 0.0) 10.0 report

    Assert.Single(result) |> ignore
    Assert.Equal(3.0, result.[0].IdleGapBetweenCycles, 5)
    Assert.Equal(40.0, result.[0].EfficiencyRate, 5)
