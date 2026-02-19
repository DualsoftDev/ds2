namespace Ds2.Core

open System
open System.Reflection

// =============================================================================
// 속성 기반 클래스 (CRTP: 자기 자신을 타입 파라미터로 받음)
// ⚠️ 하위 클래스는 프리미티브 타입만 허용 (bool, int, enum, Guid, TimeSpan, DateTimeOffset, string, option)
// =============================================================================
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

// =============================================================================
// 속성 타입
// =============================================================================
type ProjectProperties() =
    inherit PropertiesBase<ProjectProperties>()
    member val Author   : string option          = None with get, set
    member val DateTime : DateTimeOffset option  = None with get, set
    member val Version  : string option          = None with get, set

type SystemProperties() =
    inherit PropertiesBase<SystemProperties>()
    member val EngineVersion : string option         = None with get, set
    member val LangVersion   : string option         = None with get, set
    member val Author        : string option         = None with get, set
    member val DateTime      : DateTimeOffset option = None with get, set
    member val IRI           : string option         = None with get, set


type FlowProperties() =
    inherit PropertiesBase<FlowProperties>()

type WorkProperties() =
    inherit PropertiesBase<WorkProperties>()
    member val Motion        : string option   = None  with get, set
    member val Script        : string option   = None  with get, set
    member val ExternalStart : bool            = false with get, set
    member val IsFinished    : bool            = false with get, set
    member val NumRepeat     : int             = 0     with get, set
    member val Duration      : TimeSpan option = None  with get, set

type CallProperties() =
    inherit PropertiesBase<CallProperties>()
    member val CallType    : CallType        = CallType.WaitForCompletion with get, set
    member val Timeout     : TimeSpan option = None with get, set
    member val SensorDelay : int option      = None with get, set

type ApiDefProperties() =
    inherit PropertiesBase<ApiDefProperties>()
    member val IsPush    : bool          = false with get, set
    member val TxGuid    : Guid option   = None  with get, set
    member val RxGuid    : Guid option   = None  with get, set
    member val Duration  : int           = 0     with get, set
    member val Memo      : string option = None  with get, set
