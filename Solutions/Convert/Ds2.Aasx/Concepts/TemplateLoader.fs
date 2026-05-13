namespace Ds2.Aasx

open System
open System.IO
open System.IO.Compression
open AasCore.Aas3_1

/// IDTA published 템플릿 .aasx 파일을 임베디드 리소스로부터 런타임 로드.
///
/// 코드로 SM 구조를 매번 직조하는 대신, admin-shell-io/submodel-templates 에서 받은
/// 원본 .aasx 를 무수정 임베드하고 본 모듈로 SM/CD 를 추출한다. 새 IDTA 버전이 publish 되면
/// 임베디드 .aasx 만 교체.
///
/// 임베디드 리소스 prefix: `Ds2.Aasx.Templates.<filename>.aasx` (fsproj LogicalName 참조)
module AasxTemplateLoader =

    /// 임베디드 리소스 → byte[] 또는 None.
    let private readResource (resourceName: string) : byte[] option =
        let asm = System.Reflection.Assembly.GetExecutingAssembly()
        use s = asm.GetManifestResourceStream(resourceName)
        if isNull s then None
        else
            use mem = new MemoryStream()
            s.CopyTo(mem)
            Some (mem.ToArray())

    /// AASX zip 안의 main .aas.xml 경로를 _rels 에서 찾는다.
    let private findAasSpecPath (archive: ZipArchive) : string option =
        let relsEntry = archive.GetEntry("aasx/_rels/aasx-origin.rels")
        if isNull relsEntry then None
        else
            use rs = relsEntry.Open()
            use rd = new StreamReader(rs, Text.Encoding.UTF8)
            let xml = rd.ReadToEnd()
            let doc = Xml.XmlDocument()
            doc.LoadXml(xml)
            let nsm = Xml.XmlNamespaceManager(doc.NameTable)
            nsm.AddNamespace("r", "http://schemas.openxmlformats.org/package/2006/relationships")
            let pickType t =
                let q = sprintf "//r:Relationship[@Type='%s']" t
                let n = doc.SelectSingleNode(q, nsm)
                if isNull n then None
                else
                    let attr = n.Attributes.["Target"]
                    if isNull attr then None
                    else Some (attr.Value.TrimStart('/'))
            pickType "http://www.admin-shell.io/aasx/relationships/aas-spec"
            |> Option.orElseWith (fun () ->
                pickType "http://admin-shell.io/aasx/relationships/aas-spec")

    /// 임베디드 .aasx 의 Environment 디시리얼라이즈. 실패 시 None.
    let private tryReadEnvironment (resourceName: string) : Environment option =
        match readResource resourceName with
        | None ->
            eprintfn $"[TemplateLoader] resource not found: {resourceName}"
            None
        | Some bytes ->
            try
                use mem = new MemoryStream(bytes)
                use archive = new ZipArchive(mem, ZipArchiveMode.Read)
                match findAasSpecPath archive with
                | None ->
                    eprintfn $"[TemplateLoader] aas-spec rels not found in {resourceName}"
                    None
                | Some path ->
                    let entry = archive.GetEntry(path)
                    if isNull entry then
                        eprintfn $"[TemplateLoader] entry not found: {path} in {resourceName}"
                        None
                    else
                        use es = entry.Open()
                        use rd = new StreamReader(es, Text.Encoding.UTF8)
                        let xmlText = rd.ReadToEnd()
                        // IDTA published 템플릿은 AAS V3.0 namespace 사용. AasCore.Aas3_1 라이브러리는
                        // V3.1 namespace 만 인식 → 호환을 위해 root xmlns 만 3/0 → 3/1 로 치환.
                        // (V3.1 은 V3.0 backward-compatible. semanticId 등 다른 "3/0" 문자열은 element value 안이라 영향 없음.)
                        let normalized =
                            xmlText.Replace(
                                "xmlns=\"https://admin-shell.io/aas/3/0\"",
                                "xmlns=\"https://admin-shell.io/aas/3/1\"")
                        use sr = new StringReader(normalized)
                        use xr = Xml.XmlReader.Create(sr)
                        xr.MoveToContent() |> ignore
                        Some (Xmlization.Deserialize.EnvironmentFrom(xr))
            with ex ->
                eprintfn $"[TemplateLoader] {resourceName} 디시리얼라이즈 실패: {ex.GetType().Name}: {ex.Message}"
                None

    /// 임베디드 템플릿에서 모든 Submodel 추출.
    let loadAllSubmodels (resourceName: string) : ISubmodel list =
        match tryReadEnvironment resourceName with
        | None -> []
        | Some env when isNull env.Submodels -> []
        | Some env -> env.Submodels |> Seq.cast<ISubmodel> |> Seq.toList

    /// 임베디드 템플릿에서 idShort 일치하는 Submodel 1개. 없으면 None.
    let tryLoadSubmodel (resourceName: string) (submodelIdShort: string) : ISubmodel option =
        loadAllSubmodels resourceName
        |> List.tryFind (fun sm -> sm.IdShort = submodelIdShort)

    /// 임베디드 템플릿의 모든 ConceptDescription.
    let loadAllConceptDescriptions (resourceName: string) : IConceptDescription list =
        match tryReadEnvironment resourceName with
        | None -> []
        | Some env when isNull env.ConceptDescriptions -> []
        | Some env -> env.ConceptDescriptions |> Seq.cast<IConceptDescription> |> Seq.toList

    // ─── 표준 IDTA 템플릿 리소스 이름 (fsproj LogicalName 과 일치) ──────────
    [<Literal>]
    let NameplateResource = "Ds2.Aasx.Templates.Nameplate.aasx"

    [<Literal>]
    let HandoverDocumentationResource = "Ds2.Aasx.Templates.HandoverDocumentation.aasx"

    [<Literal>]
    let TechnicalDataResource = "Ds2.Aasx.Templates.TechnicalData.aasx"

    [<Literal>]
    let SequenceModelResource = "Ds2.Aasx.Templates.SequenceModel.aasx"

    // ═══════════════════════════════════════════════════════════════════════
    // 파일시스템 경로 기반 로드 — 사용자 지정 폴더의 .aasx 도 동일 API 로 처리.
    // 임베디드 리소스는 ds2 가 내장한 IDTA 표준 SM (Nameplate/HD/TD).
    // 파일 경로는 사용자가 추가하는 도메인 SM (OperationData / 사내 표준 등).
    // ═══════════════════════════════════════════════════════════════════════

    /// 단일 .aasx 파일에서 Environment 로드. 실패 시 None.
    let private tryReadEnvironmentFromFile (filePath: string) : Environment option =
        if not (System.IO.File.Exists filePath) then
            eprintfn $"[TemplateLoader] file not found: {filePath}"
            None
        else
            try
                use fs = System.IO.File.OpenRead(filePath)
                use mem = new MemoryStream()
                fs.CopyTo(mem)
                mem.Position <- 0L
                use archive = new ZipArchive(mem, ZipArchiveMode.Read)
                match findAasSpecPath archive with
                | None ->
                    eprintfn $"[TemplateLoader] aas-spec rels not found in {filePath}"
                    None
                | Some path ->
                    let entry = archive.GetEntry(path)
                    if isNull entry then None
                    else
                        use es = entry.Open()
                        use rd = new StreamReader(es, Text.Encoding.UTF8)
                        let xmlText = rd.ReadToEnd()
                        // V3.0 → V3.1 namespace 호환 (임베디드 로드와 동일)
                        let normalized =
                            xmlText.Replace(
                                "xmlns=\"https://admin-shell.io/aas/3/0\"",
                                "xmlns=\"https://admin-shell.io/aas/3/1\"")
                        use sr = new StringReader(normalized)
                        use xr = Xml.XmlReader.Create(sr)
                        xr.MoveToContent() |> ignore
                        Some (Xmlization.Deserialize.EnvironmentFrom(xr))
            with ex ->
                eprintfn $"[TemplateLoader] {filePath} 디시리얼라이즈 실패: {ex.GetType().Name}: {ex.Message}"
                None

    /// 단일 .aasx 파일의 모든 Submodel 추출.
    let loadAllSubmodelsFromFile (filePath: string) : ISubmodel list =
        match tryReadEnvironmentFromFile filePath with
        | None -> []
        | Some env when isNull env.Submodels -> []
        | Some env -> env.Submodels |> Seq.cast<ISubmodel> |> Seq.toList

    /// 단일 .aasx 파일의 모든 ConceptDescription 추출.
    let loadAllConceptDescriptionsFromFile (filePath: string) : IConceptDescription list =
        match tryReadEnvironmentFromFile filePath with
        | None -> []
        | Some env when isNull env.ConceptDescriptions -> []
        | Some env -> env.ConceptDescriptions |> Seq.cast<IConceptDescription> |> Seq.toList

    /// 폴더 안의 모든 *.aasx 파일을 찾아 (filename, [Submodel list]) 튜플 리스트로 반환.
    /// 폴더가 없거나 .aasx 가 없으면 빈 리스트. 하위 폴더는 검색하지 않음.
    let scanFolderSubmodels (folderPath: string) : (string * ISubmodel list) list =
        if String.IsNullOrEmpty folderPath || not (System.IO.Directory.Exists folderPath) then []
        else
            try
                System.IO.Directory.GetFiles(folderPath, "*.aasx")
                |> Array.sort
                |> Array.toList
                |> List.map (fun fp ->
                    System.IO.Path.GetFileName fp,
                    loadAllSubmodelsFromFile fp)
                |> List.filter (fun (_, sms) -> not sms.IsEmpty)
            with ex ->
                eprintfn $"[TemplateLoader] 폴더 스캔 실패 {folderPath}: {ex.Message}"
                []

    /// 폴더 안의 모든 .aasx 의 ConceptDescription 도 함께 수집 (CD 통합용).
    let scanFolderConceptDescriptions (folderPath: string) : IConceptDescription list =
        if String.IsNullOrEmpty folderPath || not (System.IO.Directory.Exists folderPath) then []
        else
            try
                System.IO.Directory.GetFiles(folderPath, "*.aasx")
                |> Array.sort
                |> Array.toList
                |> List.collect loadAllConceptDescriptionsFromFile
            with ex ->
                eprintfn $"[TemplateLoader] CD 폴더 스캔 실패 {folderPath}: {ex.Message}"
                []
