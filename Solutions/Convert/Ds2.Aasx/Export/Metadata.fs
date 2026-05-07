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

    /// 파일 경로 / URL 확장자로 image content type 추정. File element 의 ContentType 채움.
    let private guessImageContentType (pathOrUrl: string) : string =
        if String.IsNullOrEmpty pathOrUrl then "application/octet-stream"
        else
            let lower = pathOrUrl.ToLowerInvariant()
            if   lower.EndsWith(".png")  then "image/png"
            elif lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") then "image/jpeg"
            elif lower.EndsWith(".gif")  then "image/gif"
            elif lower.EndsWith(".svg")  then "image/svg+xml"
            elif lower.EndsWith(".webp") then "image/webp"
            elif lower.EndsWith(".pdf")  then "application/pdf"
            else "application/octet-stream"

    /// Template-driven Nameplate emit (IDTA 02006-3-0-1 Digital Nameplate).
    /// 임베디드 Nameplate.aasx 템플릿이 SM 의 구조/CD/semanticId/MLP 언어슬롯을 정의하고,
    /// 본 함수는 ds2 Nameplate 의 사용자 값만 inject 한다. IDTA 가 새 버전 publish 시
    /// Concepts/Templates/Nameplate.aasx 만 교체하면 자동 동기화.
    let nameplateToSubmodel (np: Nameplate) (projectId: Guid) : Submodel =
        let sm =
            match AasxTemplateLoader.tryLoadSubmodel
                    AasxTemplateLoader.NameplateResource NameplateSubmodelIdShort with
            | Some sm -> sm :?> Submodel
            | None ->
                failwith "Nameplate.aasx 템플릿을 임베디드 리소스에서 로드할 수 없습니다 (Concepts/Templates/Nameplate.aasx)"

        AasxTemplateScaffold.assignInstanceId sm $"urn:dualsoft:nameplate:{projectId}"

        // ── 스칼라 Property/MLP 주입 ───────────────────────────────────────
        // ── AddressInformation (v3.0.1 SMT drop-in placeholder 채움) ──────
        // 템플릿의 빈 SMC 에 ds2 AddressInfo 의 Street/Phone/Fax/Email 등을 child 로 append.
        // 표준 SMT drop-in 의 정확한 semanticId 매핑은 후속 작업; 현재는 idShort 기반 간이 구조.
        let addr = np.AddressInformation
        let addrChildren : ISubmodelElement list = [
            mkMlp  "Street"       addr.Street
            mkMlp  "Zipcode"      addr.Zipcode
            mkMlp  "CityTown"     addr.CityTown
            mkProp "NationalCode" addr.NationalCode
            mkSmc "Phone" [
                mkMlp  "TelephoneNumber" addr.Phone.TelephoneNumber
                mkProp "TypeOfTelephone" addr.Phone.TypeOfTelephone
            ]
            mkSmc "Fax" [
                mkMlp  "FaxNumber"       addr.Fax.FaxNumber
                mkProp "TypeOfFaxNumber" addr.Fax.TypeOfFaxNumber
            ]
            mkSmc "Email" [
                mkProp "EmailAddress"       addr.Email.EmailAddress
                mkMlp  "PublicKey"          addr.Email.PublicKey
                mkProp "TypeOfEmailAddress" addr.Email.TypeOfEmailAddress
            ]
        ]
        AasxTemplateScaffold.appendChildren sm "AddressInformation" addrChildren |> ignore

        AasxTemplateScaffold.setProp sm "URIOfTheProduct" np.URIOfTheProduct |> ignore
        AasxTemplateScaffold.setMlpEn sm "ManufacturerName" np.ManufacturerName |> ignore
        AasxTemplateScaffold.setMlpEn sm "ManufacturerProductDesignation" np.ManufacturerProductDesignation |> ignore
        AasxTemplateScaffold.setMlpEn sm "ManufacturerProductRoot" np.ManufacturerProductRoot |> ignore
        AasxTemplateScaffold.setMlpEn sm "ManufacturerProductFamily" np.ManufacturerProductFamily |> ignore
        AasxTemplateScaffold.setProp sm "ManufacturerProductType" np.ManufacturerProductType |> ignore
        AasxTemplateScaffold.setProp sm "OrderCodeOfManufacturer" np.OrderCodeOfManufacturer |> ignore
        AasxTemplateScaffold.setProp sm "ProductArticleNumberOfManufacturer" np.ProductArticleNumberOfManufacturer |> ignore
        AasxTemplateScaffold.setProp sm "SerialNumber" np.SerialNumber |> ignore
        AasxTemplateScaffold.setProp sm "YearOfConstruction" np.YearOfConstruction |> ignore
        AasxTemplateScaffold.setProp sm "DateOfManufacture" np.DateOfManufacture |> ignore
        AasxTemplateScaffold.setProp sm "HardwareVersion" np.HardwareVersion |> ignore
        AasxTemplateScaffold.setProp sm "FirmwareVersion" np.FirmwareVersion |> ignore
        AasxTemplateScaffold.setProp sm "SoftwareVersion" np.SoftwareVersion |> ignore
        AasxTemplateScaffold.setProp sm "CountryOfOrigin" np.CountryOfOrigin |> ignore
        AasxTemplateScaffold.setProp sm "UniqueFacilityIdentifier" np.UniqueFacilityIdentifier |> ignore
        // CompanyLogo 는 v3 에서 File element. ContentType 은 확장자 추정 (없으면 image/png 디폴트).
        if not (String.IsNullOrEmpty np.CompanyLogo) then
            AasxTemplateScaffold.setFile sm "CompanyLogo" np.CompanyLogo (Some (guessImageContentType np.CompanyLogo))
            |> ignore

        // ── Markings SML 확장 ─────────────────────────────────────────────
        if np.Markings.Count > 0 then
            AasxTemplateScaffold.expandSml sm "Markings" np.Markings.Count (Some "Marking")
            |> ignore
            np.Markings |> Seq.iteri (fun i m ->
                let prefix = sprintf "Markings/Marking%02d" i
                AasxTemplateScaffold.setProp sm (prefix + "/MarkingName") m.MarkingName |> ignore
                AasxTemplateScaffold.setProp sm (prefix + "/DesignationOfCertificateOrApproval") m.DesignationOfCertificateOrApproval |> ignore
                AasxTemplateScaffold.setProp sm (prefix + "/IssueDate") m.IssueDate |> ignore
                AasxTemplateScaffold.setProp sm (prefix + "/ExpiryDate") m.ExpiryDate |> ignore
                AasxTemplateScaffold.setProp sm (prefix + "/MarkingAdditionalText") m.MarkingAdditionalText |> ignore
                if not (String.IsNullOrEmpty m.MarkingFile) then
                    AasxTemplateScaffold.setFile sm (prefix + "/MarkingFile") m.MarkingFile (Some (guessImageContentType m.MarkingFile))
                    |> ignore)
        sm

    /// Template-driven HandoverDocumentation emit (IDTA 02004-2-0).
    /// 임베디드 HandoverDocumentation.aasx 템플릿이 Documents/DocumentIds/DocumentVersions 등의
    /// 중첩 SML 구조를 정의. ds2 Document 데이터를 expandSml + setProp 로 inject.
    ///
    /// v1.x → v2.0 idShort 차이 매핑:
    ///   ds2.DocumentId.ValueId         → "DocumentIdentifier"
    ///   ds2.DocumentId.IsPrimary       → "DocumentIsPrimary"
    ///   ds2.DocumentVersion.DocumentVersionId → "Version"
    ///   ds2.DocumentVersion.SubTitle   → "Subtitle"
    ///   ds2.DocumentVersion.Summary    → "Description"
    ///   ds2.DocumentVersion.OrganizationName → "OrganizationShortName"
    ///   ds2.DocumentVersion.SetDate / Role → v2.0 에 없음 (drop)
    let documentationToSubmodel (hd: HandoverDocumentation) (projectId: Guid) : Submodel =
        let sm =
            match AasxTemplateLoader.tryLoadSubmodel
                    AasxTemplateLoader.HandoverDocumentationResource DocumentationSubmodelIdShort with
            | Some sm -> sm :?> Submodel
            | None ->
                failwith "HandoverDocumentation.aasx 템플릿을 임베디드 리소스에서 로드할 수 없습니다 (Concepts/Templates/HandoverDocumentation.aasx)"

        AasxTemplateScaffold.assignInstanceId sm $"urn:dualsoft:documentation:{projectId}"

        // Documents 가 비어있으면 기본 샘플 1개 보장 (AAS Submodel 최소 1개 element)
        let docs =
            if hd.Documents.Count = 0 then
                let d = Document()
                let id = DocumentId()
                id.DocumentDomainId <- "ManufacturerDocumentId"
                id.ValueId <- "DS2-DOC-001"
                id.IsPrimary <- true
                d.DocumentIds.Add(id)
                let cls = DocumentClassification()
                cls.ClassId <- "03-02"
                cls.ClassName <- "Operating instructions"
                cls.ClassificationSystem <- "VDI2770:2018"
                d.DocumentClassifications.Add(cls)
                let v = DocumentVersion()
                v.Languages.Add("en")
                v.DocumentVersionId <- "1.0"
                v.Title <- "Project Documentation"
                v.Summary <- "Default project documentation"
                v.StatusValue <- "Released"
                d.DocumentVersions.Add(v)
                [ d ]
            else hd.Documents |> Seq.toList

        // Documents SML 확장
        AasxTemplateScaffold.expandSml sm "Documents" docs.Length (Some "Document") |> ignore

        docs |> List.iteri (fun di doc ->
            let docPrefix = sprintf "Documents/Document%02d" di

            // DocumentIds SML
            if doc.DocumentIds.Count > 0 then
                AasxTemplateScaffold.expandSml sm (docPrefix + "/DocumentIds") doc.DocumentIds.Count (Some "DocumentId") |> ignore
                doc.DocumentIds |> Seq.iteri (fun i id ->
                    let p = sprintf "%s/DocumentIds/DocumentId%02d" docPrefix i
                    AasxTemplateScaffold.setProp sm (p + "/DocumentDomainId") id.DocumentDomainId |> ignore
                    AasxTemplateScaffold.setProp sm (p + "/DocumentIdentifier") id.ValueId |> ignore
                    AasxTemplateScaffold.setProp sm (p + "/DocumentIsPrimary") (id.IsPrimary.ToString().ToLowerInvariant()) |> ignore)

            // DocumentClassifications SML
            if doc.DocumentClassifications.Count > 0 then
                AasxTemplateScaffold.expandSml sm (docPrefix + "/DocumentClassifications") doc.DocumentClassifications.Count (Some "DocumentClassification") |> ignore
                doc.DocumentClassifications |> Seq.iteri (fun i cls ->
                    let p = sprintf "%s/DocumentClassifications/DocumentClassification%02d" docPrefix i
                    AasxTemplateScaffold.setProp sm (p + "/ClassId") cls.ClassId |> ignore
                    AasxTemplateScaffold.setProp sm (p + "/ClassName") cls.ClassName |> ignore
                    AasxTemplateScaffold.setProp sm (p + "/ClassificationSystem") cls.ClassificationSystem |> ignore)

            // DocumentVersions SML
            if doc.DocumentVersions.Count > 0 then
                AasxTemplateScaffold.expandSml sm (docPrefix + "/DocumentVersions") doc.DocumentVersions.Count (Some "DocumentVersion") |> ignore
                doc.DocumentVersions |> Seq.iteri (fun i v ->
                    let p = sprintf "%s/DocumentVersions/DocumentVersion%02d" docPrefix i
                    AasxTemplateScaffold.setProp sm (p + "/Version") v.DocumentVersionId |> ignore
                    AasxTemplateScaffold.setMlpEn sm (p + "/Title") v.Title |> ignore
                    AasxTemplateScaffold.setMlpEn sm (p + "/Subtitle") v.SubTitle |> ignore
                    AasxTemplateScaffold.setMlpEn sm (p + "/Description") v.Summary |> ignore
                    AasxTemplateScaffold.setProp sm (p + "/KeyWords") v.KeyWords |> ignore
                    AasxTemplateScaffold.setProp sm (p + "/StatusSetDate") v.StatusSetDate |> ignore
                    AasxTemplateScaffold.setProp sm (p + "/StatusValue") v.StatusValue |> ignore
                    AasxTemplateScaffold.setProp sm (p + "/OrganizationShortName") v.OrganizationName |> ignore
                    AasxTemplateScaffold.setProp sm (p + "/OrganizationOfficialName") v.OrganizationOfficialName |> ignore
                    // Languages SML (Property items) — 다국어 코드 리스트.
                    if v.Languages.Count > 0 then
                        AasxTemplateScaffold.setSmlOfStrings sm (p + "/Language") v.Languages "Language"
                        |> ignore
                    // DigitalFiles SML (File items) — 파일 경로 리스트.
                    if v.DigitalFiles.Count > 0 then
                        AasxTemplateScaffold.setSmlOfFiles sm (p + "/DigitalFiles") v.DigitalFiles "DigitalFile" guessImageContentType
                        |> ignore
                    if not (String.IsNullOrEmpty v.PreviewFile) then
                        AasxTemplateScaffold.setFile sm (p + "/PreviewFile") v.PreviewFile (Some (guessImageContentType v.PreviewFile))
                        |> ignore))
        sm

    // ── 진입점 ─────────────────────────────────────────────────────────────────
