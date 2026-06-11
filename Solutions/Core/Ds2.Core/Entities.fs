namespace Ds2.Core

open System
open System.Text.Json.Serialization

// =============================================================================
// 엔티티 (Entity)
// =============================================================================

/// 프로젝트 루트 엔티티.
/// DsSystem과 상호참조 없음 — ID 목록으로만 연결.
type Project [<JsonConstructor>] internal (name) =
    inherit DsEntity(name)

    // ── 연결된 시스템 (ID 참조) ──────────────────────────────────────────────
    [<AasxField("ActiveSystemIds",       Skip = true)>] member val ActiveSystemIds       = ResizeArray<Guid>() with get, set
    [<AasxField("PassiveSystemIds",      Skip = true)>] member val PassiveSystemIds      = ResizeArray<Guid>() with get, set

    // ── 별도 Submodel로 직렬화되는 필드 ─────────────────────────────────────
    [<AasxField("Nameplate",             Skip = true)>] member val Nameplate             : Nameplate option             = None    with get, set
    [<AasxField("HandoverDocumentation", Skip = true)>] member val HandoverDocumentation : HandoverDocumentation option = None    with get, set
    [<AasxField("TechnicalData",         Skip = true)>] member val TechnicalData         : TechnicalData option         = None    with get, set
    /// SequenceSimulation 서브모델로 emit 되는 시뮬레이션 박제 (Meta + KPI 그룹).
    /// 이전에는 TechnicalData.SimulationResult 였음. AAS 표준 SM 분리 정책에 따라 Project 레벨로 이동.
    [<AasxField("SimulationResult",      Skip = true)>] member val SimulationResult      : SimulationScenario option    = None    with get, set

    // ── 프로젝트 메타데이터 ──────────────────────────────────────────────────
    [<AasxField("TokenSpecs")>]                         member val TokenSpecs            = ResizeArray<TokenSpec>()              with get, set
    [<AasxField("Author")>]                             member val Author                : string         = ""                  with get, set
    [<AasxField("DateTime")>]                           member val DateTime              : DateTimeOffset = DateTimeOffset.Now  with get, set
    [<AasxField("Version")>]                            member val Version               : string         = "1.0.0"             with get, set



/// 장치·설비 등 독립 시스템 단위.
type DsSystem [<JsonConstructor>] internal (name) =
    inherit DsEntity(name)

    member val Properties = ResizeArray<SystemSubmodelProperty>() with get, set

    [<AasxField("IRI")>]        member val IRI        : string option = None with get, set
    [<AasxField("SystemType")>] member val SystemType : string option = None with get, set

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this


/// 공정 흐름 단위.
type Flow [<JsonConstructor>] internal (name, parentId) =
    inherit DsChild(name, parentId)

    member val Properties = ResizeArray<FlowSubmodelProperty>() with get, set

    /// true 면 비활성화(숨김) — 트리/캔버스/런타임에서 제외되고 비활성화 섹션으로 이동한다.
    [<AasxField("IsDisabled")>] member val IsDisabled : bool = false with get, set

    /// v12 abnormal §2.3 GATING — false(수동 모드)면 이 Flow 의 abnormal 평가 제외.
    /// 기본 true(자동 운전). spec `call.Flow.IsAuto`.
    [<AasxField("IsAuto")>] member val IsAuto : bool = true with get, set

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this


// =============================================================================
// ApiCall / ApiDef (Guid 참조만이라 다른 entity 와 type-level 결합 없음)
// =============================================================================

type ApiCall [<JsonConstructor>] internal (name) =
    inherit DsEntity(name)

    [<AasxField("InTag")>]        member val InTag        : IOTag option = None           with get, set
    [<AasxField("OutTag")>]       member val OutTag       : IOTag option = None           with get, set
    [<AasxField("ApiDefId")>]     member val ApiDefId     : Guid option  = None           with get, set
    [<AasxField("InputSpec")>]    member val InputSpec    : ValueSpec    = UndefinedValue  with get, set
    [<AasxField("OutputSpec")>]   member val OutputSpec   : ValueSpec    = UndefinedValue  with get, set
    [<AasxField("OriginFlowId")>] member val OriginFlowId : Guid option  = None           with get, set
    [<AasxField("ContactKind")>]  member val ContactKind  : ContactKind  = ContactKind.NoContact with get, set
    // v10: SkipInputSensor 제거 — 가상 센서는 ApiDef.SensingType = Virtual 로 표현.

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

and ApiDef [<JsonConstructor>] internal (name, parentId) =
    inherit DsChild(name, parentId)

    /// 출력 시점 정책. 기본값 Normal None = 일반 coil (센서 감지 완료까지 유지).
    [<AasxField("ActionType")>]  member val ActionType  : ActionType  = ActionType.Normal None with get, set
    /// 감지 시점 정책. 기본값 Normal None = 일반 contact (감지 즉시 완료).
    [<AasxField("SensingType")>] member val SensingType : SensingType = SensingType.Normal None with get, set
    [<AasxField("TxGuid")>]      member val TxGuid : Guid option = None  with get, set
    [<AasxField("RxGuid")>]      member val RxGuid : Guid option = None  with get, set
    [<AasxField("Description")>] member val Description : string option = None  with get, set

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this


// =============================================================================
// Condition — ApiCall 참조 (Call / Work 공용)
// =============================================================================

