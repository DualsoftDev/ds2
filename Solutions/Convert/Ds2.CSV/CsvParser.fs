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

    let private splitCsvLine (lineNumber: int) (line: string) : Result<string list, ParseError> =
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
            else
                match ch with
                | ',' ->
                    values.Add(current.ToString())
                    current.Clear() |> ignore
                | '"' when current.Length = 0 ->
                    inQuotes <- true
                | '"' ->
                    invalidQuote <- true
                | _ ->
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
            match splitCsvLine headerLineNumber headerText with
            | Error error -> Error [ error ]
            | Ok headerFields ->
                let normalizedHeader =
                    headerFields
                    |> List.map (fun value -> trim value |> fun item -> item.ToLowerInvariant())

                let hasSystem =
                    if normalizedHeader = expectedHeader9 then Some true
                    elif normalizedHeader = expectedHeader8 then Some false
                    else None

                match hasSystem with
                | None ->
                    let expectedText = String.concat "," expectedHeader9
                    Error [ {
                        LineNumber = headerLineNumber
                        Message = $"invalid header. expected: {expectedText}"
                    } ]
                | Some hasSystemCol ->
                    let expectedCols = if hasSystemCol then 9 else 8
                    let parseErrors = ResizeArray<ParseError>()
                    let rows = ResizeArray<CsvRow>()

                    for lineNumber, line in nonEmptyLines |> Array.skip 1 do
                        match splitCsvLine lineNumber line with
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
