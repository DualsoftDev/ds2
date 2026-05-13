namespace Ds2.Core

open System

// =============================================================================
// HELPER FUNCTIONS
// =============================================================================

module LoggingHelpers =

    // -------------------------------------------------------------------------
    // Welford's Algorithm (Incremental Statistics - O(1))
    // -------------------------------------------------------------------------

    /// Welford 알고리즘으로 통계 업데이트 (O(1) 시간복잡도)
    let updateIncrementalStats
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

        let newVariance = if newCount < 2 then 0.0 else newM2 / float newCount
        let newStdDev = sqrt newVariance

        let newMin =
            match currentMin with
            | None -> Some newValue
            | Some minVal -> Some (min minVal newValue)

        let newMax =
            match currentMax with
            | None -> Some newValue
            | Some maxVal -> Some (max maxVal newValue)

        {
            Count = newCount
            Mean = newMean
            Variance = newVariance
            StdDev = newStdDev
            Min = newMin
            Max = newMax
            M2 = newM2
        }

    /// 빈 통계 생성
    let emptyStats : IncrementalStatsResult =
        {
            Count = 0
            Mean = 0.0
            Variance = 0.0
            StdDev = 0.0
            Min = None
            Max = None
            M2 = 0.0
        }

    /// 변동계수 (CV) 계산: (StdDev / Mean) * 100
    let calculateCoefficientOfVariation (mean: float) (stdDev: float) =
        if mean > 0.0 then (stdDev / mean) * 100.0 else 0.0

    // -------------------------------------------------------------------------
    // Bottleneck Detection
    // -------------------------------------------------------------------------

    /// 병목 여부 판단 (평균의 multiplier배 이상)
    let isBottleneck (duration: float) (average: float) (multiplier: float) =
        duration >= average * multiplier

    // -------------------------------------------------------------------------
    // ColorClass Helpers
    // -------------------------------------------------------------------------

    /// ColorClass → CSS 클래스명
    let colorClassToString = function
        | Excellent -> "heatmap-excellent"
        | Good -> "heatmap-good"
        | Fair -> "heatmap-fair"
        | Poor -> "heatmap-poor"
        | Critical -> "heatmap-critical"

    /// CV 값으로 ColorClass 결정
    let classifyByCV (cv: float) : ColorClass =
        if cv <= 5.0 then Excellent
        elif cv <= 10.0 then Good
        elif cv <= 20.0 then Fair
        elif cv <= 30.0 then Poor
        else Critical

    // -------------------------------------------------------------------------
    // Moving Average Statistics (Sample Window)
    // -------------------------------------------------------------------------

    /// 이동 평균 계산 (최대 100개 샘플)
    let calculateMovingAverage (samples: int list) (newValue: int) : float =
        let allSamples = newValue :: samples |> List.truncate 100
        float (List.sum allSamples) / float allSamples.Length

    /// 표준편차 계산 (샘플 목록 기반)
    let calculateStdDevFromSamples (samples: int list) (average: float) : float =
        if samples.IsEmpty then 0.0
        else
            let variance =
                samples
                |> List.map (fun x -> let diff = float x - average in diff * diff)
                |> List.average
            sqrt variance

    /// 샘플 목록 업데이트 (최대 100개 유지)
    let updateSamples (samples: int list) (newValue: int) : int list =
        newValue :: samples |> List.truncate 100

    /// 전체 통계 계산 (평균, 표준편차, CV, 갱신된 샘플)
    let calculateWindowStatistics (samples: int list) (newValue: int) : float * float * float * int list =
        let updatedSamples = updateSamples samples newValue
        let average = calculateMovingAverage samples newValue
        let stdDev = calculateStdDevFromSamples updatedSamples average
        let cv = calculateCoefficientOfVariation average stdDev
        (average, stdDev, cv, updatedSamples)

    // -------------------------------------------------------------------------
    // Performance Metric Classification
    // -------------------------------------------------------------------------

    /// 성능 메트릭 계산 (평균, StdDev → PerformanceMetrics)
    let calculatePerformanceMetrics (average: float) (stdDev: float) : PerformanceMetrics =
        let cv = calculateCoefficientOfVariation average stdDev
        { AverageTime = average; StdDev = stdDev; CoefficientOfVariation = cv }

    /// 값 정규화 (0.0 ~ 1.0)
    let normalizeValue (value: float) (minValue: float) (maxValue: float) : float =
        if maxValue > minValue then (value - minValue) / (maxValue - minValue) else 0.5

    /// HeatmapMetric + 값으로 ColorClass 결정 (메트릭별 임계값 기준)
    let determineColorClass (metric: HeatmapMetric) (value: float) : ColorClass =
        match metric with
        | AverageTime ->
            if value < 100.0 then Excellent
            elif value < 500.0 then Good
            elif value < 1000.0 then Fair
            elif value < 2000.0 then Poor
            else Critical
        | StdDeviation ->
            if value < 50.0 then Excellent
            elif value < 100.0 then Good
            elif value < 200.0 then Fair
            elif value < 400.0 then Poor
            else Critical
        | CoefficientOfVariation ->
            classifyByCV value

    // -------------------------------------------------------------------------
    // Welford Call Stats Collector (O(1) per update)
    // -------------------------------------------------------------------------

    module CallStatsCollector =

        /// 빈 상태 생성
        let empty : CallStatsState =
            { Stats = emptyStats; LastStartAt = None }

        /// Going 시작 기록
        let recordStart (timestamp: DateTime) (state: CallStatsState) : CallStatsState =
            { state with LastStartAt = Some timestamp }

        /// Going 완료 기록 및 통계 갱신
        let recordFinish (timestamp: DateTime) (state: CallStatsState) : CallStatsState * float option =
            match state.LastStartAt with
            | None -> (state, None)
            | Some startTime ->
                let durationMs = (timestamp - startTime).TotalMilliseconds
                let s = state.Stats
                let newStats = updateIncrementalStats s.Count s.Mean s.M2 s.Min s.Max durationMs
                ({ Stats = newStats; LastStartAt = None }, Some durationMs)

        /// 통계 조회
        let getStats (state: CallStatsState) : IncrementalStatsResult = state.Stats

    // -------------------------------------------------------------------------
    // Error Definition Helpers
    // -------------------------------------------------------------------------

    module ErrorDefinitionHelpers =

        let private separator = '|'

        /// ErrorValueType → 문자열 변환
        let valueTypeToString = function
            | ErrorValueType.Bit -> "Bit"
            | ErrorValueType.Byte -> "Byte"
            | ErrorValueType.Word -> "Word"
            | ErrorValueType.DWord -> "DWord"
            | ErrorValueType.Int16 -> "Int16"
            | ErrorValueType.Int32 -> "Int32"
            | ErrorValueType.Real -> "Real"
            | ErrorValueType.StringType -> "String"

        /// 문자열 → ErrorValueType 파싱
        let parseValueType (s: string) : ErrorValueType =
            match s.Trim().ToUpperInvariant() with
            | "BIT" | "BOOL" -> ErrorValueType.Bit
            | "BYTE" -> ErrorValueType.Byte
            | "WORD" | "UINT16" -> ErrorValueType.Word
            | "DWORD" | "UINT32" -> ErrorValueType.DWord
            | "INT16" | "INT" | "SHORT" -> ErrorValueType.Int16
            | "INT32" | "DINT" | "LONG" -> ErrorValueType.Int32
            | "REAL" | "FLOAT" -> ErrorValueType.Real
            | "STRING" | "STR" -> ErrorValueType.StringType
            | _ -> ErrorValueType.Bit

        /// 구조화 문자열 → ErrorDefinition 파싱
        /// 형식: "에러이름|태그주소|값타입"
        let parse (encoded: string) : ErrorDefinition option =
            if String.IsNullOrWhiteSpace(encoded) then None
            else
                let parts = encoded.Split(separator)
                if parts.Length >= 3 then
                    Some {
                        Name = parts.[0].Trim()
                        TagAddress = parts.[1].Trim()
                        ValueType = parseValueType parts.[2]
                    }
                elif parts.Length = 2 then
                    Some {
                        Name = parts.[0].Trim()
                        TagAddress = parts.[1].Trim()
                        ValueType = Bit
                    }
                else
                    None

        /// ErrorDefinition → 구조화 문자열 직렬화
        let format (def: ErrorDefinition) : string =
            sprintf "%s%c%s%c%s" def.Name separator def.TagAddress separator (valueTypeToString def.ValueType)

        /// Flow의 ErrorDefinitions 전체 파싱
        let parseAll (encodedList: ResizeArray<string>) : ErrorDefinition list =
            encodedList
            |> Seq.choose parse
            |> Seq.toList

        /// ErrorDefinition 리스트 → ResizeArray<string> 직렬화
        let formatAll (definitions: ErrorDefinition list) : ResizeArray<string> =
            definitions
            |> List.map format
            |> ResizeArray
