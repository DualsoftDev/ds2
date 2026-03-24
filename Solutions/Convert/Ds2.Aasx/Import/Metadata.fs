namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open log4net
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Store

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
        m.MarkingFile                         <- getAnyStringValue smc "MarkingFile"
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
                    | "CompanyLogo"                         -> np.CompanyLogo <- (valueOrEmpty p)
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
                | _ -> ()
            np

    // ── HandoverDocumentation 역직렬화 (IDTA 02004-1-2) ─────────────────────────

    let smcToDocumentId (smc: SubmodelElementCollection) : DocumentId =
        let d = DocumentId()
        d.DocumentDomainId <- getAnyStringValue smc "DocumentDomainId"
        d.ValueId          <- getAnyStringValue smc "ValueId"
        let isPrimaryStr   = getAnyStringValue smc "IsPrimary"
        d.IsPrimary        <- isPrimaryStr.Equals("true", StringComparison.OrdinalIgnoreCase)
        d

    let smcToDocClassification (smc: SubmodelElementCollection) : DocumentClassification =
        let c = DocumentClassification()
        c.ClassId              <- getAnyStringValue smc "ClassId"
        c.ClassName            <- getAnyStringValue smc "ClassName"
        c.ClassificationSystem <- getAnyStringValue smc "ClassificationSystem"
        c

    let smcToDocVersion (smc: SubmodelElementCollection) : DocumentVersion =
        let dv = DocumentVersion()
        // Languages SML
        if smc.Value <> null then
            smc.Value |> Seq.iter (fun elem ->
                match elem with
                | :? SubmodelElementList as sml when sml.IdShort = "Languages" ->
                    if sml.Value <> null then
                        for item in sml.Value do
                            match item with
                            | :? Property as p when p.Value <> null -> dv.Languages.Add(p.Value)
                            | _ -> ()
                | :? SubmodelElementList as sml when sml.IdShort = "DigitalFiles" ->
                    if sml.Value <> null then
                        for item in sml.Value do
                            match item with
                            | :? Property as p when p.Value <> null -> dv.DigitalFiles.Add(p.Value)
                            | _ -> ()
                | _ -> ())
        dv.DocumentVersionId       <- getAnyStringValue smc "DocumentVersionId"
        dv.Title                   <- getAnyStringValue smc "Title"
        dv.SubTitle                <- getAnyStringValue smc "SubTitle"
        dv.Summary                 <- getAnyStringValue smc "Summary"
        dv.KeyWords                <- getAnyStringValue smc "KeyWords"
        dv.SetDate                 <- getAnyStringValue smc "SetDate"
        dv.StatusSetDate           <- getAnyStringValue smc "StatusSetDate"
        dv.StatusValue             <- getAnyStringValue smc "StatusValue"
        dv.OrganizationName        <- getAnyStringValue smc "OrganizationName"
        dv.OrganizationOfficialName <- getAnyStringValue smc "OrganizationOfficialName"
        dv.Role                    <- getAnyStringValue smc "Role"
        dv.PreviewFile             <- getAnyStringValue smc "PreviewFile"
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
