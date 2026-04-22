module Ds2.Aasx.AasxFileIO

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Xml
open AasCore.Aas3_1

let private log = SimpleLog.create "Ds2.Aasx.AasxFileIO"

type AasxThumbnail =
    { EntryName: string
      ContentType: string
      Bytes: byte[] }

/// AAS XML 네임스페이스 정규화 (모든 버전 → 3.1로 변환)
let private normalizeAasXml (xml: string) : string =
    xml
        // AAS 버전 네임스페이스 정규화 (1.0, 2.0, 3.0 → 3.1)
        .Replace("http://www.admin-shell.io/aas/1/0", "https://admin-shell.io/aas/3/1")
        .Replace("http://admin-shell.io/aas/1/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://www.admin-shell.io/aas/1/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://admin-shell.io/aas/1/0", "https://admin-shell.io/aas/3/1")
        .Replace("http://www.admin-shell.io/aas/2/0", "https://admin-shell.io/aas/3/1")
        .Replace("http://admin-shell.io/aas/2/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://www.admin-shell.io/aas/2/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://admin-shell.io/aas/2/0", "https://admin-shell.io/aas/3/1")
        .Replace("http://www.admin-shell.io/aas/3/0", "https://admin-shell.io/aas/3/1")
        .Replace("http://admin-shell.io/aas/3/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://www.admin-shell.io/aas/3/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://admin-shell.io/aas/3/0", "https://admin-shell.io/aas/3/1")
        // HTTP → HTTPS 정규화 (3.1)
        .Replace("http://admin-shell.io/aas/3/1", "https://admin-shell.io/aas/3/1")
        .Replace("http://www.admin-shell.io/aas/3/1", "https://admin-shell.io/aas/3/1")
        // IEC 버전 네임스페이스 정규화 (3.1로 변환)
        .Replace("http://www.admin-shell.io/IEC61360/1/0", "https://admin-shell.io/aas/3/1")
        .Replace("http://admin-shell.io/IEC61360/1/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://www.admin-shell.io/IEC61360/1/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://admin-shell.io/IEC61360/1/0", "https://admin-shell.io/aas/3/1")
        .Replace("http://www.admin-shell.io/IEC61360/2/0", "https://admin-shell.io/aas/3/1")
        .Replace("http://admin-shell.io/IEC61360/2/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://www.admin-shell.io/IEC61360/2/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://admin-shell.io/IEC61360/2/0", "https://admin-shell.io/aas/3/1")
        .Replace("https://admin-shell.io/aas/3/0/IEC61360", "https://admin-shell.io/aas/3/1")
        .Replace("http://admin-shell.io/aas/3/0/IEC61360", "https://admin-shell.io/aas/3/1")
        // 루트 엘리먼트 이름 정규화 (aasenv, aasEnv → environment)
        .Replace("<aasenv ", "<environment ")
        .Replace("</aasenv>", "</environment>")
        .Replace("<aasEnv ", "<environment ")
        .Replace("</aasEnv>", "</environment>")

/// AAS 3.1 엄격 스키마 위반 보정:
/// <embeddedDataSpecification> 에 <dataSpecification> 참조가 누락된 경우 기본 IEC61360 참조를 주입.
/// (SICK, 일부 카탈로그 파일처럼 dataSpecificationContent만 있는 경우를 허용)
let private fixMissingDataSpecifications (xml: string) : string =
    try
        let doc = System.Xml.Linq.XDocument.Parse(xml)
        let ns = doc.Root.GetDefaultNamespace()
        let edsName         = ns + "embeddedDataSpecification"
        let dataSpecName    = ns + "dataSpecification"
        let contentName     = ns + "dataSpecificationContent"
        let typeName        = ns + "type"
        let keysName        = ns + "keys"
        let keyName         = ns + "key"
        let valueName       = ns + "value"

        let mutable fixedCount = 0
        for eds in doc.Descendants(edsName) |> Seq.toList do
            if isNull (eds.Element(dataSpecName)) then
                let dataSpec =
                    System.Xml.Linq.XElement(dataSpecName,
                        System.Xml.Linq.XElement(typeName, "ExternalReference"),
                        System.Xml.Linq.XElement(keysName,
                            System.Xml.Linq.XElement(keyName,
                                System.Xml.Linq.XElement(typeName, "GlobalReference"),
                                System.Xml.Linq.XElement(valueName,
                                    "https://admin-shell.io/aas/3/0/DataSpecificationIEC61360"))))
                // dataSpecification은 dataSpecificationContent 앞에 와야 함
                let contentEl = eds.Element(contentName)
                if not (isNull contentEl) then
                    contentEl.AddBeforeSelf(dataSpec)
                else
                    eds.AddFirst(dataSpec)
                fixedCount <- fixedCount + 1

        if fixedCount > 0 then
            log.Info($"AAS XML: 누락된 dataSpecification 참조 {fixedCount}개 보정")
        doc.ToString()
    with ex ->
        log.Warn($"dataSpecification 보정 실패 (원본 사용): {ex.Message}")
        xml

