namespace Ds2.Core.Store

open System
open System.Collections.Generic
open Ds2.Core

// =============================================================================
// StoreIndex — 부모→자식 역인덱스를 그 자리에서 만들어 쓰고 버리는 헬퍼
// =============================================================================

/// <summary>
/// store 의 부모→자식 질의(<c>Queries.worksOf</c> 등)는 해당 딕셔너리를 통째로 훑는다.
/// 부모마다 부르면 O(부모수 × 전체자식수) 가 되어 트리 투영·삭제 캐스케이드가 모델 규모의
/// 제곱으로 자란다. 한 작업 안에서 여러 부모를 묻는 경우 이 모듈로 한 번만 그룹핑하면
/// O(전체자식수) 가 된다.
///
/// <para><b>store 에 캐시하지 않는다.</b> 얹어두면 add/remove/reparent 모든 경로에 무효화를
/// 심어야 하고 한 군데라도 빠지면 유령 자식이 살아남는다. 수명이 한 함수인 지역 인덱스는 그
/// 위험이 없다 — 대신 만든 뒤의 store 변경은 반영되지 않으므로 스냅샷으로 다뤄야 한다.</para>
///
/// <para>열거 순서는 원본 <c>dict.Values</c> 순서를 그대로 물려받는다. 트리·캔버스 노드 순서가
/// 사용자에게 보이므로 <c>Queries</c> 의 전수 스캔판과 결과가 같아야 한다.</para>
/// </summary>
module StoreIndex =

    /// 값마다 키 1개로 묶는다. 같은 키 안에서는 입력 순서 유지.
    let groupBy (keyOf: 'T -> Guid) (values: seq<'T>) : Dictionary<Guid, ResizeArray<'T>> =
        let index = Dictionary<Guid, ResizeArray<'T>>()
        for value in values do
            let key = keyOf value
            match index.TryGetValue key with
            | true, bucket -> bucket.Add value
            | _ ->
                let bucket = ResizeArray<'T>()
                bucket.Add value
                index.[key] <- bucket
        index

    /// 값마다 키 여러 개로 묶는다 — 화살표를 source/target 양쪽에 걸 때.
    /// 한 값이 같은 키로 두 번 오는 경우(source=target 자기순환)는 한 번만 담는다.
    /// 한 값의 키들이 연속 처리되므로 버킷 마지막 원소만 보면 걸러진다.
    let groupByMany<'T when 'T : not struct>
        (keysOf: 'T -> Guid list) (values: seq<'T>) : Dictionary<Guid, ResizeArray<'T>> =
        let index = Dictionary<Guid, ResizeArray<'T>>()
        for value in values do
            for key in keysOf value do
                match index.TryGetValue key with
                | true, bucket ->
                    if bucket.Count = 0
                       || not (LanguagePrimitives.PhysicalEquality bucket.[bucket.Count - 1] value) then
                        bucket.Add value
                | _ ->
                    let bucket = ResizeArray<'T>()
                    bucket.Add value
                    index.[key] <- bucket
        index

    /// 조회 — 없으면 빈 리스트. 원본 버킷의 복사본이라 호출자가 store 를 변경해도 안전하다
    /// (전수 스캔판의 `Seq.toList` 와 같은 성질).
    let find (index: Dictionary<Guid, ResizeArray<'T>>) (key: Guid) : 'T list =
        match index.TryGetValue key with
        | true, bucket -> List.ofSeq bucket
        | _ -> []

    /// 조회 — 버킷을 그대로 돌려준다(복사 없음). 읽기 전용 투영처럼 순회 중 store 를
    /// 건드리지 않는 곳에서만 쓸 것.
    let findSeq (index: Dictionary<Guid, ResizeArray<'T>>) (key: Guid) : seq<'T> =
        match index.TryGetValue key with
        | true, bucket -> bucket :> seq<'T>
        | _ -> Seq.empty


/// <summary>
/// store 계층(System→Flow→Work→Call, System→ApiDef) 의 부모→자식 역인덱스 한 벌.
///
/// <para>여러 부모를 묻는 작업 — 트리/캔버스 투영, System 주소 수집, 삭제 캐스케이드 — 진입 시
/// 1회 만들어 그 작업 안에서만 쓴다. 각 칸은 <c>Lazy</c> 라 안 쓰는 축은 훑지 않는다.</para>
///
/// <para>스냅샷 성질과 store 에 캐시하지 않는 이유는 <c>StoreIndex</c> 주석 참조.</para>
/// </summary>
type StoreHierarchyIndex =
    { FlowsOfSystem   : Lazy<Dictionary<Guid, ResizeArray<Flow>>>
      WorksOfFlow     : Lazy<Dictionary<Guid, ResizeArray<Work>>>
      CallsOfWork     : Lazy<Dictionary<Guid, ResizeArray<Call>>>
      ApiDefsOfSystem : Lazy<Dictionary<Guid, ResizeArray<ApiDef>>> }

    member this.Flows(systemId: Guid)   = StoreIndex.find this.FlowsOfSystem.Value   systemId
    member this.Works(flowId: Guid)     = StoreIndex.find this.WorksOfFlow.Value     flowId
    member this.Calls(workId: Guid)     = StoreIndex.find this.CallsOfWork.Value     workId
    member this.ApiDefs(systemId: Guid) = StoreIndex.find this.ApiDefsOfSystem.Value systemId

    /// 복사 없는 순회 — 순회 중 store 를 건드리지 않는 곳에서만.
    member this.CallsSeq(workId: Guid)  = StoreIndex.findSeq this.CallsOfWork.Value workId
    member this.WorksSeq(flowId: Guid)  = StoreIndex.findSeq this.WorksOfFlow.Value flowId


[<AutoOpen>]
module StoreHierarchyIndexBuilder =

    /// 이 시점의 store 로 계층 역인덱스를 만든다. 만든 뒤의 store 변경은 반영되지 않는다.
    let buildHierarchyIndex (store: DsStore) : StoreHierarchyIndex =
        { FlowsOfSystem =
            lazy (StoreIndex.groupBy (fun (f: Flow) -> f.ParentId) store.FlowsReadOnly.Values)
          WorksOfFlow =
            lazy (StoreIndex.groupBy (fun (w: Work) -> w.ParentId) store.WorksReadOnly.Values)
          CallsOfWork =
            lazy (StoreIndex.groupBy (fun (c: Call) -> c.ParentId) store.CallsReadOnly.Values)
          ApiDefsOfSystem =
            lazy (StoreIndex.groupBy (fun (d: ApiDef) -> d.ParentId) store.ApiDefsReadOnly.Values) }
