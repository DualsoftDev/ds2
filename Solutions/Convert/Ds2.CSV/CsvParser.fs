namespace Ds2.CSV

open System
open System.Text

module CsvParser =

    let private expectedHeader9 = [ "flow"; "work"; "device"; "system"; "api"; "inname"; "inaddress"; "outname"; "outaddress" ]
    let private expectedHeader8 = [ "flow"; "work"; "device"; "api"; "inname"; "inaddress"; "outname"; "outaddress" ]

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
        let text =
            if String.IsNullOrEmpty(content) then ""
            elif content.[0] = '\uFEFF' then content[1..]
            else content

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

                let hasSystem =
                    if normalizedHeader = expectedHeader9 then Some true
                    elif normalizedHeader = expectedHeader8 then Some false
                    else None

                match hasSystem with
                | None ->
                    let expectedText = String.concat "," expectedHeader9
                    Error [ {
                        LineNumber = headerLineNumber
                        Message = $"invalid header. expected: {expectedText} (쉼표 또는 탭 구분)"
                    } ]
                | Some hasSystemCol ->
                    let expectedCols = if hasSystemCol then 9 else 8
                    let parseErrors = ResizeArray<ParseError>()
                    let rows = ResizeArray<CsvRow>()

                    for lineNumber, line in nonEmptyLines |> Array.skip 1 do
                        match splitLine separator lineNumber line with
                        | Error error ->
                            parseErrors.Add(error)
                        | Ok values when values.Length <> expectedCols ->
                            parseErrors.Add({
                                LineNumber = lineNumber
                                Message = $"expected {expectedCols} columns but found {values.Length}"
                            })
                        | Ok values ->
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
                                    SourceLines = [ row.LineNumber ]
                                })
                            |> Seq.toList

                        Ok { Entries = entries }
