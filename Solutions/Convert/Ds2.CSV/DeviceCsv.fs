namespace Ds2.CSV

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open Ds2.Core
open Ds2.Core.Store

/// Device CSV 한 행 — Device System 과 그 Api(ApiDef) 선언(#253).
type DeviceCsvRow = {
    LineNumber : int
    Device     : string
    Api        : string
    ActionType : ActionType
    SensingType: SensingType
    Description: string option
}

/// Device CSV — Device 와 Api 만 선언해 기존 프로젝트에 더하는 형식(#253).
/// 표준 CSV 는 Call 한 줄이 기준이라 Flow·Work 없이 Device·Api 만 적을 수 없다. I/O 주소는 Call 쪽 정보라 이 형식엔 없다.
/// 헤더: Device,Api,ActionType,SensingType,Description — 앞의 Device·Api 만 있어도 된다(뒤 열은 생략·빈칸 가능, 종류 기본값 Normal).
/// 종류 글자(대소문자 무시): 동작 Normal | Normal(ms) | Pulse | Pulse(ms) | Latch | Virtual,
/// 감지 Normal | Normal(ms) | Latch(ms) | Virtual(ms) — ApiDef 편집 대화상자의 선택지와 일대일.
module DeviceCsv =

    let headerText = "Device,Api,ActionType,SensingType,Description"

    let private headerFields = [ "device"; "api"; "actiontype"; "sensingtype"; "description" ]

    /// "Name" 또는 "Name(123)" → (소문자 이름, ms). 괄호 형식이 틀리거나 ms 가 음수면 None.
    let private splitTyped (text: string) : (string * int option) option =
        let t = text.Trim()
        let openIdx = t.IndexOf '('
        if openIdx < 0 then Some (t.ToLowerInvariant(), None)
        elif t.EndsWith ")" then
            let name = t.Substring(0, openIdx).Trim().ToLowerInvariant()
            let inner = t.Substring(openIdx + 1, t.Length - openIdx - 2).Trim()
            match Int32.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, ms when ms >= 0 -> Some (name, Some ms)
            | _ -> None
        else None

    /// 동작 종류 글자 → ActionType. 빈칸은 Normal.
    let tryParseActionType (text: string) : ActionType option =
        if String.IsNullOrWhiteSpace text then Some (ActionType.Normal None)
        else
            match splitTyped text with
            | Some ("normal", ms) -> Some (ActionType.Normal ms)
            | Some ("pulse", ms) -> Some (ActionType.Pulse ms)
            | Some ("latch", None) -> Some ActionType.Latch
            | Some ("virtual", None) -> Some ActionType.Virtual
            | _ -> None

    /// 감지 종류 글자 → SensingType. 빈칸은 Normal. Latch·Virtual 은 ms 필수.
    let tryParseSensingType (text: string) : SensingType option =
        if String.IsNullOrWhiteSpace text then Some (SensingType.Normal None)
        else
            match splitTyped text with
            | Some ("normal", ms) -> Some (SensingType.Normal ms)
            | Some ("latch", Some ms) -> Some (SensingType.Latch ms)
            | Some ("virtual", Some ms) -> Some (SensingType.Virtual ms)
            | _ -> None

    /// ActionType → 종류 글자 — tryParseActionType 의 역.
    let formatActionType (action: ActionType) : string =
        match action with
        | ActionType.Normal None -> "Normal"
        | ActionType.Normal (Some ms) -> $"Normal({ms})"
        | ActionType.Pulse None -> "Pulse"
        | ActionType.Pulse (Some ms) -> $"Pulse({ms})"
        | ActionType.Latch -> "Latch"
        | ActionType.Virtual -> "Virtual"

    /// SensingType → 종류 글자 — tryParseSensingType 의 역.
    let formatSensingType (sensing: SensingType) : string =
        match sensing with
        | SensingType.Normal None -> "Normal"
        | SensingType.Normal (Some ms) -> $"Normal({ms})"
        | SensingType.Latch ms -> $"Latch({ms})"
        | SensingType.Virtual ms -> $"Virtual({ms})"

    /// 프로젝트의 Device(Passive) System 과 Api 를 Device CSV 로 — 고쳐서 다른 프로젝트에 불러오거나 양식으로 쓴다(#253).
    /// 예전엔 불러오기만 있어서 이 형식을 손으로 처음부터 적어야 했다. Api 가 없는 Device 는 행이 없으니 남지 않는다.
    let projectToCsv (store: DsStore) (projectId: Guid) : string =
        let builder = StringBuilder()
        builder.AppendLine(headerText) |> ignore
        for device in Queries.passiveSystemsOf projectId store do
            for apiDef in Queries.apiDefsOf device.Id store do
                [ device.Name
                  apiDef.Name
                  formatActionType apiDef.ActionType
                  formatSensingType apiDef.SensingType
                  apiDef.Description |> Option.defaultValue "" ]
                |> List.map CsvExporter.escape
                |> String.concat ","
                |> builder.AppendLine
                |> ignore
        builder.ToString()

    /// 내용을 읽어 행 목록으로. 잘못된 행은 줄 번호와 함께 전부 모아 돌려준다(하나라도 있으면 Error).
    let parse (content: string) : Result<DeviceCsvRow list, ParseError list> =
        let text = if String.IsNullOrEmpty content then "" else CsvParser.normalize content
        let lines =
            text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
            |> Array.mapi (fun idx line -> idx + 1, line)
            |> Array.filter (fun (_, line) -> not (String.IsNullOrWhiteSpace line))
        if lines.Length = 0 then
            Error [ { LineNumber = 0; Message = "CSV 가 비었습니다." } ]
        else
            let headerLine, headerRaw = lines.[0]
            let separator = CsvParser.detectSeparator headerRaw
            match CsvParser.splitLine separator headerLine headerRaw with
            | Error error -> Error [ error ]
            | Ok fields ->
                let header = fields |> List.map CsvParser.normalizeHeaderField
                let columnCount = header.Length
                if columnCount < 2 || columnCount > headerFields.Length || header <> List.truncate columnCount headerFields then
                    Error [ { LineNumber = headerLine; Message = $"헤더가 맞지 않습니다. 예: {headerText}" } ]
                else
                    let errors = ResizeArray<ParseError>()
                    let rows = ResizeArray<DeviceCsvRow>()
                    let firstLineOf = Dictionary<struct (string * string), int>()
                    for lineNumber, line in lines |> Array.skip 1 do
                        match CsvParser.splitLine separator lineNumber line with
                        | Error error -> errors.Add error
                        | Ok values when values.Length > columnCount ->
                            errors.Add { LineNumber = lineNumber; Message = $"열이 {columnCount}개여야 하는데 {values.Length}개입니다." }
                        | Ok values ->
                            let cell index = if index < values.Length then values.[index].Trim() else ""
                            let device, api = cell 0, cell 1
                            if device = "" || api = "" then
                                errors.Add { LineNumber = lineNumber; Message = "Device 와 Api 는 비울 수 없습니다." }
                            else
                                match tryParseActionType (cell 2), tryParseSensingType (cell 3) with
                                | None, _ ->
                                    errors.Add { LineNumber = lineNumber
                                                 Message = $"알 수 없는 ActionType 입니다: '{cell 2}' (Normal, Normal(ms), Pulse, Pulse(ms), Latch, Virtual)" }
                                | _, None ->
                                    errors.Add { LineNumber = lineNumber
                                                 Message = $"알 수 없는 SensingType 입니다: '{cell 3}' (Normal, Normal(ms), Latch(ms), Virtual(ms))" }
                                | Some action, Some sensing ->
                                    let key = struct (device, api)
                                    match firstLineOf.TryGetValue key with
                                    | true, first ->
                                        errors.Add { LineNumber = lineNumber; Message = $"{device}.{api} 가 {first}행에도 있습니다." }
                                    | _ ->
                                        firstLineOf.[key] <- lineNumber
                                        rows.Add {
                                            LineNumber = lineNumber
                                            Device = device
                                            Api = api
                                            ActionType = action
                                            SensingType = sensing
                                            Description = (let d = cell 4 in if d = "" then None else Some d) }
                    if errors.Count > 0 then Error (List.ofSeq errors) else Ok (List.ofSeq rows)
