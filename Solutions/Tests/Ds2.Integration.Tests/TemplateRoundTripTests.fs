module Ds2.Integration.Tests.TemplateRoundTripTests

open System
open System.IO
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Aasx

// =============================================================================
// Template-driven SM round-trip 검증.
//   1) ds2 데이터 → AASX export
//   2) 같은 파일 import → ds2 데이터
//   3) 핵심 필드가 보존되는지 확인
// IDTA 02006-3-0-1 / 02004-2-0 / 02003-2-0 템플릿 기반 emit 의 round-trip 안전성 검증.
// =============================================================================

let private tempPath name =
    let dir = Path.Combine(Path.GetTempPath(), "ds2-rt-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    Path.Combine(dir, name + ".aasx")

let private buildMinimalProject () : DsStore * Project =
    let store = DsStore()
    let project = Project("RoundTripTest")
    store.DirectWrite(store.Projects, project)
    let sys = DsSystem("ActiveSys")
    sys.SystemType <- Some "Cylinder_1"
    store.DirectWrite(store.Systems, sys)
    project.ActiveSystemIds.Add(sys.Id)
    store, project

let private exportImport (_project: Project) (store: DsStore) : Project =
    let path = tempPath "rt"
    AasxExporter.exportFromStore store path "https://dualsoft.com/" false false |> ignore

    // 신규 store 에 import
    let importedStore = DsStore()
    AasxImporter.importIntoStoreOrRaise importedStore path
    let imported =
        Queries.allProjects importedStore
        |> List.tryHead
        |> Option.defaultWith (fun () -> failwith "import 후 project 없음")

    try File.Delete path with _ -> ()
    try Directory.Delete(Path.GetDirectoryName path, true) with _ -> ()
    imported

[<Fact>]
let ``Nameplate scalars round-trip`` () =
    let store, project = buildMinimalProject()
    let np = Nameplate()
    np.URIOfTheProduct <- "https://example.com/product/X100"
    np.ManufacturerName <- "DualSoft"
    np.ManufacturerProductDesignation <- "DS2-X100"
    np.SerialNumber <- "SN-12345"
    np.YearOfConstruction <- "2026"
    np.HardwareVersion <- "HW1.0"
    np.FirmwareVersion <- "FW2.3"
    np.SoftwareVersion <- "SW3.1.4"
    np.CountryOfOrigin <- "KR"
    np.CompanyLogo <- "/aasx/files/logo.png"
    project.Nameplate <- Some np

    let imported = exportImport project store
    Assert.True(imported.Nameplate.IsSome, "imported Nameplate is None")
    let inp = imported.Nameplate.Value
    Assert.Equal("https://example.com/product/X100", inp.URIOfTheProduct)
    Assert.Equal("DualSoft", inp.ManufacturerName)
    Assert.Equal("DS2-X100", inp.ManufacturerProductDesignation)
    Assert.Equal("SN-12345", inp.SerialNumber)
    Assert.Equal("2026", inp.YearOfConstruction)
    Assert.Equal("HW1.0", inp.HardwareVersion)
    Assert.Equal("FW2.3", inp.FirmwareVersion)
    Assert.Equal("SW3.1.4", inp.SoftwareVersion)
    Assert.Equal("KR", inp.CountryOfOrigin)
    Assert.Equal("/aasx/files/logo.png", inp.CompanyLogo)

[<Fact>]
let ``Nameplate Markings round-trip`` () =
    let store, project = buildMinimalProject()
    let np = Nameplate()
    np.URIOfTheProduct <- "https://example.com/x"
    let m1 = MarkingInfo()
    m1.MarkingName <- "CE"
    m1.IssueDate <- "2026-01-15"
    m1.MarkingFile <- "/aasx/files/ce.png"
    m1.MarkingAdditionalText <- "EU compliance"
    let m2 = MarkingInfo()
    m2.MarkingName <- "UL"
    m2.IssueDate <- "2026-03-20"
    np.Markings.Add(m1)
    np.Markings.Add(m2)
    project.Nameplate <- Some np

    let imported = exportImport project store
    Assert.True(imported.Nameplate.IsSome)
    let inp = imported.Nameplate.Value
    Assert.Equal(2, inp.Markings.Count)
    Assert.Equal("CE", inp.Markings.[0].MarkingName)
    Assert.Equal("2026-01-15", inp.Markings.[0].IssueDate)
    Assert.Equal("/aasx/files/ce.png", inp.Markings.[0].MarkingFile)
    Assert.Equal("EU compliance", inp.Markings.[0].MarkingAdditionalText)
    Assert.Equal("UL", inp.Markings.[1].MarkingName)

[<Fact>]
let ``HandoverDocumentation Documents round-trip`` () =
    let store, project = buildMinimalProject()
    let hd = HandoverDocumentation()
    let doc = Document()

    let did = DocumentId()
    did.DocumentDomainId <- "ManufacturerDocumentId"
    did.ValueId <- "DS2-DOC-007"
    did.IsPrimary <- true
    doc.DocumentIds.Add(did)

    let cls = DocumentClassification()
    cls.ClassId <- "03-02"
    cls.ClassName <- "Operating instructions"
    cls.ClassificationSystem <- "VDI2770:2018"
    doc.DocumentClassifications.Add(cls)

    let v = DocumentVersion()
    v.DocumentVersionId <- "1.5"
    v.Title <- "RT Test Doc"
    v.SubTitle <- "Subtitle here"
    v.Summary <- "Summary text"
    v.KeyWords <- "test;round-trip"
    v.StatusValue <- "Released"
    v.OrganizationName <- "DualSoft"
    v.Languages.Add("en")
    v.Languages.Add("ko")
    v.DigitalFiles.Add("/aasx/files/manual.pdf")
    v.PreviewFile <- "/aasx/files/preview.png"
    doc.DocumentVersions.Add(v)
    hd.Documents.Add(doc)
    project.HandoverDocumentation <- Some hd

    let imported = exportImport project store
    Assert.True(imported.HandoverDocumentation.IsSome)
    let ihd = imported.HandoverDocumentation.Value
    Assert.True(ihd.Documents.Count >= 1, sprintf "expected ≥1 Document, got %d" ihd.Documents.Count)
    let idoc = ihd.Documents.[0]
    Assert.True(idoc.DocumentIds.Count >= 1)
    Assert.Equal("DS2-DOC-007", idoc.DocumentIds.[0].ValueId)
    Assert.True(idoc.DocumentIds.[0].IsPrimary)
    Assert.True(idoc.DocumentClassifications.Count >= 1)
    Assert.Equal("03-02", idoc.DocumentClassifications.[0].ClassId)
    Assert.True(idoc.DocumentVersions.Count >= 1)
    let iv = idoc.DocumentVersions.[0]
    Assert.Equal("1.5", iv.DocumentVersionId)
    Assert.Equal("RT Test Doc", iv.Title)
    Assert.Equal("Subtitle here", iv.SubTitle)
    Assert.Equal("Summary text", iv.Summary)
    Assert.Equal("Released", iv.StatusValue)
    Assert.Contains("en", iv.Languages)
    Assert.Contains("ko", iv.Languages)
    Assert.Contains("/aasx/files/manual.pdf", iv.DigitalFiles)
    Assert.Equal("/aasx/files/preview.png", iv.PreviewFile)

[<Fact>]
let ``Nameplate AddressInformation round-trip`` () =
    let store, project = buildMinimalProject()
    let np = Nameplate()
    np.URIOfTheProduct <- "https://example.com/p"
    let addr = np.AddressInformation
    addr.Street <- "Main Street 1"
    addr.Zipcode <- "12345"
    addr.CityTown <- "Seoul"
    addr.NationalCode <- "KR"
    addr.Phone.TelephoneNumber <- "+82-2-1234-5678"
    addr.Phone.TypeOfTelephone <- "Office"
    addr.Email.EmailAddress <- "info@dualsoft.com"
    project.Nameplate <- Some np

    let imported = exportImport project store
    Assert.True(imported.Nameplate.IsSome)
    let inp = imported.Nameplate.Value
    let ia = inp.AddressInformation
    Assert.Equal("Main Street 1", ia.Street)
    Assert.Equal("12345", ia.Zipcode)
    Assert.Equal("Seoul", ia.CityTown)
    Assert.Equal("KR", ia.NationalCode)
    Assert.Equal("+82-2-1234-5678", ia.Phone.TelephoneNumber)
    Assert.Equal("Office", ia.Phone.TypeOfTelephone)
    Assert.Equal("info@dualsoft.com", ia.Email.EmailAddress)

[<Fact>]
let ``TechnicalData scalars round-trip`` () =
    let store, project = buildMinimalProject()
    let td = TechnicalData()
    td.GeneralInformation.ManufacturerName <- "DualSoft Inc."
    td.GeneralInformation.ManufacturerProductDesignation <- "X-Series"
    td.GeneralInformation.ManufacturerArticleNumber <- "ART-001"
    td.GeneralInformation.ManufacturerOrderCode <- "OC-100"

    let cls = TdProductClassificationItem()
    cls.ClassificationSystem <- "ECLASS"
    cls.ClassificationVersion <- "12.0"
    cls.ProductClassId <- "27-37-09-04"
    td.ProductClassifications.Add(cls)

    td.FurtherInformation.TextStatement <- "Validity terms apply"
    td.FurtherInformation.ValidDate <- "2030-01-01"
    project.TechnicalData <- Some td

    let imported = exportImport project store
    Assert.True(imported.TechnicalData.IsSome)
    let itd = imported.TechnicalData.Value
    Assert.Equal("DualSoft Inc.", itd.GeneralInformation.ManufacturerName)
    Assert.Equal("X-Series", itd.GeneralInformation.ManufacturerProductDesignation)
    Assert.Equal("ART-001", itd.GeneralInformation.ManufacturerArticleNumber)
    Assert.Equal("OC-100", itd.GeneralInformation.ManufacturerOrderCode)
    Assert.True(itd.ProductClassifications.Count >= 1)
    Assert.Equal("ECLASS", itd.ProductClassifications.[0].ClassificationSystem)
    Assert.Equal("12.0", itd.ProductClassifications.[0].ClassificationVersion)
    Assert.Equal("27-37-09-04", itd.ProductClassifications.[0].ProductClassId)
    Assert.Equal("Validity terms apply", itd.FurtherInformation.TextStatement)
    Assert.Equal("2030-01-01", itd.FurtherInformation.ValidDate)
