using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using log4net;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// 노드 생성(System/Flow/Work/Call)을 담당하는 ViewModel
/// </summary>
public partial class NodeCreationViewModel : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(NodeCreationViewModel));

    private readonly IProjectService _projectService;
    private readonly IDialogService _dialogService;
    private readonly Func<DsStore> _getStore;
    private readonly Action _requestRebuildAll;
    private readonly Func<(EntityKind?, Guid?, TabKind?, Guid?)> _snapshotContext;
    private readonly Func<TreePaneKind> _getActiveTreePane;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSystemCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddFlowCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddWorkCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddCallCommand))]
    private bool _hasProject;

    public NodeCreationViewModel(
        IProjectService projectService,
        IDialogService dialogService,
        Func<DsStore> getStore,
        Action requestRebuildAll,
        Func<(EntityKind?, Guid?, TabKind?, Guid?)> snapshotContext,
        Func<TreePaneKind> getActiveTreePane)
    {
        _projectService = projectService;
        _dialogService = dialogService;
        _getStore = getStore;
        _requestRebuildAll = requestRebuildAll;
        _snapshotContext = snapshotContext;
        _getActiveTreePane = getActiveTreePane;
    }

    private bool CanAddSystem()
    {
        if (!HasProject)
            return false;

        var store = _getStore();
        var projects = DsQuery.allProjects(store);
        if (projects.IsEmpty)
            return true;

        var activeSystems = DsQuery.activeSystemsOf(projects.Head.Id, store);
        return activeSystems.IsEmpty;
    }

    [RelayCommand(CanExecute = nameof(CanAddSystem))]
    private void AddSystem()
    {
        var name = _dialogService.PromptName("New System", "NewSystem");
        if (name is null)
            return;

        try
        {
            var store = _getStore();
            var (selType, selId, tabKind, tabRoot) = _snapshotContext();
            var isControl = _getActiveTreePane() == TreePaneKind.Control;

            _projectService.AddSystem(name, isControl, store, selType, selId, tabKind, tabRoot);
            _requestRebuildAll();
            Log.Info($"System added: {name}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to add system: {name}", ex);
            _dialogService.ShowError($"System 추가 실패: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddFlow()
    {
        var name = _dialogService.PromptName("New Flow", "NewFlow");
        if (name is null)
            return;

        try
        {
            var store = _getStore();
            var (selType, selId, tabKind, tabRoot) = _snapshotContext();

            _projectService.AddFlow(name, store, selType, selId, tabKind, tabRoot);
            _requestRebuildAll();
            Log.Info($"Flow added: {name}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to add flow: {name}", ex);
            _dialogService.ShowError($"Flow 추가 실패: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddWork()
    {
        var name = _dialogService.PromptName("New Work", "NewWork");
        if (name is null)
            return;

        try
        {
            var store = _getStore();

            // Flow ID를 찾아야 함 (현재 활성 탭이나 선택된 Flow에서)
            var (_, selId, tabKind, tabRoot) = _snapshotContext();
            Guid? flowId = tabKind == TabKind.Flow ? tabRoot : null;

            if (flowId is null)
            {
                _dialogService.ShowWarning("Flow를 선택해주세요.");
                return;
            }

            var workId = _projectService.AddWork(name, flowId.Value, store);
            _requestRebuildAll();
            Log.Info($"Work added: {name}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to add work: {name}", ex);
            _dialogService.ShowError($"Work 추가 실패: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddCall()
    {
        var name = _dialogService.PromptName("New Call", "NewCall");
        if (name is null)
            return;

        try
        {
            var store = _getStore();

            // Work ID를 찾아야 함
            var (_, selId, tabKind, tabRoot) = _snapshotContext();
            Guid? workId = tabKind == TabKind.Work ? tabRoot : null;

            if (workId is null)
            {
                _dialogService.ShowWarning("Work를 선택해주세요.");
                return;
            }

            var callId = _projectService.AddCall(name, workId.Value, store);
            _requestRebuildAll();
            Log.Info($"Call added: {name}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to add call: {name}", ex);
            _dialogService.ShowError($"Call 추가 실패: {ex.Message}");
        }
    }
}
