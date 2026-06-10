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

    /// <summary>
    /// 샘플 모델 경로 — build output 의 `Sample\NewProject.json`.
    /// 원본 = `<repo>/Apps/Promaker/Sample/NewProject.json` (Promaker.csproj Content 등록).
    /// 추가 샘플은 해당 폴더에 두고 향후 메뉴 분기로 확장.
    /// </summary>
    private static string SamplePath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Sample", "NewProject.json");

    /// <summary>
    /// 샘플 모델 즉시 로드 — <see cref="SamplePath"/> 파일을 OpenFilePath 로 위임.
    /// 파일 부재 시 안내 메시지만 표시 (silent fail 회피).
    /// </summary>
    [RelayCommand]
    private void CreateSampleProject()
    {
        if (!GuardSimulationSemanticEdit("샘플 만들기"))
            return;

        var path = SamplePath;
        if (!System.IO.File.Exists(path))
        {
            _dialogService.ShowWarning($"샘플 파일을 찾을 수 없습니다:\n{path}");
            return;
        }

        if (!ConfirmDiscardChanges())
            return;

        OpenFilePath(path);
        StatusText = $"샘플 로드 완료 — {path}";
    }

    private void Reset()
    {
        Simulation.ResetForNewStore();

        _store = new DsStore();
        WireEvents();
        // Hot-fix-7: LLM Chat 의 _store reference 도 동기화. 새 프로젝트 추가가 LLM 측에 안 보이는 문제 회피.

        _currentFilePath = null;
        FileWatcher.ResetSignature();
        _loadedAsLossy = false;
        IsDirty = false;
        HasProject = false;
        CanUndo = false;
        CanRedo = false;
        HistoryItems.Clear();
        HistoryItems.Add(new HistoryPanelItem("(초기 상태)", isRedo: false));
        CurrentHistoryIndex = 0;

        _clipboardSelection.Clear();
        _clipboardIsCut = false;
        Selection.ApplyCutPendingVisuals([]);
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
        _clipboardIsCut = false;
        Selection.ApplyCutPendingVisuals([]);
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
