namespace Ds2.Editor

open System
open Ds2.Core
open Ds2.Core.Store

/// <summary>
/// EditorCanvas 컨텍스트 메뉴의 항목별 가시성/체크 상태를 한 번에 산출한 projection.
/// C# 측은 ShowXxx boolean 만 읽어 Visibility 로 변환하면 된다.
/// </summary>
[<Sealed>]
type CanvasContextMenuState(
    showAddWork: bool,
    showAddCall: bool,
    showAddRefWork: bool,
    showAddRefCall: bool,
    showTokenRole: bool,
    tokenRoleSourceChecked: bool,
    tokenRoleIgnoreChecked: bool,
    tokenRoleSinkChecked: bool) =

    member _.ShowAddWork = showAddWork
    member _.ShowAddCall = showAddCall
    member _.ShowAddRefWork = showAddRefWork
    member _.ShowAddRefCall = showAddRefCall
    member _.ShowTokenRole = showTokenRole
    member _.TokenRoleSourceChecked = tokenRoleSourceChecked
    member _.TokenRoleIgnoreChecked = tokenRoleIgnoreChecked
    member _.TokenRoleSinkChecked = tokenRoleSinkChecked

    /// 활성 탭이 없을 때(컨텍스트 메뉴 자체를 표시하지 않을 때) 의 비어있는 상태.
    static member Empty =
        CanvasContextMenuState(false, false, false, false, false, false, false, false)

    /// <summary>
    /// 메뉴 가시성 + TokenRole 체크 상태 빌드.
    /// </summary>
    /// <param name="store">현재 store (TokenRole 조회에 사용)</param>
    /// <param name="tabKind">활성 탭 종류. null = 컨텍스트 메뉴를 표시하지 않음.</param>
    /// <param name="selectedEntityKind">현재 선택된 단일 노드의 EntityKind. 없으면 null.</param>
    /// <param name="selectedNodeId">현재 선택된 단일 노드 Id. TokenRole 조회용.</param>
    static member Build(
            store: DsStore,
            tabKind: Nullable<TabKind>,
            selectedEntityKind: Nullable<EntityKind>,
            selectedNodeId: Nullable<Guid>) : CanvasContextMenuState =
        if not tabKind.HasValue then
            CanvasContextMenuState.Empty
        else
            let tk = tabKind.Value
            let selKind =
                if selectedEntityKind.HasValue then Some selectedEntityKind.Value else None

            let showAddWork = (tk = TabKind.System)
            let showAddCall = (tk = TabKind.Work)
            let showAddRefWork =
                selKind = Some EntityKind.Work
                && (tk = TabKind.System || tk = TabKind.Flow)
            let showAddRefCall =
                selKind = Some EntityKind.Call && tk = TabKind.Work
            let isWorkSelected = selKind = Some EntityKind.Work
            let showTokenRole = isWorkSelected

            let mutable src = false
            let mutable ign = false
            let mutable snk = false
            if isWorkSelected && selectedNodeId.HasValue then
                match Queries.getWork selectedNodeId.Value store with
                | Some w ->
                    let role = w.TokenRole
                    src <- role.HasFlag(TokenRole.Source)
                    ign <- role.HasFlag(TokenRole.Ignore)
                    snk <- role.HasFlag(TokenRole.Sink)
                | None -> ()

            CanvasContextMenuState(
                showAddWork = showAddWork,
                showAddCall = showAddCall,
                showAddRefWork = showAddRefWork,
                showAddRefCall = showAddRefCall,
                showTokenRole = showTokenRole,
                tokenRoleSourceChecked = src,
                tokenRoleIgnoreChecked = ign,
                tokenRoleSinkChecked = snk)
