namespace Ds2.Core

open System

/// IDTA 02003 — Submodel "TechnicalData" (v1.2)
///
/// 본 타입은 Nameplate / HandoverDocumentation 처럼 **얇은 표준 컨테이너**.
/// 4-블록 골격 중 표준 3 블록(GeneralInformation / ProductClassifications /
/// FurtherInformation) 만 보유. 시퀀스/시뮬레이션 도메인 데이터는 별도 위치:
///   - 시뮬레이션 KPI 박제 → Project.SimulationResult (SequenceSimulation 서브모델로 emit)
///   - 시퀀스 도메인 통계  → 각 도메인 서브모델 (SequenceModel/Control/Monitoring 등)
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

    // ── FurtherInformation ─────────────────────────────────────────────────
    type TdFurtherInformation() =
        member val TextStatement     = "" with get, set
        member val ValidDate         = "" with get, set
        member val ReferenceDocuments = ResizeArray<string>() with get, set

    /// IDTA 02003 TechnicalData 서브모델 — 표준 3 블록만 보유 (얇은 컨테이너).
    type TechnicalData() =
        member val GeneralInformation     = TdGeneralInformation() with get, set
        member val ProductClassifications = ResizeArray<TdProductClassificationItem>() with get, set
        member val FurtherInformation     = TdFurtherInformation() with get, set
