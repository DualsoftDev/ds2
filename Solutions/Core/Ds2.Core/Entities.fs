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
    [<AasxField("FlowIds")>] member val FlowIds = ResizeArray<Guid>() with get, set

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this


/// 공정 흐름 단위.
type Flow [<JsonConstructor>] internal (name, parentId) =
    inherit DsChild(name, parentId)

    member val Properties = ResizeArray<FlowSubmodelProperty>() with get, set
    [<AasxField("WorkIds")>] member val WorkIds = ResizeArray<Guid>() with get, set

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this


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


// =============================================================================
// Call / ApiCall / CallCondition / ApiDef  (상호참조로 and 사용)
// =============================================================================

/// 장치 API 호출 단위.
/// Name = "{DevicesAlias}.{ApiName}" 형태로 구성됨.
type Call [<JsonConstructor>] internal (devicesAlias: string, apiName: string, parentId: Guid) =
    inherit DsChild("", parentId)

    member val Properties = ResizeArray<CallSubmodelProperty>() with get, set

    [<AasxField("Status")>]                 member val Status4        : Status4                = Status4.Ready  with get, set
    [<AasxField("Position")>]               member val Position       : Xywh option            = None           with get, set
    [<AasxField("ApiCalls",  Skip = true)>] member val ApiCalls = ResizeArray<ApiCall>()               with get, set
    [<AasxField("CallConditions")>]         member val CallConditions = ResizeArray<CallCondition>()            with get, set
    [<AasxField("ReferenceOf")>]            member val ReferenceOf    : Guid option            = None           with get, set

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

and ApiCall [<JsonConstructor>] internal (name) =
    inherit DsEntity(name)

    [<AasxField("InTag")>]        member val InTag        : IOTag option = None           with get, set
    [<AasxField("OutTag")>]       member val OutTag       : IOTag option = None           with get, set
    [<AasxField("ApiDefId")>]     member val ApiDefId     : Guid option  = None           with get, set
    [<AasxField("InputSpec")>]    member val InputSpec    : ValueSpec    = UndefinedValue  with get, set
    [<AasxField("OutputSpec")>]   member val OutputSpec   : ValueSpec    = UndefinedValue  with get, set
    [<AasxField("OriginFlowId")>] member val OriginFlowId : Guid option  = None           with get, set

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

and CallCondition [<JsonConstructor>] internal () =
    member val Id         : Guid                     = Guid.NewGuid() with get, set
    member val Type       : CallConditionType option = None           with get, set
    member val Conditions = ResizeArray<ApiCall>()                    with get, set
    member val Children   = ResizeArray<CallCondition>()              with get, set
    member val IsOR       = false                                     with get, set
    member val IsRising   = false                                     with get, set

    // DsEntity 비상속 → jsonClone (ID 보존)
    member this.DeepCopy() = DeepCopyHelper.jsonClone<CallCondition> this

and ApiDef [<JsonConstructor>] internal (name, parentId) =
    inherit DsChild(name, parentId)

    [<AasxField("ApiDefActionType")>] member val ApiDefActionType : ApiDefActionType = ApiDefActionType.Normal with get, set
    [<AasxField("TxGuid")>]           member val TxGuid : Guid option = None  with get, set
    [<AasxField("RxGuid")>]           member val RxGuid : Guid option = None  with get, set

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
