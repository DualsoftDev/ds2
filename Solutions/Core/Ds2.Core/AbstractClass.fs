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

    /// Record, DU 등 DsEntity가 아닌 타입의 깊은 복사
    let jsonClone<'T> (value: 'T) : 'T =
        cloneViaJson value typeof<'T> :?> 'T

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
// ⚠️ 하위 클래스는 프리미티브 타입 및 string만 허용 (bool, int, enum, Guid, TimeSpan, DateTimeOffset, string)
// ⚠️ array, option, collection 타입은 MemberwiseClone의 shallow copy 위험으로 인해 허용되지 않음
// =============================================================================

/// 기본 속성을 포함하는 추상 클래스 - Description과 DeepCopy만 제공
[<AbstractClass>]
type PropertiesBase<'T when 'T :> PropertiesBase<'T>>() =
    static let invalidPropertyTypes =
        typeof<'T>.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
        |> Array.choose (fun p ->
            let t =
                if p.PropertyType.IsGenericType
                   && p.PropertyType.GetGenericTypeDefinition() = typedefof<option<_>>
                then p.PropertyType.GetGenericArguments().[0]
                else p.PropertyType
            if t.IsValueType || t = typeof<string> then None
            else Some(p.Name, t.Name))

    static do
        if invalidPropertyTypes.Length > 0 then
            let detail =
                invalidPropertyTypes
                |> Array.map (fun (propName, typeName) -> $"{propName}:{typeName}")
                |> String.concat ", "
            invalidOp $"PropertiesBase<{typeof<'T>.Name}> has unsupported reference properties: {detail}"

    member val Description : string option = None with get, set

    member this.DeepCopy() =
        this.MemberwiseClone() :?> 'T
