namespace Ds2.IOList

open System
open System.IO
open System.Text

/// IO CSV Import — 양식 9열 + Extended 11열 자동 감지
module CsvImporter =

    /// CSV import 결과 행
    type IoImportRow = {
        VarName   : string
        DataType  : string
        Address   : string
        FlowName  : string
        WorkName  : string   // 9열 구버전 CSV에서는 빈 문자열
        DeviceName: string
        ApiName   : string
        Direction : string   // "Input" | "Output"
    }

    /// CSV 필드 파싱 (인용부호 처리)
    let private parseFields (line: string) : string list =
        let mutable fields = []
        let mutable i = 0
        let sb = StringBuilder()
        let mutable inQuote = false

        while i < line.Length do
            let c = line.[i]
            if inQuote then
                if c = '"' && i + 1 < line.Length && line.[i + 1] = '"' then
                    sb.Append('"') |> ignore
                    i <- i + 2
                elif c = '"' then
                    inQuote <- false
                    i <- i + 1
                else
                    sb.Append(c) |> ignore
                    i <- i + 1
            else
                if c = '"' then
                    inQuote <- true
                    i <- i + 1
                elif c = ',' then
                    fields <- sb.ToString().Trim() :: fields
                    sb.Clear() |> ignore
                    i <- i + 1
                else
                    sb.Append(c) |> ignore
                    i <- i + 1

        fields <- sb.ToString().Trim() :: fields
        List.rev fields

    /// call_name에서 Api 이름 추출 (예: "CYL1.Up" → "Up")
    let private extractApiName (callName: string) =
        match callName.LastIndexOf('.') with
        | -1 -> callName
        | idx -> callName.Substring(idx + 1)

    /// CSV 파일 텍스트를 trim된 줄 배열로 변환 (빈 줄 제거)
    let private readLines (filePath: string) : string array =
        let text =
            use fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            use sr = new StreamReader(fs, Encoding.UTF8)
            sr.ReadToEnd()
        text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s.Length > 0)

    /// Extended 11열 파싱: var_name,data_type,address,io_type,category,flow_name,work_name,call_name,device_name,direction,comment
    let private parseExtended11 (lines: string array) : IoImportRow list =
        lines
        |> Array.choose (fun line ->
            let fields = parseFields line
            if fields.Length >= 10 then
                let direction = fields.[9]
                if direction = "Input" || direction = "Output" then
                    Some {
                        VarName    = fields.[0]
                        DataType   = fields.[1]
                        Address    = fields.[2]
                        FlowName   = fields.[5]
                        WorkName   = fields.[6]
                        DeviceName = fields.[8]
                        ApiName    = extractApiName fields.[7]
                        Direction  = direction
                    }
                else None
            else None)
        |> Array.toList

    /// 9열/10열 양식 파싱
    /// 9열: Flow,Device,Api,OutTag,OutDataType,OutAddress,InTag,InDataType,InAddress
    /// 10열: Flow,Work,Device,Api,OutTag,OutDataType,OutAddress,InTag,InDataType,InAddress
    let private parseTemplate (hasWork: bool) (lines: string array) : IoImportRow list =
        let offset = if hasWork then 1 else 0
        lines
        |> Array.collect (fun line ->
            let fields = parseFields line
            if fields.Length >= 3 + offset then
                let flow = fields.[0]
                let work = if hasWork then fields.[1] else ""
                let device = fields.[1 + offset]
                let api = fields.[2 + offset]
                let outSymbol   = if fields.Length >= 4 + offset then fields.[3 + offset] else ""
                let outDataType = if fields.Length >= 5 + offset then fields.[4 + offset] else ""
                let outAddress  = if fields.Length >= 6 + offset then fields.[5 + offset] else ""
                let inSymbol    = if fields.Length >= 7 + offset then fields.[6 + offset] else ""
                let inDataType  = if fields.Length >= 8 + offset then fields.[7 + offset] else ""
                let inAddress   = if fields.Length >= 9 + offset then fields.[8 + offset] else ""

                [|  if inAddress <> "" && inAddress <> "-" then
                        yield { VarName = inSymbol; DataType = inDataType; Address = inAddress
                                FlowName = flow; WorkName = work; DeviceName = device; ApiName = api; Direction = "Input" }
                    if outAddress <> "" && outAddress <> "-" then
                        yield { VarName = outSymbol; DataType = outDataType; Address = outAddress
                                FlowName = flow; WorkName = work; DeviceName = device; ApiName = api; Direction = "Output" }
                |]
            else [||])
        |> Array.toList

    /// CSV 파일을 파싱하여 IoImportRow 리스트 반환 (헤더 자동 감지)
    let parseIoCsv (filePath: string) : Result<IoImportRow list, string> =
        try
            let lines = readLines filePath
            if lines.Length < 2 then
                Error "CSV 파일에 데이터가 없습니다."
            else
                let header = lines.[0].ToLowerInvariant()
                let dataLines = lines.[1..]

                if header.Contains("var_name") && header.Contains("direction") then
                    Ok (parseExtended11 dataLines)
                elif header.Contains("flow") && header.Contains("device") && header.Contains("api") then
                    let hasWork = header.Contains("work")
                    Ok (parseTemplate hasWork dataLines)
                else
                    Error "CSV 헤더를 인식할 수 없습니다.\n\n지원 형식:\n- 양식: Flow,Work,Device,Api,OutTag,OutDataType,OutAddress,InTag,InDataType,InAddress\n- 양식(구버전): Flow,Device,Api,OutTag,OutDataType,OutAddress,InTag,InDataType,InAddress\n- Extended: var_name,...,direction,comment (11열)"
        with ex ->
            Error $"CSV 파일 읽기 실패: {ex.Message}"
