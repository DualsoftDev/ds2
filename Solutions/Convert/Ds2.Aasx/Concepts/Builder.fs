namespace Ds2.Aasx

open System
open System.Collections.Generic
open AasCore.Aas3_1

module AasxConceptDescriptions =

    open AasxConceptDescriptionCatalog
    open AasxConceptDescriptionLoader

    /// IEC61360 DataSpecification Reference (AAS 3.1 표준)
    let private iec61360DataSpecificationRef =
        Reference(
            ReferenceTypes.ExternalReference,
            ResizeArray<IKey>([
                Key(KeyTypes.GlobalReference, "https://admin-shell.io/aas/3/1/DataSpecificationIec61360") :> IKey
            ])
        )

    /// ConceptDescriptionInfo로부터 AAS ConceptDescription 객체 생성 (커스텀용)
    let internal createConceptDescriptionFromInfo (info: ConceptDescriptionInfo) : ConceptDescription =
        let preferredName = ResizeArray<ILangStringPreferredNameTypeIec61360>([
            LangStringPreferredNameTypeIec61360("de", info.PreferredNameDe) :> ILangStringPreferredNameTypeIec61360
            LangStringPreferredNameTypeIec61360("en", info.PreferredNameEn) :> ILangStringPreferredNameTypeIec61360
        ])
        let shortName = ResizeArray<ILangStringShortNameTypeIec61360>([
            LangStringShortNameTypeIec61360("en", info.ShortName) :> ILangStringShortNameTypeIec61360
        ])
        let definition = ResizeArray<ILangStringDefinitionTypeIec61360>([
            LangStringDefinitionTypeIec61360("de", info.DefinitionDe) :> ILangStringDefinitionTypeIec61360
            LangStringDefinitionTypeIec61360("en", info.DefinitionEn) :> ILangStringDefinitionTypeIec61360
        ])
        let dataSpecContent = DataSpecificationIec61360(
            preferredName = preferredName,
            shortName = shortName,
            definition = definition,
            value = Unchecked.defaultof<string>
        )
        let embeddedDataSpec = EmbeddedDataSpecification(
            dataSpecification = iec61360DataSpecificationRef,
            dataSpecificationContent = dataSpecContent
        )
        let isCaseOfRef = Reference(
            ReferenceTypes.ModelReference,
            ResizeArray<IKey>([
                Key(KeyTypes.ConceptDescription, info.Id) :> IKey
            ])
        )
        let cd = ConceptDescription(
            id = info.Id,
            embeddedDataSpecifications = ResizeArray<IEmbeddedDataSpecification>([ embeddedDataSpec :> IEmbeddedDataSpecification ]),
            isCaseOf = ResizeArray<IReference>([ isCaseOfRef :> IReference ])
        )
        // AASd-117: idShort 설정 (IRI는 마지막 부분, IRDI는 sanitize)
        let idShort =
            if info.Id.StartsWith("http") then
                // IRI 형식: 마지막 경로 세그먼트 사용
                let parts = info.Id.Split([|'/'|], StringSplitOptions.RemoveEmptyEntries)
                if parts.Length > 0 then parts.[parts.Length - 1] else "CustomCD"
            else
                // IRDI 형식: sanitize
                let sanitized =
                    info.Id.ToCharArray()
                    |> Array.map (fun c -> if Char.IsLetterOrDigit(c) then c else '_')
                    |> String
                if Char.IsLetter(sanitized.[0]) then sanitized else "N" + sanitized
        cd.IdShort <- idShort
        cd

    /// 모든 ConceptDescription 생성
    /// - IDTA 공식 템플릿 (JSON 파일에서 로드) - 우선 사용
    /// - fallback: 코드로 생성 (Catalog.fs의 nameplateDocumentationInfos)
    /// - 커스텀 Sequence CD (코드로 생성)
    let createAllConceptDescriptions (idtaJsonPath: string option) : ResizeArray<IConceptDescription> =
        let result = ResizeArray<IConceptDescription>()

        // 1. IDTA 공식 템플릿 로드 시도
        let idtaLoaded =
            match idtaJsonPath with
            | Some path when IO.File.Exists(path) ->
                let idtaCds = loadFromJson path
                result.AddRange(idtaCds)
                printfn $"Loaded {idtaCds.Length} ConceptDescriptions from IDTA template: {path}"
                true
            | Some path ->
                printfn $"Warning: IDTA template file not found: {path}"
                false
            | None ->
                false

        // 2. IDTA 로드 실패 시 fallback: Catalog.fs의 nameplateDocumentationInfos 사용
        if not idtaLoaded then
            printfn "Using fallback: generating CDs from Catalog.fs"
            for info in nameplateDocumentationInfos do
                let cd = createConceptDescriptionFromInfo info
                result.Add(cd :> IConceptDescription)

        // 3. 커스텀 Sequence CD 생성
        for info in sequenceConceptDescriptionInfos do
            let cd = createConceptDescriptionFromInfo info
            result.Add(cd :> IConceptDescription)

        printfn $"Total ConceptDescriptions: {result.Count}"
        result

    /// 임베디드 리소스에서 IDTA 템플릿 로드 + 커스텀 CD 생성
    let createAllConceptDescriptionsFromEmbedded (resourceName: string option) : ResizeArray<IConceptDescription> =
        let result = ResizeArray<IConceptDescription>()

        // 1. 임베디드 리소스에서 IDTA 템플릿 로드 시도
        let idtaLoaded =
            match resourceName with
            | Some name ->
                let assembly = Reflection.Assembly.GetExecutingAssembly()
                let idtaCds = loadFromEmbeddedResource assembly name
                result.AddRange(idtaCds)
                printfn $"Loaded {idtaCds.Length} ConceptDescriptions from embedded resource: {name}"
                idtaCds.Length > 0
            | None ->
                false

        // 2. IDTA 로드 실패 시 fallback
        if not idtaLoaded then
            printfn "Using fallback: generating CDs from Catalog.fs"
            for info in nameplateDocumentationInfos do
                let cd = createConceptDescriptionFromInfo info
                result.Add(cd :> IConceptDescription)

        // 3. 커스텀 Sequence CD 생성
        for info in sequenceConceptDescriptionInfos do
            let cd = createConceptDescriptionFromInfo info
            result.Add(cd :> IConceptDescription)

        printfn $"Total ConceptDescriptions: {result.Count}"
        result

    /// Sequence CD만 생성 (IDTA 템플릿 없이)
    let createSequenceConceptDescriptions () : ResizeArray<IConceptDescription> =
        let result = ResizeArray<IConceptDescription>()
        for info in sequenceConceptDescriptionInfos do
            let cd = createConceptDescriptionFromInfo info
            result.Add(cd :> IConceptDescription)
        result

    /// 새로운 커스텀 CD를 쉽게 추가하기 위한 헬퍼
    let addCustomConceptDescription
        (id: string)
        (preferredNameEn: string)
        (preferredNameDe: string)
        (shortName: string)
        (definitionEn: string)
        (definitionDe: string)
        : ConceptDescription =
        let info = {
            Id = id
            PreferredNameEn = preferredNameEn
            PreferredNameDe = preferredNameDe
            ShortName = shortName
            DefinitionEn = definitionEn
            DefinitionDe = definitionDe
        }
        createConceptDescriptionFromInfo info

    /// Submodel ID 생성 (Project GUID 마지막 바이트에 offset 더하기)
    let mkSubmodelId (projectId: Guid) (offset: byte) : string =
        let bytes = projectId.ToByteArray()
        bytes.[15] <- bytes.[15] + offset
        let newGuid = Guid(bytes)
        newGuid.ToString()