/// 네임스페이스 버전 감지
let private detectAasVersion (xml: string) : string option =
    let patterns = [
        ("1.0", "admin-shell.io/aas/1/0")
        ("2.0", "admin-shell.io/aas/2/0")
        ("3.0", "admin-shell.io/aas/3/0")
        ("3.1", "admin-shell.io/aas/3/1")
    ]
    patterns
    |> List.tryPick (fun (version, pattern) ->
        if xml.Contains(pattern) then Some version else None)

/// AASX ZIP의 모든 엔트리를 읽어서 Dictionary로 반환
let internal readAllZipEntries (path: string) : System.Collections.Generic.Dictionary<string, byte[]> option =
    try
        use fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        use archive = new ZipArchive(fileStream, ZipArchiveMode.Read)
        let entries = System.Collections.Generic.Dictionary<string, byte[]>()

        for entry in archive.Entries do
            use stream = entry.Open()
            use memStream = new IO.MemoryStream()
            stream.CopyTo(memStream)
            entries.[entry.FullName] <- memStream.ToArray()

        Some entries
    with ex ->
        log.Warn($"ZIP 엔트리 읽기 실패: {path} - {ex.Message}")
        None

/// ZipArchive에서 Environment를 읽어 반환 (핵심 로직, 버전 정규화 포함)
let private readEnvironmentFromArchive (archive: ZipArchive) : Result<Environment, string> =
    let resolveAasPath () =
        let relsEntry = archive.GetEntry("aasx/_rels/aasx-origin.rels")
        if relsEntry = null then Error "AASX 파일 구조 오류: aasx/_rels/aasx-origin.rels 파일을 찾을 수 없습니다."
        else
            use stream = relsEntry.Open()
            use reader = new IO.StreamReader(stream, Encoding.UTF8)
            let xml = reader.ReadToEnd()
            let doc = Xml.XmlDocument()
            doc.LoadXml(xml)
            let nsm = Xml.XmlNamespaceManager(doc.NameTable)
            nsm.AddNamespace("r", "http://schemas.openxmlformats.org/package/2006/relationships")

            // www 포함/미포함 두 가지 URL 패턴 모두 시도
            let node =
                doc.SelectSingleNode(
                    "//r:Relationship[@Type='http://www.admin-shell.io/aasx/relationships/aas-spec']",
                    nsm)
            let nodeCompat =
                if node = null then
                    doc.SelectSingleNode(
                        "//r:Relationship[@Type='http://admin-shell.io/aasx/relationships/aas-spec']",
                        nsm)
                else node

            if nodeCompat = null then
                Error "AASX 파일 구조 오류: AAS 스펙 관계를 찾을 수 없습니다."
            else
                let target = nodeCompat.Attributes.["Target"].Value.TrimStart('/')
                Ok target

    match resolveAasPath () with
    | Error msg -> Error msg
    | Ok aasPath ->
        let entry = archive.GetEntry(aasPath)
        if entry = null then
            Error $"AASX 파일 구조 오류: {aasPath} 파일을 찾을 수 없습니다."
        else
            try
                use aasStream = entry.Open()
                if aasPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) then
                    // XML: 네임스페이스 정규화 후 역직렬화
                    use rdr = new IO.StreamReader(aasStream, Encoding.UTF8)
                    let xml = rdr.ReadToEnd()
                    let detectedVersion = detectAasVersion xml
                    let normalizedXml =
                        xml
                        |> normalizeAasXml
                        |> fixMissingDataSpecifications
                    use stringReader = new IO.StringReader(normalizedXml)
                    use xmlReader = XmlReader.Create(stringReader)
                    xmlReader.MoveToContent() |> ignore
                    let env = Xmlization.Deserialize.EnvironmentFrom(xmlReader)

                    match detectedVersion with
                    | Some v when v <> "3.1" ->
                        log.Info($"AAS {v} 파일을 3.1 형식으로 변환하여 읽었습니다.")
                    | _ -> ()

                    Ok env
                else
                    // JSON
                    use rdr = new IO.StreamReader(aasStream, Encoding.UTF8)
                    let json = rdr.ReadToEnd()
                    let node = Text.Json.Nodes.JsonNode.Parse(json)
                    let env = Jsonization.Deserialize.EnvironmentFrom(node)
                    Ok env
            with
            | :? AasCore.Aas3_1.Xmlization.Exception as ex ->
                let detectedVersion =
                    try
                        use rdr2 = new IO.StreamReader(entry.Open(), Encoding.UTF8)
                        detectAasVersion (rdr2.ReadToEnd())
                    with _ -> None

                let versionMsg =
                    match detectedVersion with
                    | Some v -> $"감지된 AAS 버전: {v}"
                    | None -> "AAS 버전을 감지할 수 없습니다."

                Error $"AAS XML 역직렬화 실패:\n\n{ex.Message}\n\n{versionMsg}\n\n파일이 손상되었거나 지원하지 않는 형식일 수 있습니다."
            | ex ->
                Error $"파일 읽기 실패:\n\n{ex.Message}"

