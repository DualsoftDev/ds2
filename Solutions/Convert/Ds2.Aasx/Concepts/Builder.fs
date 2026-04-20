namespace Ds2.Aasx

open System
open System.IO
open System.IO.Compression
open AasCore.Aas3_1

module AasxConceptDescriptions =

    /// 임베디드 SequenceModel.aasx에서 ConceptDescription 로드
    let private loadFromEmbeddedAasx () : IConceptDescription list =
        try
            let assembly = Reflection.Assembly.GetExecutingAssembly()
            use stream = assembly.GetManifestResourceStream("Ds2.Aasx.Templates.SequenceModel.aasx")
            if isNull stream then
                printfn "Warning: Embedded SequenceModel.aasx not found"
                []
            else
                use mem = new MemoryStream()
                stream.CopyTo(mem)
                mem.Position <- 0L
                use archive = new ZipArchive(mem, ZipArchiveMode.Read)

                let relsEntry = archive.GetEntry("aasx/_rels/aasx-origin.rels")
                if relsEntry = null then []
                else
                    use relsStream = relsEntry.Open()
                    use relsReader = new IO.StreamReader(relsStream, Text.Encoding.UTF8)
                    let relsXml = relsReader.ReadToEnd()
                    let doc = Xml.XmlDocument()
                    doc.LoadXml(relsXml)
                    let nsm = Xml.XmlNamespaceManager(doc.NameTable)
                    nsm.AddNamespace("r", "http://schemas.openxmlformats.org/package/2006/relationships")
                    let node =
                        let n = doc.SelectSingleNode("//r:Relationship[@Type='http://www.admin-shell.io/aasx/relationships/aas-spec']", nsm)
                        if n = null then doc.SelectSingleNode("//r:Relationship[@Type='http://admin-shell.io/aasx/relationships/aas-spec']", nsm)
                        else n
                    if node = null then []
                    else
                        let aasPath = node.Attributes.["Target"].Value.TrimStart('/')
                        let aasEntry = archive.GetEntry(aasPath)
                        if aasEntry = null then []
                        else
                            use aasStream = aasEntry.Open()
                            use rdr = new IO.StreamReader(aasStream, Text.Encoding.UTF8)
                            use sr = new IO.StringReader(rdr.ReadToEnd())
                            use xmlReader = Xml.XmlReader.Create(sr)
                            xmlReader.MoveToContent() |> ignore
                            let env = Xmlization.Deserialize.EnvironmentFrom(xmlReader)
                            if env.ConceptDescriptions = null then []
                            else env.ConceptDescriptions |> Seq.cast<IConceptDescription> |> Seq.toList
        with ex ->
            printfn $"Warning: Failed to load SequenceModel.aasx: {ex.Message}"
            []

    let createAllConceptDescriptions () : ResizeArray<IConceptDescription> =
        ResizeArray<IConceptDescription>(loadFromEmbeddedAasx ())

    let mkSubmodelId (projectId: Guid) (offset: byte) : string =
        let bytes = projectId.ToByteArray()
        bytes.[15] <- bytes.[15] + offset
        Guid(bytes).ToString()
