namespace Ds2.Runtime.Sim.Report.Model

open System

/// 상태 세그먼트 - 하나의 상태 구간
[<CLIMutable>]
type StateSegment = {
    /// RGFH 상태
    State: string
    /// 상태 전체 이름
    StateFullName: string
    /// 세그먼트 시작 시간
    StartTime: DateTime
    /// 세그먼트 종료 시간 (None = 진행 중)
    EndTime: DateTime option
    /// 지속 시간 (초)
    DurationSeconds: float
}

/// 상태 세그먼트 헬퍼
module StateSegment =
    /// 상태 코드에서 전체 이름 반환
    let getFullName state =
        match state with
        | "R" -> "Ready"
        | "G" -> "Going"
        | "F" -> "Finish"
        | "H" -> "Homing"
        | _   -> "Unknown"

    /// 상태에 따른 HTML 색상 반환
    let getColor state =
        match state with
        | "R" -> "#32CD32"  // LimeGreen
        | "G" -> "#FFA500"  // Orange
        | "F" -> "#1E90FF"  // DodgerBlue
        | "H" -> "#808080"  // Gray
        | _   -> "#808080"

    /// 세그먼트의 startSec / endSec / duration 계산 (리포트 시작 기준)
    let timeRange (reportStartTime: DateTime) (totalDurationSec: float) (segment: StateSegment) : float * float * float =
        let startSec = (segment.StartTime - reportStartTime).TotalSeconds
        let endSec =
            match segment.EndTime with
            | Some et -> (et - reportStartTime).TotalSeconds
            | None -> totalDurationSec
        (startSec, endSec, endSec - startSec)

    /// 세그먼트 생성
    let create (state: string) (startTime: DateTime) (endTime: DateTime option) =
        let duration =
            match endTime with
            | Some et -> (et - startTime).TotalSeconds
            | None -> 0.0
        {
            State = state
            StateFullName = getFullName state
            StartTime = startTime
            EndTime = endTime
            DurationSeconds = duration
        }

/// 리포트 항목 - Work 또는 Call 행
[<CLIMutable>]
type ReportEntry = {
    /// 노드 ID
    Id: string
    /// 표시 이름
    Name: string
    /// 노드 타입 ("Work" 또는 "Call")
    Type: string
    /// 시스템 ID
    SystemId: string
    /// Call인 경우 부모 Work ID
    ParentWorkId: string option
    /// 상태 세그먼트 목록
    Segments: StateSegment list
    /// 행 인덱스
    RowIndex: int
}

/// 리포트 항목 헬퍼
module ReportEntry =
    /// 표시용 이름 (Call인 경우 접두사 포함)
    let getDisplayName entry =
        if entry.Type = "Call" then sprintf "└ %s" entry.Name
        else entry.Name

    /// Going 상태 총 시간 (초)
    let getTotalGoingTime entry =
        entry.Segments
        |> List.filter (fun s -> s.State = "G")
        |> List.sumBy (fun s -> s.DurationSeconds)

    /// 상태 변경 횟수
    let getStateChangeCount entry =
        entry.Segments.Length

/// 리포트 메타데이터
[<CLIMutable>]
type ReportMetadata = {
    /// 시뮬레이션 시작 시간
    StartTime: DateTime
    /// 시뮬레이션 종료 시간
    EndTime: DateTime
    /// 총 소요 시간
    TotalDuration: TimeSpan
    /// Work 개수
    WorkCount: int
    /// Call 개수
    CallCount: int
    /// 생성 시간
    GeneratedAt: DateTime
}

/// 시뮬레이션 리포트 전체 데이터
[<CLIMutable>]
type SimulationReport = {
    /// 메타데이터
    Metadata: ReportMetadata
    /// 리포트 항목 목록
    Entries: ReportEntry list
}

/// 시뮬레이션 리포트 헬퍼
module SimulationReport =
    /// 빈 리포트 생성
    let empty () =
        let now = DateTime.Now
        {
            Metadata = {
                StartTime = now
                EndTime = now
                TotalDuration = TimeSpan.Zero
                WorkCount = 0
                CallCount = 0
                GeneratedAt = now
            }
            Entries = []
        }

    /// 총 소요 시간 (초)
    let getTotalDurationSeconds report =
        report.Metadata.TotalDuration.TotalSeconds

    /// 모든 Work 항목
    let getWorks report =
        report.Entries |> List.filter (fun e -> e.Type = "Work")

    /// 모든 Call 항목
    let getCalls report =
        report.Entries |> List.filter (fun e -> e.Type = "Call")

/// 내보내기 형식
type ExportFormat =
    | Csv
    | CsvSummary
    | Html
    | Excel

/// 내보내기 옵션
[<CLIMutable>]
type ExportOptions = {
    /// 내보내기 형식
    Format: ExportFormat
    /// 출력 파일 경로
    FilePath: string
    /// 간트차트 포함 여부 (HTML, Excel)
    IncludeGanttChart: bool
    /// 요약 데이터 포함 여부
    IncludeSummary: bool
    /// 상세 데이터 포함 여부
    IncludeDetails: bool
    /// 픽셀/초 (차트 스케일)
    PixelsPerSecond: float
}

/// 내보내기 옵션 헬퍼
module ExportOptions =
    /// 기본 옵션
    let defaults format filePath = {
        Format            = format
        FilePath          = filePath
        IncludeGanttChart = true
        IncludeSummary    = true
        IncludeDetails    = true
        PixelsPerSecond   = 10.0
    }