/// Stream에서 AASX를 읽어 Environment를 반환합니다 (버전 정규화 포함).
/// 호출자가 stream 수명을 관리합니다 (ZipArchive는 leaveOpen=true).
let readEnvironmentFromStreamWithError (stream: Stream) : Result<Environment, string> =
    try
        use archive = new ZipArchive(stream, ZipArchiveMode.Read, true)
        readEnvironmentFromArchive archive
    with ex ->
        Error $"AASX 스트림 읽기 실패:\n\n{ex.Message}"

/// AASX ZIP에서 Environment를 읽어 반환합니다.
/// 실패 시 Result로 에러 메시지 반환
let readEnvironmentWithError (path: string) : Result<Environment, string> =
    try
        use fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        use archive = new ZipArchive(fileStream, ZipArchiveMode.Read)
        readEnvironmentFromArchive archive
    with
    | :? FileNotFoundException ->
        Error $"파일을 찾을 수 없습니다:\n\n{path}"
    | :? UnauthorizedAccessException ->
        Error $"파일 접근 권한이 없습니다:\n\n{path}"
    | ex ->
        Error $"예상치 못한 오류:\n\n{ex.Message}"

/// AASX ZIP에서 Environment를 읽어 반환합니다 (레거시 호환)
let readEnvironment (path: string) : Environment option =
    match readEnvironmentWithError path with
    | Ok env -> Some env
    | Error msg ->
        log.Warn($"AASX 읽기 실패: {msg}")
        None

/// AASX ZIP에서 Environment를 읽어 반환합니다.
/// 실패 시 예외를 발생시켜 호출자가 직접 처리할 수 있게 합니다.
let readEnvironmentOrRaise (path: string) : Environment =
    match readEnvironmentWithError path with
    | Ok env -> env
    | Error msg -> raise (InvalidDataException(msg))

// ZipArchiveMode.Create 에서는 이전 엔트리 스트림이 닫혀야 다음 엔트리를 열 수 있음.
// F# `use`는 함수 스코프 끝까지 유지되므로 별도 함수로 분리하여 즉시 dispose되도록 함.
let private writeTextEntry (archive: ZipArchive) (entryName: string) (content: string) =
    let entry = archive.CreateEntry(entryName)
    use writer = new StreamWriter(entry.Open(), Encoding.UTF8)
    writer.Write(content)

let private writeBinaryEntry (archive: ZipArchive) (entryName: string) (content: byte[]) =
    let entry = archive.CreateEntry(entryName)
    use stream = entry.Open()
    stream.Write(content, 0, content.Length)

let private buildThumbnailContentTypeXml (thumbnail: AasxThumbnail option) =
    match thumbnail with
    | Some thumb when thumb.EntryName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ->
        "\n  <Default Extension=\"png\" ContentType=\"image/png\" />"
    | Some thumb when thumb.EntryName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || thumb.EntryName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ->
        "\n  <Default Extension=\"jpg\" ContentType=\"image/jpeg\" />"
    | Some thumb ->
        $"\n  <Override PartName=\"/{thumb.EntryName}\" ContentType=\"{thumb.ContentType}\" />"
    | None ->
        ""

