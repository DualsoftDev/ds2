namespace DSPilot.Engine

open System

/// Call을 고유하게 식별하기 위한 복합 키
/// DB 스키마: UNIQUE(CallName, FlowName, WorkName)와 일치
[<Struct>]
type CallKey =
    { FlowName: string
      CallName: string
      WorkName: string option }

    /// FlowName과 CallName을 사용한 생성
    static member Create(flowName: string, callName: string) =
        { FlowName = flowName
          CallName = callName
          WorkName = None }

    /// FlowName, CallName, WorkName을 모두 사용한 생성
    static member CreateFull(flowName: string, callName: string, workName: string option) =
        { FlowName = flowName
          CallName = callName
          WorkName = workName }

    /// 문자열 표현 (로깅용)
    override this.ToString() =
        match this.WorkName with
        | Some wn -> sprintf "%s/%s/%s" this.FlowName wn this.CallName
        | None -> sprintf "%s/%s" this.FlowName this.CallName

    /// 해시 키 생성 (Dictionary 키로 사용)
    member this.ToHashKey() =
        match this.WorkName with
        | Some wn -> sprintf "%s|%s|%s" this.FlowName wn this.CallName
        | None -> sprintf "%s|%s" this.FlowName this.CallName


/// Flow 엔티티 - dsp.db의 dspFlow 테이블
[<CLIMutable>]
type DspFlowEntity =
    { Id: int
      FlowName: string
      MT: int option
      WT: int option
      CT: int option
      State: string option
      MovingStartName: string option
      MovingEndName: string option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

    /// 새로운 Flow 생성 (ID는 DB에서 자동 생성)
    static member Create(flowName: string) =
        { Id = 0
          FlowName = flowName
          MT = None
          WT = None
          CT = None
          State = Some "Ready"
          MovingStartName = None
          MovingEndName = None
          CreatedAt = DateTime.UtcNow
          UpdatedAt = DateTime.UtcNow }

/// Dapper용 Flow DTO (Option 타입을 Nullable로 변환)
[<CLIMutable>]
type DapperFlowDto =
    { FlowName: string
      MT: Nullable<int>
      WT: Nullable<int>
      CT: Nullable<int>
      State: string
      MovingStartName: string
      MovingEndName: string }

    static member FromEntity(entity: DspFlowEntity) =
        { FlowName = entity.FlowName
          MT = match entity.MT with Some v -> Nullable v | None -> Nullable()
          WT = match entity.WT with Some v -> Nullable v | None -> Nullable()
          CT = match entity.CT with Some v -> Nullable v | None -> Nullable()
          State = match entity.State with Some v -> v | None -> null
          MovingStartName = match entity.MovingStartName with Some v -> v | None -> null
          MovingEndName = match entity.MovingEndName with Some v -> v | None -> null }


/// Call 엔티티 - dsp.db의 dspCall 테이블 (통계 필드 포함)
[<CLIMutable>]
type DspCallEntity =
    { Id: int
      CallName: string
      ApiCall: string
      WorkName: string
      FlowName: string
      Next: string option
      Prev: string option
      AutoPre: string option
      CommonPre: string option
      State: string
      ProgressRate: float
      PreviousGoingTime: int option
      AverageGoingTime: float option
      StdDevGoingTime: float option
      GoingCount: int
      Device: string option
      ErrorText: string option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

    /// 새로운 Call 생성 (ID는 DB에서 자동 생성)
    static member Create(callName: string, apiCall: string, workName: string, flowName: string) =
        { Id = 0
          CallName = callName
          ApiCall = apiCall
          WorkName = workName
          FlowName = flowName
          Next = None
          Prev = None
          AutoPre = None
          CommonPre = None
          State = "Ready"
          ProgressRate = 0.0
          PreviousGoingTime = None
          AverageGoingTime = None
          StdDevGoingTime = None
          GoingCount = 0
          Device = None
          ErrorText = None
          CreatedAt = DateTime.UtcNow
          UpdatedAt = DateTime.UtcNow }

/// Dapper용 Call DTO (Option 타입을 Nullable로 변환)
[<CLIMutable>]
type DapperCallDto =
    { CallName: string
      ApiCall: string
      WorkName: string
      FlowName: string
      Next: string
      Prev: string
      AutoPre: string
      CommonPre: string
      State: string
      ProgressRate: float
      PreviousGoingTime: Nullable<int>
      AverageGoingTime: Nullable<float>
      StdDevGoingTime: Nullable<float>
      GoingCount: int
      Device: string
      ErrorText: string }

    static member FromEntity(entity: DspCallEntity) =
        { CallName = entity.CallName
          ApiCall = entity.ApiCall
          WorkName = entity.WorkName
          FlowName = entity.FlowName
          Next = match entity.Next with Some v -> v | None -> null
          Prev = match entity.Prev with Some v -> v | None -> null
          AutoPre = match entity.AutoPre with Some v -> v | None -> null
          CommonPre = match entity.CommonPre with Some v -> v | None -> null
          State = entity.State
          ProgressRate = entity.ProgressRate
          PreviousGoingTime = match entity.PreviousGoingTime with Some v -> Nullable v | None -> Nullable()
          AverageGoingTime = match entity.AverageGoingTime with Some v -> Nullable v | None -> Nullable()
          StdDevGoingTime = match entity.StdDevGoingTime with Some v -> Nullable v | None -> Nullable()
          GoingCount = entity.GoingCount
          Device = match entity.Device with Some v -> v | None -> null
          ErrorText = match entity.ErrorText with Some v -> v | None -> null }

/// Dapper용 CallInfo DTO (Dapper 역직렬화를 위한 명시적 타입)
[<CLIMutable>]
type CallInfoDto =
    { WorkName: string
      FlowName: string }

/// Call 통계 DTO
[<CLIMutable>]
type CallStatisticsDto =
    { CallName: string
      FlowName: string
      WorkName: string
      AverageGoingTime: float
      StdDevGoingTime: float
      GoingCount: int }


/// Flow 상태 (UI 표시용)
[<CLIMutable>]
type FlowState =
    { Id: int
      FlowName: string
      MT: int option
      WT: int option
      State: string
      MovingStartName: string option
      MovingEndName: string option }


/// Call 상태 DTO (UI 표시용)
[<CLIMutable>]
type CallStateDto =
    { Id: int
      CallName: string
      FlowName: string
      WorkName: string
      State: string
      ProgressRate: float
      GoingCount: int
      AverageGoingTime: float option
      Device: string option
      ErrorText: string option }


/// DSP Database 스냅샷 (DspDbService에서 사용)
type DspDbSnapshot =
    { Flows: FlowState list
      Calls: CallStateDto list
      CallsByFlow: Map<string, CallStateDto list>
      Timestamp: DateTimeOffset }

    static member Empty =
        { Flows = []
          Calls = []
          CallsByFlow = Map.empty
          Timestamp = DateTimeOffset.UtcNow }


/// 데이터베이스 경로 설정 (Unified 모드 전용)
type DatabasePaths =
    { SharedDbPath: string }

    /// Flow/Call 테이블 이름 반환 (항상 dsp* 접두사 사용)
    member this.GetFlowTableName() = "dspFlow"

    member this.GetCallTableName() = "dspCall"

    member this.GetCallIOEventTableName() = "dspCallIOEvent"
