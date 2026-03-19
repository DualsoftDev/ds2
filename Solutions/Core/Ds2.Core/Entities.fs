namespace Ds2.Core

open System
open System.Text.Json.Serialization

// =============================================================================
// 엔티티
// =============================================================================

// Project와 DsSystem의 상호참조(and) 제거
// 이유: 두 타입은 실제 서로를 참조하지 않음
type Project [<JsonConstructor>] internal (name) =
    inherit DsEntity(name)
    member val Properties             = ProjectProperties()        with get, set
    member val ActiveSystemIds        = ResizeArray<Guid>()        with get, set
    member val PassiveSystemIds       = ResizeArray<Guid>()        with get, set
    member val Nameplate              = Nameplate()                with get, set
    member val HandoverDocumentation  = HandoverDocumentation()    with get, set
    member val TokenSpecs             = ResizeArray<TokenSpec>()   with get, set

type DsSystem [<JsonConstructor>] internal (name) =
    inherit DsEntity(name)
    member val Properties = SystemProperties() with get, set
    member val IRI : string option = None      with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

type Flow [<JsonConstructor>] internal (name, parentId) =
    inherit DsChild(name, parentId)
    member val Properties = FlowProperties() with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

type Work [<JsonConstructor>] internal (name, parentId) =
    inherit DsChild(name, parentId)
    member val Properties = WorkProperties()    with get, set
    member val Status4    : Status4 = Status4.Ready with get, set
    member val Position   : Xywh option = None  with get, set
    member val TokenRole  : TokenRole = TokenRole.None with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

// Call, ApiCall, CallCondition, ApiDef 은 실제 상호참조가 있어 and 유지
//   Call         → ApiCall, CallCondition
//   CallCondition → ApiCall
type Call [<JsonConstructor>] internal (devicesAlias: string, apiName: string, parentId: Guid) =
    inherit DsChild("", parentId)
    member val Properties     = CallProperties()             with get, set
    member val Status4        : Status4 = Status4.Ready      with get, set
    member val Position       : Xywh option = None           with get, set
    member val ApiCalls       = ResizeArray<ApiCall>()       with get, set
    member val CallConditions = ResizeArray<CallCondition>() with get, set
    /// 저장된 Device 별칭 — '.'을 포함할 수 없음
    member val DevicesAlias   = devicesAlias with get, set
    /// 저장된 ApiDef 이름 — Rename 대상이 아님, ApiDef에 연동
    member val ApiName        = apiName      with get, set

    override this.Name
        with get() = $"{this.DevicesAlias}.{this.ApiName}"
        and  set value =
            match value.IndexOf('.') with
            | -1  ->
                invalidArg (nameof value)
                    $"Call 이름 형식 오류: '{value}'. 올바른 형식: 'DevicesAlias.ApiName'"
            | idx ->
                let alias = value[..idx - 1]
                let apiName = value[idx + 1..]
                if this.ApiName <> apiName then
                    invalidArg (nameof value)
                        $"Call Name setter는 ApiName 변경을 허용하지 않습니다. 기존='{this.ApiName}', 입력='{apiName}'"
                this.DevicesAlias <- alias

    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

and ApiCall [<JsonConstructor>] internal (name) =
    inherit DsEntity(name)
    member val InTag      : IOTag option  = None           with get, set
    member val OutTag     : IOTag option  = None           with get, set
    member val ApiDefId   : Guid option   = None           with get, set
    member val InputSpec  : ValueSpec     = UndefinedValue with get, set
    member val OutputSpec : ValueSpec     = UndefinedValue with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

and CallCondition [<JsonConstructor>] internal () =
    member val Id         : Guid                      = Guid.NewGuid() with get, set
    member val Type       : CallConditionType option  = None           with get, set
    member val Conditions = ResizeArray<ApiCall>()                     with get, set
    member val Children   = ResizeArray<CallCondition>()               with get, set
    member val IsOR       = false with get, set
    member val IsRising   = false with get, set
    // CallCondition은 DsEntity 비상속 → jsonClone (ID 보존)
    member this.DeepCopy() = DeepCopyHelper.jsonClone<CallCondition> this

and ApiDef [<JsonConstructor>] internal (name, parentId) =
    inherit DsChild(name, parentId)
    member val Properties = ApiDefProperties() with get, set
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

// =============================================================================
// Hardware Component
// =============================================================================

type HwButton    [<JsonConstructor>] internal (name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

type HwLamp      [<JsonConstructor>] internal (name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

type HwCondition [<JsonConstructor>] internal (name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

type HwAction    [<JsonConstructor>] internal (name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this
