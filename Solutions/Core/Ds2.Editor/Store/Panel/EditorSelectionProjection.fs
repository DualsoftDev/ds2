namespace Ds2.Editor

open System
open Ds2.Core
open Ds2.Core.Store

/// <summary>
/// PropertyPanel / Tree / Canvas 가 *같은 selection 이벤트*에 대해
/// 동일한 projection 을 소비하도록 모은 통합 결과.
///
/// 현재 슬롯: Summary / NameParts / WorkState / CallState / SystemType.
/// 향후 ApiDef/ApiCall edit state (v10 ActionType/SensingType/ValueSpec/Validation)
/// 슬롯 추가 시 이 타입에 멤버를 *추가*하는 식으로 확장한다 (record/sealed 구조라 호출부 retouch 없음).
/// </summary>
[<Sealed>]
type EditorSelectionProjection(
    summary: SelectionSummary,
    nameParts: NameEditorParts,
    workState: WorkSelectionState,
    callState: CallSelectionState,
    systemType: string) =

    member _.Summary    = summary
    member _.NameParts  = nameParts
    member _.WorkState  = workState
    member _.CallState  = callState
    member _.SystemType = systemType

    static member Empty =
        EditorSelectionProjection(
            SelectionSummary.Build([]),
            NameEditorParts.ForFallback "",
            WorkSelectionState.Empty,
            CallSelectionState.Empty,
            "")

    /// <summary>
    /// 단일/다중 선택을 한 번에 받아 PropertyPanel 의 모든 슬롯을 산출.
    /// 활성 단일 노드의 정보(Id/Kind/Name/ReferenceOfId)는 Nullable 인자로 받는다.
    /// 단일 선택이 아닌 경우 selectedId 등은 null 이어도 무방하며, 슬롯들이 자동으로 Empty 로 떨어진다.
    /// </summary>
    static member Build(
            store: DsStore,
            selectedKeys: seq<SelectionKey>,
            selectedId: Nullable<Guid>,
            selectedKind: Nullable<EntityKind>,
            selectedName: string,
            selectedReferenceOfId: Nullable<Guid>) : EditorSelectionProjection =

        ignore selectedKind  // 현재는 summary 가 keys 로부터 도출하므로 미사용. v10 ApiDef/ApiCall 슬롯 추가 시 활용 예약.

        let summary = SelectionSummary.Build selectedKeys
        let baseName = if isNull selectedName then "" else selectedName

        // Work 의 경우 store 에서 *full name* 을 가져온다 (prefix.local 형태).
        let fullName =
            if summary.IsSingleWorkSelected && selectedId.HasValue then
                match Queries.tryGetWorkFullName selectedId.Value store with
                | Some s -> s
                | None -> baseName
            else
                baseName

        let nameParts =
            if summary.IsSingleWorkSelected then NameEditorParts.ForWork fullName
            elif summary.IsSingleCallSelected then NameEditorParts.ForCall fullName
            else NameEditorParts.ForFallback fullName

        let workState =
            if summary.IsWorkSelected then
                let canonical =
                    selectedKeys
                    |> Seq.filter (fun k -> k.EntityKind = EntityKind.Work)
                    |> Seq.map (fun k -> Queries.resolveOriginalWorkId k.Id store)
                    |> Seq.distinct
                let singleResolved =
                    if summary.IsSingleWorkSelected && selectedId.HasValue then
                        let resolved =
                            if selectedReferenceOfId.HasValue then selectedReferenceOfId.Value
                            else selectedId.Value
                        Nullable(resolved)
                    else Nullable()
                let singleRaw =
                    if summary.IsSingleWorkSelected && selectedId.HasValue then Nullable(selectedId.Value)
                    else Nullable()
                WorkSelectionState.Build(store, canonical, singleResolved, singleRaw)
            else
                WorkSelectionState.Empty

        let callState =
            if summary.IsCallSelected then
                let callIds =
                    selectedKeys
                    |> Seq.filter (fun k -> k.EntityKind = EntityKind.Call)
                    |> Seq.map (fun k -> Queries.resolveOriginalCallId k.Id store)
                    |> Seq.distinct
                CallSelectionState.Build(store, callIds)
            else
                CallSelectionState.Empty

        let systemType =
            if summary.IsSingleSystemSelected && selectedId.HasValue then
                SystemSelectionState.resolveSystemType store selectedId.Value
            else
                ""

        EditorSelectionProjection(summary, nameParts, workState, callState, systemType)
