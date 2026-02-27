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

/// Environment를 AASX ZIP으로 저장합니다 (XML 직렬화).
let writeEnvironment (env: Environment) (path: string) : unit =
    use fileStream = new FileStream(path, FileMode.Create)
    use archive = new ZipArchive(fileStream, ZipArchiveMode.Create)

    // 1. [Content_Types].xml
    let e1 = archive.CreateEntry("[Content_Types].xml")
    use w1 = new StreamWriter(e1.Open(), Encoding.UTF8)
    w1.Write("""<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
  <Default Extension="xml" ContentType="text/xml" />
  <Override PartName="/aasx/aasx-origin" ContentType="text/plain" />
</Types>""")
    w1.Flush()

    // 2. _rels/.rels
    let e2 = archive.CreateEntry("_rels/.rels")
    use w2 = new StreamWriter(e2.Open(), Encoding.UTF8)
    w2.Write("""<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://www.admin-shell.io/aasx/relationships/aasx-origin" Target="/aasx/aasx-origin" Id="R320e13957d794f91" />
</Relationships>""")
    w2.Flush()

    // 3. aasx/aasx-origin (빈 마커)
    let e3 = archive.CreateEntry("aasx/aasx-origin")
    use w3 = new StreamWriter(e3.Open(), Encoding.UTF8)
    w3.Write("Intentionally empty.")
    w3.Flush()

    // 4. aasx/_rels/aasx-origin.rels
    let e4 = archive.CreateEntry("aasx/_rels/aasx-origin.rels")
    use w4 = new StreamWriter(e4.Open(), Encoding.UTF8)
    w4.Write("""<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://www.admin-shell.io/aasx/relationships/aas-spec" Target="/aasx/aas/aas.aas.xml" Id="R40528201d6544e91" />
</Relationships>""")
    w4.Flush()

    // 5. aasx/aas/aas.aas.xml
    let e5 = archive.CreateEntry("aasx/aas/aas.aas.xml")
    use stream5 = e5.Open()
    let settings = XmlWriterSettings(Indent = true, Encoding = Encoding.UTF8)
    use xmlWriter = XmlWriter.Create(stream5, settings)
    Xmlization.Serialize.To(env, xmlWriter)
    xmlWriter.Flush()
