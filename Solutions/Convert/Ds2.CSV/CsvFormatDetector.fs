namespace Ds2.CSV

open System

/// CSV 첫 줄(헤더)만으로 규격을 판별한다.
///
/// 세 규격의 헤더 집합은 서로소이므로 판별은 결정적이다. 파일이 이미 자기 형식을 선언하고
/// 있으니 사용자에게 다시 물을 이유가 없다 — 불러오기 다이얼로그의 형식 선택 라디오를
/// 없앤 근거가 이것이다. 틀린 선택이 "파일이 잘못됐다"는 오류로 되돌아오던 경로를 끊는다.
///
/// 판별에 실패하면 본문은 건드리지 않는다. 가장 가까워 보이는 파서에 본문을 먹이면
/// 헤더 한 글자 문제가 행마다 쏟아지는 데이터 오류로 둔갑해 진짜 원인을 가린다.
/// 대신 가장 가까운 규격과의 '헤더 차이'만 짚어 준다.
module CsvFormatDetector =

    /// (규격, 정규화된 기대 헤더, 사용자에게 보여줄 헤더 표기)
    let private candidates =
        [ CsvFormat.Basic3,    BasicCsvParser.expectedHeaderFields, "FLOW,WORK,CALL"
          CsvFormat.Standard9, CsvParser.expectedHeader9,           "Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress"
          CsvFormat.Standard8, CsvParser.expectedHeader8,           "Flow,Work,Device,Api,InName,InAddress,OutName,OutAddress" ]

    /// 감지 배지에 쓰는 짧은 이름.
    let formatName (format: CsvFormat) =
        match format with
        | CsvFormat.Basic3 -> "기본 3열"
        | CsvFormat.Standard9 -> "표준 9열"
        | CsvFormat.Standard8 -> "표준 8열"
        | _ -> "알 수 없는 형식"

    /// 규격을 고른 뒤 사용자가 알아야 할 부수 효과. 없으면 "".
    let formatNote (format: CsvFormat) =
        match format with
        | CsvFormat.Standard8 -> "System 열 없음 — Device 이름에서 유도"
        | _ -> ""

    let separatorName (separator: char) =
        if separator = '\t' then "탭" else "쉼표"

    let private displayField (field: string) =
        if String.IsNullOrWhiteSpace field then "(빈 열)" else field.Trim()

    /// 헤더 줄을 필드로 쪼갠다. 따옴표가 깨져 정식 분해가 실패하면 단순 분할로 폴백한다 —
    /// 판별 단계에서는 '무엇이 적혀 있는지' 되비추는 게 우선이다.
    let private splitHeader (separator: char) (headerText: string) =
        match CsvParser.splitLine separator 1 headerText with
        | Ok fields -> fields
        | Error _ -> headerText.Split(separator) |> List.ofArray

    let private matchCount (expected: string list) (actual: string list) =
        let actualSet = Set.ofList actual
        expected |> List.filter actualSet.Contains |> List.length

    /// 기대 헤더와의 차이를 한 줄로. 이름은 다 맞는데 배열만 다른 경우를 따로 짚는다 —
    /// 그 상태에서 '없는 열' 을 나열하면 사용자가 엉뚱한 곳을 고친다.
    /// 모르는 열은 정규화형이 아니라 파일에 적힌 원문으로 보여준다 — 사용자가 찾아 고칠 문자열이므로.
    let private describeDiff (expected: string list) (pairs: (string * string) list) =
        let actual = pairs |> List.map snd
        let expectedSet = Set.ofList expected
        let actualSet = Set.ofList actual
        let missing = expected |> List.filter (fun f -> not (actualSet.Contains f))
        let extra = pairs |> List.filter (fun (_, norm) -> not (expectedSet.Contains norm))
        let parts =
            [ if not missing.IsEmpty then
                yield "없는 열: " + (missing |> List.map displayField |> String.concat ", ")
              if not extra.IsEmpty then
                yield "모르는 열: " + (extra |> List.map (fst >> displayField) |> String.concat ", ")
              if missing.IsEmpty && extra.IsEmpty then
                yield "열 이름은 모두 맞지만 순서가 다릅니다" ]
        $"""{matchCount expected actual}개 일치 / {String.concat " · " parts}"""

    /// 일치하는 열이 가장 많은 후보. 동수면 열 개수가 가까운 쪽.
    /// 열 개수를 먼저 보면 System 열이 빠진 9열을 8열과 비교해 문제를 두 개로 불리게 된다.
    let private nearest (actual: string list) =
        candidates
        |> List.sortBy (fun (_, expected, _) ->
            -(matchCount expected actual), abs (List.length expected - List.length actual))
        |> List.head

    let private acceptedHeadersText =
        candidates
        |> List.map (fun (format, _, sample) -> $"  {formatName format}   {sample}")
        |> String.concat "\n"

    let private buildDiagnostic (separator: char) (rawFields: string list) (normalizedFields: string list) =
        // 어느 규격의 열 이름도 하나 못 맞추면 헤더가 아니라 데이터 행일 가능성이 높다.
        // 그때는 규격과의 비교가 소음이다 — 고칠 곳은 열 이름이 아니라 빠진 헤더 줄 자체다.
        let looksHeaderless =
            candidates |> List.forall (fun (_, expected, _) -> matchCount expected normalizedFields = 0)
        let readHeader = rawFields |> List.map displayField |> String.concat ", "
        let comparison =
            if looksHeaderless then ""
            else
                let nearFormat, nearExpected, _ = nearest normalizedFields
                $"{formatName nearFormat}과 비교:  {describeDiff nearExpected (List.zip rawFields normalizedFields)}\n"
        let headerlessHint =
            if looksHeaderless
            then "\n\n첫 줄은 헤더여야 합니다 — 데이터 행만 있는 파일은 불러올 수 없습니다."
            else ""
        $"지원하지 않는 CSV 헤더입니다. (구분자: {separatorName separator}, {List.length rawFields}개 열)\n\n"
        + $"읽은 헤더:  {readHeader}\n"
        + comparison
        + $"\n받아들이는 헤더:\n{acceptedHeadersText}"
        + headerlessHint

    /// 내용 첫 줄만 보고 규격을 판별한다.
    /// 내용이 비어 있으면 Unknown + 빈 Diagnostic — 아직 판별할 게 없는 상태이지 오류가 아니다.
    let detect (content: string) : CsvHeaderInfo =
        let normalized = CsvParser.normalize content
        let headerText =
            normalized.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
            |> Array.tryFind (fun line -> not (String.IsNullOrWhiteSpace line))

        match headerText with
        | None ->
            { Format = CsvFormat.Unknown; Separator = ','; FieldCount = 0; HeaderText = ""; Diagnostic = "" }
        | Some header ->
            let separator = CsvParser.detectSeparator header
            let rawFields = splitHeader separator header
            let normalizedFields = rawFields |> List.map CsvParser.normalizeHeaderField
            match candidates |> List.tryFind (fun (_, expected, _) -> expected = normalizedFields) with
            | Some (format, _, _) ->
                { Format = format
                  Separator = separator
                  FieldCount = List.length rawFields
                  HeaderText = header.Trim()
                  Diagnostic = "" }
            | None ->
                { Format = CsvFormat.Unknown
                  Separator = separator
                  FieldCount = List.length rawFields
                  HeaderText = header.Trim()
                  Diagnostic = buildDiagnostic separator rawFields normalizedFields }
