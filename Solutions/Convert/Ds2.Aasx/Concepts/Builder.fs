namespace Ds2.Aasx

open System.Collections.Generic
open AasCore.Aas3_0

module AasxConceptDescriptions =

    open AasxConceptDescriptionCatalog

    /// IRDI -> ConceptDescriptionInfo 매핑 딕셔너리
    let private conceptDescriptionMap: Dictionary<string, ConceptDescriptionInfo> =
        conceptDescriptionInfos
        |> List.map (fun info -> info.Id, info)
        |> dict
        |> Dictionary

    /// IEC61360 DataSpecification Reference
    let private iec61360DataSpecificationRef =
        Reference(
            ReferenceTypes.ExternalReference,
            ResizeArray<IKey>([
                Key(KeyTypes.GlobalReference, "http://admin-shell.io/DataSpecificationTemplates/DataSpecificationIEC61360/3/0") :> IKey
            ])
        )

    /// ConceptDescriptionInfo로부터 AAS ConceptDescription 객체 생성
    let private createConceptDescription (info: ConceptDescriptionInfo) : ConceptDescription =
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
            definition = definition
        )
        let embeddedDataSpec = EmbeddedDataSpecification(
            dataSpecification = iec61360DataSpecificationRef,
            dataSpecificationContent = dataSpecContent
        )
        let isCaseOfRef = Reference(
            ReferenceTypes.ModelReference,
            ResizeArray<IKey>([
                Key(KeyTypes.GlobalReference, info.Id) :> IKey
            ])
        )
        ConceptDescription(
            id = info.Id,
            administration = AdministrativeInformation(revision = "1"),
            embeddedDataSpecifications = ResizeArray<IEmbeddedDataSpecification>([ embeddedDataSpec :> IEmbeddedDataSpecification ]),
            isCaseOf = ResizeArray<IReference>([ isCaseOfRef :> IReference ])
        )

    /// Nameplate에서 사용하는 모든 IRDI 수집
    let collectNameplateIrdis () : string list =
        [
            "0173-1#02-AAY811#001"  // URIOfTheProduct
            "0173-1#02-AAO677#002"  // ManufacturerName
            "0173-1#02-AAW338#001"  // ManufacturerProductDesignation
            "0173-1#02-AAO227#002"  // OrderCodeOfManufacturer
            "0173-1#02-AAU732#001"  // ManufacturerProductRoot
            "0173-1#02-AAU731#001"  // ManufacturerProductFamily
            "0173-1#02-AAO057#002"  // ManufacturerProductType
            "0173-1#02-AAO676#003"  // ProductArticleNumberOfManufacturer
            "0173-1#02-AAM556#002"  // SerialNumber
            "0173-1#02-AAP906#001"  // YearOfConstruction
            "0173-1#02-AAR972#002"  // DateOfManufacture
            "0173-1#02-AAN270#002"  // HardwareVersion
            "0173-1#02-AAM985#002"  // FirmwareVersion
            "0173-1#02-AAM737#002"  // SoftwareVersion
            "0173-1#02-AAO259#003"  // CountryOfOrigin
            "0173-1#02-AAW515#001"  // CompanyLogo
            "0173-1#02-AAO128#002"  // Street
            "0173-1#02-AAO129#002"  // Zipcode
            "0173-1#02-AAO132#002"  // CityTown
            "0173-1#02-AAO134#002"  // NationalCode
            "0173-1#02-AAO136#002"  // TelephoneNumber
            "0173-1#02-AAO137#003"  // TypeOfTelephone
            "0173-1#02-AAO195#003"  // FaxNumber
            "0173-1#02-AAO196#003"  // TypeOfFaxNumber
            "0173-1#02-AAO198#002"  // EmailAddress
            "0173-1#02-AAO200#002"  // PublicKey
            "0173-1#02-AAO199#003"  // TypeOfEmailAddress
            "0173-1#01-AGZ673#001"  // Markings
            "0173-1#01-AHD206#001"  // Marking
            "0173-1#02-BAB392#015"  // MarkingName
            "0173-1#02-ABH783#001"  // DesignationOfCertificateOrApproval
            "0173-1#02-AAO003#003"  // IssueDate
            "0173-1#02-AAO004#003"  // ExpiryDate
            "0173-1#02-AAA801#004"  // MarkingFile
            "0173-1#02-AAM954#002"  // MarkingAdditionalText
        ]

    /// Documentation에서 사용하는 모든 IRDI 수집
    let collectDocumentationIrdis () : string list =
        [
            "0173-1#01-AHF578#001"  // HandoverDocumentation
            "0173-1#02-ABI500#001/0173-1#01-AHF579#001"  // Document
            "0173-1#02-ABI501#001/0173-1#01-AHF580#001"  // DocumentId
            "0173-1#02-ABI502#001/0173-1#01-AHF581#001"  // DocumentClassification
            "0173-1#02-ABI503#001/0173-1#01-AHF582#001"  // DocumentVersion
            "0173-1#02-ABI504#001/0173-1#01-AHF583#001"  // DigitalFile
        ]

    /// Nameplate와 Documentation에서 사용하는 모든 IRDI에 대한 ConceptDescription 생성
    let createAllConceptDescriptions (includeNameplate: bool) (includeDocumentation: bool) : ResizeArray<IConceptDescription> =
        let irdis = ResizeArray<string>()
        if includeNameplate then
            irdis.AddRange(collectNameplateIrdis())
        if includeDocumentation then
            irdis.AddRange(collectDocumentationIrdis())
        let result = ResizeArray<IConceptDescription>()
        for irdi in irdis do
            match conceptDescriptionMap.TryGetValue(irdi) with
            | true, info ->
                result.Add(createConceptDescription info :> IConceptDescription)
            | false, _ -> ()
        result
