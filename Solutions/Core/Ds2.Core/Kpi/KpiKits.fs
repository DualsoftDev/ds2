namespace Ds2.Core.Kpi

open Ds2.Core.StandardSubmodels

/// 시퀀스 엔티티 → 자동 생성 KPI Kit 규약 (Convention-Driven Approach A).
///
/// 이 파일은 **규약 그 자체**. 확장/변경 시 여기 한 곳만 편집하면
/// AID / AIMC / OperationalData 3 서브모델과 CD 등록까지 일괄 반영됨.
///
/// 원칙:
/// - 엔티티 타입별 고정 메트릭 세트 (System/Work/Call/ArrowWork/UserTag)
/// - 각 메트릭은 SemanticId 규칙 `urn:ds:kpi/{Entity}/{Metric}/1/0`
/// - append 는 idempotent — (SubmodelId, SemanticId, IdShort) 3-tuple 기반
[<AutoOpen>]
module KpiKitTypes =

    /// 수집 힌트 — AID SignalPolicy 및 Adapter 로 힌트 전달.
    type UpdateHint =
        /// 주기적 sampling (밀리초)
        | Cyclic of intervalMs: int
        /// 값 변화 시 이벤트 전송
        | OnChange
        /// 외부 계산 결과 push (기본 없음, 애플리케이션 계산기가 넣음)
        | Computed

    /// 하나의 KPI 메트릭 정의.
    type KpiMetric = {
        /// IdShort 접미사 (`Kpi_Sys_XXXXXXXX_OEE` 의 마지막 부분)
        IdShortSuffix: string
        /// SemanticId — CD 등록도 이 ID 로.
        SemanticId: string
        /// AAS DataType (AID `type`, OpData `ValueType`)
        DataType: XsdType
        /// 단위 (없으면 "")
        Unit: string
        /// 수집 힌트
        UpdateHint: UpdateHint
        /// KPI 요약 (CD Description 에 사용)
        DescriptionKr: string
        DescriptionEn: string
    }

    /// 엔티티 타입 하나에 대한 KPI Kit.
    type KpiEntityKind =
        | SystemKind
        | WorkKind
        | CallKind
        | ArrowWorkKind
        | UserTagKind

    type EntityKpiKit = {
        Kind: KpiEntityKind
        /// 엔티티 타입 짧은 이름 (SemanticId · IdShort 에 사용)
        TypeShort: string
        /// 규약된 메트릭 리스트
        Metrics: KpiMetric list
    }


