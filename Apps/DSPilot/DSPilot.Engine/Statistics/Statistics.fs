namespace DSPilot.Engine

/// 통계 계산 모듈
module Statistics =

    /// 이동 평균 계산 (최대 100개 샘플)
    let calculateMovingAverage (samples: int list) (newValue: int) : double =
        let maxSamples = 100
        let allSamples = newValue :: samples |> List.truncate maxSamples
        let sum = allSamples |> List.sum |> float
        let count = allSamples.Length |> float
        sum / count

    /// 표준편차 계산
    let calculateStdDev (samples: int list) (average: double) : double =
        if samples.IsEmpty then 0.0
        else
            let variance =
                samples
                |> List.map (fun x ->
                    let diff = float x - average
                    diff * diff)
                |> List.average
            sqrt variance

    /// 변동계수(CV) 계산
    let calculateCoefficientOfVariation (average: double) (stdDev: double) : double =
        if average = 0.0 then 0.0
        else (stdDev / average) * 100.0

    /// 샘플 목록 업데이트 (최대 100개 유지)
    let updateSamples (samples: int list) (newValue: int) : int list =
        newValue :: samples |> List.truncate 100

    /// 전체 통계 계산 (평균, 표준편차, CV를 한번에)
    let calculateStatistics (samples: int list) (newValue: int) : double * double * double * int list =
        let updatedSamples = updateSamples samples newValue
        let average = calculateMovingAverage samples newValue
        let stdDev = calculateStdDev updatedSamples average
        let cv = calculateCoefficientOfVariation average stdDev
        (average, stdDev, cv, updatedSamples)
