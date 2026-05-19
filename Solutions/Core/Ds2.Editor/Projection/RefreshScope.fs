namespace Ds2.Editor

open System
open System.Runtime.CompilerServices

/// <summary>
/// Promaker 에서 trigger (user action / EditorEvent) 이후 갱신이 필요한 영역 분류.
/// C# applier 는 본 enum 값들을 받아 visual 갱신을 *분기 호출* — refresh 호출 순서가
/// hidden semantic dependency 가 되지 않도록 한다.
/// </summary>
[<Flags>]
type RefreshScope =
    | None              = 0
    /// Tree 표시 (Control/Device tree roots + 셀렉션 표식) 재구축.
    | Tree              = 0b0000_0001
    /// 활성 캔버스 컨텐츠 (노드/화살표) 재구축.
    | Canvas            = 0b0000_0010
    /// PropertyPanel slot 재계산 (selection projection 기반).
    | PropertyPanel     = 0b0000_0100
    /// RelayCommand 들의 CanExecute 재평가 (EditorCommandRefresher).
    | CommandAvailability = 0b0000_1000
    /// theme/language/arrow path 같은 visual-only 갱신.
    | VisualOnly        = 0b0001_0000
    /// 모든 semantic refresh 가 필요한 경우 (RebuildAll 등치).
    | All               = 0b0000_1111

/// <summary>
/// Selection 변경 또는 다른 trigger 의 결과를 C# 측이 일관되게 소비하도록 모은 typed result.
/// Projection 은 selection 결과, Scopes 는 그 결과 적용 후 어떤 visual 영역을 갱신해야 하는지 명시.
/// </summary>
[<Sealed>]
type SelectionApplyResult(projection: EditorSelectionProjection, scopes: RefreshScope) =
    member _.Projection = projection
    member _.Scopes     = scopes

    static member Empty =
        SelectionApplyResult(EditorSelectionProjection.Empty, RefreshScope.None)

/// <summary>
/// Promaker trigger 별로 어떤 RefreshScope 가 필요한지 결정하는 단일 source.
/// MainViewModel.HandleEvent / Selection 변경 / Save/Open lifecycle 등에서 같은 함수를 호출해
/// scattered C# heuristic 을 통합한다.
/// </summary>
module RefreshScopeDecision =

    /// <summary>Selection 변경 — PropertyPanel + CommandAvailability 갱신 필수. Tree/Canvas 는 selection visual 강조용으로 함께.</summary>
    [<CompiledName("ForSelectionChanged")>]
    let forSelectionChanged () : RefreshScope =
        RefreshScope.PropertyPanel ||| RefreshScope.CommandAvailability ||| RefreshScope.Tree ||| RefreshScope.Canvas

    /// <summary>Store 의 EditorEvent 별 trigger 분류 — 어떤 visual 영역 재구축 필요한지.</summary>
    [<CompiledName("ForEditorEvent")>]
    let forEditorEvent (evt: EditorEvent) : RefreshScope =
        match evt with
        // pure 이동 — 위치만 변경. tree/property panel 재구축 불필요. canvas 의 arrow path 만 갱신.
        | EntitiesMoved _ ->
            RefreshScope.VisualOnly
        // light event 들 — content 변화 없이 visual hint 만.
        | EntityRenamed _ ->
            RefreshScope.Tree ||| RefreshScope.Canvas ||| RefreshScope.PropertyPanel ||| RefreshScope.CommandAvailability
        // 속성 변경 (Project/System/Work/Call/ApiDef) — PropertyPanel + Tree (이름/상태 표시) 갱신.
        | ProjectPropsChanged _
        | SystemPropsChanged _
        | WorkPropsChanged _
        | CallPropsChanged _
        | ApiDefPropsChanged _ ->
            RefreshScope.PropertyPanel ||| RefreshScope.Tree
        // 화살표 변화 — Canvas 만.
        | ArrowWorkAdded _ | ArrowWorkRemoved _
        | ArrowCallAdded _ | ArrowCallRemoved _ ->
            RefreshScope.Canvas ||| RefreshScope.CommandAvailability
        // 엔티티 추가/제거 — Tree + Canvas + PropertyPanel + Commands.
        | ProjectAdded _ | ProjectRemoved _
        | SystemAdded _ | SystemRemoved _
        | FlowAdded _ | FlowRemoved _
        | WorkAdded _ | WorkRemoved _
        | CallAdded _ | CallRemoved _
        | ApiDefAdded _ | ApiDefRemoved _ ->
            RefreshScope.All
        | HwComponentAdded _ | HwComponentRemoved _ ->
            RefreshScope.Tree ||| RefreshScope.CommandAvailability
        | ConnectionsChanged ->
            RefreshScope.Canvas
        | HistoryChanged _ ->
            RefreshScope.CommandAvailability
        | StoreRefreshed ->
            RefreshScope.All

    /// <summary>RebuildAll / 새 store 로딩 / undo-to-initial 같은 전면 재구축.</summary>
    [<CompiledName("ForRebuildAll")>]
    let forRebuildAll () : RefreshScope = RefreshScope.All


/// <summary>RefreshScope 의 .NET Flags 체크 helper — C# 측에서 if (scope.HasFlag(Tree)) 패턴 통일.</summary>
[<Extension>]
type RefreshScopeExtensions =
    [<Extension>]
    static member Contains(scope: RefreshScope, flag: RefreshScope) =
        (scope &&& flag) = flag && flag <> RefreshScope.None
