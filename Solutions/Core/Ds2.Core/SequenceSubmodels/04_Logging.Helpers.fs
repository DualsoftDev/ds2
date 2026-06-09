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
    // User Tag Helpers
    // -------------------------------------------------------------------------

    module UserTagHelpers =

        let private separator = '|'

        /// PlcValueType → 문자열 변환
        let valueTypeToString = function
            | PlcValueType.Bit -> "Bit"
            | PlcValueType.Byte -> "Byte"
            | PlcValueType.Word -> "Word"
            | PlcValueType.DWord -> "DWord"
            | PlcValueType.Int16 -> "Int16"
            | PlcValueType.Int32 -> "Int32"
            | PlcValueType.Real -> "Real"
            | PlcValueType.StringType -> "String"

        /// 문자열 → PlcValueType 파싱
        let parseValueType (s: string) : PlcValueType =
            match s.Trim().ToUpperInvariant() with
            | "BIT" | "BOOL" -> PlcValueType.Bit
            | "BYTE" -> PlcValueType.Byte
            | "WORD" | "UINT16" -> PlcValueType.Word
            | "DWORD" | "UINT32" -> PlcValueType.DWord
            | "INT16" | "INT" | "SHORT" -> PlcValueType.Int16
            | "INT32" | "DINT" | "LONG" -> PlcValueType.Int32
            | "REAL" | "FLOAT" -> PlcValueType.Real
            | "STRING" | "STR" -> PlcValueType.StringType
            | _ -> PlcValueType.Bit

        /// UserTagLogLevel → 문자열 변환
        let logLevelToString = function
            | UserTagLogLevel.Info -> "Info"
            | UserTagLogLevel.Warning -> "Warning"
            | UserTagLogLevel.Error -> "Error"

        /// 문자열 → UserTagLogLevel 파싱 (미일치 시 Info)
        let parseLogLevel (s: string) : UserTagLogLevel =
            match s.Trim().ToUpperInvariant() with
            | "INFO" -> UserTagLogLevel.Info
            | "WARN" | "WARNING" -> UserTagLogLevel.Warning
            | "ERROR" | "ERR" -> UserTagLogLevel.Error
            | _ -> UserTagLogLevel.Info

        /// UserTagMatchOp → 문자열 변환
        let matchOpToString = function
            | UserTagMatchOp.Eq -> "Eq"
            | UserTagMatchOp.Neq -> "Neq"
            | UserTagMatchOp.Gt -> "Gt"
            | UserTagMatchOp.Gte -> "Gte"
            | UserTagMatchOp.Lt -> "Lt"
            | UserTagMatchOp.Lte -> "Lte"
            | UserTagMatchOp.RisingEdge -> "RisingEdge"
            | UserTagMatchOp.FallingEdge -> "FallingEdge"
            | UserTagMatchOp.Changed -> "Changed"

        /// 문자열 → UserTagMatchOp 파싱
        let parseMatchOp (s: string) : UserTagMatchOp =
            match s.Trim().ToUpperInvariant() with
            | "EQ" | "==" | "=" -> UserTagMatchOp.Eq
            | "NEQ" | "!=" | "<>" -> UserTagMatchOp.Neq
            | "GT" | ">" -> UserTagMatchOp.Gt
            | "GTE" | ">=" -> UserTagMatchOp.Gte
            | "LT" | "<" -> UserTagMatchOp.Lt
            | "LTE" | "<=" -> UserTagMatchOp.Lte
            | "RISINGEDGE" | "RISING" -> UserTagMatchOp.RisingEdge
            | "FALLINGEDGE" | "FALLING" -> UserTagMatchOp.FallingEdge
            | "CHANGED" -> UserTagMatchOp.Changed
            | _ -> UserTagMatchOp.RisingEdge

        /// ValueType 에 맞는 기본 MatchOp 결정 — 레거시 4필드 정의 호환용.
        let defaultMatchOpFor (vt: PlcValueType) : UserTagMatchOp =
            match vt with
            | PlcValueType.Bit -> UserTagMatchOp.RisingEdge
            | _ -> UserTagMatchOp.Changed

        /// 구조화 문자열 → UserTag 파싱
        /// 형식 (v2): "이름|로그레벨|태그주소|값타입|매칭조건|기준값"
        /// 형식 (v1, 하위호환): "이름|로그레벨|태그주소|값타입"
        ///   → MatchOp 는 ValueType 에 따라 RisingEdge / Changed 로 자동 설정.
        let parse (encoded: string) : UserTag option =
            if String.IsNullOrWhiteSpace(encoded) then None
            else
                let parts = encoded.Split(separator)
                if parts.Length >= 4 then
                    let vt = parseValueType parts.[3]
                    let op =
                        if parts.Length >= 5 && not (String.IsNullOrWhiteSpace(parts.[4]))
                        then parseMatchOp parts.[4]
                        else defaultMatchOpFor vt
                    let mv =
                        if parts.Length >= 6 then parts.[5].Trim() else ""
                    Some {
                        Name = parts.[0].Trim()
                        LogLevel = parseLogLevel parts.[1]
                        TagAddress = parts.[2].Trim()
                        ValueType = vt
                        MatchOp = op
                        MatchValue = mv
                    }
                else
                    None

        /// UserTag → 구조화 문자열 직렬화 (항상 v2 6필드로 기록)
        let format (tag: UserTag) : string =
            sprintf "%s%c%s%c%s%c%s%c%s%c%s"
                tag.Name separator
                (logLevelToString tag.LogLevel) separator
                tag.TagAddress separator
                (valueTypeToString tag.ValueType) separator
                (matchOpToString tag.MatchOp) separator
                tag.MatchValue

        /// System의 UserTags 전체 파싱
        let parseAll (encodedList: ResizeArray<string>) : UserTag list =
            encodedList
            |> Seq.choose parse
            |> Seq.toList

        /// UserTag 리스트 → ResizeArray<string> 직렬화
        let formatAll (tags: UserTag list) : ResizeArray<string> =
            tags
            |> List.map format
            |> ResizeArray

        // -------------------------------------------------------------------------
        // 매칭 평가
        // -------------------------------------------------------------------------

        /// "0/1/true/false" 등을 0|1 로 정규화 (Bit 비교용)
        let private normalizeBool (v: string) : string =
            if isNull v then "0"
            else
                let l = v.Trim().ToLowerInvariant()
                if l = "1" || l = "true" || l = "on" then "1" else "0"

        /// 문자열을 double 로 안전하게 변환 (CultureInfo.Invariant)
        let private tryParseNumber (v: string) : float option =
            if String.IsNullOrWhiteSpace(v) then None
            else
                match Double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture) with
                | true, x -> Some x
                | _ -> None

        let private nz (s: string) = if isNull s then "" else s

        /// 두 문자열이 같은 값을 의미하는지 (Bit / 수치 / 문자열 모두 지원)
        let private valueEquals (vt: PlcValueType) (a: string) (b: string) : bool =
            match vt with
            | PlcValueType.Bit -> normalizeBool a = normalizeBool b
            | PlcValueType.StringType -> nz a = nz b
            | _ ->
                match tryParseNumber a, tryParseNumber b with
                | Some x, Some y -> x = y
                | _ -> (nz a).Trim() = (nz b).Trim()

        /// (prevValue, newValue) 와 (op, matchValue) 로 알림 발화 여부 판정.
        /// prevValue 가 없으면(첫 샘플) edge/Changed 류는 발화하지 않음 — Eq/Gte 등은 새 값이 조건 만족하면 1건.
        let shouldFire (vt: PlcValueType) (op: UserTagMatchOp) (matchValue: string)
                      (prevValue: string option) (newValue: string) : bool =
            let cmp () =
                // 수치 비교 — 양쪽 다 수치로 파싱 가능해야 함.
                tryParseNumber newValue, tryParseNumber matchValue
            match op with
            | UserTagMatchOp.RisingEdge ->
                let cur = normalizeBool newValue
                let prev = prevValue |> Option.map normalizeBool |> Option.defaultValue "0"
                prev = "0" && cur = "1"
            | UserTagMatchOp.FallingEdge ->
                let cur = normalizeBool newValue
                let prev = prevValue |> Option.map normalizeBool |> Option.defaultValue "1"
                prev = "1" && cur = "0"
            | UserTagMatchOp.Changed ->
                match prevValue with
                | None -> false                       // 첫 샘플은 변경으로 보지 않음
                | Some p -> not (valueEquals vt p newValue)
            | UserTagMatchOp.Eq ->
                let nowMatches = valueEquals vt newValue matchValue
                let prevMatches = prevValue |> Option.map (fun p -> valueEquals vt p matchValue) |> Option.defaultValue false
                nowMatches && not prevMatches         // "같아질 때" — 전이 1건
            | UserTagMatchOp.Neq ->
                let nowMatches = not (valueEquals vt newValue matchValue)
                let prevMatches = prevValue |> Option.map (fun p -> not (valueEquals vt p matchValue)) |> Option.defaultValue false
                nowMatches && not prevMatches
            | UserTagMatchOp.Gt ->
                match cmp () with
                | Some n, Some t ->
                    let prevSatisfies =
                        prevValue
                        |> Option.bind tryParseNumber
                        |> Option.map (fun p -> p > t)
                        |> Option.defaultValue false
                    n > t && not prevSatisfies
                | _ -> false
            | UserTagMatchOp.Gte ->
                match cmp () with
                | Some n, Some t ->
                    let prevSatisfies =
                        prevValue
                        |> Option.bind tryParseNumber
                        |> Option.map (fun p -> p >= t)
                        |> Option.defaultValue false
                    n >= t && not prevSatisfies
                | _ -> false
            | UserTagMatchOp.Lt ->
                match cmp () with
                | Some n, Some t ->
                    let prevSatisfies =
                        prevValue
                        |> Option.bind tryParseNumber
                        |> Option.map (fun p -> p < t)
                        |> Option.defaultValue false
                    n < t && not prevSatisfies
                | _ -> false
            | UserTagMatchOp.Lte ->
                match cmp () with
                | Some n, Some t ->
                    let prevSatisfies =
                        prevValue
                        |> Option.bind tryParseNumber
                        |> Option.map (fun p -> p <= t)
                        |> Option.defaultValue false
                    n <= t && not prevSatisfies
                | _ -> false

        /// 현재 값이 매칭 조건을 (정상상태로) 만족하는가 — edge/전이가 아닌 "지금 조건이 걸려 있는가" 판정.
        /// 라이브 활성알람 자동 해소(조건 풀림 → 표시 제거)에 사용: shouldResolve = not (isConditionActive ...).
        /// edge 연산(RisingEdge/FallingEdge)은 정상상태 개념이 없어 "매칭된 레벨에 머물러 있는지"로 폴백,
        /// Changed 는 정상상태가 없으므로 항상 false(다음 평가에 즉시 해소).
        let isConditionActive (vt: PlcValueType) (op: UserTagMatchOp) (matchValue: string)
                              (currentValue: string) : bool =
            let num () = tryParseNumber currentValue, tryParseNumber matchValue
            match op with
            | UserTagMatchOp.RisingEdge -> normalizeBool currentValue = "1"
            | UserTagMatchOp.FallingEdge -> normalizeBool currentValue = "0"
            | UserTagMatchOp.Changed -> false
            | UserTagMatchOp.Eq -> valueEquals vt currentValue matchValue
            | UserTagMatchOp.Neq -> not (valueEquals vt currentValue matchValue)
            | UserTagMatchOp.Gt -> match num () with Some n, Some t -> n > t | _ -> false
            | UserTagMatchOp.Gte -> match num () with Some n, Some t -> n >= t | _ -> false
            | UserTagMatchOp.Lt -> match num () with Some n, Some t -> n < t | _ -> false
            | UserTagMatchOp.Lte -> match num () with Some n, Some t -> n <= t | _ -> false

        /// MatchOp + MatchValue 의 사람-친화 설명 — 정의 화면 / 알림 행에서 노출.
        /// vt 는 현재 메시지 표시 로직에서 분기에 쓰이지 않으나, 향후 타입별 단위 (예: "85 ℃") 표시 확장을 위해 시그니처에 유지.
        let describeCondition (_vt: PlcValueType) (op: UserTagMatchOp) (matchValue: string) : string =
            let mv = if String.IsNullOrWhiteSpace(matchValue) then "?" else matchValue
            match op with
            | UserTagMatchOp.RisingEdge -> "0 → 1"
            | UserTagMatchOp.FallingEdge -> "1 → 0"
            | UserTagMatchOp.Changed -> "값 변경 시"
            | UserTagMatchOp.Eq -> sprintf "= %s" mv
            | UserTagMatchOp.Neq -> sprintf "≠ %s" mv
            | UserTagMatchOp.Gt -> sprintf "> %s" mv
            | UserTagMatchOp.Gte -> sprintf "≥ %s" mv
            | UserTagMatchOp.Lt -> sprintf "< %s" mv
            | UserTagMatchOp.Lte -> sprintf "≤ %s" mv
