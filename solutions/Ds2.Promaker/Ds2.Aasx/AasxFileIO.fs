module Ds2.Aasx.AasxFileIO

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Xml
open AasCore.Aas3_0

/// AASX ZIP에서 aasx-origin.rels를 파싱하여 AAS 파일 경로를 반환합니다.
let private resolveAasPath (archive: ZipArchive) : string option =
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

/// AASX ZIP에서 Environment를 읽어 반환합니다.
let readEnvironment (path: string) : Environment option =
    try
        use fileStream = new FileStream(path, FileMode.Open, FileAccess.Read)
        use archive = new ZipArchive(fileStream, ZipArchiveMode.Read)
        resolveAasPath archive
        |> Option.bind (fun aasPath ->
            let entry = archive.GetEntry(aasPath)
            if entry = null then None
            else
                use aasStream = entry.Open()
                if aasPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) then
                    use xmlReader = XmlReader.Create(aasStream)
                    Some (Xmlization.Deserialize.EnvironmentFrom(xmlReader))
                else
                    use rdr = new IO.StreamReader(aasStream, Encoding.UTF8)
                    let json = rdr.ReadToEnd()
                    let node = Text.Json.Nodes.JsonNode.Parse(json)
                    Some (Jsonization.Deserialize.EnvironmentFrom(node)))
    with _ -> None

// ZipArchiveMode.Create 에서는 이전 엔트리 스트림이 닫혀야 다음 엔트리를 열 수 있음.
// F# `use`는 함수 스코프 끝까지 유지되므로 별도 함수로 분리하여 즉시 dispose되도록 함.
let private writeTextEntry (archive: ZipArchive) (entryName: string) (content: string) =
    let entry = archive.CreateEntry(entryName)
    use writer = new StreamWriter(entry.Open(), Encoding.UTF8)
    writer.Write(content)

let private writeXmlEntry (archive: ZipArchive) (entryName: string) (env: Environment) =
    let entry = archive.CreateEntry(entryName)
    use stream = entry.Open()
    let settings = XmlWriterSettings(Indent = true, Encoding = Encoding.UTF8)
    use xmlWriter = XmlWriter.Create(stream, settings)
    Xmlization.Serialize.To(env, xmlWriter)

/// Environment를 AASX ZIP으로 저장합니다 (XML 직렬화).
let writeEnvironment (env: Environment) (path: string) : unit =
    use fileStream = new FileStream(path, FileMode.Create)
    use archive = new ZipArchive(fileStream, ZipArchiveMode.Create)

    writeTextEntry archive "[Content_Types].xml" """<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
  <Default Extension="xml" ContentType="text/xml" />
  <Override PartName="/aasx/aasx-origin" ContentType="text/plain" />
</Types>"""

    writeTextEntry archive "_rels/.rels" """<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://www.admin-shell.io/aasx/relationships/aasx-origin" Target="/aasx/aasx-origin" Id="R320e13957d794f91" />
</Relationships>"""

    writeTextEntry archive "aasx/aasx-origin" "Intentionally empty."

    writeTextEntry archive "aasx/_rels/aasx-origin.rels" """<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://www.admin-shell.io/aasx/relationships/aas-spec" Target="/aasx/aas/aas.aas.xml" Id="R40528201d6544e91" />
</Relationships>"""

    writeXmlEntry archive "aasx/aas/aas.aas.xml" env
