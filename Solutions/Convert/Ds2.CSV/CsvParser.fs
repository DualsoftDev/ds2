namespace Ds2.CSV

open System
open System.Collections.Generic
open System.Text

module CsvParser =

    type private EntryAccumulator = {
        Row         : CsvRow
        mutable InName : string option
        mutable InTag  : string option
        mutable OutName: string option
        mutable OutTag : string option
        SourceLines : ResizeArray<int>
    }

    let private expectedHeader = [ "flow"; "work"; "device"; "system"; "api"; "inname"; "inaddress"; "outname"; "outaddress" ]

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

    let private mergeAddress
        (kind: string)
        (lineNumber: int)
        (current: string option)
        (incoming: string option)
        : Result<string option, ParseError> =
        match current, incoming with
        | Some existing, Some next when not (String.Equals(existing, next, StringComparison.Ordinal)) ->
            Error {
                LineNumber = lineNumber
                Message = $"conflicting {kind} address: '{existing}' vs '{next}'"
            }
        | None, Some next -> Ok (Some next)
        | _ -> Ok current

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

                if normalizedHeader <> expectedHeader then
                    let expectedText = String.concat "," expectedHeader
                    Error [ {
                        LineNumber = headerLineNumber
                        Message = $"invalid header. expected: {expectedText}"
                    } ]
                else
                    let parseErrors = ResizeArray<ParseError>()
                    let rows = ResizeArray<CsvRow>()

                    for lineNumber, line in nonEmptyLines |> Array.skip 1 do
                        match splitCsvLine lineNumber line with
                        | Error error ->
                            parseErrors.Add(error)
                        | Ok values when values.Length <> 9 ->
                            parseErrors.Add({
                                LineNumber = lineNumber
                                Message = $"expected 9 columns but found {values.Length}"
                            })
                        | Ok values ->
                            let flowName = trim values.[0]
                            let workName = trim values.[1]
                            let deviceName = trim values.[2]
                            let systemName = trim values.[3]
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
                                    ApiName = trim values.[4]
                                    InName = trim values.[5]
                                    InAddress = trim values.[6]
                                    OutName = trim values.[7]
                                    OutAddress = trim values.[8]
                                    LineNumber = lineNumber
                                })

                    if parseErrors.Count > 0 then
                        Error (parseErrors |> Seq.toList)
                    else
                        let groups = Dictionary<string * string * string * string * string, EntryAccumulator>()
                        let ordered = ResizeArray<EntryAccumulator>()

                        for row in rows do
                            let key = (row.FlowName, row.WorkName, row.DeviceName, row.SystemName, row.ApiName)
                            let incomingInName = toOption row.InName
                            let incomingInAddress = toOption row.InAddress
                            let incomingOutName = toOption row.OutName
                            let incomingOutAddress = toOption row.OutAddress

                            match groups.TryGetValue(key) with
                            | true, existing ->
                                existing.SourceLines.Add(row.LineNumber)
                                match mergeAddress "inName" row.LineNumber existing.InName incomingInName with
                                | Error error -> parseErrors.Add(error)
                                | Ok value -> existing.InName <- value
                                match mergeAddress "inAddress" row.LineNumber existing.InTag incomingInAddress with
                                | Error error -> parseErrors.Add(error)
                                | Ok value -> existing.InTag <- value
                                match mergeAddress "outName" row.LineNumber existing.OutName incomingOutName with
                                | Error error -> parseErrors.Add(error)
                                | Ok value -> existing.OutName <- value
                                match mergeAddress "outAddress" row.LineNumber existing.OutTag incomingOutAddress with
                                | Error error -> parseErrors.Add(error)
                                | Ok value -> existing.OutTag <- value
                            | false, _ ->
                                let acc = {
                                    Row = row
                                    InName = incomingInName
                                    InTag = incomingInAddress
                                    OutName = incomingOutName
                                    OutTag = incomingOutAddress
                                    SourceLines = ResizeArray([ row.LineNumber ])
                                }
                                groups.[key] <- acc
                                ordered.Add(acc)

                        if parseErrors.Count > 0 then
                            Error (parseErrors |> Seq.toList)
                        else
                            let entries =
                                ordered
                                |> Seq.map (fun acc ->
                                    let alias = resolveDeviceAlias acc.Row.DeviceName
                                    let sysName =
                                        let s = acc.Row.SystemName
                                        if String.IsNullOrWhiteSpace(s) then alias else s
                                    {
                                        FlowName = acc.Row.FlowName
                                        WorkName = acc.Row.WorkName
                                        DeviceName = acc.Row.DeviceName
                                        DeviceAlias = alias
                                        SystemName = sysName
                                        ApiName = resolveApiName acc.Row.ApiName acc.InTag acc.OutTag
                                        IsSyntheticApi = String.IsNullOrWhiteSpace(acc.Row.ApiName)
                                        InName = acc.InName
                                        InAddress = acc.InTag
                                        OutName = acc.OutName
                                        OutAddress = acc.OutTag
                                        SourceLines = acc.SourceLines |> Seq.toList
                                    })
                                |> Seq.toList

                            Ok { Entries = entries }
