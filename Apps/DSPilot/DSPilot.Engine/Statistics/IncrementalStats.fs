namespace DSPilot.Engine.Stats

/// Incremental Statistics using Welford's Method
/// https://en.wikipedia.org/wiki/Algorithms_for_calculating_variance#Welford's_online_algorithm

[<CLIMutable>]
type IncrementalStatsResult =
    { Count: int
      Mean: float
      Variance: float
      StdDev: float
      Min: float option
      Max: float option
      M2: float }

module IncrementalStats =

    /// Update statistics with new value (Welford's Method)
    /// O(1) time complexity
    let update
        (currentCount: int)
        (currentMean: float)
        (currentM2: float)
        (currentMin: float option)
        (currentMax: float option)
        (newValue: float)
        : IncrementalStatsResult =

        let newCount = currentCount + 1
        let delta = newValue - currentMean
        let newMean = currentMean + delta / float newCount
        let delta2 = newValue - newMean
        let newM2 = currentM2 + delta * delta2

        let newVariance =
            if newCount < 2 then 0.0
            else newM2 / float newCount

        let newStdDev = sqrt newVariance

        let newMin =
            match currentMin with
            | None -> Some newValue
            | Some minVal -> Some (min minVal newValue)

        let newMax =
            match currentMax with
            | None -> Some newValue
            | Some maxVal -> Some (max maxVal newValue)

        { Count = newCount
          Mean = newMean
          Variance = newVariance
          StdDev = newStdDev
          Min = newMin
          Max = newMax
          M2 = newM2 }

    /// Create empty statistics
    let empty : IncrementalStatsResult =
        { Count = 0
          Mean = 0.0
          Variance = 0.0
          StdDev = 0.0
          Min = None
          Max = None
          M2 = 0.0 }
