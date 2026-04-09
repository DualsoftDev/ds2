namespace Ds2.Core

open System
open System.Text.Json
open System.Reflection

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
    // 화살표는 이름이 없는 개념 — Name set을 봉인
    override _.Name with get() = "" and set _ = ()
    member val SourceId  = sourceId  with get, set
    member val TargetId  = targetId  with get, set
    member val ArrowType = arrowType with get, set

// =============================================================================
// DeepCopy 헬퍼
// =============================================================================

module DeepCopyHelper =
    let private jsonOptions = JsonOptions.createDeepCopyOptions ()

    // private 유지 — obj/Type 기반 비타입 API를 외부에 노출하지 않음
    let private cloneViaJson (entity: obj) (t: Type) : obj =
        let json = JsonSerializer.Serialize(entity, t, jsonOptions)
        JsonSerializer.Deserialize(json, t, jsonOptions)

    /// Record, DU 등 DsEntity가 아닌 타입의 깊은 복사 (컴파일 타임 타입 사용)
    let jsonClone<'T> (value: 'T) : 'T =
        cloneViaJson value typeof<'T> :?> 'T

    /// PropertiesBase용 깊은 복사 (런타임 타입 사용)
    let jsonCloneProperties<'T> (value: 'T) : 'T =
        cloneViaJson value (value.GetType()) :?> 'T

    /// Undo 백업용 — 원본 GUID 유지 (ID 재할당 안 함)
    let backupEntityAs<'T when 'T :> DsEntity> (entity: 'T) : 'T =
        cloneViaJson entity (entity.GetType()) :?> 'T

    /// 엔티티 복제용 DeepCopy — 새 GUID 할당
    let jsonCloneEntity<'T when 'T :> DsEntity> (entity: 'T) : 'T =
        let cloned = cloneViaJson entity (entity.GetType()) :?> 'T
        cloned.Id <- Guid.NewGuid()
        cloned


        

// =============================================================================
// 속성 기반 클래스
// ✅ JSON 직렬화 기반 DeepCopy로 array, ResizeArray, option 등 모든 타입 지원
// =============================================================================

/// 기본 속성을 포함하는 추상 클래스 - Description과 DeepCopy 제공
[<AbstractClass>]
type PropertiesBase<'T when 'T :> PropertiesBase<'T>>() =
    member val Description : string option = None with get, set

    member this.DeepCopy() : 'T =
        DeepCopyHelper.jsonCloneProperties (this :> obj) :?> 'T