/// 헬퍼: ZIP 아카이브에 Environment를 작성
let private writeEnvironmentToArchive (archive: ZipArchive) (env: Environment) (thumbnail: AasxThumbnail option) (preservedEntries: System.Collections.Generic.Dictionary<string, byte[]> option) : unit =
    // 보존된 엔트리가 있으면 먼저 복사 (교체할 파일은 제외)
    // 썸네일도 교체 대상에 추가 (중복 방지)
    let thumbnailEntryName = thumbnail |> Option.map (fun t -> t.EntryName)
    let filesToReplaceList = [
        "[Content_Types].xml"
        "_rels/.rels"
        "aasx/aasx-origin"
        "aasx/_rels/aasx-origin.rels"
        "aasx/aas/aas.aas.xml"
    ]
    let filesToReplace =
        match thumbnailEntryName with
        | Some name -> Set.ofList (name :: filesToReplaceList)
        | None -> Set.ofList filesToReplaceList

    match preservedEntries with
    | Some entries ->
        for kvp in entries do
            if not (filesToReplace.Contains(kvp.Key)) then
                writeBinaryEntry archive kvp.Key kvp.Value
    | None -> ()

    let thumbnailContentTypeXml = buildThumbnailContentTypeXml thumbnail

    let thumbnailRelationship =
        thumbnail
        |> Option.map (fun thumb ->
            $"\n  <Relationship Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail\" Target=\"/{thumb.EntryName}\" Id=\"Rthumbnail\" />")
        |> Option.defaultValue ""

    writeTextEntry archive "[Content_Types].xml" ($"""<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
  <Default Extension="xml" ContentType="text/xml" />
  <Override PartName="/aasx/aasx-origin" ContentType="text/plain" />
  <Override PartName="/aasx/aas/aas.aas.xml" ContentType="text/xml" />{thumbnailContentTypeXml}
</Types>""")

    let rootRelationshipsXml =
        ($"""<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://www.admin-shell.io/aasx/relationships/aasx-origin" Target="/aasx/aasx-origin" Id="R320e13957d794f91" />
</Relationships>""")
            .Replace("</Relationships>", $"{thumbnailRelationship}\n</Relationships>")

    writeTextEntry archive "_rels/.rels" rootRelationshipsXml

    writeTextEntry archive "aasx/aasx-origin" "Intentionally empty."

    writeTextEntry archive "aasx/_rels/aasx-origin.rels" """<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://www.admin-shell.io/aasx/relationships/aas-spec" Target="/aasx/aas/aas.aas.xml" Id="R40528201d6544e91" />
</Relationships>"""

    let writeXmlEntry (entryName: string) =
        let entry = archive.CreateEntry(entryName)
        use stream = entry.Open()
        let settings = XmlWriterSettings(Indent = true, Encoding = Encoding.UTF8)
        use xmlWriter = XmlWriter.Create(stream, settings)
        Xmlization.Serialize.To(env, xmlWriter)
    writeXmlEntry "aasx/aas/aas.aas.xml"

    thumbnail
    |> Option.iter (fun thumb -> writeBinaryEntry archive thumb.EntryName thumb.Bytes)

/// Environment를 AASX ZIP으로 저장합니다 (XML 직렬화).
/// preservedEntries가 제공되면 기존 엔트리를 복사하고 필요한 파일만 교체합니다.
let writeEnvironment (env: Environment) (path: string) (thumbnail: AasxThumbnail option) (preservedEntries: System.Collections.Generic.Dictionary<string, byte[]> option) : unit =
    let tempPath = path + ".tmp"

    try
        // 임시 파일에 ZIP 작성
        do
            use fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)
            use archive = new ZipArchive(fileStream, ZipArchiveMode.Create)
            writeEnvironmentToArchive archive env thumbnail preservedEntries

        // 파일 핸들 완전 해제
        System.GC.Collect()
        System.GC.WaitForPendingFinalizers()

        // 파일 잠금 방지: 임시 파일을 원본으로 복사 (재시도 포함)
        let rec replaceWithRetry (attempt: int) =
            try
                if File.Exists(path) then
                    File.Copy(tempPath, path, overwrite = true)
                    try File.Delete(tempPath) with _ -> ()
                else
                    File.Move(tempPath, path)
            with
            | :? UnauthorizedAccessException | :? IOException when attempt < 10 ->
                System.Threading.Thread.Sleep(500 * attempt)
                replaceWithRetry (attempt + 1)

        replaceWithRetry 1

    with ex ->
        if File.Exists(tempPath) then
            try File.Delete(tempPath) with _ -> ()
        reraise()
