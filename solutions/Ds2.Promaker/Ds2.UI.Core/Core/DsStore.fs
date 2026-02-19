namespace Ds2.UI.Core

open System
open System.Collections.Generic
open Ds2.Core

/// <summary>
/// 중앙 도메인 저장소 (캡슐화된 컬렉션)
///
/// 컬렉션은 직렬화를 위해 public getter/setter를 가지지만,
/// 직접 수정하지 말고 반드시 Mutation 모듈을 통해 수정해야 합니다.
/// 외부 API는 ReadOnly 뷰를 사용하세요.
/// </summary>
type DsStore() =
    // ─── 컬렉션 (JSON 직렬화를 위해 public, 하지만 Mutation 모듈을 통해서만 수정) ───
    member val Projects   = Dictionary<Guid, Project>() with get, set
    member val Systems    = Dictionary<Guid, DsSystem>() with get, set
    member val Flows      = Dictionary<Guid, Flow>() with get, set
    member val Works      = Dictionary<Guid, Work>() with get, set
    member val Calls      = Dictionary<Guid, Call>() with get, set
    member val ApiDefs    = Dictionary<Guid, ApiDef>() with get, set
    member val ApiCalls   = Dictionary<Guid, ApiCall>() with get, set
    member val ArrowWorks = Dictionary<Guid, ArrowBetweenWorks>() with get, set
    member val ArrowCalls = Dictionary<Guid, ArrowBetweenCalls>() with get, set
    member val HwButtons    = Dictionary<Guid, HwButton>() with get, set
    member val HwLamps      = Dictionary<Guid, HwLamp>() with get, set
    member val HwConditions = Dictionary<Guid, HwCondition>() with get, set
    member val HwActions    = Dictionary<Guid, HwAction>() with get, set

    // ─── ReadOnly 뷰 (외부 API용) ───

    member this.ProjectsReadOnly   : IReadOnlyDictionary<Guid, Project>          = this.Projects   :> IReadOnlyDictionary<_, _>
    member this.SystemsReadOnly    : IReadOnlyDictionary<Guid, DsSystem>         = this.Systems    :> IReadOnlyDictionary<_, _>
    member this.FlowsReadOnly      : IReadOnlyDictionary<Guid, Flow>             = this.Flows      :> IReadOnlyDictionary<_, _>
    member this.WorksReadOnly      : IReadOnlyDictionary<Guid, Work>             = this.Works      :> IReadOnlyDictionary<_, _>
    member this.CallsReadOnly      : IReadOnlyDictionary<Guid, Call>             = this.Calls      :> IReadOnlyDictionary<_, _>
    member this.ApiDefsReadOnly    : IReadOnlyDictionary<Guid, ApiDef>           = this.ApiDefs    :> IReadOnlyDictionary<_, _>
    member this.ApiCallsReadOnly   : IReadOnlyDictionary<Guid, ApiCall>          = this.ApiCalls   :> IReadOnlyDictionary<_, _>
    member this.ArrowWorksReadOnly : IReadOnlyDictionary<Guid, ArrowBetweenWorks> = this.ArrowWorks :> IReadOnlyDictionary<_, _>
    member this.ArrowCallsReadOnly : IReadOnlyDictionary<Guid, ArrowBetweenCalls> = this.ArrowCalls :> IReadOnlyDictionary<_, _>
    member this.HwButtonsReadOnly    : IReadOnlyDictionary<Guid, HwButton>    = this.HwButtons    :> IReadOnlyDictionary<_, _>
    member this.HwLampsReadOnly      : IReadOnlyDictionary<Guid, HwLamp>      = this.HwLamps      :> IReadOnlyDictionary<_, _>
    member this.HwConditionsReadOnly : IReadOnlyDictionary<Guid, HwCondition> = this.HwConditions :> IReadOnlyDictionary<_, _>
    member this.HwActionsReadOnly    : IReadOnlyDictionary<Guid, HwAction>    = this.HwActions    :> IReadOnlyDictionary<_, _>

    /// <summary>빈 스토어 생성</summary>
    static member empty() = DsStore()

module StoreCopy =
    /// <summary>
    /// source 스토어의 모든 컬렉션을 target 스토어로 교체한다.
    /// </summary>
    let replaceAllCollections (source: DsStore) (target: DsStore) : unit =
        let replace (src: Dictionary<Guid, 'T>) (dst: Dictionary<Guid, 'T>) =
            dst.Clear()
            for kv in src do
                dst.[kv.Key] <- kv.Value
        replace source.Projects   target.Projects
        replace source.Systems    target.Systems
        replace source.Flows      target.Flows
        replace source.Works      target.Works
        replace source.Calls      target.Calls
        replace source.ApiDefs    target.ApiDefs
        replace source.ApiCalls   target.ApiCalls
        replace source.ArrowWorks target.ArrowWorks
        replace source.ArrowCalls target.ArrowCalls
        replace source.HwButtons    target.HwButtons
        replace source.HwLamps      target.HwLamps
        replace source.HwConditions target.HwConditions
        replace source.HwActions    target.HwActions