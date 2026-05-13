namespace Ds2.Aasx

open System
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

module internal AasxImportMetadata =

    open AasxImportCore
    open AasxImportGraph

    let smcToPhoneInfo (smc: SubmodelElementCollection) : PhoneInfo =
        let p = PhoneInfo()
        p.TelephoneNumber <- getAnyStringValue smc "TelephoneNumber"
        p.TypeOfTelephone <- getAnyStringValue smc "TypeOfTelephone"
        p

    let smcToFaxInfo (smc: SubmodelElementCollection) : FaxInfo =
        let f = FaxInfo()
        f.FaxNumber       <- getAnyStringValue smc "FaxNumber"
        f.TypeOfFaxNumber <- getAnyStringValue smc "TypeOfFaxNumber"
        f

    let smcToEmailInfo (smc: SubmodelElementCollection) : EmailInfo =
        let e = EmailInfo()
        e.EmailAddress       <- getAnyStringValue smc "EmailAddress"
        e.PublicKey           <- getAnyStringValue smc "PublicKey"
        e.TypeOfEmailAddress <- getAnyStringValue smc "TypeOfEmailAddress"
        e

    let smcToAddressInfo (smc: SubmodelElementCollection) : AddressInfo =
        let a = AddressInfo()
        a.Street       <- getAnyStringValue smc "Street"
        a.Zipcode      <- getAnyStringValue smc "Zipcode"
        a.CityTown     <- getAnyStringValue smc "CityTown"
        a.NationalCode <- getAnyStringValue smc "NationalCode"
        tryGetSmcChild smc "Phone" |> Option.iter (fun c -> a.Phone <- smcToPhoneInfo c)
        tryGetSmcChild smc "Fax"   |> Option.iter (fun c -> a.Fax   <- smcToFaxInfo c)
        tryGetSmcChild smc "Email" |> Option.iter (fun c -> a.Email <- smcToEmailInfo c)
        a

    let smcToMarkingInfo (smc: SubmodelElementCollection) : MarkingInfo =
        let m = MarkingInfo()
        m.MarkingName                         <- getAnyStringValue smc "MarkingName"
        m.DesignationOfCertificateOrApproval  <- getAnyStringValue smc "DesignationOfCertificateOrApproval"
        m.IssueDate                           <- getAnyStringValue smc "IssueDate"
        m.ExpiryDate                          <- getAnyStringValue smc "ExpiryDate"
        // v3: File element / v1.x: Property
        m.MarkingFile                         <- getFileOrStringValue smc "MarkingFile"
        m.MarkingAdditionalText               <- getAnyStringValue smc "MarkingAdditionalText"
        m

    let submodelToNameplate (sm: ISubmodel) : Nameplate =
        let np = Nameplate()
        if sm.SubmodelElements = null then np
        else
            for elem in sm.SubmodelElements do
                match elem with
                | :? Property as p ->
                    match p.IdShort with
                    | "URIOfTheProduct"                     -> np.URIOfTheProduct <- (valueOrEmpty p)
                    | "OrderCodeOfManufacturer"             -> np.OrderCodeOfManufacturer <- (valueOrEmpty p)
                    | "ManufacturerProductType"             -> np.ManufacturerProductType <- (valueOrEmpty p)
                    | "ProductArticleNumberOfManufacturer"  -> np.ProductArticleNumberOfManufacturer <- (valueOrEmpty p)
                    | "SerialNumber"                        -> np.SerialNumber <- (valueOrEmpty p)
                    | "YearOfConstruction"                  -> np.YearOfConstruction <- (valueOrEmpty p)
                    | "DateOfManufacture"                   -> np.DateOfManufacture <- (valueOrEmpty p)
                    | "HardwareVersion"                     -> np.HardwareVersion <- (valueOrEmpty p)
                    | "FirmwareVersion"                     -> np.FirmwareVersion <- (valueOrEmpty p)
                    | "SoftwareVersion"                     -> np.SoftwareVersion <- (valueOrEmpty p)
                    | "CountryOfOrigin"                     -> np.CountryOfOrigin <- (valueOrEmpty p)
                    | "UniqueFacilityIdentifier"            -> np.UniqueFacilityIdentifier <- (valueOrEmpty p)
                    | _ -> ()
                | :? MultiLanguageProperty as mlp ->
                    let v = if mlp.Value = null || mlp.Value.Count = 0 then "" else mlp.Value.[0].Text
                    match mlp.IdShort with
                    | "ManufacturerName"               -> np.ManufacturerName <- v
                    | "ManufacturerProductDesignation" -> np.ManufacturerProductDesignation <- v
                    | "ManufacturerProductRoot"        -> np.ManufacturerProductRoot <- v
                    | "ManufacturerProductFamily"      -> np.ManufacturerProductFamily <- v
                    | _ -> ()
                | :? SubmodelElementCollection as smc when smc.IdShort = "AddressInformation" ->
                    np.AddressInformation <- smcToAddressInfo smc
                | :? SubmodelElementList as sml when sml.IdShort = "Markings" ->
                    if sml.Value <> null then
                        for item in sml.Value do
                            match item with
                            | :? SubmodelElementCollection as msmc -> np.Markings.Add(smcToMarkingInfo msmc)
                            | _ -> ()
                | :? File as f when f.IdShort = "CompanyLogo" ->
                    // v3: File element. v1.x 호환은 위 Property 분기에서 처리.
                    if not (isNull f.Value) then np.CompanyLogo <- f.Value
                | _ -> ()
            np

    // ── HandoverDocumentation 역직렬화 (IDTA 02004-1-2) ─────────────────────────

    let smcToDocumentId (smc: SubmodelElementCollection) : DocumentId =
        let d = DocumentId()
        d.DocumentDomainId <- getAnyStringValue smc "DocumentDomainId"
        // v2.0: DocumentIdentifier  / v1.x: ValueId
        d.ValueId          <- getAnyStringValueAlt smc [ "DocumentIdentifier"; "ValueId" ]
        // v2.0: DocumentIsPrimary   / v1.x: IsPrimary
        let isPrimaryStr   = getAnyStringValueAlt smc [ "DocumentIsPrimary"; "IsPrimary" ]
        d.IsPrimary        <- isPrimaryStr.Equals("true", StringComparison.OrdinalIgnoreCase)
        d

    let smcToDocClassification (smc: SubmodelElementCollection) : DocumentClassification =
        let c = DocumentClassification()
        c.ClassId              <- getAnyStringValue smc "ClassId"
        // v2.0: ClassName 은 MLP. getAnyStringValue 가 MLP 도 처리 (en 우선).
        c.ClassName            <- getAnyStringValueAlt smc [ "ClassName"; "ProductClassName" ]
        c.ClassificationSystem <- getAnyStringValue smc "ClassificationSystem"
        c

    let smcToDocVersion (smc: SubmodelElementCollection) : DocumentVersion =
        let dv = DocumentVersion()
        // Language(v2) / Languages(v1) SML — Property items
        // DigitalFiles SML — v2: File items / v1: Property items
        if smc.Value <> null then
            smc.Value |> Seq.iter (fun elem ->
                match elem with
                | :? SubmodelElementList as sml when sml.IdShort = "Language" || sml.IdShort = "Languages" ->
                    if sml.Value <> null then
                        for item in sml.Value do
                            match item with
                            | :? Property as p when p.Value <> null -> dv.Languages.Add(p.Value)
                            | _ -> ()
                | :? SubmodelElementList as sml when sml.IdShort = "DigitalFiles" ->
                    if sml.Value <> null then
                        for item in sml.Value do
                            match item with
                            | :? File as f when not (isNull f.Value) -> dv.DigitalFiles.Add(f.Value)
                            | :? Property as p when p.Value <> null -> dv.DigitalFiles.Add(p.Value)  // v1 호환
                            | _ -> ()
                | _ -> ())
        // v2.0: Version  / v1.x: DocumentVersionId
        dv.DocumentVersionId       <- getAnyStringValueAlt smc [ "Version"; "DocumentVersionId" ]
        dv.Title                   <- getAnyStringValue smc "Title"
        // v2.0: Subtitle / v1.x: SubTitle
        dv.SubTitle                <- getAnyStringValueAlt smc [ "Subtitle"; "SubTitle" ]
        // v2.0: Description / v1.x: Summary
        dv.Summary                 <- getAnyStringValueAlt smc [ "Description"; "Summary" ]
        dv.KeyWords                <- getAnyStringValue smc "KeyWords"
        dv.SetDate                 <- getAnyStringValue smc "SetDate"
        dv.StatusSetDate           <- getAnyStringValue smc "StatusSetDate"
        dv.StatusValue             <- getAnyStringValue smc "StatusValue"
        // v2.0: OrganizationShortName / v1.x: OrganizationName
        dv.OrganizationName        <- getAnyStringValueAlt smc [ "OrganizationShortName"; "OrganizationName" ]
        dv.OrganizationOfficialName <- getAnyStringValue smc "OrganizationOfficialName"
        dv.Role                    <- getAnyStringValue smc "Role"
        // v3: File element / v1.x: Property
        dv.PreviewFile             <- getFileOrStringValue smc "PreviewFile"
        dv

    let smcToDocument (smc: SubmodelElementCollection) : Document =
        let doc = Document()
        if smc.Value <> null then
            for elem in smc.Value do
                match elem with
                | :? SubmodelElementList as sml ->
                    match sml.IdShort with
                    | "DocumentIds" ->
                        if sml.Value <> null then
                            for item in sml.Value do
                                match item with
                                | :? SubmodelElementCollection as c -> doc.DocumentIds.Add(smcToDocumentId c)
                                | _ -> ()
                    | "DocumentClassifications" ->
                        if sml.Value <> null then
                            for item in sml.Value do
                                match item with
                                | :? SubmodelElementCollection as c -> doc.DocumentClassifications.Add(smcToDocClassification c)
                                | _ -> ()
                    | "DocumentVersions" ->
                        if sml.Value <> null then
                            for item in sml.Value do
                                match item with
                                | :? SubmodelElementCollection as c -> doc.DocumentVersions.Add(smcToDocVersion c)
                                | _ -> ()
                    | _ -> ()
                | _ -> ()
        doc

    let submodelToDocumentation (sm: ISubmodel) : HandoverDocumentation =
        let hd = HandoverDocumentation()
        if sm.SubmodelElements = null then hd
        else
            for elem in sm.SubmodelElements do
                match elem with
                | :? SubmodelElementList as sml when sml.IdShort = "Documents" ->
                    if sml.Value <> null then
                        for item in sml.Value do
                            match item with
                            | :? SubmodelElementCollection as c -> hd.Documents.Add(smcToDocument c)
                            | _ -> ()
                | _ -> ()
            hd

    // ── 진입점 ─────────────────────────────────────────────────────────────────

    /// AASX 파일에서 DsStore를 읽어 반환합니다 (Project는 store.Projects에 포함됩니다).
