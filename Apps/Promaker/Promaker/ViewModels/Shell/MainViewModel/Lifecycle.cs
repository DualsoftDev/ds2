using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.Editor;
using log4net;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void NewProject()
    {
        if (!GuardSimulationSemanticEdit("새 프로젝트 만들기"))
            return;

        if (!ConfirmDiscardChanges())
            return;

        Reset();
        TryEditorAction(() => _store.AddProject("NewProject"));

        var projectId = Queries.allProjects(_store).Head.Id;
        var systemId = _store.AddSystem("NewSystem", projectId, isActive: true);
        _store.AddFlow("NewFlow", systemId);

        _store.ClearHistory();
        IsDirty = false;
        HasProject = true;
        LlmChatVm?.OnProjectOpened();
        UpdateTitle();
        StatusText = "New project created.";

        RequestRebuildAll(() =>
        {
            ExpandAllNodes(ControlTreeRoots);
            ActivateInitialSystemTab();
            RefreshEditorCommandStates();
            ResyncView3DIfOpen();
        });
    }

    private void Reset()
    {
        Simulation.ResetForNewStore();

        _store = new DsStore();
        WireEvents();
        // Hot-fix-7: LLM Chat 의 _store reference 도 동기화. 새 프로젝트 추가가 LLM 측에 안 보이는 문제 회피.
        LlmChatVm?.UpdateStore(_store);

        _currentFilePath = null;
        _currentFileMTime = null;
        _loadedAsLossy = false;
        IsDirty = false;
        HasProject = false;
        CanUndo = false;
        CanRedo = false;
        HistoryItems.Clear();
        HistoryItems.Add(new HistoryPanelItem("(초기 상태)", isRedo: false));
        CurrentHistoryIndex = 0;

        _clipboardSelection.Clear();
        Selection.Reset();
        CanvasManager.Reset();
        _rebuildQueued = false;
        _pendingRebuildActions.Clear();
        _lastAddWorkTargetFlowId = null;
        SelectedNode = null;
        SelectedArrow = null;

        RebuildAll();
        UpdateTitle();
        StatusText = "Ready";
        RefreshEditorCommandStates();
        SearchResetRequested?.Invoke();
    }

    private void InternalClose(string statusText)
    {
        var lastPath = _currentFilePath;
        LlmChatVm?.OnProjectClosing(lastPath);
        Reset();
        StatusText = statusText;
        Log.Info($"Project closed (path={lastPath ?? "(unsaved)"}).");
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void CloseFile()
    {
        if (!GuardSimulationSemanticEdit("프로젝트 닫기"))
            return;

        if (!ConfirmDiscardChanges())
            return;

        InternalClose("Closed.");
    }

    private bool ConfirmDiscardChanges()
    {
        if (!IsDirty)
            return true;

        var result = _dialogService.AskSaveChanges();
        return DiscardChangesFlow.ShouldProceed(result, TrySaveFileDuringDiscardCheck);
    }

    public bool ConfirmDiscardChangesPublic() => ConfirmDiscardChanges();

    private void UpdateTitle()
    {
        var dirty = IsDirty ? " *" : "";
        var file = _currentFilePath is not null ? $" - {System.IO.Path.GetFileName(_currentFilePath)}" : "";
        // .yaml 저장 경로일 때 silent overwrite UX 위험 차단 — 영구 [YAML, lossy] 배지.
        // Ctrl+S 반복으로 IsDirty 해제 후에도 사용자가 lossy 의미를 즉시 인지 가능.
        var ext = _currentFilePath is not null
            ? System.IO.Path.GetExtension(_currentFilePath)
            : "";
        var isYaml = ext.Equals(Promaker.Presentation.FileExtensions.Yaml, System.StringComparison.OrdinalIgnoreCase)
                     || ext.Equals(Promaker.Presentation.FileExtensions.YamlAlt, System.StringComparison.OrdinalIgnoreCase);
        var lossyBadge = isYaml ? " [YAML, lossy]" : "";
        Title = $"{AppInfo.TitleBase}{file}{lossyBadge}{dirty}";
    }

    internal void PrepareForLoadedStore()
    {
        Simulation.ResetForNewStore();
        _clipboardSelection.Clear();
        Selection.Reset();
        CanvasManager.Reset();
        _rebuildQueued = false;
        _pendingRebuildActions.Clear();
        SelectedNode = null;
        SelectedArrow = null;
        RefreshEditorCommandStates();
        SearchResetRequested?.Invoke();
    }

    private bool TrySaveFileDuringDiscardCheck()
    {
        try
        {
            return TrySaveFile();
        }
        catch (Exception ex)
        {
            Log.Error("Save failed during discard check", ex);
            _dialogService.ShowWarning($"저장 실패: {ex.Message}");
            return false;
        }
    }
}
