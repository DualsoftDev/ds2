namespace Ds2.Core

open System

/// IDTA 02003 — Submodel "TechnicalData" (v1.2)
/// 4-블록 골격(GeneralInformation / ProductClassifications / TechnicalProperties / FurtherInformation)을
/// 그대로 따르며, ProMaker 시퀀스 도메인용 그룹과 시뮬결과 박제(SimulationResults SML)를 함께 보관한다.
[<AutoOpen>]
module TechnicalDataTypes =

    // ── GeneralInformation ─────────────────────────────────────────────────
    type TdGeneralInformation() =
        member val ManufacturerName               = "" with get, set
        member val ManufacturerProductDesignation = "" with get, set
        member val ManufacturerArticleNumber      = "" with get, set
        member val ManufacturerOrderCode          = "" with get, set
        /// 제품 이미지(들) — 파일 경로 (선택)
        member val ProductImages = ResizeArray<string>() with get, set

    // ── ProductClassifications ─────────────────────────────────────────────
    type TdProductClassificationItem() =
        member val ClassificationSystem  = "" with get, set
        member val ClassificationVersion = "" with get, set
        member val ProductClassId        = "" with get, set

    // ── TechnicalProperties — 도메인 특화 그룹들 ────────────────────────────
    /// SequenceCharacteristics — 시퀀스 모델 정체성/규모
    type TdSequenceCharacteristics() =
        member val SequenceName         = "" with get, set
        member val SequenceVersion      = "" with get, set
        member val CycleTimeNominal_s   = 0.0 with get, set
        member val CycleTimeMin_s       = 0.0 with get, set
        member val CycleTimeMax_s       = 0.0 with get, set
        member val StepCount            = 0 with get, set
        member val ParallelBranchCount  = 0 with get, set
        /// 디지털 스레드 키 — 같은 모델에서 나온 산출물 끼리 묶기 위함
        member val Ds2ModelHash         = "" with get, set
        member val SafetyCategory       = "" with get, set

    /// IOCharacteristics — 신호/통신 규모
    type TdIoCharacteristics() =
        member val DigitalInputCount  = 0 with get, set
        member val DigitalOutputCount = 0 with get, set
        member val AnalogInputCount   = 0 with get, set
        member val AnalogOutputCount  = 0 with get, set
        member val FieldbusProtocols  = ResizeArray<string>() with get, set
        member val ScanCycle_ms       = 0 with get, set

    /// ApiSurface — ApiCall 시그니처 요약 (상세는 AID SM 담당)
    type TdApiSurface() =
        member val ApiCallCount             = 0 with get, set
        member val ExposedActions           = ResizeArray<string>() with get, set
        member val ExposedReadProperties    = ResizeArray<string>() with get, set
        member val ExposedWriteProperties   = ResizeArray<string>() with get, set

    /// ControllerInfo — 제어기/엔지니어링 툴
    type TdControllerInfo() =
        member val ControllerVendor   = "" with get, set
        member val ControllerModel    = "" with get, set
        member val FirmwareVersion    = "" with get, set
        member val EngineeringTool    = "DualSoft ProMaker" with get, set

    // ── FurtherInformation ─────────────────────────────────────────────────
    type TdFurtherInformation() =
        member val TextStatement     = "" with get, set
        member val ValidDate         = "" with get, set
        member val ReferenceDocuments = ResizeArray<string>() with get, set

    /// IDTA 02003 TechnicalData 서브모델
    type TechnicalData() =
        member val GeneralInformation     = TdGeneralInformation() with get, set
        member val ProductClassifications = ResizeArray<TdProductClassificationItem>() with get, set

        // TechnicalProperties 하위 그룹 (ProMaker 시퀀스 도메인 특화)
        member val SequenceCharacteristics = TdSequenceCharacteristics() with get, set
        member val IoCharacteristics       = TdIoCharacteristics() with get, set
        member val ApiSurface              = TdApiSurface() with get, set
        member val ControllerInfo          = TdControllerInfo() with get, set

        /// 시뮬레이션 결과 (단일 항목 정책 — 최종본만 보관, 누적 안 함)
        member val SimulationResult : SimulationScenario option = None with get, set

        member val FurtherInformation = TdFurtherInformation() with get, set
