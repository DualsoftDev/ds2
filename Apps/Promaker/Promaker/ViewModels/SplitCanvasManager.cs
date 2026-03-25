using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Promaker.ViewModels;

public enum SplitDirection { Horizontal, Vertical }

public enum SplitSide { Right, Down, Left, Up }

/// <summary>1~2개 캔버스 pane을 관리하는 분할 매니저입니다.</summary>
public partial class SplitCanvasManager : ObservableObject
{
    private readonly Func<CanvasWorkspaceState> _paneFactory;

    public SplitCanvasManager(Func<CanvasWorkspaceState> paneFactory)
    {
        _paneFactory = paneFactory;
        PrimaryPane = paneFactory();
        _activePane = PrimaryPane;
        PrimaryPane.AllTabsClosed += OnPaneAllTabsClosed;
    }

    public CanvasWorkspaceState PrimaryPane { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplit))]
    private CanvasWorkspaceState? _secondaryPane;

    [ObservableProperty] private SplitDirection? _direction;

    [ObservableProperty] private CanvasWorkspaceState _activePane;

    /// <summary>Primary가 시각적으로 먼저(좌측/상단) 배치되는지 여부.</summary>
    [ObservableProperty] private bool _isPrimaryFirst = true;

    public bool IsSplit => SecondaryPane is not null;

    public CanvasWorkspaceState Canvas => ActivePane;

    public IEnumerable<CanvasWorkspaceState> AllPanes
    {
        get
        {
            yield return PrimaryPane;
            if (SecondaryPane is not null)
                yield return SecondaryPane;
        }
    }

    /// <summary>탭을 지정 방향으로 분할합니다.</summary>
    public void SplitTab(CanvasTab tab, SplitSide side)
    {
        var sourcePane = FindPaneContaining(tab);
        if (sourcePane is null) return;

        if (SecondaryPane is null)
        {
            var newPane = _paneFactory();
            newPane.AllTabsClosed += OnPaneAllTabsClosed;
            SecondaryPane = newPane;
        }

        Direction = side is SplitSide.Left or SplitSide.Right
            ? SplitDirection.Horizontal
            : SplitDirection.Vertical;

        IsPrimaryFirst = side is SplitSide.Right or SplitSide.Down;

        var targetPane = sourcePane == PrimaryPane ? SecondaryPane : PrimaryPane;

        // 탭을 source에서 제거하고 target으로 이동
        sourcePane.RemoveTab(tab);
        targetPane!.AddTab(tab);

        ActivePane = targetPane;
    }

    /// <summary>모든 pane에서 탭 중복 여부를 확인합니다.</summary>
    public CanvasWorkspaceState? FindPaneWithTab(Ds2.Editor.TabKind kind, Guid rootId)
    {
        foreach (var pane in AllPanes)
        {
            if (pane.OpenTabs.Any(t => t.Kind == kind && t.RootId == rootId))
                return pane;
        }
        return null;
    }

    /// <summary>파일 열기/새 프로젝트 시 전체 리셋.</summary>
    public void Reset()
    {
        if (SecondaryPane is not null)
        {
            SecondaryPane.AllTabsClosed -= OnPaneAllTabsClosed;
            SecondaryPane.Reset();
            SecondaryPane = null;
        }

        Direction = null;
        IsPrimaryFirst = true;
        PrimaryPane.Reset();
        ActivePane = PrimaryPane;
    }

    /// <summary>모든 pane에서 탭 타이틀 검증 및 캔버스 갱신.</summary>
    public void RebuildAllPanes()
    {
        foreach (var pane in AllPanes)
            pane.ValidateAndRefresh();
    }

    private CanvasWorkspaceState? FindPaneContaining(CanvasTab tab)
    {
        if (PrimaryPane.OpenTabs.Contains(tab)) return PrimaryPane;
        if (SecondaryPane?.OpenTabs.Contains(tab) == true) return SecondaryPane;
        return null;
    }

    private void OnPaneAllTabsClosed(CanvasWorkspaceState pane)
    {
        if (SecondaryPane is null) return;

        if (pane == SecondaryPane)
        {
            SecondaryPane.AllTabsClosed -= OnPaneAllTabsClosed;
            SecondaryPane = null;
            Direction = null;
            ActivePane = PrimaryPane;
        }
        else if (pane == PrimaryPane)
        {
            // Primary의 탭이 모두 닫히면 Secondary를 Primary로 승격
            PrimaryPane.AllTabsClosed -= OnPaneAllTabsClosed;
            PrimaryPane = SecondaryPane;
            SecondaryPane = null;
            Direction = null;
            ActivePane = PrimaryPane;
            OnPropertyChanged(nameof(PrimaryPane));
        }
    }
}
