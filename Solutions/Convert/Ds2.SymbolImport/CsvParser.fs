namespace Ds2.SymbolImport

open System
open System.IO
open System.Text
open System.Text.RegularExpressions

/// <summary>vendor 별 CSV (또는 XML) 심볼 테이블 → SymbolEntry 변환.
/// DS1 mapper 의 MX/CSVParser.fs / LSE/ConvertLSE.Xml.fs 룰 의미를 ds2 도메인으로.</summary>
module CsvParser =

    let private strip (s: string) =
        if isNull s then "" else s.Trim().Trim('"').Trim()

    let private startsWithAny (prefixes: string list) (value: string) =
        prefixes |> List.exists (fun prefix -> value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))

    /// Address/name prefix based direction inference.
    /// LS XGB/XGK CSV often stores bit I/O in P/M addresses while the variable
    /// name carries QX/IX, so the logical name is checked first for LS CSV.
    let private inferDirectionFromNameAndAddress (vendor: Vendor) (name: string) (address: string) : SymbolDirection =
        let upperName = if isNull name then "" else name.ToUpperInvariant().TrimStart([| '%' |])
        let upperAddr = if isNull address then "" else address.ToUpperInvariant().TrimStart([| '%' |])
        match vendor with
        | XG5000 | XGB | XGK ->
            if startsWithAny [ "QX"; "QW"; "QB"; "QD"; "Q_" ] upperName then SymbolDirection.Output
            elif startsWithAny [ "IX"; "IW"; "IB"; "ID"; "I_" ] upperName then SymbolDirection.Input
            elif startsWithAny [ "QX"; "QW"; "QB"; "QD"; "Q" ] upperAddr then SymbolDirection.Output
            elif startsWithAny [ "IX"; "IW"; "IB"; "ID"; "I" ] upperAddr then SymbolDirection.Input
            elif startsWithAny [ "MX"; "MW"; "MB"; "MD"; "M" ] upperAddr then SymbolDirection.Memory
            else SymbolDirection.UnknownDir
        | _ ->
            if startsWithAny [ "X"; "I" ] upperAddr then SymbolDirection.Input
            elif startsWithAny [ "Y"; "Q" ] upperAddr then SymbolDirection.Output
            elif upperAddr.StartsWith("M", StringComparison.OrdinalIgnoreCase) then SymbolDirection.Memory
            else SymbolDirection.UnknownDir

    let private inferDirection (vendor: Vendor) (address: string) : SymbolDirection =
        inferDirectionFromNameAndAddress vendor "" address

    /// Mitsubishi COMMENT.csv 실 dump 포맷:
    ///   line 0: 제목 ("CCS 조립라인 260408" 등 — 컬럼 없음, skip)
    ///   line 1: 헤더 ("Device Name"\t"Comment" 또는 "Device Name"\t"Comment"\t"Label" 등)
    ///   line 2+: 데이터 ("X0"\t"코멘트" 또는 "X0"\t"코멘트"\t"Label")
    /// 구분자 = tab (\t). quote 제거. Label 컬럼 없으면 Comment 를 Name 으로 사용.
    let parseMitsubishi (csvText: string) : CsvParseResult =
        let warnings = ResizeArray<string>()
        let lines =
            csvText.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        if lines.Length < 2 then
            { Entries = []; Warnings = [ "Mitsubishi CSV — 줄 수 부족 (제목/헤더만)" ] }
        else
            // 헤더 라인 위치 — "Device" 단어 포함된 첫 줄.
            let headerIdx =
                lines
                |> Array.tryFindIndex (fun l -> l.IndexOf("Device", StringComparison.OrdinalIgnoreCase) >= 0)
                |> Option.defaultValue 0
            // 헤더 다음 줄부터 데이터.
            let dataLines = lines |> Array.skip (headerIdx + 1)
            let entries =
                dataLines
                |> Array.choose (fun line ->
                    let parts = line.Split('\t')
                    if parts.Length < 2 then
                        warnings.Add(sprintf "Mitsubishi CSV — 컬럼 부족 (skip): %s" line)
                        None
                    else
                        let address = strip parts.[0]
                        // Label 컬럼이 있으면 [1]=Label, [2]=Comment. 없으면 [1]=Comment, Name=Comment.
                        let label   = if parts.Length >= 3 then strip parts.[1] else ""
                        let comment = if parts.Length >= 3 then strip parts.[2] else strip parts.[1]
                        let name =
                            // Label 우선, 없으면 Comment 를 Name 으로 (현장 dump 가 Label 없는 경우가 많음).
                            if not (String.IsNullOrWhiteSpace label) then label
                            else comment
                        if String.IsNullOrWhiteSpace address then None
                        else
                            Some {
                                Address = address
                                Name = name
                                Direction = inferDirection Mitsubishi address
                                Comment = comment
                                Vendor = Mitsubishi
                            })
                |> List.ofArray
            { Entries = entries; Warnings = List.ofSeq warnings }

    /// LS XG5000 — DS1 LSE/ConvertLSE.Xml 은 XML 기반. 본 ds2 parser 도 XML 입력 받음.
    /// XG5000 export 의 SymbolTable 노드 → Symbol 들의 Var(주소) / Comment / Type 추출.
    let parseXG5000Xml (xmlText: string) : CsvParseResult =
        let warnings = ResizeArray<string>()
        let entries = ResizeArray<SymbolEntry>()
        try
            let doc = System.Xml.XmlDocument()
            doc.LoadXml(xmlText)
            // XG5000 의 일반적 구조: <SymbolTable><Symbol Var="%IX0.0.0" Name="..." Comment="..."/>...
            // 또는 <Symbol><Var>%IX0.0.0</Var><Name>...</Name>...</Symbol>
            let nodes = doc.GetElementsByTagName("Symbol")
            for node in nodes do
                let attrOrChild (key: string) =
                    if node.Attributes <> null && node.Attributes.[key] <> null then
                        node.Attributes.[key].Value
                    else
                        let child = node.SelectSingleNode(key)
                        if child <> null then child.InnerText else ""
                let address = (attrOrChild "Var").Trim()
                let name    = (attrOrChild "Name").Trim()
                let comment = (attrOrChild "Comment").Trim()
                if String.IsNullOrWhiteSpace(address) || String.IsNullOrWhiteSpace(name) then
                    warnings.Add(sprintf "XG5000 — Var/Name 누락 (skip)")
                else
                    entries.Add {
                        Address = address
                        Name = name
                        Direction = inferDirectionFromNameAndAddress XG5000 name address
                        Comment = comment
                        Vendor = XG5000
                    }
        with ex ->
            warnings.Add(sprintf "XG5000 XML parse 실패: %s" ex.Message)
        { Entries = List.ofSeq entries; Warnings = List.ofSeq warnings }

    let private parseCsvLine (line: string) : string list =
        let fields = ResizeArray<string>()
        let current = StringBuilder()
        let mutable inQuotes = false
        let mutable i = 0
        while i < line.Length do
            let ch = line.[i]
            match ch with
            | '"' ->
                if inQuotes && i + 1 < line.Length && line.[i + 1] = '"' then
                    current.Append('"') |> ignore
                    i <- i + 1
                else
                    inQuotes <- not inQuotes
            | ',' when not inQuotes ->
                fields.Add(strip (current.ToString()))
                current.Clear() |> ignore
            | _ ->
                current.Append(ch) |> ignore
            i <- i + 1
        fields.Add(strip (current.ToString()))
        fields |> Seq.toList

    let private equalsOrdinalIgnoreCase expected actual =
        String.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)

    let private tryFindIndexByName (name: string) (row: string list) =
        row |> List.tryFindIndex (equalsOrdinalIgnoreCase name)

    let private isBitLike (dataType: string) =
        let upper = if isNull dataType then "" else dataType.Trim().ToUpperInvariant()
        upper = "BIT" || upper = "BOOL" || upper = "BOOLEAN"

    type private LsCsvLayout =
        { NameIndex: int
          AddressIndex: int
          DataTypeIndex: int option
          CommentIndex: int option
          DataStartIndex: int }

    let private tryDetectLsCsvLayout (rows: string list array) =
        rows
        |> Array.mapi (fun index row -> index, row)
        |> Array.tryPick (fun (index, row) ->
            match tryFindIndexByName "Variable" row, tryFindIndexByName "Address" row with
            | Some nameIndex, Some addressIndex ->
                Some
                    { NameIndex = nameIndex
                      AddressIndex = addressIndex
                      DataTypeIndex = tryFindIndexByName "DataType" row
                      CommentIndex = tryFindIndexByName "Comment" row
                      DataStartIndex = index + 1 }
            | _ ->
                if row.Length >= 6 && row |> List.exists (equalsOrdinalIgnoreCase "HMI") then
                    let hmiIndex = row |> List.findIndex (equalsOrdinalIgnoreCase "HMI")
                    if hmiIndex = 4 then
                        Some { NameIndex = 0; AddressIndex = 2; DataTypeIndex = Some 1; CommentIndex = Some 5; DataStartIndex = index + 1 }
                    elif row.Length >= 12 then
                        Some { NameIndex = 1; AddressIndex = 3; DataTypeIndex = Some 2; CommentIndex = Some 11; DataStartIndex = index + 1 }
                    else
                        None
                else
                    None)

    /// LS XGB/XGK/XGI CSV exports.
    /// Supported layouts:
    /// - XGK export: Type,Scope,Variable,Address,DataType,Property,Comment
    /// - XGB variable description: variable,type,device,use,HMI,description
    /// - XG5000 global variable CSV: kind,variable,type,memory,...,description
    let parseLsCsv (vendor: Vendor) (csvText: string) : CsvParseResult =
        let warnings = ResizeArray<string>()
        let rows =
            csvText.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map parseCsvLine
            |> Array.filter (fun row -> row |> List.exists (String.IsNullOrWhiteSpace >> not))
        match tryDetectLsCsvLayout rows with
        | None ->
            { Entries = []
              Warnings = [ "LS CSV layout not recognized." ] }
        | Some layout ->
            let valueAt index row =
                if index >= 0 && index < List.length row then strip row.[index] else ""

            let entries =
                rows
                |> Array.skip layout.DataStartIndex
                |> Array.choose (fun row ->
                    let name = valueAt layout.NameIndex row
                    let address = valueAt layout.AddressIndex row
                    let dataType = layout.DataTypeIndex |> Option.map (fun i -> valueAt i row) |> Option.defaultValue "BIT"
                    let comment =
                        layout.CommentIndex
                        |> Option.map (fun i -> valueAt i row)
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                        |> Option.defaultValue name

                    if String.IsNullOrWhiteSpace name
                       || String.IsNullOrWhiteSpace address
                       || equalsOrdinalIgnoreCase "Variable" name
                       || not (isBitLike dataType) then
                        None
                    else
                        let direction = inferDirectionFromNameAndAddress vendor name address
                        Some
                            { Address = address
                              Name = name
                              Direction = direction
                              Comment = comment
                              Vendor = vendor })
                |> List.ofArray

            if entries.IsEmpty then
                warnings.Add("LS CSV parsed but no BIT/BOOL symbol entries were found.")
            { Entries = entries; Warnings = List.ofSeq warnings }

    /// vendor dispatch — 파일 확장자 또는 명시 vendor 로 parser 선택.
    let parse (vendor: Vendor) (text: string) : CsvParseResult =
        match vendor with
        | Mitsubishi -> parseMitsubishi text
        | XG5000     ->
            if text.TrimStart().StartsWith("<", StringComparison.Ordinal) then parseXG5000Xml text
            else parseLsCsv XG5000 text
        | XGB        -> parseLsCsv XGB text
        | XGK        -> parseLsCsv XGK text
        | AB ->
            { Entries = []
              Warnings = [ "AB parser 미구현 — DS1 mapper 에 AB 코드 없음. 후속 작업." ] }

    let private tryRegisterCodePages () =
        try Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
        with _ -> ()

    let private strictUtf8 = UTF8Encoding(false, true) :> Encoding

    let private tryDecode (encoding: Encoding) (bytes: byte[]) =
        try Some (encoding.GetString(bytes))
        with _ -> None

    let private decodeText (bytes: byte[]) =
        tryRegisterCodePages ()
        if bytes.Length >= 3 && bytes.[0] = 0xEFuy && bytes.[1] = 0xBBuy && bytes.[2] = 0xBFuy then
            Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
        elif bytes.Length >= 2 && bytes.[0] = 0xFFuy && bytes.[1] = 0xFEuy then
            Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        elif bytes.Length >= 2 && bytes.[0] = 0xFEuy && bytes.[1] = 0xFFuy then
            Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
        else
            match tryDecode strictUtf8 bytes with
            | Some text -> text
            | None ->
                let cp949 =
                    try Some (Encoding.GetEncoding(949, EncoderExceptionFallback(), DecoderExceptionFallback()))
                    with _ -> None
                match cp949 |> Option.bind (fun enc -> tryDecode enc bytes) with
                | Some text -> text
                | None -> Encoding.UTF8.GetString(bytes)

    /// 파일 path 받아서 텍스트 읽고 parse.
    /// BOM, strict UTF-8, CP949 순서로 디코딩한다.
    let parseFile (vendor: Vendor) (path: string) : CsvParseResult =
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
        use ms = new MemoryStream()
        stream.CopyTo(ms)
        let text = decodeText (ms.ToArray())
        parse vendor text
