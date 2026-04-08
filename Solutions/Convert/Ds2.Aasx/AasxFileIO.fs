module Ds2.Aasx.AasxFileIO

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Xml
open AasCore.Aas3_0
open log4net

let private log = LogManager.GetLogger("Ds2.Aasx.AasxFileIO")

type AasxThumbnail =
    { EntryName: string
      ContentType: string
      Bytes: byte[] }

/// AASX ZIP에서 Environment를 읽어 반환합니다.
let readEnvironment (path: string) : Environment option =
    try
        use fileStream = new FileStream(path, FileMode.Open, FileAccess.Read)
        use archive = new ZipArchive(fileStream, ZipArchiveMode.Read)
        let resolveAasPath () =
            let relsEntry = archive.GetEntry("aasx/_rels/aasx-origin.rels")
            if relsEntry = null then None
            else
                use stream = relsEntry.Open()
                use reader = new IO.StreamReader(stream, Encoding.UTF8)
                let xml = reader.ReadToEnd()
                let doc = Xml.XmlDocument()
                doc.LoadXml(xml)
                let nsm = Xml.XmlNamespaceManager(doc.NameTable)
                nsm.AddNamespace("r", "http://schemas.openxmlformats.org/package/2006/relationships")
                let node =
                    doc.SelectSingleNode(
                        "//r:Relationship[@Type='http://www.admin-shell.io/aasx/relationships/aas-spec']",
                        nsm)
                if node = null then None
                else
                    let target = node.Attributes.["Target"].Value.TrimStart('/')
                    Some target
        resolveAasPath ()
        |> Option.bind (fun aasPath ->
            let entry = archive.GetEntry(aasPath)
            if entry = null then None
            else
                use aasStream = entry.Open()
                if aasPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) then
                    use xmlReader = XmlReader.Create(aasStream)
                    xmlReader.MoveToContent() |> ignore
                    Some (Xmlization.Deserialize.EnvironmentFrom(xmlReader))
                else
                    use rdr = new IO.StreamReader(aasStream, Encoding.UTF8)
                    let json = rdr.ReadToEnd()
                    let node = Text.Json.Nodes.JsonNode.Parse(json)
                    Some (Jsonization.Deserialize.EnvironmentFrom(node)))
    with ex ->
        log.Warn("AASX 읽기 실패", ex)
        None

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

/// Environment를 AASX ZIP으로 저장합니다 (XML 직렬화).
let writeEnvironment (env: Environment) (path: string) (thumbnail: AasxThumbnail option) : unit =
    use fileStream = new FileStream(path, FileMode.Create)
    use archive = new ZipArchive(fileStream, ZipArchiveMode.Create)

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