type Condition [<JsonConstructor>] internal () =
    member val Id         : Guid                  = Guid.NewGuid() with get, set
    member val Type       : ConditionType option  = None           with get, set
    member val ApiCalls   = ResizeArray<ApiCall>()                 with get, set
    member val Children   = ResizeArray<Condition>()               with get, set
    member val IsOR       = false                                  with get, set
    member val IsInverted = false                                  with get, set

    // DsEntity 비상속 → jsonClone (ID 보존)
    member this.DeepCopy() = DeepCopyHelper.jsonClone<Condition> this


// =============================================================================
// Work / Call (Condition 참조)
// =============================================================================

/// Flow 내 작업 단위.
/// Name = "{FlowPrefix}.{LocalName}" 형태로 구성됨.
type Work [<JsonConstructor>] internal (flowPrefix: string, localName: string, parentId: Guid) =
    inherit DsChild("", parentId)

    member val Properties = ResizeArray<WorkSubmodelProperty>() with get, set

    // ── Name 구성요소 (Name에서 파생 가능 → AASX 저장 불필요) ────────────────
    [<AasxField("FlowPrefix", Skip = true)>] member val FlowPrefix : string = (if isNull flowPrefix then "" else flowPrefix) with get, set
    [<AasxField("LocalName",  Skip = true)>] member val LocalName  : string = (if isNull localName  then "" else localName)  with get, set

    // ── 작업 속성 ────────────────────────────────────────────────────────────
    [<AasxField("ReferenceOf")>] member val ReferenceOf : Guid option  = None         with get, set
    [<AasxField("Status")>]      member val Status4     : Status4      = Status4.Ready with get, set
    [<AasxField("Position")>]    member val Position    : Xywh option  = None         with get, set
    [<AasxField("TokenRole")>]   member val TokenRole   : TokenRole    = TokenRole.None with get, set
    [<AasxField("Duration")>]    member val Duration    : TimeSpan option = None      with get, set
    [<AasxField("MinDuration")>] member val MinDuration : TimeSpan option = None      with get, set
    [<AasxField("MaxDuration")>] member val MaxDuration : TimeSpan option = None      with get, set

    // ── 조건 트리 (SkipAction 만 의미 — Call 과 동일 Condition 타입 공유) ───
    [<AasxField("Conditions")>]  member val Conditions  = ResizeArray<Condition>() with get, set

    override this.Name
        with get() =
            if String.IsNullOrEmpty(this.FlowPrefix) then this.LocalName
            else $"{this.FlowPrefix}.{this.LocalName}"
        and set value =
            match value.IndexOf('.') with
            | -1  -> this.LocalName <- value
            | idx ->
                this.FlowPrefix <- value[..idx - 1]
                this.LocalName  <- value[idx + 1..]

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this


/// 장치 API 호출 단위.
/// Name = "{DevicesAlias}.{ApiName}" 형태로 구성됨.
type Call [<JsonConstructor>] internal (devicesAlias: string, apiName: string, parentId: Guid) =
    inherit DsChild("", parentId)

    member val Properties = ResizeArray<CallSubmodelProperty>() with get, set

    [<AasxField("Status")>]                 member val Status4    : Status4              = Status4.Ready  with get, set
    [<AasxField("Position")>]               member val Position   : Xywh option          = None           with get, set
    [<AasxField("ApiCalls",  Skip = true)>] member val ApiCalls   = ResizeArray<ApiCall>()                with get, set
    [<AasxField("Conditions")>]             member val Conditions = ResizeArray<Condition>()              with get, set
    [<AasxField("ReferenceOf")>]            member val ReferenceOf : Guid option         = None           with get, set
    [<AasxField("SequenceLabel")>]          member val SequenceLabel : SequenceLabel     = SequenceLabel.Body with get, set
    /// v12 abnormal §2.3 GATING — true(인터락 중)면 이 Call 의 abnormal 평가 제외. 기본 false. spec `call.Interlocked`.
    [<AasxField("Interlocked")>]            member val Interlocked : bool                = false          with get, set

    // ── Name 구성요소 (Name에서 파생 가능 → AASX 저장 불필요) ────────────────
    [<AasxField("DevicesAlias", Skip = true)>] member val DevicesAlias = devicesAlias with get, set
    [<AasxField("ApiName",      Skip = true)>] member val ApiName      = apiName      with get, set

    override this.Name
        with get() = $"{this.DevicesAlias}.{this.ApiName}"
        and  set value =
            match value.IndexOf('.') with
            | -1  ->
                invalidArg (nameof value)
                    $"Call 이름 형식 오류: '{value}'. 올바른 형식: 'DevicesAlias.ApiName'"
            | idx ->
                let alias   = value[..idx - 1]
                let apiName = value[idx + 1..]
                if not (String.IsNullOrEmpty(this.ApiName)) && this.ApiName <> apiName then
                    invalidArg (nameof value)
                        $"Call Name setter는 ApiName 변경을 허용하지 않습니다. 기존='{this.ApiName}', 입력='{apiName}'"
                this.DevicesAlias <- alias
                this.ApiName      <- apiName

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this


// =============================================================================
// Arrow
// =============================================================================

type ArrowBetweenWorks [<JsonConstructor>] internal (parentId, sourceId, targetId, arrowType) =
    inherit DsArrow(parentId, sourceId, targetId, arrowType)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

type ArrowBetweenCalls [<JsonConstructor>] internal (parentId, sourceId, targetId, arrowType) =
    inherit DsArrow(parentId, sourceId, targetId, arrowType)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this
