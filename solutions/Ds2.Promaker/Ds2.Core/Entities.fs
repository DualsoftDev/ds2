namespace Ds2.Core

open System
open System.Text.Json

// =============================================================================
// 엔티티
// =============================================================================

type Project(name) =
    inherit DsEntity(name)
    member val Properties = ProjectProperties() with get, set
    member val ActiveSystemIds = ResizeArray<Guid>() with get, set
    member val PassiveSystemIds = ResizeArray<Guid>() with get, set

and DsSystem(name) =
    inherit DsEntity(name)
    member val Properties = SystemProperties() with get, set
    member val IRI : string option = None with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> DsSystem

type Flow(name, parentId) =
    inherit DsChild(name, parentId)
    member val Properties = FlowProperties() with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> Flow

type Work(name, parentId) =
    inherit DsChild(name, parentId)
    member val Properties = WorkProperties() with get, set
    member val Status4   : Status4 = Status4.Ready with get, set
    member val Position  : Xywh option = None with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> Work

type Call(devicesAlias: string, apiName: string, parentId: Guid) =
    inherit DsChild("", parentId)
    member val Properties     = CallProperties() with get, set
    member val Status4        : Status4 = Status4.Ready with get, set
    member val Position       : Xywh option = None with get, set
    member val ApiCalls       = ResizeArray<ApiCall>() with get, set
    member val CallConditions = ResizeArray<CallCondition>() with get, set
    /// 저장된 Device 별칭 — '.'을 포함할 수 없음
    member val DevicesAlias   = devicesAlias with get, set
    /// 저장된 ApiDef 이름 — '.'을 포함할 수 없음, Rename 대상이 아님
    member val ApiName        = apiName with get, set

    /// Name = DevicesAlias + "." + ApiName
    /// Rename 시에는 DevicesAlias만 변경됨 (ApiName은 ApiDef에 연동)
    override this.Name
        with get() = this.DevicesAlias + "." + this.ApiName
        and  set(value) =
            let dotIdx = value.IndexOf('.')
            this.DevicesAlias <-
                if dotIdx > 0 then value[..dotIdx-1]
                elif dotIdx = 0 then ""
                else value

    /// - ApiCall: 같은 인스턴스 참조 (ID 유지)
    /// - 나머지 속성: 깊은 복사 (새로운 ID 생성)
    member this.DeepCopySharedApiCalls() =
        let cloned = DeepCopyHelper.jsonCloneEntity(this) :?> Call
        cloned.ApiCalls <- ResizeArray(this.ApiCalls)
        cloned

and ApiCall(name) =
    inherit DsEntity(name)
    member val InTag  : IOTag option = None with get, set
    member val OutTag : IOTag option = None with get, set
    member val ApiDefId : Guid option = None with get, set
    member val InputSpec  : ValueSpec = UndefinedValue with get, set
    member val OutputSpec : ValueSpec = UndefinedValue with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> ApiCall

and CallCondition() =
    member val Id         : Guid              = Guid.NewGuid() with get, set
    member val Type : CallConditionType option = None with get, set
    member val Conditions = ResizeArray<ApiCall>() with get, set
    member val IsOR       = false with get, set
    member val IsRising   = false with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonClone<CallCondition>(this)

and ApiDef(name, parentId) =
    inherit DsChild(name, parentId)
    member val Properties = ApiDefProperties() with get, set
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> ApiDef

// =============================================================================
// Arrow
// =============================================================================

type ArrowBetweenWorks(parentId, sourceId, targetId, arrowType) =
    inherit DsArrow(parentId, sourceId, targetId, arrowType)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> ArrowBetweenWorks

type ArrowBetweenCalls(parentId, sourceId, targetId, arrowType) =
    inherit DsArrow(parentId, sourceId, targetId, arrowType)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> ArrowBetweenCalls

// =============================================================================
// Hardware Component
// =============================================================================

type HwButton(name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> HwButton

type HwLamp(name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> HwLamp

type HwCondition(name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> HwCondition

type HwAction(name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity(this) :?> HwAction
