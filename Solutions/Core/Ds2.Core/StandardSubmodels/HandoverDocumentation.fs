namespace Ds2.Core

open System

/// Handover Documentation (IDTA 02004-1-2) 관련 타입 정의
/// VDI 2770 표준 기반 문서 메타데이터
[<AutoOpen>]
module HandoverDocumentationTypes =

    /// 문서 ID 정보 (DocumentId SMC)
    /// IRDI: 0173-1#02-ABI501#001/0173-1#01-AHF580#001
    type DocumentId() =
        /// 문서 도메인 식별자 (예: "ManufacturerDocumentId", "CustomerDocumentId")
        member val DocumentDomainId = "" with get, set
        /// 문서 값 ID
        member val ValueId = "" with get, set
        /// 기본 문서 ID 여부
        member val IsPrimary = false with get, set

    /// 문서 분류 정보 (DocumentClassification SMC)
    /// IRDI: 0173-1#02-ABI502#001/0173-1#01-AHF581#001
    type DocumentClassification() =
        /// 분류 ID (VDI 2770 코드, 예: "02-01", "03-02")
        member val ClassId = "" with get, set
        /// 분류명 (예: "Technical specification", "Operating instructions")
        member val ClassName = "" with get, set
        /// 분류 시스템 (기본값: "VDI2770:2018")
        member val ClassificationSystem = "VDI2770:2018" with get, set

    /// 문서 버전 정보 (DocumentVersion SMC)
    /// IRDI: 0173-1#02-ABI503#001/0173-1#01-AHF582#001
    type DocumentVersion() =
        /// 언어 목록 (ISO 639-1, 예: "en", "de", "ko")
        member val Languages = ResizeArray<string>() with get, set
        /// 문서 버전 ID
        member val DocumentVersionId = "" with get, set
        /// 문서 제목
        member val Title = "" with get, set
        /// 부제목
        member val SubTitle = "" with get, set
        /// 요약
        member val Summary = "" with get, set
        /// 키워드
        member val KeyWords = "" with get, set
        /// 설정 일자 (xs:date 형식, 예: "2024-01-15")
        member val SetDate = "" with get, set
        /// 상태 설정 일자
        member val StatusSetDate = "" with get, set
        /// 상태 값 (예: "Released", "Draft")
        member val StatusValue = "" with get, set
        /// 조직명
        member val OrganizationName = "" with get, set
        /// 조직 공식명
        member val OrganizationOfficialName = "" with get, set
        /// 역할 (예: "Author", "Manufacturer")
        member val Role = "" with get, set
        /// 디지털 파일 경로 목록
        member val DigitalFiles = ResizeArray<string>() with get, set
        /// 미리보기 파일 경로
        member val PreviewFile = "" with get, set

    /// 문서 정보 (Document SMC)
    /// IRDI: 0173-1#02-ABI500#001/0173-1#01-AHF579#001
    type Document() =
        /// 문서 ID 목록 (1..*)
        member val DocumentIds = ResizeArray<DocumentId>() with get, set
        /// 문서 분류 목록 (1..*, VDI 2770 필수)
        member val DocumentClassifications = ResizeArray<DocumentClassification>() with get, set
        /// 문서 버전 목록 (1..*)
        member val DocumentVersions = ResizeArray<DocumentVersion>() with get, set

    /// Handover Documentation (IDTA 02004-1-2)
    /// AAS Submodel "HandoverDocumentation"에 대응하는 F# 타입
    /// VDI 2770 표준에 따른 문서 인수인계 정보
    type HandoverDocumentation() =
        /// 문서 목록 (0..*)
        member val Documents = ResizeArray<Document>() with get, set

        /// 기본 샘플 Document 생성 (프로젝트 초기화 시 호출)
        static member CreateWithDefaultDocument() : HandoverDocumentation =
            let doc = HandoverDocumentation()

            let sampleDoc = Document()

            // DocumentId 추가 (필수)
            let docId = DocumentId()
            docId.DocumentDomainId <- "ManufacturerDocumentId"
            docId.ValueId <- "DS2-DOC-001"
            docId.IsPrimary <- true
            sampleDoc.DocumentIds.Add(docId)

            // DocumentClassification 추가 (필수)
            let classification = DocumentClassification()
            classification.ClassId <- "03-02"
            classification.ClassName <- "Operating instructions"
            classification.ClassificationSystem <- "VDI2770:2018"
            sampleDoc.DocumentClassifications.Add(classification)

            // DocumentVersion 추가 (필수)
            let version = DocumentVersion()
            version.Languages.Add("en")
            version.DocumentVersionId <- "1.0"
            version.Title <- "Project Documentation"
            version.SubTitle <- "DS2 Sequence Model"
            version.OrganizationName <- "Dualsoft"
            version.Role <- "Manufacturer"
            version.StatusValue <- "Released"
            sampleDoc.DocumentVersions.Add(version)

            doc.Documents.Add(sampleDoc)
            doc
