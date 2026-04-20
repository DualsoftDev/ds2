namespace DSPilot.Engine.Stats

/// Thin wrapper over Ds2.Core.LoggingHelpers (Welford's Algorithm)
/// 하위 호환을 위해 DSPilot.Engine.Stats 네임스페이스 유지

type IncrementalStatsResult = Ds2.Core.IncrementalStatsResult

module IncrementalStats =

    let update = Ds2.Core.LoggingHelpers.updateIncrementalStats
    let empty = Ds2.Core.LoggingHelpers.emptyStats
