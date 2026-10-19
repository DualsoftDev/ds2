namespace Ds2.CSV

open System
open System.Text

module CsvParser =

    let internal expectedHeader9 = [ "flow"; "work"; "device"; "system"; "api"; "inname"; "inaddress"; "outname"; "outaddress" ]
    let internal expectedHeader8 = [ "flow"; "work"; "device"; "api"; "inname"; "inaddress"; "outname"; "outaddress" ]
    /// 9열 뒤에 Active System 소속과 Reference 원본 Work 를 붙인 11열.
    /// 9열만으로는 여러 Active System 이 하나로 합쳐지고 Reference Call 이 일반 Call 로 되돌아왔다.
    /// 맨 뒤에 붙였으므로 9열 파일은 같은 파서에서 두 값이 빈칸인 채 읽힌다.
    let internal expectedHeader11 = expectedHeader9 @ [ "activesystem"; "referenceof" ]
    /// 11열 헤더 표기 — 내보내기 헤더와 형식 안내문이 같은 문자열을 쓴다.
    let internal header11Text = "Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress,ActiveSystem,ReferenceOf"

    /// BOM 제거 + NFC 정규화 + 전각->반각 폴딩(U+FF01..U+FF5E, U+3000).
    /// 한국어 LLM 출력의 전각 구분자(> ; , =)를 흡수한다.
    /// 표준 9열/8열과 기본 3열이 같은 전처리를 거쳐야 형식 판별과 실제 파싱의 판정이 어긋나지 않는다.
    let internal normalize (content: string) : string =
        let noBom =
            if not (String.IsNullOrEmpty content) && content.[0] = '\uFEFF'
            then content.Substring(1) else content
        let nfc = noBom.Normalize(NormalizationForm.FormC)
        let sb = StringBuilder(nfc.Length)
        for ch in nfc do
            let code = int ch
            if code >= 0xFF01 && code <= 0xFF5E then sb.Append(char (code - 0xFEE0)) |> ignore
            elif code = 0x3000 then sb.Append(' ') |> ignore
            else sb.Append(ch) |> ignore
        sb.ToString()

    let private trim (value: string) =
        if isNull value then "" else value.Trim()

    let private toOption (value: string) =
        let normalized = trim value
        if String.IsNullOrWhiteSpace(normalized) then None else Some normalized

    let private sanitizePart (value: string) =
        trim value
        |> fun normalized -> normalized.Replace(".", "_")

    let private resolveDeviceAlias (deviceName: string) =
        let alias = sanitizePart deviceName
        if String.IsNullOrWhiteSpace(alias) then "Device" else alias

    let private resolveApiName (rawApi: string) (inAddress: string option) (outAddress: string option) =
        match toOption rawApi with
        | Some api ->
            let normalized = sanitizePart api
            if String.IsNullOrWhiteSpace(normalized) then "Signal" else normalized
        | None ->
            let seed =
                outAddress
                |> Option.orElse inAddress
                |> Option.defaultValue "Signal"
                |> sanitizePart
            if String.IsNullOrWhiteSpace(seed) then "Signal" else $"Signal_{seed}"

    /// 헤더 열 이름 정규화 — 대소문자, 공백/언더스코어/하이픈, addr↔address 축약을 흡수한다.
    /// Excel 에서 편집한 표는 'IN Name', 'IN_ADDR', 'Out Addr' 처럼 열 이름이 흔히 달라진다.
    let internal normalizeHeaderField (value: string) =
        let compact =
            (trim value).ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "")
        match compact with
        | "inaddr" -> "inaddress"
        | "outaddr" -> "outaddress"
        | other -> other

    /// 붙여넣기 호환 — Excel/스프레드시트 복사본은 탭 구분(TSV), 일반 CSV 는 쉼표 구분이다.
    /// 헤더 행의 탭이 쉼표보다 많으면 탭을 구분자로 판정한다.
    let internal detectSeparator (headerLine: string) : char =
        if String.IsNullOrEmpty headerLine then ','
        else
            let count (target: char) = headerLine |> Seq.filter ((=) target) |> Seq.length
            if count '\t' > count ',' then '\t' else ','

    let internal splitLine (separator: char) (lineNumber: int) (line: string) : Result<string list, ParseError> =
        let values = ResizeArray<string>()
        let current = StringBuilder()
        let mutable index = 0
        let mutable inQuotes = false
        let mutable invalidQuote = false

        while index < line.Length && not invalidQuote do
            let ch = line.[index]
            if inQuotes then
                if ch = '"' then
                    if index + 1 < line.Length && line.[index + 1] = '"' then
                        current.Append('"') |> ignore
                        index <- index + 1
                    else
                        inQuotes <- false
                else
                    current.Append(ch) |> ignore
            elif ch = separator then
                values.Add(current.ToString())
                current.Clear() |> ignore
            elif ch = '"' && current.Length = 0 then
                inQuotes <- true
            elif ch = '"' then
                invalidQuote <- true
            else
                current.Append(ch) |> ignore
            index <- index + 1

        if invalidQuote then
            Error {
                LineNumber = lineNumber
                Message = "invalid quote placement"
            }
        elif inQuotes then
            Error {
                LineNumber = lineNumber
                Message = "unterminated quoted field"
            }
        else
            values.Add(current.ToString())
            Ok (values |> Seq.toList)

    let internal splitCsvLine (lineNumber: int) (line: string) : Result<string list, ParseError> =
        splitLine ',' lineNumber line

    let parse (content: string) : Result<CsvDocument, ParseError list> =
        let text = if String.IsNullOrEmpty(content) then "" else normalize content
        let normalized = text.Replace("\r\n", "\n").Replace("\r", "\n")
        let nonEmptyLines =
            normalized.Split('\n')
            |> Array.mapi (fun idx line -> idx + 1, line)
            |> Array.filter (fun (_, line) -> not (String.IsNullOrWhiteSpace(line)))

        if nonEmptyLines.Length = 0 then
            Error [ {
                LineNumber = 0
                Message = "CSV is empty"
            } ]
        else
            let headerLineNumber, headerText = nonEmptyLines.[0]
            let separator = detectSeparator headerText
            match splitLine separator headerLineNumber headerText with
            | Error error -> Error [ error ]
            | Ok headerFields ->
                let normalizedHeader = headerFields |> List.map normalizeHeaderField

                // (System 열 유무, 헤더 열 수)
                let layout =
                    if normalizedHeader = expectedHeader11 then Some (true, 11)
                    elif normalizedHeader = expectedHeader9 then Some (true, 9)
                    elif normalizedHeader = expectedHeader8 then Some (false, 8)
                    else None

                match layout with
                | None ->
                    let expectedText = String.concat "," expectedHeader11
                    Error [ {
                        LineNumber = headerLineNumber
                        Message = $"invalid header. expected: {expectedText} (쉼표 또는 탭 구분)"
                    } ]
                | Some (hasSystemCol, headerCols) ->
                    // 맨 뒤 두 열(ActiveSystem, ReferenceOf)은 손으로 쓴 행이 뒤쪽 빈칸을 빼먹어도 빈 값으로 읽는다.
                    let requiredCols = if hasSystemCol then 9 else 8
                    let expectedColsText =
                        if requiredCols = headerCols then $"{headerCols}" else $"{requiredCols}~{headerCols}"
                    let parseErrors = ResizeArray<ParseError>()
                    let rows = ResizeArray<CsvRow>()

                    for lineNumber, line in nonEmptyLines |> Array.skip 1 do
                        match splitLine separator lineNumber line with
                        | Error error ->
                            parseErrors.Add(error)
                        | Ok values when values.Length < requiredCols || values.Length > headerCols ->
                            parseErrors.Add({
                                LineNumber = lineNumber
                                Message = $"expected {expectedColsText} columns but found {values.Length}"
                            })
                        | Ok values ->
                            let cell index = if index < values.Length then trim values.[index] else ""
                            let flowName = trim values.[0]
                            let workName = trim values.[1]
                            let deviceName = trim values.[2]
                            let systemName  = if hasSystemCol then trim values.[3] else ""
                            let apiName     = trim values.[if hasSystemCol then 4 else 3]
                            let inName      = trim values.[if hasSystemCol then 5 else 4]
                            let inAddress   = trim values.[if hasSystemCol then 6 else 5]
                            let outName     = trim values.[if hasSystemCol then 7 else 6]
                            let outAddress  = trim values.[if hasSystemCol then 8 else 7]
                            if String.IsNullOrWhiteSpace(flowName) then
                                parseErrors.Add({
                                    LineNumber = lineNumber
                                    Message = "flow is required"
                                })
                            elif String.IsNullOrWhiteSpace(workName) then
                                parseErrors.Add({
                                    LineNumber = lineNumber
                                    Message = "work is required"
                                })
                            elif String.IsNullOrWhiteSpace(deviceName) then
                                parseErrors.Add({
                                    LineNumber = lineNumber
                                    Message = "device is required"
                                })
                            else
                                rows.Add({
                                    FlowName = flowName
                                    WorkName = workName
                                    DeviceName = deviceName
                                    SystemName = systemName
                                    ApiName = apiName
                                    InName = inName
                                    InAddress = inAddress
                                    OutName = outName
                                    OutAddress = outAddress
                                    // 8열·9열 헤더는 여기까지 열이 없어 빈칸이 된다.
                                    ActiveSystemName = cell 9
                                    ReferenceOf = cell 10
                                    LineNumber = lineNumber
                                })

                    if parseErrors.Count > 0 then
                        Error (parseErrors |> Seq.toList)
                    else
                        let entries =
                            rows
                            |> Seq.map (fun row ->
                                let alias = resolveDeviceAlias row.DeviceName
                                let sysName =
                                    let s = row.SystemName
                                    if String.IsNullOrWhiteSpace(s) then alias else s
                                let inName = toOption row.InName
                                let inAddress = toOption row.InAddress
                                let outName = toOption row.OutName
                                let outAddress = toOption row.OutAddress
                                {
                                    FlowName = row.FlowName
                                    WorkName = row.WorkName
                                    DeviceName = row.DeviceName
                                    DeviceAlias = alias
                                    SystemName = sysName
                                    ApiName = resolveApiName row.ApiName inAddress outAddress
                                    IsSyntheticApi = String.IsNullOrWhiteSpace(row.ApiName)
                                    InName = inName
                                    InAddress = inAddress
                                    OutName = outName
                                    OutAddress = outAddress
                                    ActiveSystemName = toOption row.ActiveSystemName
                                    ReferenceOf = toOption row.ReferenceOf
                                    SourceLines = [ row.LineNumber ]
                                })
                            |> Seq.toList

                        Ok { Entries = entries }
