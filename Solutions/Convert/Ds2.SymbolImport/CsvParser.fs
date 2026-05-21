namespace Ds2.SymbolImport

open System
open System.IO
open System.Text
open System.Text.RegularExpressions

/// <summary>vendor 별 CSV (또는 XML) 심볼 테이블 → SymbolEntry 변환.
/// DS1 mapper 의 MX/CSVParser.fs / LSE/ConvertLSE.Xml.fs 룰 의미를 ds2 도메인으로.</summary>
module CsvParser =

    /// 주소 prefix → 방향 추론. vendor 별 prefix 규칙:
    ///   Mitsubishi: X/Y/M (input/output/memory)
    ///   LS XG5000:  %IX / %QX / %MX (또는 %IW / %QW / %MW) — 정수 비트/워드 동일 prefix 분류
    let private inferDirection (vendor: Vendor) (address: string) : SymbolDirection =
        let upper = if isNull address then "" else address.ToUpperInvariant().TrimStart([| '%' |])
        match vendor, upper with
        | _, s when s.StartsWith("X") || s.StartsWith("I") -> SymbolDirection.Input
        | _, s when s.StartsWith("Y") || s.StartsWith("Q") -> SymbolDirection.Output
        | _, s when s.StartsWith("M") -> SymbolDirection.Memory
        | _ -> SymbolDirection.UnknownDir

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
                        let strip (s: string) = s.Trim().Trim('"').Trim()
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
                        Direction = inferDirection XG5000 address
                        Comment = comment
                        Vendor = XG5000
                    }
        with ex ->
            warnings.Add(sprintf "XG5000 XML parse 실패: %s" ex.Message)
        { Entries = List.ofSeq entries; Warnings = List.ofSeq warnings }

    /// vendor dispatch — 파일 확장자 또는 명시 vendor 로 parser 선택.
    let parse (vendor: Vendor) (text: string) : CsvParseResult =
        match vendor with
        | Mitsubishi -> parseMitsubishi text
        | XG5000     -> parseXG5000Xml text
        | AB ->
            { Entries = []
              Warnings = [ "AB parser 미구현 — DS1 mapper 에 AB 코드 없음. 후속 작업." ] }

    /// 파일 path 받아서 텍스트 읽고 parse.
    /// UTF-16 LE BOM 자동 감지 — Mitsubishi dump 가 보통 UTF-16 LE.
    let parseFile (vendor: Vendor) (path: string) : CsvParseResult =
        // StreamReader detectEncodingFromByteOrderMarks=true 가 BOM 보고 UTF-8/16/32 자동 감지.
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
        use reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks = true)
        let text = reader.ReadToEnd()
        parse vendor text
