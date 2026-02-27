namespace Ds2.Core

open System
open System.Text.Json

// =============================================================================
// 기반 클래스
// =============================================================================

[<AbstractClass>]
type DsEntity(name: string) =
    let mutable _name = name
    member val Id  = Guid.NewGuid() with get, set
    abstract Name  : string with get, set
    default  _.Name with get() = _name and set(v) = _name <- v

[<AbstractClass>]
type DsChild(name, parentId: Guid) =
    inherit DsEntity(name)
    member val ParentId = parentId with get, set

[<AbstractClass>]
type DsArrow(parentId, sourceId: Guid, targetId: Guid, arrowType: ArrowType) =
    inherit DsChild("", parentId)
    member val SourceId = sourceId with get, set
    member val TargetId = targetId with get, set
    member val ArrowType = arrowType with get, set

[<AbstractClass>]
type HwComponent(name, parentId) =
    inherit DsChild(name, parentId)
    member val InTag     : IOTag option = None with get, set
    member val OutTag    : IOTag option = None with get, set
    member val FlowGuids = ResizeArray<Guid>() with get, set

// =============================================================================
// DeepCopy 헬퍼
// =============================================================================

module DeepCopyHelper =
    let private jsonOptions = JsonOptions.createDeepCopyOptions ()

    let private cloneViaJson (obj: obj) (t: Type) : obj =
        let json = JsonSerializer.Serialize(obj, t, jsonOptions)
        JsonSerializer.Deserialize(json, t, jsonOptions)

    let jsonClone<'T> (obj: 'T) : 'T = cloneViaJson obj typeof<'T> :?> 'T

    /// Undo 백업용 — 원본 GUID 유지 (ID 재할당 안 함)
    let backupEntityAs<'T when 'T :> DsEntity> (entity: 'T) : 'T =
        cloneViaJson entity typeof<'T> :?> 'T

    /// DsEntity용 - DeepCopy (새 GUID 생성 — 엔티티 복제용)
    let jsonCloneEntity (entity: DsEntity) : DsEntity =
        let actualType = entity.GetType()
        let cloned = cloneViaJson entity actualType :?> DsEntity

        // DeepCopy는 새로운 엔티티를 생성하므로 새로운 ID 할당
        cloned.Id <- Guid.NewGuid()
        cloned
