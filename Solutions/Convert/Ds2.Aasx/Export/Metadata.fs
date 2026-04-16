namespace Ds2.Aasx

open System
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

module internal AasxExportMetadata =

    open AasxExportCore

    let phoneToSmc (phone: PhoneInfo) : ISubmodelElement =
        mkSmc "Phone" [
            mkMlp  "TelephoneNumber" phone.TelephoneNumber
            mkProp "TypeOfTelephone" phone.TypeOfTelephone
        ]

    let faxToSmc (fax: FaxInfo) : ISubmodelElement =
        mkSmc "Fax" [
            mkMlp  "FaxNumber"       fax.FaxNumber
            mkProp "TypeOfFaxNumber" fax.TypeOfFaxNumber
        ]

    let emailToSmc (email: EmailInfo) : ISubmodelElement =
        mkSmc "Email" [
            mkProp "EmailAddress"       email.EmailAddress
            mkMlp  "PublicKey"          email.PublicKey
            mkProp "TypeOfEmailAddress" email.TypeOfEmailAddress
        ]

    let addressToSmc (addr: AddressInfo) : ISubmodelElement =
        mkSmc "AddressInformation" [
            mkMlp  "Street"       addr.Street
            mkMlp  "Zipcode"      addr.Zipcode
            mkMlp  "CityTown"     addr.CityTown
            mkProp "NationalCode" addr.NationalCode
            phoneToSmc addr.Phone
            faxToSmc   addr.Fax
            emailToSmc addr.Email
        ]

    let markingToSmc (m: MarkingInfo) : ISubmodelElement =
        mkSmc "Marking" [
            mkProp "MarkingName"                         m.MarkingName
            mkProp "DesignationOfCertificateOrApproval"  m.DesignationOfCertificateOrApproval
            mkProp "IssueDate"                           m.IssueDate
            mkProp "ExpiryDate"                          m.ExpiryDate
            mkProp "MarkingFile"                         m.MarkingFile
            mkMlp  "MarkingAdditionalText"               m.MarkingAdditionalText
        ]

    let nameplateToSubmodel (np: Nameplate) (projectId: Guid) : Submodel =
        let elems : ISubmodelElement list = [
            // 필수 요소
            mkProp "URIOfTheProduct"                np.URIOfTheProduct
            mkMlp  "ManufacturerName"               np.ManufacturerName
            mkMlp  "ManufacturerProductDesignation" np.ManufacturerProductDesignation
            addressToSmc np.AddressInformation
            mkProp "OrderCodeOfManufacturer"        np.OrderCodeOfManufacturer
            // 선택 요소
            mkMlp  "ManufacturerProductRoot"        np.ManufacturerProductRoot
            mkMlp  "ManufacturerProductFamily"      np.ManufacturerProductFamily
            mkProp "ManufacturerProductType"        np.ManufacturerProductType
            mkProp "ProductArticleNumberOfManufacturer" np.ProductArticleNumberOfManufacturer
            mkProp "SerialNumber"                   np.SerialNumber
            mkProp "YearOfConstruction"             np.YearOfConstruction
            mkProp "DateOfManufacture"              np.DateOfManufacture
            mkProp "HardwareVersion"                np.HardwareVersion
            mkProp "FirmwareVersion"                np.FirmwareVersion
            mkProp "SoftwareVersion"                np.SoftwareVersion
            mkProp "CountryOfOrigin"                np.CountryOfOrigin
            mkProp "UniqueFacilityIdentifier"       np.UniqueFacilityIdentifier
            mkProp "CompanyLogo"                    np.CompanyLogo
        ]
        let markingsElems =
            mkSml "Markings" (np.Markings |> Seq.map markingToSmc |> Seq.toList) |> Option.toList
        mkSubmodel
            $"urn:dualsoft:nameplate:{projectId}"
            NameplateSubmodelIdShort
            NameplateSemanticId
            (elems @ markingsElems)

    // ── HandoverDocumentation → AAS Submodel (IDTA 02004-1-2) ──────────────────

    let documentIdToSmc (did: DocumentId) : ISubmodelElement =
        mkSmc "DocumentId" [
            mkProp "DocumentDomainId" did.DocumentDomainId
            mkProp "ValueId"          did.ValueId
            mkProp "IsPrimary"        (did.IsPrimary.ToString().ToLowerInvariant())
        ]

    let documentClassToSmc (dc: DocumentClassification) : ISubmodelElement =
        mkSmc "DocumentClassification" [
            mkProp "ClassId"               dc.ClassId
            mkProp "ClassName"             dc.ClassName
            mkProp "ClassificationSystem"  dc.ClassificationSystem
        ]

    let documentVersionToSmc (dv: DocumentVersion) : ISubmodelElement =
        let baseElems : ISubmodelElement list = [
            yield! mkSmlProp "Languages" (dv.Languages |> Seq.map (fun lang -> mkProp "Language" lang) |> Seq.toList) |> Option.toList
            mkProp "DocumentVersionId"       dv.DocumentVersionId
            mkProp "Title"                   dv.Title
            mkProp "SubTitle"                dv.SubTitle
            mkProp "Summary"                 dv.Summary
            mkProp "KeyWords"                dv.KeyWords
            mkProp "SetDate"                 dv.SetDate
            mkProp "StatusSetDate"           dv.StatusSetDate
            mkProp "StatusValue"             dv.StatusValue
            mkProp "OrganizationName"        dv.OrganizationName
            mkProp "OrganizationOfficialName" dv.OrganizationOfficialName
            mkProp "Role"                    dv.Role
            yield! mkSmlProp "DigitalFiles" (dv.DigitalFiles |> Seq.map (fun f -> mkProp "DigitalFile" f) |> Seq.toList) |> Option.toList
            mkProp "PreviewFile"             dv.PreviewFile
        ]
        mkSmc "DocumentVersion" baseElems

    let documentToSmc (doc: Document) : ISubmodelElement =
        let elems : ISubmodelElement list = [
            yield! mkSml "DocumentIds" (doc.DocumentIds |> Seq.map documentIdToSmc |> Seq.toList) |> Option.toList
            yield! mkSml "DocumentClassifications" (doc.DocumentClassifications |> Seq.map documentClassToSmc |> Seq.toList) |> Option.toList
            yield! mkSml "DocumentVersions" (doc.DocumentVersions |> Seq.map documentVersionToSmc |> Seq.toList) |> Option.toList
        ]
        mkSmc "Document" elems

    let documentationToSubmodel (hd: HandoverDocumentation) (projectId: Guid) : Submodel =
        // Documents가 비어있으면 기본 샘플 Document 추가 (AAS 규칙: Submodel은 최소 1개의 element 필요)
        let documents =
            if hd.Documents.Count = 0 then
                let defaultDoc = Document()
                // 기본 DocumentId 추가
                let docId = DocumentId()
                docId.DocumentDomainId <- "ManufacturerDocumentId"
                docId.ValueId <- "DS2-DOC-001"
                docId.IsPrimary <- true
                defaultDoc.DocumentIds.Add(docId)
                // 기본 DocumentClassification 추가 (VDI 2770 필수)
                let docClass = DocumentClassification()
                docClass.ClassId <- "03-02"
                docClass.ClassName <- "Operating instructions"
                docClass.ClassificationSystem <- "VDI2770:2018"
                defaultDoc.DocumentClassifications.Add(docClass)
                // 기본 DocumentVersion 추가
                let ver = DocumentVersion()
                ver.Languages.Add("en")
                ver.DocumentVersionId <- "1.0"
                ver.Title <- "Project Documentation"
                ver.Summary <- "Default project documentation"
                ver.SetDate <- DateTime.Now.ToString("yyyy-MM-dd")
                defaultDoc.DocumentVersions.Add(ver)
                [documentToSmc defaultDoc]
            else
                hd.Documents |> Seq.map documentToSmc |> Seq.toList

        let elems : ISubmodelElement list = [
            yield! mkSml "Documents" documents |> Option.toList
        ]
        mkSubmodel
            $"urn:dualsoft:documentation:{projectId}"
            DocumentationSubmodelIdShort
            DocumentationSemanticId
            elems

    // ── 진입점 ─────────────────────────────────────────────────────────────────
