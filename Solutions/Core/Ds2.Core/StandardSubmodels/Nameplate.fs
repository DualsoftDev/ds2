namespace Ds2.Core

open System

/// Digital Nameplate (IDTA 02006-3-0) 관련 타입 정의
[<AutoOpen>]
module NameplateTypes =

    /// 연락처 정보 - Phone (AddressInformation/Phone SMC)
    type PhoneInfo() =
        member val TelephoneNumber = "" with get, set
        member val TypeOfTelephone = "" with get, set

    /// 연락처 정보 - Fax (AddressInformation/Fax SMC)
    type FaxInfo() =
        member val FaxNumber = "" with get, set
        member val TypeOfFaxNumber = "" with get, set

    /// 연락처 정보 - Email (AddressInformation/Email SMC)
    type EmailInfo() =
        member val EmailAddress = "" with get, set
        member val PublicKey = "" with get, set
        member val TypeOfEmailAddress = "" with get, set

    /// 주소 정보 (AddressInformation SMC)
    type AddressInfo() =
        member val Street = "" with get, set
        member val Zipcode = "" with get, set
        member val CityTown = "" with get, set
        member val NationalCode = "" with get, set
        member val Phone = PhoneInfo() with get, set
        member val Fax = FaxInfo() with get, set
        member val Email = EmailInfo() with get, set

    /// 마킹 정보 (Marking SMC)
    type MarkingInfo() =
        member val MarkingName = "" with get, set
        member val DesignationOfCertificateOrApproval = "" with get, set
        member val IssueDate = "" with get, set
        member val ExpiryDate = "" with get, set
        member val MarkingFile = "" with get, set
        member val MarkingAdditionalText = "" with get, set

    /// Digital Nameplate (IDTA 02006-3-0)
    /// AAS Submodel "Nameplate"에 대응하는 F# 타입
    type Nameplate() =
        // === 필수 요소 (Cardinality: One) ===
        /// 제품의 전역 고유 식별자 (IRDI: 0173-1#02-AAY811#001)
        member val URIOfTheProduct = "" with get, set
        /// 제조사명 (IRDI: 0173-1#02-AAO677#002)
        member val ManufacturerName = "" with get, set
        /// 제조사가 지정한 제품명 (IRDI: 0173-1#02-AAW338#001)
        member val ManufacturerProductDesignation = "" with get, set
        /// 주소 정보
        member val AddressInformation = AddressInfo() with get, set
        /// 제조사 주문 코드 (IRDI: 0173-1#02-AAO227#002)
        member val OrderCodeOfManufacturer = "" with get, set

        // === 선택 요소 (Cardinality: ZeroToOne) ===
        /// 제품 루트 (IRDI: 0173-1#02-AAU732#001)
        member val ManufacturerProductRoot = "" with get, set
        /// 제품 패밀리 (IRDI: 0173-1#02-AAU731#001)
        member val ManufacturerProductFamily = "" with get, set
        /// 제품 유형 (IRDI: 0173-1#02-AAO057#002)
        member val ManufacturerProductType = "" with get, set
        /// 제조사 제품 물품번호 (IRDI: 0173-1#02-AAO676#003)
        member val ProductArticleNumberOfManufacturer = "" with get, set
        /// 시리얼 번호 (IRDI: 0173-1#02-AAM556#002)
        member val SerialNumber = "" with get, set
        /// 제조 연도 (IRDI: 0173-1#02-AAP906#001)
        member val YearOfConstruction = "" with get, set
        /// 제조 일자 (IRDI: 0173-1#02-AAR972#002)
        member val DateOfManufacture = "" with get, set
        /// 하드웨어 버전 (IRDI: 0173-1#02-AAN270#002)
        member val HardwareVersion = "" with get, set
        /// 펌웨어 버전 (IRDI: 0173-1#02-AAM985#002)
        member val FirmwareVersion = "" with get, set
        /// 소프트웨어 버전 (IRDI: 0173-1#02-AAM737#002)
        member val SoftwareVersion = "" with get, set
        /// 원산지 국가 코드 (IRDI: 0173-1#02-AAO259#003, ISO 3166-1 alpha-2)
        member val CountryOfOrigin = "" with get, set
        /// 고유 시설 식별자 (ESPR 규정)
        member val UniqueFacilityIdentifier = "" with get, set
        /// 회사 로고 파일 경로 (IRDI: 0173-1#02-AAW515#001)
        member val CompanyLogo = "" with get, set

        // === 컬렉션 요소 ===
        /// 마킹 정보 목록 (Markings SML)
        member val Markings = ResizeArray<MarkingInfo>() with get, set
