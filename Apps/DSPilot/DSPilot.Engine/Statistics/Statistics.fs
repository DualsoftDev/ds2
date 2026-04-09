namespace DSPilot.Engine

open Ds2.Core.LoggingHelpers

/// 통계 계산 모듈 — Ds2.Core.LoggingHelpers 위임
module Statistics =

    let calculateMovingAverage = calculateMovingAverage
    let calculateStdDev = calculateStdDevFromSamples
    let calculateCoefficientOfVariation = calculateCoefficientOfVariation
    let updateSamples = updateSamples
    let calculateStatistics = calculateWindowStatistics
