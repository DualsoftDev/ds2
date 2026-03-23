namespace DSPilot.Engine

open System

/// Dapper Flow DTO (converts Option types to Nullable)
[<CLIMutable>]
type DapperFlowDto =
    { FlowName: string
      MT: Nullable<int>
      WT: Nullable<int>
      CT: Nullable<int>
      AvgMT: Nullable<float>
      AvgWT: Nullable<float>
      AvgCT: Nullable<float>
      State: string
      MovingStartName: string
      MovingEndName: string }

    static member FromEntity(entity: DspFlowEntity) =
        { FlowName = entity.FlowName
          MT = match entity.MT with Some v -> Nullable v | None -> Nullable()
          WT = match entity.WT with Some v -> Nullable v | None -> Nullable()
          CT = match entity.CT with Some v -> Nullable v | None -> Nullable()
          AvgMT = match entity.AvgMT with Some v -> Nullable v | None -> Nullable()
          AvgWT = match entity.AvgWT with Some v -> Nullable v | None -> Nullable()
          AvgCT = match entity.AvgCT with Some v -> Nullable v | None -> Nullable()
          State = match entity.State with Some v -> v | None -> null
          MovingStartName = match entity.MovingStartName with Some v -> v | None -> null
          MovingEndName = match entity.MovingEndName with Some v -> v | None -> null }

/// Dapper FlowHistory DTO (converts Option types to Nullable)
[<CLIMutable>]
type DapperFlowHistoryDto =
    { FlowName: string
      MT: Nullable<int>
      WT: Nullable<int>
      CT: Nullable<int>
      CycleNo: Nullable<int>
      RecordedAt: DateTime }

    static member FromEntity(entity: DspFlowHistoryEntity) =
        { FlowName = entity.FlowName
          MT = match entity.MT with Some v -> Nullable v | None -> Nullable()
          WT = match entity.WT with Some v -> Nullable v | None -> Nullable()
          CT = match entity.CT with Some v -> Nullable v | None -> Nullable()
          CycleNo = match entity.CycleNo with Some v -> Nullable v | None -> Nullable()
          RecordedAt = entity.RecordedAt }

/// Dapper Call DTO (converts Option types to Nullable)
[<CLIMutable>]
type DapperCallDto =
    { CallId: Guid
      CallName: string
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
        { CallId = entity.CallId
          CallName = entity.CallName
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

/// Dapper CallInfo DTO (for Dapper deserialization)
[<CLIMutable>]
type CallInfoDto =
    { WorkName: string
      FlowName: string }

/// Call statistics DTO
[<CLIMutable>]
type CallStatisticsDto =
    { CallId: Guid
      CallName: string
      FlowName: string
      WorkName: string
      AverageGoingTime: float
      StdDevGoingTime: float
      GoingCount: int }
