namespace Ds2.Runtime.Report.Templates

open System
open System.Text
open System.Net
open Ds2.Runtime.Report.Model

/// HTML 템플릿 생성
module HtmlTemplate =

    /// CSS 스타일
    let private css = """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Segoe UI', Tahoma, sans-serif; background: #1e1e1e; color: #fff; padding: 20px; }
        h1 { margin-bottom: 20px; color: #4fc3f7; }
        h2 { margin-top: 30px; margin-bottom: 15px; color: #4fc3f7; }
        .info { margin-bottom: 20px; color: #aaa; font-size: 14px; }
        .info div { margin: 4px 0; }
        .gantt-container { display: flex; border: 1px solid #333; background: #252526; margin-bottom: 20px; }
        .labels { min-width: 200px; border-right: 1px solid #333; }
        .label-row { height: 28px; padding: 4px 8px; border-bottom: 1px solid #333; display: flex; align-items: center; font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .label-row.work { background: rgba(255,165,0,0.1); font-weight: bold; }
        .label-row.call { padding-left: 24px; color: #aaa; }
        .timeline { flex: 1; overflow-x: auto; position: relative; }
        .timeline-header { height: 28px; background: #333; border-bottom: 1px solid #444; position: relative; }
        .time-marker { position: absolute; top: 0; height: 100%; border-left: 1px solid #444; font-size: 10px; color: #888; padding-left: 4px; }
        .timeline-row { height: 28px; border-bottom: 1px solid #333; position: relative; }
        .segment { position: absolute; height: 18px; top: 5px; border-radius: 3px; min-width: 2px; }
        .segment.R { background: #32CD32; }
        .segment.G { background: #FFA500; }
        .segment.F { background: #1E90FF; }
        .segment.H { background: #808080; }
        .legend { margin: 20px 0; display: flex; gap: 20px; flex-wrap: wrap; }
        .legend-item { display: flex; align-items: center; gap: 8px; font-size: 12px; }
        .legend-color { width: 20px; height: 14px; border-radius: 2px; }
        table { width: 100%; border-collapse: collapse; font-size: 12px; margin-top: 15px; }
        th, td { padding: 8px; text-align: left; border: 1px solid #333; }
        th { background: #333; color: #4fc3f7; }
        tr:nth-child(even) { background: #2a2a2a; }
        tr:hover { background: #363636; }
        .state-R { color: #32CD32; }
        .state-G { color: #FFA500; }
        .state-F { color: #1E90FF; }
        .state-H { color: #808080; }
    """

    /// HTML 헤더 생성
    let private header (pageTitle: string) : string =
        "<!DOCTYPE html>\n<html lang=\"ko\">\n<head>\n    <meta charset=\"UTF-8\">\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n    <title>" + pageTitle + "</title>\n    <style>\n" + css + "\n    </style>\n</head>\n<body>\n    <h1>" + pageTitle + "</h1>\n"

    /// HTML 푸터 생성
    let private footer : string = "\n</body>\n</html>\n"

    /// 메타데이터 정보 섹션 생성
    let private infoSection (metadata: ReportMetadata) : string =
        let sb = StringBuilder()
        sb.Append("    <div class=\"info\">\n") |> ignore
        sb.Append("        <div>시작 시간: " + metadata.StartTime.ToString(ReportFormats.DateTimeFormat) + "</div>\n") |> ignore
        sb.Append("        <div>종료 시간: " + metadata.EndTime.ToString(ReportFormats.DateTimeFormat) + "</div>\n") |> ignore
        sb.Append("        <div>총 소요 시간: " + metadata.TotalDuration.ToString(@"hh\:mm\:ss\.fff") + "</div>\n") |> ignore
        sb.Append("        <div>Works: " + string metadata.WorkCount + ", Calls: " + string metadata.CallCount + "</div>\n") |> ignore
        sb.Append("        <div>생성 시간: " + metadata.GeneratedAt.ToString(ReportFormats.DateTimeFormat) + "</div>\n") |> ignore
        sb.Append("    </div>\n") |> ignore
        sb.ToString()

    /// 범례 생성
    let private legend : string = """    <div class="legend">
        <div class="legend-item"><div class="legend-color" style="background: #32CD32;"></div>Ready</div>
        <div class="legend-item"><div class="legend-color" style="background: #FFA500;"></div>Going</div>
        <div class="legend-item"><div class="legend-color" style="background: #1E90FF;"></div>Finish</div>
        <div class="legend-item"><div class="legend-color" style="background: #808080;"></div>Homing</div>
    </div>
"""

    /// 간트차트 생성
    let ganttChart (report: SimulationReport) (pixelsPerSecond: float) : string =
        let sb = StringBuilder()
        let totalDuration = max 1.0 report.Metadata.TotalDuration.TotalSeconds
        let chartWidth = totalDuration * pixelsPerSecond + 100.0

        // 컨테이너 시작
        sb.Append("    <div class=\"gantt-container\">\n") |> ignore
        sb.Append("        <div class=\"labels\">\n") |> ignore

        // 라벨 행
        for entry in report.Entries do
            let rowClass = if entry.Type = "Work" then "work" else "call"
            let displayName = ReportEntry.getDisplayName entry
            sb.Append("            <div class=\"label-row " + rowClass + "\">" + (WebUtility.HtmlEncode displayName) + "</div>\n") |> ignore

        sb.Append("        </div>\n") |> ignore
        sb.Append("        <div class=\"timeline\" style=\"width: " + string (int chartWidth) + "px;\">\n") |> ignore

        // 시간 헤더
        sb.Append("            <div class=\"timeline-header\" style=\"width: " + string (int chartWidth) + "px;\">\n") |> ignore
        let timeInterval = max 1 (int (totalDuration / 20.0))
        for sec in 0 .. timeInterval .. int totalDuration do
            let x = float sec * pixelsPerSecond
            sb.Append("                <div class=\"time-marker\" style=\"left: " + string (int x) + "px;\">" + string sec + "s</div>\n") |> ignore
        sb.Append("            </div>\n") |> ignore

        // 타임라인 행
        for entry in report.Entries do
            sb.Append("            <div class=\"timeline-row\" style=\"width: " + string (int chartWidth) + "px;\">\n") |> ignore
            for segment in entry.Segments do
                let startSec, endSec, _ = StateSegment.timeRange report.Metadata.StartTime totalDuration segment
                let x = startSec * pixelsPerSecond
                let width = max 2.0 ((endSec - startSec) * pixelsPerSecond)
                let title = segment.StateFullName + ": " + (ReportFormats.fmtFloat startSec) + "s - " + (ReportFormats.fmtFloat endSec) + "s (" + (ReportFormats.fmtFloat (endSec - startSec)) + "s)"
                sb.Append("                <div class=\"segment " + segment.State + "\" style=\"left: " + (sprintf "%.1f" x) + "px; width: " + (sprintf "%.1f" width) + "px;\" title=\"" + title + "\"></div>\n") |> ignore
            sb.Append("            </div>\n") |> ignore

        sb.Append("        </div>\n") |> ignore
        sb.Append("    </div>\n") |> ignore
        sb.ToString()

    /// 요약 테이블 생성
    let summaryTable (report: SimulationReport) : string =
        let sb = StringBuilder()
        sb.Append("    <h2>요약</h2>\n") |> ignore
        sb.Append("    <table>\n") |> ignore
        sb.Append("        <thead><tr><th>No</th><th>Type</th><th>Name</th><th>System</th><th>Going Time (sec)</th><th>State Changes</th></tr></thead>\n") |> ignore
        sb.Append("        <tbody>\n") |> ignore

        for i, entry in report.Entries |> List.indexed do
            let goingTime = ReportEntry.getTotalGoingTime entry
            let stateChanges = ReportEntry.getStateChangeCount entry
            sb.Append("            <tr><td>" + string (i + 1) + "</td><td>" + entry.Type + "</td><td>" + (WebUtility.HtmlEncode entry.Name) + "</td><td>" + (WebUtility.HtmlEncode entry.SystemId) + "</td><td>" + (ReportFormats.fmtFloat goingTime) + "</td><td>" + string stateChanges + "</td></tr>\n") |> ignore

        sb.Append("        </tbody>\n") |> ignore
        sb.Append("    </table>\n") |> ignore
        sb.ToString()

    /// 상세 테이블 생성
    let detailTable (report: SimulationReport) : string =
        let sb = StringBuilder()
        sb.Append("    <h2>상세 데이터</h2>\n") |> ignore
        sb.Append("    <table>\n") |> ignore
        sb.Append("        <thead><tr><th>No</th><th>Type</th><th>Name</th><th>System</th><th>State</th><th>Start (sec)</th><th>End (sec)</th><th>Duration (sec)</th></tr></thead>\n") |> ignore
        sb.Append("        <tbody>\n") |> ignore

        let mutable rowNo = 1
        for entry in report.Entries do
            for segment in entry.Segments do
                let startSec, endSec, duration = StateSegment.timeRange report.Metadata.StartTime report.Metadata.TotalDuration.TotalSeconds segment
                sb.Append("            <tr><td>" + string rowNo + "</td><td>" + entry.Type + "</td><td>" + (WebUtility.HtmlEncode entry.Name) + "</td><td>" + (WebUtility.HtmlEncode entry.SystemId) + "</td><td class=\"state-" + segment.State + "\">" + segment.StateFullName + "</td><td>" + (ReportFormats.fmtFloat startSec) + "</td><td>" + (ReportFormats.fmtFloat endSec) + "</td><td>" + (ReportFormats.fmtFloat duration) + "</td></tr>\n") |> ignore
                rowNo <- rowNo + 1

        sb.Append("        </tbody>\n") |> ignore
        sb.Append("    </table>\n") |> ignore
        sb.ToString()

    /// 전체 HTML 생성
    let generate (report: SimulationReport) (options: ExportOptions) : string =
        let sb = StringBuilder()
        sb.Append(header "시뮬레이션 결과") |> ignore
        sb.Append(infoSection report.Metadata) |> ignore

        if options.IncludeGanttChart then
            sb.Append(ganttChart report options.PixelsPerSecond) |> ignore
            sb.Append(legend) |> ignore

        if options.IncludeSummary then
            sb.Append(summaryTable report) |> ignore

        if options.IncludeDetails then
            sb.Append(detailTable report) |> ignore

        sb.Append(footer) |> ignore
        sb.ToString()