module KpiKits =

    /// SemanticId 규칙: urn:ds:kpi/{Entity}/{Metric}/1/0
    let semanticId (entityShort: string) (metric: string) : string =
        sprintf "urn:ds:kpi/%s/%s/1/0" entityShort metric

    let private metric idShort dataType unit hint krDesc enDesc =
        { IdShortSuffix = idShort
          SemanticId    = semanticId "" idShort  // 나중에 kit 조립 시 교체
          DataType      = dataType
          Unit          = unit
          UpdateHint    = hint
          DescriptionKr = krDesc
          DescriptionEn = enDesc }

    /// System 표준 KPI 6종. 실제 생성 대상은 KpiWalker 에서 Active System으로 제한한다.
    let systemKit : EntityKpiKit =
        let kind = "System"
        { Kind = SystemKind
          TypeShort = kind
          Metrics = [
            { metric "OEE"          XsDouble "%" (Cyclic 1000) "설비 종합 효율 (Availability × Performance × Quality)"       "Overall Equipment Effectiveness"
                with SemanticId = semanticId kind "OEE" }
            { metric "Availability" XsDouble "%" (Cyclic 1000) "가용률 = 실가동시간 / 계획가동시간"                             "Availability rate"
                with SemanticId = semanticId kind "Availability" }
            { metric "Performance"  XsDouble "%" (Cyclic 1000) "성능률 = 이론사이클타임 / 실사이클타임"                          "Performance rate"
                with SemanticId = semanticId kind "Performance" }
            { metric "Quality"      XsDouble "%" (Cyclic 1000) "양품률 = 양품수 / 총생산수"                                    "Quality rate"
                with SemanticId = semanticId kind "Quality" }
            { metric "MTBF"         XsDouble "s" (Cyclic 60000) "Mean Time Between Failures"                                 "Mean Time Between Failures"
                with SemanticId = semanticId kind "MTBF" }
            { metric "MTTR"         XsDouble "s" (Cyclic 60000) "Mean Time To Repair"                                        "Mean Time To Repair"
                with SemanticId = semanticId kind "MTTR" }
          ] }

    /// Work (작업 단계) 표준 KPI 4종.
    let workKit : EntityKpiKit =
        let kind = "Work"
        { Kind = WorkKind
          TypeShort = kind
          Metrics = [
            { metric "CT"            XsDouble "s" OnChange     "Cycle Time — 사이클 타임"                          "Cycle Time"
                with SemanticId = semanticId kind "CT" }
            { metric "MT"            XsDouble "s" OnChange     "Machine Time — 실행 시간"                          "Machine Time"
                with SemanticId = semanticId kind "MT" }
            { metric "IdleTime"      XsDouble "s" OnChange     "Idle Time — 대기 시간"                             "Idle Time"
                with SemanticId = semanticId kind "IdleTime" }
            { metric "DowntimeCount" XsLong   ""  OnChange     "Downtime Count — 정지 횟수"                        "Downtime count"
                with SemanticId = semanticId kind "DowntimeCount" }
          ] }

    /// Call (=Action, 디바이스 API 호출) 표준 KPI 3종.
    let callKit : EntityKpiKit =
        let kind = "Call"
        { Kind = CallKind
          TypeShort = kind
          Metrics = [
            { metric "ExecCount"        XsLong   ""    OnChange "Call 실행 횟수 누계"                    "Execution count"
                with SemanticId = semanticId kind "ExecCount" }
            { metric "LastDurationMs"   XsDouble "ms"  OnChange "직전 호출의 소요 시간 (밀리초)"         "Last execution duration"
                with SemanticId = semanticId kind "LastDurationMs" }
            { metric "TimeoutCount"     XsLong   ""    OnChange "타임아웃 발생 횟수 누계"                 "Timeout count"
                with SemanticId = semanticId kind "TimeoutCount" }
          ] }

    /// ArrowWork (Work 간 전이) 표준 KPI 2종.
    let arrowWorkKit : EntityKpiKit =
        let kind = "ArrowWork"
        { Kind = ArrowWorkKind
          TypeShort = kind
          Metrics = [
            { metric "TransitionCount" XsLong   ""   OnChange "전이 발생 횟수 누계"                       "Transition count"
                with SemanticId = semanticId kind "TransitionCount" }
            { metric "AvgLatencyMs"    XsDouble "ms" OnChange "직전 N개 전이의 평균 지연 (밀리초)"        "Average transition latency"
                with SemanticId = semanticId kind "AvgLatencyMs" }
          ] }

    /// UserTag — InTag/OutTag pass-through. 사용자 IOTag 를 그대로 KPI 신호로 노출.
    /// 이 kit 은 metric 개수가 IOTag 개수에 따라 가변이므로, `Metrics` 는 placeholder.
    /// 실제 walk 시점에 IOTag 마다 KpiMetric 이 동적으로 생성됨.
    let userTagKit : EntityKpiKit =
        let kind = "UserTag"
        { Kind = UserTagKind
          TypeShort = kind
          Metrics = [] }  // 동적 확장

    /// 규약 전체.
    let all : EntityKpiKit list = [
        systemKit; workKit; callKit; arrowWorkKit; userTagKit
    ]

    /// 모든 컴파일-타임 확정 SemanticId — CD 등록기가 사용.
    let allStaticSemanticIds : string seq =
        all
        |> Seq.collect (fun k -> k.Metrics |> Seq.map (fun m -> m.SemanticId))
