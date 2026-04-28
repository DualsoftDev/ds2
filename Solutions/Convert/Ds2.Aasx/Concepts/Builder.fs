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

    /// ConceptDescriptionInfo → AAS IConceptDescription (DisplayName/Description 다국어 — en/de/ko)
    let private toConceptDescription (info: AasxConceptDescriptionCatalog.ConceptDescriptionInfo) : IConceptDescription =
        let cd = ConceptDescription(id = info.Id)
        cd.IdShort <- info.ShortName
        let names =
            ResizeArray<ILangStringNameType>([
                LangStringNameType("en", info.PreferredNameEn) :> ILangStringNameType
                LangStringNameType("de", info.PreferredNameDe) :> ILangStringNameType
            ])
        if not (System.String.IsNullOrEmpty info.PreferredNameKr) then
            names.Add(LangStringNameType("ko", info.PreferredNameKr) :> ILangStringNameType)
        cd.DisplayName <- names
        let descs =
            ResizeArray<ILangStringTextType>([
                LangStringTextType("en", info.DefinitionEn) :> ILangStringTextType
                LangStringTextType("de", info.DefinitionDe) :> ILangStringTextType
            ])
        if not (System.String.IsNullOrEmpty info.DefinitionKr) then
            descs.Add(LangStringTextType("ko", info.DefinitionKr) :> ILangStringTextType)
        cd.Description <- descs
        cd :> IConceptDescription

    let createAllConceptDescriptions () : ResizeArray<IConceptDescription> =
        let result = ResizeArray<IConceptDescription>()
        // 1) 외부 표준 CD (Nameplate, Documentation 등) — 임베디드 IDTA AASX 템플릿에서
        result.AddRange(loadFromEmbeddedAasx ())
        // 2) ds2 자체 발급 CD (Entities + Submodel + Simulation)
        let existingIds = result |> Seq.choose (fun cd -> Option.ofObj cd.Id) |> Set.ofSeq
        for info in AasxConceptDescriptionCatalog.allConceptDescriptionInfos do
            if not (existingIds.Contains info.Id) then
                result.Add(toConceptDescription info)
        result

    let mkSubmodelId (projectId: Guid) (offset: byte) : string =
        let bytes = projectId.ToByteArray()
        bytes.[15] <- bytes.[15] + offset
        Guid(bytes).ToString()
