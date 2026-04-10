using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CostSim.Presentation;
using Ds2.Aasx;
using Ds2.Core;
using Ds2.Editor;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.Report;
using Ds2.Runtime.Report.Exporters;
using Ds2.Runtime.Report.Model;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;
using Microsoft.Win32;

namespace CostSim;

public partial class MainWindow : Window
{
    private const int SequenceStep = 10;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private readonly ObservableCollection<TreeNodeItem> _treeRoots = [];
    private readonly ObservableCollection<LibraryCatalogNode> _libraryRoots = [];
    private readonly ObservableCollection<WorkCostRow> _workRows = [];
    private readonly OutputPanelBuffer _outputBuffer = new();

    private DsStore _store = new();
    private SimulationReport? _lastReport;
    private string? _currentFilePath;
    private TreeNodeItem? _selectedNode;
    private LibraryCatalogNode? _selectedLibraryNode;
    private Guid? _selectedWorkId;
    private bool _isUpdatingSelection;

    public MainWindow()
    {
        InitializeComponent();

        ExplorerTreeView.ItemsSource = _treeRoots;
        LibraryTreeView.ItemsSource = _libraryRoots;
        WorkDataGrid.ItemsSource = _workRows;
        RefreshThemeToggleButton();
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;

        CreateSampleStore();
        InitializeLibraryPanel();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowTheme();
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
    }

    private void ThemeManager_ThemeChanged(AppTheme theme)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshThemeToggleButton();
            ApplyWindowTheme();
        });
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.ToggleTheme();
        RefreshThemeToggleButton();
        SetStatus(ThemeManager.CurrentTheme == AppTheme.Dark ? "Black theme applied." : "White theme applied.");
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "unknown";
        MessageBox.Show(
            this,
            $"CostSim\nVersion {version}\n\nPromaker-style process cost simulation workspace.",
            "About CostSim",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CreateDemoLibrary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var demoPath = CreateDemoLibrary();
            LibraryPathTextBox.Text = demoPath;
            RefreshLibraryCatalog(showStatusFeedback: false);
            AppendActivity($"Demo 라이브러리 생성: {demoPath}");
            SetStatus("Demo library ready.");
        }
        catch (Exception ex)
        {
            ShowError($"데모 라이브러리 생성 실패: {ex.Message}");
        }
    }

    private void RefreshThemeToggleButton()
    {
        if (ThemeToggleButton is null)
            return;

        ThemeToggleButton.Content = ThemeManager.CurrentTheme == AppTheme.Dark ? "White Theme" : "Black Theme";
        ThemeToggleButton.ToolTip = ThemeManager.CurrentTheme == AppTheme.Dark ? "Switch to white theme" : "Switch to black theme";
    }

    private void InitializeLibraryPanel()
    {
        var savedPath = AppSettingStore.LoadStringOrDefault(CostSimPathService.LibraryPathSettingsPath, string.Empty);
        var resolvedPath = ResolvePreferredLibraryPath(savedPath);
        LibraryPathTextBox.Text = resolvedPath;
        AppSettingStore.SaveString(CostSimPathService.LibraryPathSettingsPath, resolvedPath);
        RefreshLibraryCatalog(showStatusFeedback: false);
    }

    private void SetLibraryPath_Click(object sender, RoutedEventArgs e)
    {
        var initialValue = string.IsNullOrWhiteSpace(LibraryPathTextBox.Text)
            ? CostSimPathService.EnsureDirectory(CostSimPathService.DefaultLibraryRootPath)
            : LibraryPathTextBox.Text.Trim();
        var selectedPath = PromptForText("Library Path", "공법 라이브러리 폴더 경로", initialValue);
        if (selectedPath is null)
            return;

        LibraryPathTextBox.Text = selectedPath;
        RefreshLibraryCatalog(showStatusFeedback: true);
    }

    private void RefreshLibrary_Click(object sender, RoutedEventArgs e)
    {
        RefreshLibraryCatalog(showStatusFeedback: true);
    }

    private void LibraryTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        SetSelectedLibraryNode(e.NewValue as LibraryCatalogNode);
    }

    private void LibraryTreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: LibraryCatalogNode node } item)
            return;

        item.IsSelected = true;
        item.Focus();
        SetSelectedLibraryNode(node);
    }

    private void LibraryTreeView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeView tree || tree.ContextMenu is not { } menu)
            return;

        if (ResolveContextMenuLibraryNode(e.OriginalSource as DependencyObject) is { } node)
            SetSelectedLibraryNode(node);

        LibraryMenuAppend.IsEnabled = CanAppendLibrarySelection();
        LibraryMenuRebuild.IsEnabled = CanRebuildLibrarySelection();
        CollapseDuplicateSeparators(menu);
    }

    private void SetSelectedLibraryNode(LibraryCatalogNode? node)
    {
        _selectedLibraryNode = node;
        RefreshLibrarySelectionSummary();
    }

    private string CreateDemoLibrary()
        => DemoLibraryBuilder.CreateDemoLibrary(CostSimPathService.DemoLibraryRootPath);

    private void ApplyWindowTheme()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        TrySetDwmAttribute(hwnd, DwmwaUseImmersiveDarkMode, ThemeManager.CurrentTheme == AppTheme.Dark ? 1 : 0);
        TrySetDwmAttribute(hwnd, DwmwaCaptionColor, GetColorRef("ToolbarShellBrush", ThemeManager.CurrentTheme == AppTheme.Dark ? Color.FromRgb(0x12, 0x21, 0x36) : Color.FromRgb(0xED, 0xF3, 0xFA)));
        TrySetDwmAttribute(hwnd, DwmwaTextColor, GetColorRef("PrimaryTextBrush", ThemeManager.CurrentTheme == AppTheme.Dark ? Colors.White : Color.FromRgb(0x1F, 0x29, 0x37)));
        TrySetDwmAttribute(hwnd, DwmwaBorderColor, GetColorRef("BorderBrush", ThemeManager.CurrentTheme == AppTheme.Dark ? Color.FromRgb(0x41, 0x54, 0x6C) : Color.FromRgb(0xC4, 0xCF, 0xDB)));
    }

    private int GetColorRef(string resourceKey, Color fallback)
    {
        var brush = TryFindResource(resourceKey) as SolidColorBrush;
        var color = brush?.Color ?? fallback;
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private static void TrySetDwmAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch
        {
            // Ignore unsupported DWM attributes on older Windows builds.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private void NewSample_Click(object sender, RoutedEventArgs e) => CreateSampleStore();

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForText("Add Project", "새 Project 이름", $"Project-{_store.Projects.Count + 1}");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var projectId = _store.AddProject(name);
        AppendActivity($"Project 추가: {name}");
        RefreshAll(projectId);
    }

    private void AddSystem_Click(object sender, RoutedEventArgs e)
    {
        var projectId = ResolveProjectIdForSelection();
        if (projectId is null)
        {
            ShowInfo("System을 추가할 Project를 선택하세요.");
            return;
        }

        var name = PromptForText("Add System", "새 System 이름", $"Line-{_store.Systems.Count + 1}");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var systemId = _store.AddSystem(name, projectId.Value, isActive: true);
        EnsureSystemCostSettings(systemId, emitHistory: true);
        AppendActivity($"System 추가: {name}");
        RefreshAll(systemId);
    }

    private void AddFlow_Click(object sender, RoutedEventArgs e)
    {
        var systemId = ResolveSystemIdForSelection();
        if (systemId is null)
        {
            ShowInfo("Flow를 추가할 System을 선택하세요.");
            return;
        }

        var name = PromptForText("Add Flow", "새 Flow 이름", $"Flow-{CountFlows(systemId.Value) + 1}");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var flowId = _store.AddFlow(name, systemId.Value);
        EnsureSequenceOrdersAllFlows(emitHistory: false);
        AppendActivity($"Flow 추가: {name}");
        RefreshAll(flowId);
    }

    private void AddWork_Click(object sender, RoutedEventArgs e)
    {
        var flowId = ResolveFlowIdForSelection();
        if (flowId is null)
        {
            ShowInfo("Work를 추가할 Flow를 선택하세요.");
            return;
        }

        var name = PromptForText("Add Work", "새 Work 이름", $"Work-{CountWorks(flowId.Value) + 1}");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var workId = _store.AddWork(name, flowId.Value);
        UpdateWorkProperties(
            workId,
            operationCode: $"OP-{_store.Works.Count:000}",
            durationSeconds: 60.0,
            workerCount: 1,
            laborCostPerHour: 35000.0,
            equipmentCostPerHour: 18000.0,
            overheadCostPerHour: 12000.0,
            utilityCostPerHour: 5000.0,
            yieldRate: 0.98,
            defectRate: 0.02,
            transactionLabel: "CostSim Work 초기화",
            emitHistory: true);

        SetSequenceOrderDirect(_store.Works[workId], GetNextSequenceOrder(flowId.Value));
        ApplyTreeOrderDirect([flowId.Value], autoBoundaryRoles: true);
        RecalculateAllCostsCore(emitHistory: true);

        AppendActivity($"Work 추가: {name}");
        RefreshAll(workId);
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            ShowInfo("삭제할 항목을 선택하세요.");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"'{_selectedNode.Name}' 항목을 삭제합니다.",
            "CostSim",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        _store.RemoveEntities(new[] { Tuple.Create(_selectedNode.EntityKind, _selectedNode.Id) });
        AppendActivity($"삭제: {_selectedNode.EntityKind} / {_selectedNode.Name}");
        _selectedNode = null;
        _selectedWorkId = null;
        RefreshAll();
    }

    private void RenameSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            ShowInfo("이름을 바꿀 항목을 선택하세요.");
            return;
        }

        var currentName = ResolveEntityNameForEdit(_selectedNode);
        var renamed = PromptForText("Rename", "새 이름", currentName);
        if (string.IsNullOrWhiteSpace(renamed))
            return;

        ApplyEntityRenameIfChanged(_selectedNode, renamed.Trim());
        AppendActivity($"이름 변경: {_selectedNode.EntityKind} / {renamed.Trim()}");
        RefreshAll(_selectedNode.Id);
        SetStatus("Name updated.");
    }

    private void ApplyProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            ShowInfo("속성을 적용할 항목을 선택하세요.");
            return;
        }

        try
        {
            switch (_selectedNode.EntityKind)
            {
                case EntityKind.Project:
                    ApplyProjectProperties();
                    break;
                case EntityKind.System:
                    ApplySystemProperties();
                    break;
                case EntityKind.Flow:
                    ApplyFlowProperties();
                    break;
                case EntityKind.Work:
                    if (!TryApplyWorkProperties(showSuccessMessage: false))
                        return;
                    break;
                default:
                    return;
            }

            AppendActivity($"속성 적용: {_selectedNode.EntityKind} / {EntityNameTextBox.Text.Trim()}");
            RefreshAll(_selectedNode.Id);
            SetStatus("Properties applied.");
        }
        catch (Exception ex)
        {
            ShowError($"속성 적용 실패: {ex.Message}");
        }
    }

    private void SetSelectedSource_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode?.EntityKind != EntityKind.Work || _selectedWorkId is not { } selectedWorkId)
        {
            ShowInfo("Source로 지정할 Work를 선택하세요.");
            return;
        }

        RunTransaction(_store, "CostSim Source Work 지정", () =>
        {
            foreach (var workId in _store.Works.Keys.ToList())
            {
                TrackMutate(_store, _store.Works, workId, work =>
                {
                    var nextRole = work.TokenRole & ~TokenRole.Source;
                    if (workId == selectedWorkId)
                        nextRole |= TokenRole.Source;
                    work.TokenRole = nextRole;
                });
            }
        });
        _store.EmitRefreshAndHistory();

        AppendActivity($"Source 지정: {_store.Works[selectedWorkId].Name}");
        RefreshAll(selectedWorkId);
    }

    private void MoveSelectedWorkUp_Click(object sender, RoutedEventArgs e) => MoveSelectedWork(-1);

    private void MoveSelectedWorkDown_Click(object sender, RoutedEventArgs e) => MoveSelectedWork(1);

    private void NormalizeFlowOrder_Click(object sender, RoutedEventArgs e)
    {
        var flowId = ResolveSelectedFlowForOrder();
        if (flowId is null)
        {
            ShowInfo("순서를 정리할 Flow 또는 Work를 선택하세요.");
            return;
        }

        RunTransaction(_store, "CostSim Flow 순서 정규화", () =>
        {
            NormalizeFlowOrderTracked(flowId.Value);
            ApplyFlowTreeOrderTracked(flowId.Value, autoBoundaryRoles: true);
        });
        _store.EmitRefreshAndHistory();

        AppendActivity($"Flow 순서 정규화: {_store.Flows[flowId.Value].Name}");
        RefreshAll(_selectedWorkId ?? flowId);
    }

    private void ApplyTreeOrder_Click(object sender, RoutedEventArgs e)
    {
        var flowIds = ResolveScopedFlowIds().ToList();
        if (flowIds.Count == 0)
        {
            ShowInfo("트리 순서를 적용할 Flow 범위를 선택하세요.");
            return;
        }

        RunTransaction(_store, "CostSim 트리 순서 인과 적용", () =>
        {
            foreach (var flowId in flowIds)
            {
                NormalizeFlowOrderTracked(flowId);
                ApplyFlowTreeOrderTracked(flowId, autoBoundaryRoles: true);
            }
        });
        _store.EmitRefreshAndHistory();

        AppendActivity($"트리 순서 인과 적용: {flowIds.Count}개 Flow");
        RefreshAll(_selectedWorkId ?? _selectedNode?.Id);
    }

    private void SaveDocument_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyWorkProperties(showSuccessMessage: false))
            return;

        EnsureSequenceOrdersAllFlows(emitHistory: false);
        ApplyTreeOrderDirect(ResolveAllFlowIds(), autoBoundaryRoles: true);
        RecalculateAllCostsCore(emitHistory: true);

        var path = _currentFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = PromptForDocumentPath(saveMode: true);
            if (string.IsNullOrWhiteSpace(path))
                return;
        }

        SaveStore(path);
        _currentFilePath = path;
        RefreshDocumentLabels();
        AppendActivity($"저장 완료: {path}");
        SetStatus($"Saved: {path}");
    }

    private void OpenDocument_Click(object sender, RoutedEventArgs e)
    {
        var path = PromptForDocumentPath(saveMode: false);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var store = new DsStore();
            LoadStore(store, path);
            BindStore(store, path, selectEntityId: store.Works.Values.OrderBy(GetSequenceOrder).ThenBy(work => work.Name).Select(work => (Guid?)work.Id).FirstOrDefault());
            AppendActivity($"문서 로드: {path}");
            SetStatus($"Opened: {path}");
        }
        catch (Exception ex)
        {
            ShowError($"문서 로드 실패: {ex.Message}");
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _store.Undo();
        AppendActivity("Undo");
        RefreshAll(_selectedWorkId ?? _selectedNode?.Id);
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        _store.Redo();
        AppendActivity("Redo");
        RefreshAll(_selectedWorkId ?? _selectedNode?.Id);
    }

    private void RecalculateAll_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyWorkProperties(showSuccessMessage: false))
            return;

        EnsureSequenceOrdersAllFlows(emitHistory: false);
        RecalculateAllCostsCore(emitHistory: true);
        AppendActivity("전체 원가 재계산");
        RefreshAll(_selectedWorkId ?? _selectedNode?.Id);
    }

    private async void RunSimulation_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyWorkProperties(showSuccessMessage: false))
            return;

        EnsureSequenceOrdersAllFlows(emitHistory: false);
        ApplyTreeOrderDirect(ResolveAllFlowIds(), autoBoundaryRoles: true);
        RecalculateAllCostsCore(emitHistory: true);
        RefreshAll(_selectedWorkId ?? _selectedNode?.Id);
        SetStatus("Simulation running...");

        try
        {
            var result = await Task.Run(RunSimulationCore);
            _lastReport = result.Report;
            ReportTextBox.Text = result.ReportText;
            AppendActivity($"시뮬레이션 완료: STEP {result.StepCount}회, 이벤트 {result.EventCount}건");
            SetStatus($"Simulation completed: {result.EventCount} events");
        }
        catch (Exception ex)
        {
            ShowError($"시뮬레이션 실패: {ex.Message}");
        }
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null)
        {
            ShowInfo("먼저 시뮬레이션을 실행하세요.");
            return;
        }

        var path = PromptForReportPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        ExportCurrentReport(path, "리포트 저장", "Report exported");
    }

    private void ExportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null)
        {
            ShowInfo("먼저 시뮬레이션을 실행하세요.");
            return;
        }

        var path = PromptForExcelReportPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        ExportCurrentReport(path, "엑셀 리포트 저장", "Excel exported");
    }

    private void TreeSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var selectedId = _selectedNode?.Id ?? _selectedWorkId;
        RefreshExplorerTree();
        if (selectedId is { } id)
            SelectEntityById(id, preferWorkSelection: false);
    }

    private void ExplorerTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isUpdatingSelection)
            return;

        if (e.NewValue is TreeNodeItem node)
            SetSelectedNode(node, preferWorkSelection: true);
    }

    private void ExplorerTreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: TreeNodeItem node } item)
            return;

        item.IsSelected = true;
        item.Focus();
        SetSelectedNode(node, preferWorkSelection: true);
    }

    private void ExplorerTreeView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeView tree || tree.ContextMenu is not { } menu)
            return;

        if (ResolveContextMenuTreeNode(e.OriginalSource as DependencyObject) is { } node)
            SetSelectedNode(node, preferWorkSelection: true);

        var kind = _selectedNode?.EntityKind;
        var hasFlowScope = ResolveScopedFlowIds().Any();

        SetMenuVisibility(ExplorerMenuAddProject, kind is null || kind == EntityKind.Project);
        SetMenuVisibility(ExplorerMenuAddSystem, kind == EntityKind.Project);
        SetMenuVisibility(ExplorerMenuAddFlow, kind == EntityKind.System);
        SetMenuVisibility(ExplorerMenuAddWork, kind == EntityKind.Flow);
        SetMenuVisibility(ExplorerMenuRename, kind is not null);
        SetMenuVisibility(ExplorerMenuApply, kind is not null);
        SetMenuVisibility(ExplorerMenuSetSource, kind == EntityKind.Work);
        SetMenuVisibility(ExplorerMenuMoveUp, kind == EntityKind.Work);
        SetMenuVisibility(ExplorerMenuMoveDown, kind == EntityKind.Work);
        SetMenuVisibility(ExplorerMenuNormalize, kind is EntityKind.Flow or EntityKind.Work);
        SetMenuVisibility(ExplorerMenuApplyTree, kind is not null && hasFlowScope);
        SetMenuVisibility(ExplorerMenuDelete, kind is not null);
        SetMenuVisibility(ExplorerMenuExpandAll, _treeRoots.Count > 0);
        SetMenuVisibility(ExplorerMenuCollapseAll, _treeRoots.Count > 0);

        CollapseDuplicateSeparators(menu);
    }

    private void ExplorerExpandAll_Click(object sender, RoutedEventArgs e)
    {
        SetExplorerExpansion(true);
        SetStatus("Explorer expanded.");
    }

    private void ExplorerCollapseAll_Click(object sender, RoutedEventArgs e)
    {
        SetExplorerExpansion(false);
        SetStatus("Explorer collapsed.");
    }

    private void WorkDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection)
            return;

        if (WorkDataGrid.SelectedItem is WorkCostRow row)
            SelectEntityById(row.WorkId, preferWorkSelection: true);
    }

    private void WorkDataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { Item: WorkCostRow row } gridRow)
            return;

        gridRow.IsSelected = true;
        WorkDataGrid.SelectedItem = row;
        SelectEntityById(row.WorkId, preferWorkSelection: true);
    }

    private void WorkDataGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid || grid.ContextMenu is not { } menu)
            return;

        if (ResolveContextMenuRow(e.OriginalSource as DependencyObject) is { } row)
        {
            WorkDataGrid.SelectedItem = row;
            SelectEntityById(row.WorkId, preferWorkSelection: true);
        }

        var hasWork = WorkDataGrid.SelectedItem is WorkCostRow;
        SetMenuVisibility(GridMenuSelectInExplorer, hasWork);
        SetMenuVisibility(GridMenuApply, hasWork);
        SetMenuVisibility(GridMenuSetSource, hasWork);
        SetMenuVisibility(GridMenuMoveUp, hasWork);
        SetMenuVisibility(GridMenuMoveDown, hasWork);
        SetMenuVisibility(GridMenuNormalize, hasWork);
        SetMenuVisibility(GridMenuApplyTree, hasWork);
        SetMenuVisibility(GridMenuDelete, hasWork);
        SetMenuVisibility(GridMenuRecalculate, _store.Works.Count > 0);
        SetMenuVisibility(GridMenuRun, _store.Works.Count > 0);

        CollapseDuplicateSeparators(menu);
    }

    private void GridSelectInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (WorkDataGrid.SelectedItem is not WorkCostRow row)
        {
            ShowInfo("Explorer로 이동할 Work를 선택하세요.");
            return;
        }

        SelectEntityById(row.WorkId, preferWorkSelection: true);
        ExplorerTreeView.Focus();
        SetStatus($"Explorer selected: {row.WorkName}");
    }

    private void CreateSampleStore()
    {
        _store = new DsStore();
        var projectId = _store.AddProject("CostSim Demo Project");
        var systemId = _store.AddSystem("AssemblyLine-A", projectId, isActive: true);
        EnsureSystemCostSettings(systemId, emitHistory: true);
        var flowId = _store.AddFlow("MainFlow", systemId);

        var cutId = _store.AddWork("CutPanel", flowId);
        var weldId = _store.AddWork("WeldFrame", flowId);
        var inspectId = _store.AddWork("FinalInspect", flowId);

        UpdateWorkProperties(cutId, "OP-010", 120.0, 2, 42000.0, 28000.0, 15000.0, 6000.0, 0.98, 0.01, "CostSim 샘플 입력", emitHistory: true);
        UpdateWorkProperties(weldId, "OP-020", 240.0, 3, 46000.0, 38000.0, 18000.0, 8500.0, 0.96, 0.03, "CostSim 샘플 입력", emitHistory: true);
        UpdateWorkProperties(inspectId, "OP-030", 90.0, 1, 36000.0, 12000.0, 9000.0, 3500.0, 0.995, 0.005, "CostSim 샘플 입력", emitHistory: true);

        SetSequenceOrderDirect(_store.Works[cutId], 10);
        SetSequenceOrderDirect(_store.Works[weldId], 20);
        SetSequenceOrderDirect(_store.Works[inspectId], 30);

        _store.UpdateWorkTokenRole(cutId, TokenRole.Source);
        EnableSinkFlagDirect(_store.Works[inspectId], true);
        ApplyTreeOrderDirect([flowId], autoBoundaryRoles: true);
        RecalculateAllCostsCore(emitHistory: true);

        _currentFilePath = null;
        _lastReport = null;

        AppendActivity("샘플 프로젝트 생성");
        RefreshAll(cutId);
    }

    private void BindStore(DsStore store, string? filePath, Guid? selectEntityId)
    {
        _store = store;
        _currentFilePath = filePath;
        _lastReport = null;
        _selectedNode = null;
        _selectedWorkId = null;

        EnsureSequenceOrdersAllFlows(emitHistory: false);
        ApplyTreeOrderDirect(ResolveAllFlowIds(), autoBoundaryRoles: true);
        RefreshAll(selectEntityId);
    }

    private void RefreshAll(Guid? selectEntityId = null)
    {
        var preferredId = selectEntityId ?? _selectedWorkId ?? _selectedNode?.Id;

        RefreshExplorerTree();
        RefreshWorkRows();
        RefreshDocumentLabels();

        if (preferredId is { } id && SelectEntityById(id, preferWorkSelection: false))
        {
            // selection restored
        }
        else if (_treeRoots.Count > 0)
        {
            SetSelectedNode(_treeRoots[0], preferWorkSelection: false);
        }
        else
        {
            ClearSelection();
        }

        RefreshSummary();
        RefreshLibrarySelectionSummary();

        if (_lastReport is null)
            ReportTextBox.Text = "시뮬레이션 리포트가 없습니다.";
    }

    private void RefreshExplorerTree()
    {
        _treeRoots.Clear();
        var roots = BuildExplorerTree();
        var query = TreeSearchTextBox.Text.Trim();

        if (!string.IsNullOrWhiteSpace(query))
            roots = FilterTree(roots, query);

        foreach (var root in roots)
            _treeRoots.Add(root);

        ExplorerSummaryTextBlock.Text = $"Projects={_store.Projects.Count}, Systems={_store.Systems.Count}, Flows={_store.Flows.Count}, Works={_store.Works.Count}";
        ExplorerHintTextBlock.Text = "우클릭 메뉴로 Add/Rename/Delete/Source/순서 변경을 바로 실행할 수 있습니다. Work는 트리 순서(SequenceOrder)대로 처리됩니다.";
    }

    private List<TreeNodeItem> BuildExplorerTree()
    {
        return _store.Projects.Values
            .OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(BuildProjectNode)
            .ToList();
    }

    private void RefreshLibraryCatalog(bool showStatusFeedback)
    {
        _libraryRoots.Clear();
        _selectedLibraryNode = null;

        var libraryPath = ResolvePreferredLibraryPath(LibraryPathTextBox.Text);
        LibraryPathTextBox.Text = libraryPath;
        AppSettingStore.SaveString(CostSimPathService.LibraryPathSettingsPath, libraryPath);

        var candidateFiles = EnumerateLibraryFiles(libraryPath).ToList();

        var skipped = new List<string>();
        foreach (var filePath in candidateFiles)
        {
            try
            {
                var sourceStore = new DsStore();
                LoadStore(sourceStore, filePath);
                if (sourceStore.Projects.Count == 0 && sourceStore.Systems.Count == 0 && sourceStore.Flows.Count == 0 && sourceStore.Works.Count == 0)
                {
                    skipped.Add($"{Path.GetFileName(filePath)} : no systems/flows/works");
                    continue;
                }

                _libraryRoots.Add(BuildLibraryFileNode(filePath, sourceStore));
            }
            catch (Exception ex)
            {
                skipped.Add($"{Path.GetFileName(filePath)} : {ex.Message}");
            }
        }

        LibraryStatusTextBlock.Text = $"Library files: {_libraryRoots.Count} loaded / {skipped.Count} skipped";
        if (_libraryRoots.Count > 0)
        {
            SetSelectedLibraryNode(_libraryRoots[0]);
        }
        else
        {
            RefreshLibrarySelectionSummary();
            if (skipped.Count > 0)
            {
                LibraryPreviewTextBox.Text = string.Join(
                    Environment.NewLine,
                    skipped.Take(8).Prepend("[Skipped Files]"));
            }
        }

        if (showStatusFeedback)
            SetStatus($"Library refreshed: {_libraryRoots.Count} file(s)");
    }

    private static string ResolvePreferredLibraryPath(string? requestedPath)
        => CostSimPathService.ResolvePreferredLibraryPath(requestedPath);

    private static IEnumerable<string> EnumerateLibraryFiles(string libraryPath)
        => CostSimPathService.EnumerateLibraryFiles(libraryPath);

    private LibraryCatalogNode BuildLibraryFileNode(string filePath, DsStore sourceStore)
    {
        var node = CreateLibraryNode(
            LibraryNodeKind.File,
            filePath,
            sourceStore,
            null,
            Path.GetFileNameWithoutExtension(filePath),
            Path.GetFileName(filePath),
            SummarizeStoreCounts(sourceStore),
            "L",
            "#2563EB");

        if (sourceStore.Projects.Count > 0)
        {
            foreach (var project in sourceStore.Projects.Values.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase))
                node.Children.Add(BuildLibraryProjectNode(filePath, sourceStore, project));
        }
        else
        {
            foreach (var system in sourceStore.Systems.Values.OrderBy(system => system.Name, StringComparer.CurrentCultureIgnoreCase))
                node.Children.Add(BuildLibrarySystemNode(filePath, sourceStore, system));
        }

        return node;
    }

    private LibraryCatalogNode BuildLibraryProjectNode(string filePath, DsStore sourceStore, Project project)
    {
        var systemCount = project.ActiveSystemIds.Concat(project.PassiveSystemIds).Distinct().Count();
        var node = CreateLibraryNode(
            LibraryNodeKind.Project,
            filePath,
            sourceStore,
            project.Id,
            project.Name,
            project.Name,
            $"{systemCount} systems · v{project.Version}",
            "P",
            "#1D4ED8");

        var systems = project.ActiveSystemIds
            .Concat(project.PassiveSystemIds)
            .Distinct()
            .Select(systemId => sourceStore.Systems.TryGetValue(systemId, out var system) ? system : null)
            .Where(system => system is not null)
            .Cast<DsSystem>()
            .OrderBy(system => system.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (var system in systems)
            node.Children.Add(BuildLibrarySystemNode(filePath, sourceStore, system));

        return node;
    }

    private LibraryCatalogNode BuildLibrarySystemNode(string filePath, DsStore sourceStore, DsSystem system)
    {
        var props = system.GetCostAnalysisProperties() is { } simProps ? simProps.Value : null;
        var systemType = ReadOption(system.SystemType);
        var flows = GetFlowsInSystem(sourceStore, system.Id).ToList();
        var works = flows.Sum(flow => GetOrderedWorksInFlow(sourceStore, flow.Id).Count());
        var displayType = string.IsNullOrWhiteSpace(systemType) ? "System" : systemType;

        var node = CreateLibraryNode(
            LibraryNodeKind.System,
            filePath,
            sourceStore,
            system.Id,
            system.Name,
            system.Name,
            $"{displayType} · {flows.Count} flows · {works} works",
            "S",
            "#0F766E");

        foreach (var flow in flows.OrderBy(flow => flow.Name, StringComparer.CurrentCultureIgnoreCase))
            node.Children.Add(BuildLibraryFlowNode(filePath, sourceStore, flow));

        return node;
    }

    private LibraryCatalogNode BuildLibraryFlowNode(string filePath, DsStore sourceStore, Flow flow)
    {
        var props = flow.GetSimulationProperties() is { } simProps ? simProps.Value : null;
        var mode = string.IsNullOrWhiteSpace(props?.FlowSimulationMode) ? "TreeOrder" : props!.FlowSimulationMode;
        var works = GetOrderedWorksInFlow(sourceStore, flow.Id).ToList();

        var node = CreateLibraryNode(
            LibraryNodeKind.Flow,
            filePath,
            sourceStore,
            flow.Id,
            flow.Name,
            flow.Name,
            $"{works.Count} works · {mode}",
            "F",
            "#7C3AED");

        foreach (var work in works)
            node.Children.Add(BuildLibraryWorkNode(filePath, sourceStore, work));

        return node;
    }

    private LibraryCatalogNode BuildLibraryWorkNode(string filePath, DsStore sourceStore, Work work)
    {
        var props = GetExistingProps(work);
        var opCode = ReadOption(props?.OperationCode);
        var durationSeconds = props?.Duration is { } duration ? duration.Value.TotalSeconds : 0.0;
        var sourceMark = work.TokenRole.HasFlag(TokenRole.Source) ? "SRC" : "RUN";
        var secondaryText = $"{opCode} · {durationSeconds:0.#}s · {sourceMark}";

        return CreateLibraryNode(
            LibraryNodeKind.Work,
            filePath,
            sourceStore,
            work.Id,
            work.LocalName,
            $"{GetSequenceOrder(work):00}. {work.LocalName}",
            secondaryText,
            "W",
            work.TokenRole.HasFlag(TokenRole.Source) ? "#EA580C" : "#334155");
    }

    private static LibraryCatalogNode CreateLibraryNode(
        LibraryNodeKind nodeKind,
        string filePath,
        DsStore sourceStore,
        Guid? sourceEntityId,
        string name,
        string displayName,
        string secondaryText,
        string glyph,
        string accentHex)
        => new(
            nodeKind,
            filePath,
            sourceStore,
            sourceEntityId,
            name,
            displayName,
            secondaryText,
            glyph,
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentHex)));

    private void RefreshLibrarySelectionSummary()
    {
        var targetText = DescribeCurrentImportTarget();
        if (_selectedLibraryNode is null)
        {
            LibrarySelectionTextBlock.Text = $"{targetText}{Environment.NewLine}Select a library model to append or rebuild.";
            if (_libraryRoots.Count == 0)
                LibraryPreviewTextBox.Text = "라이브러리 폴더를 지정하면 DS2 JSON/AASX 모델을 읽어와 공법을 재구성할 수 있습니다.";
        }
        else
        {
            LibrarySelectionTextBlock.Text = $"{targetText}{Environment.NewLine}Selected: {_selectedLibraryNode.NodeKind} / {_selectedLibraryNode.DisplayName}";
            LibraryPreviewTextBox.Text = BuildLibraryPreviewText(_selectedLibraryNode);
        }

        LibraryAppendButton.IsEnabled = CanAppendLibrarySelection();
        LibraryRebuildButton.IsEnabled = CanRebuildLibrarySelection();
    }

    private TreeNodeItem BuildProjectNode(Project project)
    {
        var authorText = string.IsNullOrWhiteSpace(project.Author) ? "Author 미입력" : project.Author;
        var node = CreateTreeNode(
            project.Id,
            null,
            EntityKind.Project,
            project.Name,
            project.Name,
            $"{authorText} · v{project.Version}",
            "P",
            "#1D4ED8");

        var systems = project.ActiveSystemIds
            .Concat(project.PassiveSystemIds)
            .Distinct()
            .Select(systemId => _store.Systems.TryGetValue(systemId, out var system) ? system : null)
            .Where(system => system is not null)
            .Cast<DsSystem>()
            .OrderBy(system => system.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (var system in systems)
            node.Children.Add(BuildSystemNode(system, project.Id));

        return node;
    }

    private TreeNodeItem BuildSystemNode(DsSystem system, Guid projectId)
    {
        var props = system.GetCostAnalysisProperties() is { } simProps ? simProps.Value : null;
        var systemType = ReadOption(system.SystemType);
        var currency = props?.DefaultCurrency ?? "KRW";
        var displayType = string.IsNullOrWhiteSpace(systemType) ? "System" : systemType;

        var node = CreateTreeNode(
            system.Id,
            projectId,
            EntityKind.System,
            system.Name,
            system.Name,
            $"{displayType} · {currency}",
            "S",
            "#0F766E");

        foreach (var flow in GetFlowsInSystem(system.Id).OrderBy(flow => flow.Name, StringComparer.CurrentCultureIgnoreCase))
            node.Children.Add(BuildFlowNode(flow, system.Id));

        return node;
    }

    private TreeNodeItem BuildFlowNode(Flow flow, Guid systemId)
    {
        var props = flow.GetSimulationProperties() is { } simProps ? simProps.Value : null;
        var mode = string.IsNullOrWhiteSpace(props?.FlowSimulationMode) ? "TreeOrder" : props!.FlowSimulationMode;
        var works = GetOrderedWorksInFlow(flow.Id).ToList();

        var node = CreateTreeNode(
            flow.Id,
            systemId,
            EntityKind.Flow,
            flow.Name,
            flow.Name,
            $"{works.Count} works · {mode}",
            "F",
            "#7C3AED");

        foreach (var work in works)
            node.Children.Add(BuildWorkNode(work, flow.Id));

        return node;
    }

    private TreeNodeItem BuildWorkNode(Work work, Guid flowId)
    {
        var props = GetExistingProps(work);
        var sequence = GetSequenceOrder(work);
        var opCode = ReadOption(props?.OperationCode);
        var durationSeconds = props?.Duration is { } duration ? duration.Value.TotalSeconds : 0.0;
        var sourceMark = work.TokenRole.HasFlag(TokenRole.Source) ? "SRC" : "RUN";
        var secondaryText = $"{opCode} · {durationSeconds:0.#}s · {sourceMark}";

        return CreateTreeNode(
            work.Id,
            flowId,
            EntityKind.Work,
            work.LocalName,
            $"{sequence:00}. {work.LocalName}",
            secondaryText,
            "W",
            work.TokenRole.HasFlag(TokenRole.Source) ? "#EA580C" : "#334155");
    }

    private static TreeNodeItem CreateTreeNode(
        Guid id,
        Guid? parentId,
        EntityKind entityKind,
        string name,
        string displayName,
        string secondaryText,
        string glyph,
        string accentHex)
        => new(
            id,
            parentId,
            entityKind,
            name,
            displayName,
            secondaryText,
            glyph,
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentHex)));

    private static List<TreeNodeItem> FilterTree(IEnumerable<TreeNodeItem> sourceRoots, string query)
    {
        var result = new List<TreeNodeItem>();
        foreach (var root in sourceRoots)
        {
            var filtered = CloneFilteredNode(root, query);
            if (filtered is not null)
                result.Add(filtered);
        }
        return result;
    }

    private static TreeNodeItem? CloneFilteredNode(TreeNodeItem source, string query)
    {
        var childMatches = source.Children
            .Select(child => CloneFilteredNode(child, query))
            .Where(child => child is not null)
            .Cast<TreeNodeItem>()
            .ToList();

        var selfMatches =
            source.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || source.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || source.SecondaryText.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        if (!selfMatches && childMatches.Count == 0)
            return null;

        var clone = new TreeNodeItem(
            source.Id,
            source.ParentId,
            source.EntityKind,
            source.Name,
            source.DisplayName,
            source.SecondaryText,
            source.Glyph,
            source.AccentBrush);

        foreach (var child in childMatches)
            clone.Children.Add(child);

        return clone;
    }

    private void RefreshWorkRows()
    {
        var works = GetScopedWorks()
            .OrderBy(work => ResolveSystemName(work), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(work => ResolveFlowName(work), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(work => GetSequenceOrder(work))
            .ThenBy(work => work.LocalName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _workRows.Clear();
        foreach (var work in works)
            _workRows.Add(BuildWorkRow(work));

        var scopeLabel = _selectedNode is null
            ? "전체 Work"
            : $"{_selectedNode.EntityKind} / {_selectedNode.Name}";
        GridScopeTextBlock.Text = $"현재 범위: {scopeLabel} · 실행 기준: Tree Sequence";
    }

    private IEnumerable<Work> GetScopedWorks()
    {
        if (_selectedNode is null)
            return _store.Works.Values;

        return _selectedNode.EntityKind switch
        {
            EntityKind.Project => GetWorksInProject(_selectedNode.Id),
            EntityKind.System => GetWorksInSystem(_selectedNode.Id),
            EntityKind.Flow => GetOrderedWorksInFlow(_selectedNode.Id),
            EntityKind.Work => _store.Works.TryGetValue(_selectedNode.Id, out var work) ? [work] : [],
            _ => _store.Works.Values
        };
    }

    private bool SelectEntityById(Guid id, bool preferWorkSelection)
    {
        var node = FindNodeById(id);
        if (node is null)
            return false;

        SetSelectedNode(node, preferWorkSelection);
        return true;
    }

    private TreeNodeItem? FindNodeById(Guid id)
        => _treeRoots.SelectMany(Flatten).FirstOrDefault(node => node.Id == id);

    private static IEnumerable<TreeNodeItem> Flatten(TreeNodeItem root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten))
            yield return child;
    }

    private void SetSelectedNode(TreeNodeItem? node, bool preferWorkSelection)
    {
        _isUpdatingSelection = true;
        try
        {
            _selectedNode = node;
            _selectedWorkId = node?.EntityKind == EntityKind.Work ? node.Id : null;

            RefreshWorkRows();
            RefreshPropertyPanel();
            RefreshSummary();
            RefreshLibrarySelectionSummary();

            if (preferWorkSelection && _selectedWorkId is { } workId)
            {
                var row = _workRows.FirstOrDefault(item => item.WorkId == workId);
                WorkDataGrid.SelectedItem = row;
            }
            else
            {
                WorkDataGrid.SelectedItem = null;
            }
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void ClearSelection()
    {
        _selectedNode = null;
        _selectedWorkId = null;
        WorkDataGrid.SelectedItem = null;
        ClearPropertyPanel();
        RefreshLibrarySelectionSummary();
    }

    private void RefreshPropertyPanel()
    {
        ProjectPanel.Visibility = Visibility.Collapsed;
        SystemPanel.Visibility = Visibility.Collapsed;
        FlowPanel.Visibility = Visibility.Collapsed;
        WorkPanel.Visibility = Visibility.Collapsed;

        if (_selectedNode is null)
        {
            SelectionTypeTextBlock.Text = "선택 없음";
            SelectionNameTextBlock.Text = "";
            EntityNameTextBox.Text = "";
            ClearPropertyPanelFields();
            return;
        }

        SelectionTypeTextBlock.Text = $"Type: {_selectedNode.EntityKind}";
        SelectionNameTextBlock.Text = _selectedNode.DisplayName;
        EntityNameTextBox.Text = ResolveEntityNameForEdit(_selectedNode);

        switch (_selectedNode.EntityKind)
        {
            case EntityKind.Project:
                LoadProjectPanel(_selectedNode.Id);
                break;
            case EntityKind.System:
                LoadSystemPanel(_selectedNode.Id);
                break;
            case EntityKind.Flow:
                LoadFlowPanel(_selectedNode.Id);
                break;
            case EntityKind.Work:
                LoadWorkPanel(_selectedNode.Id);
                break;
            default:
                ClearPropertyPanelFields();
                break;
        }
    }

    private void ClearPropertyPanel()
    {
        SelectionTypeTextBlock.Text = "선택 없음";
        SelectionNameTextBlock.Text = "";
        EntityNameTextBox.Text = "";
        ClearPropertyPanelFields();
        ProjectPanel.Visibility = Visibility.Collapsed;
        SystemPanel.Visibility = Visibility.Collapsed;
        FlowPanel.Visibility = Visibility.Collapsed;
        WorkPanel.Visibility = Visibility.Collapsed;
    }

    private void ClearPropertyPanelFields()
    {
        ProjectAuthorTextBox.Text = "";
        ProjectVersionTextBox.Text = "";
        ProjectDateTextBlock.Text = "";
        SystemTypeTextBox.Text = "";
        SystemCurrencyTextBox.Text = "";
        SystemEnableCostCheckBox.IsChecked = false;
        FlowEnabledCheckBox.IsChecked = false;
        FlowModeTextBox.Text = "";
        FlowSequencePreviewTextBox.Text = "";
        WorkSequenceOrderTextBox.Text = "";
        OperationCodeTextBox.Text = "";
        DurationSecondsTextBox.Text = "";
        WorkerCountTextBox.Text = "";
        LaborCostTextBox.Text = "";
        EquipmentCostTextBox.Text = "";
        OverheadCostTextBox.Text = "";
        UtilityCostTextBox.Text = "";
        YieldRateTextBox.Text = "";
        DefectRateTextBox.Text = "";
        WorkSourceCheckBox.IsChecked = false;
        SelectedWorkSummaryTextBox.Text = "";
    }

    private void LoadProjectPanel(Guid projectId)
    {
        if (!_store.Projects.TryGetValue(projectId, out var project))
            return;

        ProjectPanel.Visibility = Visibility.Visible;
        ProjectAuthorTextBox.Text = project.Author;
        ProjectVersionTextBox.Text = project.Version;
        ProjectDateTextBlock.Text = project.DateTime.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture);
    }

    private void LoadSystemPanel(Guid systemId)
    {
        if (!_store.Systems.TryGetValue(systemId, out var system))
            return;

        SystemPanel.Visibility = Visibility.Visible;
        var props = system.GetCostAnalysisProperties() is { } simProps ? simProps.Value : null;
        SystemTypeTextBox.Text = ReadOption(system.SystemType);
        SystemCurrencyTextBox.Text = props?.DefaultCurrency ?? "KRW";
        SystemEnableCostCheckBox.IsChecked = props?.EnableCostSimulation ?? true;
    }

    private void LoadFlowPanel(Guid flowId)
    {
        if (!_store.Flows.TryGetValue(flowId, out var flow))
            return;

        FlowPanel.Visibility = Visibility.Visible;
        var props = flow.GetSimulationProperties() is { } simProps ? simProps.Value : null;
        FlowEnabledCheckBox.IsChecked = props?.FlowSimulationEnabled ?? true;
        FlowModeTextBox.Text = string.IsNullOrWhiteSpace(props?.FlowSimulationMode) ? "TreeOrder" : props!.FlowSimulationMode;
        FlowSequencePreviewTextBox.Text = BuildFlowSequencePreview(flowId);
    }

    private void LoadWorkPanel(Guid workId)
    {
        if (!_store.Works.TryGetValue(workId, out var work))
            return;

        WorkPanel.Visibility = Visibility.Visible;
        var props = GetExistingProps(work);
        var costs = CalculateCosts(work);

        WorkSequenceOrderTextBox.Text = GetSequenceOrder(work).ToString(CultureInfo.CurrentCulture);
        OperationCodeTextBox.Text = ReadOption(props?.OperationCode);
        DurationSecondsTextBox.Text = FormatNumber(costs.DurationSeconds);
        WorkerCountTextBox.Text = (props?.WorkerCount ?? 1).ToString(CultureInfo.CurrentCulture);
        LaborCostTextBox.Text = FormatNumber(props?.LaborCostPerHour ?? 0.0);
        EquipmentCostTextBox.Text = FormatNumber(props?.EquipmentCostPerHour ?? 0.0);
        OverheadCostTextBox.Text = FormatNumber(props?.OverheadCostPerHour ?? 0.0);
        UtilityCostTextBox.Text = FormatNumber(props?.UtilityCostPerHour ?? 0.0);
        YieldRateTextBox.Text = FormatNumber(props?.YieldRate ?? 1.0);
        DefectRateTextBox.Text = FormatNumber(props?.DefectRate ?? 0.0);
        WorkSourceCheckBox.IsChecked = work.TokenRole.HasFlag(TokenRole.Source);

        var summary = new StringBuilder();
        summary.AppendLine($"System         : {ResolveSystemName(work)}");
        summary.AppendLine($"Flow           : {ResolveFlowName(work)}");
        summary.AppendLine($"Sequence       : {GetSequenceOrder(work)}");
        summary.AppendLine($"Source         : {work.TokenRole.HasFlag(TokenRole.Source)}");
        summary.AppendLine($"Sink           : {work.TokenRole.HasFlag(TokenRole.Sink)}");
        summary.AppendLine($"Labor Cost     : {costs.LaborCost:N0}");
        summary.AppendLine($"Equipment Cost : {costs.EquipmentCost:N0}");
        summary.AppendLine($"Overhead Cost  : {costs.OverheadCost:N0}");
        summary.AppendLine($"Utility Cost   : {costs.UtilityCost:N0}");
        summary.AppendLine($"Total Cost     : {costs.TotalCost:N0}");
        summary.AppendLine($"Unit Cost      : {costs.UnitCost:N0}");
        summary.AppendLine($"Effective Yield: {costs.EffectiveYield:P1}");

        SelectedWorkSummaryTextBox.Text = summary.ToString();
    }

    private string BuildFlowSequencePreview(Guid flowId) => BuildFlowSequencePreview(_store, flowId);

    private static string BuildFlowSequencePreview(DsStore store, Guid flowId)
    {
        var orderedWorks = GetOrderedWorksInFlow(store, flowId).ToList();
        if (orderedWorks.Count == 0)
            return "Flow에 Work가 없습니다.";

        var builder = new StringBuilder();
        foreach (var work in orderedWorks)
        {
            var props = GetExistingProps(work);
            var duration = props?.Duration is { } ts ? $"{ts.Value.TotalSeconds:0.#}s" : "0s";
            var opCode = ReadOption(props?.OperationCode);
            var source = work.TokenRole.HasFlag(TokenRole.Source) ? " [SRC]" : "";
            var sink = work.TokenRole.HasFlag(TokenRole.Sink) ? " [SINK]" : "";
            builder.AppendLine($"{GetSequenceOrder(work),2} -> {work.LocalName} ({opCode}, {duration}){source}{sink}");
        }

        return builder.ToString();
    }

    private string BuildLibraryPreviewText(LibraryCatalogNode node)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"File           : {node.FilePath}");
        builder.AppendLine($"Node           : {node.NodeKind}");
        builder.AppendLine($"Name           : {node.Name}");

        switch (node.NodeKind)
        {
            case LibraryNodeKind.File:
            {
                builder.AppendLine($"Contents       : {SummarizeStoreCounts(node.SourceStore)}");
                break;
            }

            case LibraryNodeKind.Project when node.SourceEntityId is { } projectId && node.SourceStore.Projects.TryGetValue(projectId, out var project):
            {
                builder.AppendLine($"Author         : {project.Author}");
                builder.AppendLine($"Version        : {project.Version}");
                builder.AppendLine($"Systems        : {GetProjectSystemIds(project).Count()}");
                break;
            }

            case LibraryNodeKind.System when node.SourceEntityId is { } systemId && node.SourceStore.Systems.TryGetValue(systemId, out var system):
            {
                var systemFlows = GetFlowsInSystem(node.SourceStore, systemId).ToList();
                var systemProps = system.GetCostAnalysisProperties() is { } simProps ? simProps.Value : null;
                builder.AppendLine($"SystemType     : {ReadOption(system.SystemType)}");
                builder.AppendLine($"Currency       : {systemProps?.DefaultCurrency ?? "KRW"}");
                builder.AppendLine($"Flows          : {systemFlows.Count}");
                builder.AppendLine($"Works          : {systemFlows.Sum(flow => GetOrderedWorksInFlow(node.SourceStore, flow.Id).Count())}");
                break;
            }

            case LibraryNodeKind.Flow when node.SourceEntityId is { } flowId && node.SourceStore.Flows.TryGetValue(flowId, out var flow):
            {
                builder.AppendLine($"System         : {(node.SourceStore.Systems.TryGetValue(flow.ParentId, out var flowSystem) ? flowSystem.Name : "(unknown)")}");
                builder.AppendLine($"WorkCount      : {GetOrderedWorksInFlow(node.SourceStore, flowId).Count()}");
                builder.AppendLine();
                builder.AppendLine("[Sequence]");
                builder.Append(BuildFlowSequencePreview(node.SourceStore, flowId));
                break;
            }

            case LibraryNodeKind.Work when node.SourceEntityId is { } workId && node.SourceStore.Works.TryGetValue(workId, out var work):
            {
                var costs = CalculateCosts(work);
                builder.AppendLine($"System         : {ResolveSystemName(node.SourceStore, work)}");
                builder.AppendLine($"Flow           : {ResolveFlowName(node.SourceStore, work)}");
                builder.AppendLine($"Sequence       : {GetSequenceOrder(work)}");
                builder.AppendLine($"OperationCode  : {ReadOption(GetExistingProps(work)?.OperationCode)}");
                builder.AppendLine($"Duration       : {costs.DurationSeconds:0.#} sec");
                builder.AppendLine($"Total Cost     : {costs.TotalCost:N0}");
                builder.AppendLine($"Unit Cost      : {costs.UnitCost:N0}");
                break;
            }
        }

        builder.AppendLine();
        builder.AppendLine("[Import]");
        builder.AppendLine($"Target         : {DescribeCurrentImportTarget()}");
        builder.AppendLine($"Append         : {(CanAppendLibrarySelection() ? "가능" : "불가")}");
        builder.AppendLine($"Rebuild        : {(CanRebuildLibrarySelection() ? "가능" : "불가")}");
        return builder.ToString().TrimEnd();
    }

    private static string SummarizeStoreCounts(DsStore store)
        => $"{store.Projects.Count} projects · {store.Systems.Count} systems · {store.Flows.Count} flows · {store.Works.Count} works";

    private string DescribeCurrentImportTarget()
    {
        if (ResolveFlowIdForSelection() is { } flowId && _store.Flows.TryGetValue(flowId, out var flow))
            return $"Target Flow: {flow.Name}";
        if (ResolveSystemIdForSelection() is { } systemId && _store.Systems.TryGetValue(systemId, out var system))
            return $"Target System: {system.Name}";
        if (ResolveProjectIdForSelection() is { } projectId && _store.Projects.TryGetValue(projectId, out var project))
            return $"Target Project: {project.Name}";
        return "Target not selected";
    }

    private bool CanAppendLibrarySelection()
        => _selectedLibraryNode is not null;

    private bool CanRebuildLibrarySelection()
    {
        return _selectedLibraryNode?.NodeKind switch
        {
            LibraryNodeKind.System => ResolveSystemIdForSelection() is not null,
            LibraryNodeKind.Flow => ResolveFlowIdForSelection() is not null,
            _ => false
        };
    }

    private void AppendLibrarySelection_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryNode is null)
        {
            ShowInfo("가져올 라이브러리 모델을 선택하세요.");
            return;
        }

        if (!TryApplyWorkProperties(showSuccessMessage: false))
            return;

        try
        {
            var importedId = AppendLibrarySelectionCore(_selectedLibraryNode);
            _lastReport = null;
            AppendActivity($"라이브러리 추가: {_selectedLibraryNode.DisplayName}");
            SetStatus($"Library appended: {_selectedLibraryNode.DisplayName}");
            RefreshAll(importedId);
        }
        catch (Exception ex)
        {
            ShowError($"라이브러리 추가 실패: {ex.Message}");
        }
    }

    private void RebuildFromLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryNode is null)
        {
            ShowInfo("재구성할 라이브러리 모델을 선택하세요.");
            return;
        }

        if (!TryApplyWorkProperties(showSuccessMessage: false))
            return;

        try
        {
            var rebuiltId = RebuildFromLibraryCore(_selectedLibraryNode);
            _lastReport = null;
            AppendActivity($"라이브러리 재구성: {_selectedLibraryNode.DisplayName}");
            SetStatus($"Library rebuilt: {_selectedLibraryNode.DisplayName}");
            RefreshAll(rebuiltId);
        }
        catch (Exception ex)
        {
            ShowError($"라이브러리 재구성 실패: {ex.Message}");
        }
    }

    private Guid? AppendLibrarySelectionCore(LibraryCatalogNode node)
    {
        switch (node.NodeKind)
        {
            case LibraryNodeKind.File:
            {
                var targetProjectId = EnsureImportProjectTarget(node.Name);
                Guid? importedId = null;
                RunTransaction(_store, $"Library 파일 추가: {node.Name}", () =>
                {
                    importedId = ImportAllSystems(node.SourceStore, targetProjectId);
                });
                _store.EmitRefreshAndHistory();
                return importedId;
            }

            case LibraryNodeKind.Project when node.SourceEntityId is { } projectId:
            {
                var targetProjectId = EnsureImportProjectTarget(node.Name);
                Guid? importedId = null;
                RunTransaction(_store, $"Library 프로젝트 추가: {node.Name}", () =>
                {
                    importedId = ImportProjectSystems(node.SourceStore, projectId, targetProjectId);
                });
                _store.EmitRefreshAndHistory();
                return importedId;
            }

            case LibraryNodeKind.System when node.SourceEntityId is { } systemId:
            {
                var targetProjectId = EnsureImportProjectTarget(node.Name);
                Guid? importedId = null;
                RunTransaction(_store, $"Library 시스템 추가: {node.Name}", () =>
                {
                    importedId = ImportSystemFromLibrary(node.SourceStore, systemId, targetProjectId);
                });
                _store.EmitRefreshAndHistory();
                return importedId;
            }

            case LibraryNodeKind.Flow when node.SourceEntityId is { } flowId:
            {
                var suggestedSystemName =
                    node.SourceStore.Flows.TryGetValue(flowId, out var sourceFlow) &&
                    node.SourceStore.Systems.TryGetValue(sourceFlow.ParentId, out var sourceSystem)
                        ? sourceSystem.Name
                        : "Imported System";
                var targetSystemId = EnsureImportSystemTarget(suggestedSystemName);
                Guid? importedId = null;
                RunTransaction(_store, $"Library Flow 추가: {node.Name}", () =>
                {
                    importedId = CloneFlowIntoSystem(node.SourceStore, flowId, targetSystemId);
                });
                _store.EmitRefreshAndHistory();
                return importedId;
            }

            case LibraryNodeKind.Work when node.SourceEntityId is { } workId:
            {
                var targetFlowId = EnsureImportFlowTarget(node.Name);
                Guid? importedId = null;
                RunTransaction(_store, $"Library Work 추가: {node.Name}", () =>
                {
                    if (!node.SourceStore.Works.TryGetValue(workId, out var sourceWork))
                        throw new InvalidOperationException("선택한 Work를 찾을 수 없습니다.");

                    var importedIds = CopyWorksToFlow(new[] { sourceWork }, targetFlowId, GetNextSequenceOrder(targetFlowId), clearBoundaryRoles: true);
                    importedId = importedIds.Count > 0 ? importedIds[0] : null;
                    ApplyFlowTreeOrderTracked(targetFlowId, autoBoundaryRoles: true);
                });
                _store.EmitRefreshAndHistory();
                return importedId;
            }

            default:
                throw new InvalidOperationException("가져올 수 없는 라이브러리 항목입니다.");
        }
    }

    private Guid? RebuildFromLibraryCore(LibraryCatalogNode node)
    {
        switch (node.NodeKind)
        {
            case LibraryNodeKind.System when node.SourceEntityId is { } systemId:
            {
                var targetSystemId = ResolveSystemIdForSelection()
                    ?? throw new InvalidOperationException("재구성할 대상 System을 먼저 선택하세요.");
                var targetName = _store.Systems.TryGetValue(targetSystemId, out var targetSystem)
                    ? targetSystem.Name
                    : "System";

                var answer = MessageBox.Show(
                    this,
                    $"'{targetName}' System 아래 Flow를 모두 지우고 '{node.DisplayName}' 라이브러리로 다시 구성합니다.",
                    "CostSim",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                    return targetSystemId;

                var flowSelections = _store.Flows.Values
                    .Where(flow => flow.ParentId == targetSystemId)
                    .Select(flow => Tuple.Create(EntityKind.Flow, flow.Id))
                    .ToList();
                if (flowSelections.Count > 0)
                    _store.RemoveEntities(flowSelections);

                RunTransaction(_store, $"Library 시스템 재구성: {node.Name}", () =>
                {
                    ReplaceSystemWithLibrary(node.SourceStore, systemId, targetSystemId);
                });
                _store.EmitRefreshAndHistory();
                return targetSystemId;
            }

            case LibraryNodeKind.Flow when node.SourceEntityId is { } flowId:
            {
                var targetFlowId = ResolveFlowIdForSelection()
                    ?? throw new InvalidOperationException("재구성할 대상 Flow를 먼저 선택하세요.");
                var targetName = _store.Flows.TryGetValue(targetFlowId, out var targetFlow)
                    ? targetFlow.Name
                    : "Flow";

                var answer = MessageBox.Show(
                    this,
                    $"'{targetName}' Flow의 Work를 모두 지우고 '{node.DisplayName}' 라이브러리로 다시 구성합니다.",
                    "CostSim",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                    return targetFlowId;

                var workSelections = _store.Works.Values
                    .Where(work => work.ParentId == targetFlowId)
                    .Select(work => Tuple.Create(EntityKind.Work, work.Id))
                    .ToList();
                if (workSelections.Count > 0)
                    _store.RemoveEntities(workSelections);

                RunTransaction(_store, $"Library Flow 재구성: {node.Name}", () =>
                {
                    ReplaceFlowWithLibrary(node.SourceStore, flowId, targetFlowId);
                });
                _store.EmitRefreshAndHistory();
                return targetFlowId;
            }

            default:
                throw new InvalidOperationException("System 또는 Flow만 재구성할 수 있습니다.");
        }
    }

    private Guid EnsureImportProjectTarget(string suggestedName)
    {
        if (ResolveProjectIdForSelection() is { } projectId)
            return projectId;

        var baseName = string.IsNullOrWhiteSpace(suggestedName) ? "Imported Project" : $"{suggestedName} Project";
        var projectName = GetUniqueName(baseName, _store.Projects.Values.Select(project => project.Name));
        return _store.AddProject(projectName);
    }

    private Guid EnsureImportSystemTarget(string suggestedName)
    {
        if (ResolveSystemIdForSelection() is { } systemId)
            return systemId;

        var projectId = EnsureImportProjectTarget(suggestedName);
        var systemName = GetUniqueName(
            string.IsNullOrWhiteSpace(suggestedName) ? "Imported System" : suggestedName,
            GetSystemNamesInProject(projectId));
        return _store.AddSystem(systemName, projectId, isActive: true);
    }

    private Guid EnsureImportFlowTarget(string suggestedName)
    {
        if (ResolveFlowIdForSelection() is { } flowId)
            return flowId;

        var systemId = EnsureImportSystemTarget("Imported System");
        var flowName = GetUniqueName(
            string.IsNullOrWhiteSpace(suggestedName) ? "Imported Flow" : suggestedName,
            GetFlowsInSystem(systemId).Select(flow => flow.Name));
        return _store.AddFlow(flowName, systemId);
    }

    private Guid? ImportAllSystems(DsStore sourceStore, Guid targetProjectId)
    {
        Guid? importedId = null;
        if (sourceStore.Projects.Count > 0)
        {
            foreach (var project in sourceStore.Projects.Values.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase))
                importedId ??= ImportProjectSystems(sourceStore, project.Id, targetProjectId);
            return importedId;
        }

        foreach (var system in sourceStore.Systems.Values.OrderBy(system => system.Name, StringComparer.CurrentCultureIgnoreCase))
            importedId ??= ImportSystemFromLibrary(sourceStore, system.Id, targetProjectId);
        return importedId;
    }

    private Guid? ImportProjectSystems(DsStore sourceStore, Guid sourceProjectId, Guid targetProjectId)
    {
        if (!sourceStore.Projects.TryGetValue(sourceProjectId, out var project))
            return null;

        Guid? importedId = null;
        foreach (var systemId in GetProjectSystemIds(project))
        {
            if (!sourceStore.Systems.TryGetValue(systemId, out _))
                continue;

            importedId ??= ImportSystemFromLibrary(sourceStore, systemId, targetProjectId);
        }

        return importedId;
    }

    private Guid ImportSystemFromLibrary(DsStore sourceStore, Guid sourceSystemId, Guid targetProjectId)
    {
        if (!sourceStore.Systems.TryGetValue(sourceSystemId, out var sourceSystem))
            throw new InvalidOperationException("라이브러리 System을 찾을 수 없습니다.");

        var clonedSystem = sourceSystem.DeepCopy();
        clonedSystem.Name = GetUniqueName(sourceSystem.Name, GetSystemNamesInProject(targetProjectId));

        _store.TrackAdd(_store.Systems, clonedSystem);
        TrackMutate(_store, _store.Projects, targetProjectId, project =>
        {
            if (!project.ActiveSystemIds.Contains(clonedSystem.Id))
                project.ActiveSystemIds.Add(clonedSystem.Id);
        });

        foreach (var flow in GetFlowsInSystem(sourceStore, sourceSystemId).OrderBy(flow => flow.Name, StringComparer.CurrentCultureIgnoreCase))
            CloneFlowIntoSystem(sourceStore, flow.Id, clonedSystem.Id);

        return clonedSystem.Id;
    }

    private Guid CloneFlowIntoSystem(DsStore sourceStore, Guid sourceFlowId, Guid targetSystemId)
    {
        if (!sourceStore.Flows.TryGetValue(sourceFlowId, out var sourceFlow))
            throw new InvalidOperationException("라이브러리 Flow를 찾을 수 없습니다.");

        var clonedFlow = sourceFlow.DeepCopy();
        clonedFlow.ParentId = targetSystemId;
        clonedFlow.Name = GetUniqueName(sourceFlow.Name, GetFlowsInSystem(targetSystemId).Select(flow => flow.Name));

        _store.TrackAdd(_store.Flows, clonedFlow);
        CopyWorksToFlow(GetOrderedWorksInFlow(sourceStore, sourceFlowId), clonedFlow.Id, SequenceStep, clearBoundaryRoles: false);
        NormalizeFlowOrderTracked(clonedFlow.Id);
        ApplyFlowTreeOrderTracked(clonedFlow.Id, autoBoundaryRoles: true);
        return clonedFlow.Id;
    }

    private void ReplaceSystemWithLibrary(DsStore sourceStore, Guid sourceSystemId, Guid targetSystemId)
    {
        if (!sourceStore.Systems.TryGetValue(sourceSystemId, out var sourceSystem))
            throw new InvalidOperationException("라이브러리 System을 찾을 수 없습니다.");

        TrackMutate(_store, _store.Systems, targetSystemId, target =>
        {
            var clonedProps = CloneOption(sourceSystem.GetSimulationProperties(), props => props.DeepCopy());
            if (clonedProps != null && FSharpOption<SimulationSystemProperties>.get_IsSome(clonedProps))
                target.SetSimulationProperties(clonedProps.Value);
            target.IRI = sourceSystem.IRI;
        });

        foreach (var flow in GetFlowsInSystem(sourceStore, sourceSystemId).OrderBy(flow => flow.Name, StringComparer.CurrentCultureIgnoreCase))
            CloneFlowIntoSystem(sourceStore, flow.Id, targetSystemId);
    }

    private void ReplaceFlowWithLibrary(DsStore sourceStore, Guid sourceFlowId, Guid targetFlowId)
    {
        if (!sourceStore.Flows.TryGetValue(sourceFlowId, out var sourceFlow))
            throw new InvalidOperationException("라이브러리 Flow를 찾을 수 없습니다.");

        TrackMutate(_store, _store.Flows, targetFlowId, target =>
        {
            var clonedProps = CloneOption(sourceFlow.GetSimulationProperties(), props => props.DeepCopy());
            if (clonedProps != null && FSharpOption<SimulationFlowProperties>.get_IsSome(clonedProps))
                target.SetSimulationProperties(clonedProps.Value);
        });

        CopyWorksToFlow(GetOrderedWorksInFlow(sourceStore, sourceFlowId), targetFlowId, SequenceStep, clearBoundaryRoles: false);
        NormalizeFlowOrderTracked(targetFlowId);
        ApplyFlowTreeOrderTracked(targetFlowId, autoBoundaryRoles: true);
    }

    private List<Guid> CopyWorksToFlow(IEnumerable<Work> sourceWorks, Guid targetFlowId, int initialSequenceOrder, bool clearBoundaryRoles)
    {
        if (!_store.Flows.TryGetValue(targetFlowId, out var targetFlow))
            throw new InvalidOperationException("대상 Flow를 찾을 수 없습니다.");

        var existingNames = new HashSet<string>(GetWorkNamesInFlow(targetFlowId), StringComparer.CurrentCultureIgnoreCase);
        var importedIds = new List<Guid>();
        var nextSequenceOrder = initialSequenceOrder;

        foreach (var sourceWork in sourceWorks)
        {
            var clonedWork = sourceWork.DeepCopy();
            var uniqueLocalName = GetUniqueName(sourceWork.LocalName, existingNames);

            existingNames.Add(uniqueLocalName);
            clonedWork.ParentId = targetFlowId;
            clonedWork.FlowPrefix = targetFlow.Name;
            clonedWork.LocalName = uniqueLocalName;
            clonedWork.ReferenceOf = null;

            if (clearBoundaryRoles)
                clonedWork.TokenRole &= ~(TokenRole.Source | TokenRole.Sink);

            GetOrCreateProps(clonedWork).SequenceOrder = nextSequenceOrder;
            ApplyCalculatedTotals(clonedWork);
            _store.TrackAdd(_store.Works, clonedWork);

            importedIds.Add(clonedWork.Id);
            nextSequenceOrder += SequenceStep;
        }

        return importedIds;
    }

    private static string GetUniqueName(string baseName, IEnumerable<string> existingNames)
    {
        var normalizedBaseName = string.IsNullOrWhiteSpace(baseName) ? "Item" : baseName.Trim();
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var nameSet = existingNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(comparer);

        if (!nameSet.Contains(normalizedBaseName))
            return normalizedBaseName;

        for (var index = 2; ; index++)
        {
            var candidate = $"{normalizedBaseName} ({index})";
            if (!nameSet.Contains(candidate))
                return candidate;
        }
    }

    private static FSharpOption<T>? CloneOption<T>(FSharpOption<T>? option, Func<T, T> clone)
        where T : class
        => option is { } value ? FSharpOption<T>.Some(clone(value.Value)) : null;

    private void ApplyProjectProperties()
    {
        var projectId = _selectedNode!.Id;
        var newName = EntityNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("Project 이름은 비워둘 수 없습니다.");

        ApplyEntityRenameIfChanged(_selectedNode, newName);

        RunTransaction(_store, "CostSim Project 속성 변경", () =>
        {
            TrackMutate(_store, _store.Projects, projectId, project =>
            {
                project.Author = ProjectAuthorTextBox.Text.Trim();
                project.Version = ProjectVersionTextBox.Text.Trim();
                project.DateTime = DateTimeOffset.Now;
            });
        });
        _store.EmitRefreshAndHistory();
    }

    private void ApplySystemProperties()
    {
        var systemId = _selectedNode!.Id;
        var newName = EntityNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("System 이름은 비워둘 수 없습니다.");

        ApplyEntityRenameIfChanged(_selectedNode, newName);

        RunTransaction(_store, "CostSim System 속성 변경", () =>
        {
            TrackMutate(_store, _store.Systems, systemId, system =>
            {
                var props = system.GetCostAnalysisProperties() is { } simProps
                    ? simProps.Value
                    : new CostAnalysisSystemProperties();

                system.SystemType = ToOption(SystemTypeTextBox.Text);
                props.DefaultCurrency = string.IsNullOrWhiteSpace(SystemCurrencyTextBox.Text) ? "KRW" : SystemCurrencyTextBox.Text.Trim();
                props.EnableCostSimulation = SystemEnableCostCheckBox.IsChecked != false;

                system.SetCostAnalysisProperties(props);
            });
        });
        _store.EmitRefreshAndHistory();
    }

    private void ApplyFlowProperties()
    {
        var flowId = _selectedNode!.Id;
        var newName = EntityNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("Flow 이름은 비워둘 수 없습니다.");

        ApplyEntityRenameIfChanged(_selectedNode, newName);

        RunTransaction(_store, "CostSim Flow 속성 변경", () =>
        {
            TrackMutate(_store, _store.Flows, flowId, flow =>
            {
                var props = flow.GetSimulationProperties() is { } simProps
                    ? simProps.Value
                    : new SimulationFlowProperties();

                props.FlowSimulationEnabled = FlowEnabledCheckBox.IsChecked != false;
                props.FlowSimulationMode = string.IsNullOrWhiteSpace(FlowModeTextBox.Text) ? "TreeOrder" : FlowModeTextBox.Text.Trim();

                flow.SetSimulationProperties(props);
            });
        });
        _store.EmitRefreshAndHistory();
    }

    private bool TryApplyWorkProperties(bool showSuccessMessage)
    {
        if (_selectedNode?.EntityKind != EntityKind.Work || _selectedWorkId is not { } workId || !_store.Works.ContainsKey(workId))
            return true;

        if (!TryParseWorkEditorValues(out var values, out var error))
        {
            ShowInfo(error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(values.Name))
        {
            ShowInfo("Work 이름은 비워둘 수 없습니다.");
            return false;
        }

        ApplyEntityRenameIfChanged(_selectedNode, values.Name);
        UpdateWorkProperties(
            workId,
            values.OperationCode,
            values.DurationSeconds,
            values.WorkerCount,
            values.LaborCostPerHour,
            values.EquipmentCostPerHour,
            values.OverheadCostPerHour,
            values.UtilityCostPerHour,
            values.YieldRate,
            values.DefectRate,
            "CostSim Work 속성 변경",
            emitHistory: true);
        UpdateWorkSourceFlag(workId, values.IsSource, emitHistory: true);
        RecalculateSelectedWork(workId);

        if (showSuccessMessage)
            SetStatus($"Applied: {_store.Works[workId].Name}");

        AppendActivity($"Work 수정: {_store.Works[workId].Name}");
        return true;
    }

    private bool TryParseWorkEditorValues(out WorkEditorValues values, out string error)
    {
        values = default;
        error = string.Empty;

        if (!TryParseDouble(DurationSecondsTextBox.Text, out var durationSeconds) || durationSeconds < 0.0)
        {
            error = "Duration (sec)은 0 이상의 숫자여야 합니다.";
            return false;
        }

        if (!TryParseInt(WorkerCountTextBox.Text, out var workerCount) || workerCount <= 0)
        {
            error = "Worker Count는 1 이상의 정수여야 합니다.";
            return false;
        }

        if (!TryParseDouble(LaborCostTextBox.Text, out var laborCost) || laborCost < 0.0
            || !TryParseDouble(EquipmentCostTextBox.Text, out var equipmentCost) || equipmentCost < 0.0
            || !TryParseDouble(OverheadCostTextBox.Text, out var overheadCost) || overheadCost < 0.0
            || !TryParseDouble(UtilityCostTextBox.Text, out var utilityCost) || utilityCost < 0.0)
        {
            error = "비용 입력은 0 이상의 숫자여야 합니다.";
            return false;
        }

        if (!TryParseDouble(YieldRateTextBox.Text, out var yieldRate) || yieldRate <= 0.0 || yieldRate > 1.0)
        {
            error = "Yield Rate는 0보다 크고 1 이하여야 합니다.";
            return false;
        }

        if (!TryParseDouble(DefectRateTextBox.Text, out var defectRate) || defectRate < 0.0 || defectRate >= 1.0)
        {
            error = "Defect Rate는 0 이상 1 미만이어야 합니다.";
            return false;
        }

        values = new WorkEditorValues(
            EntityNameTextBox.Text.Trim(),
            OperationCodeTextBox.Text.Trim(),
            durationSeconds,
            workerCount,
            laborCost,
            equipmentCost,
            overheadCost,
            utilityCost,
            yieldRate,
            defectRate,
            WorkSourceCheckBox.IsChecked == true);

        return true;
    }

    private void ApplyEntityRenameIfChanged(TreeNodeItem selectedNode, string newName)
    {
        if (!string.Equals(selectedNode.Name, newName, StringComparison.Ordinal))
            _store.RenameEntity(selectedNode.Id, selectedNode.EntityKind, newName);
    }

    private void MoveSelectedWork(int delta)
    {
        if (_selectedNode?.EntityKind != EntityKind.Work || _selectedWorkId is not { } selectedWorkId || !_store.Works.ContainsKey(selectedWorkId))
        {
            ShowInfo("순서를 변경할 Work를 선택하세요.");
            return;
        }

        var flowId = _store.Works[selectedWorkId].ParentId;
        EnsureSequenceOrdersAllFlows(emitHistory: false);

        var orderedWorks = GetOrderedWorksInFlow(flowId).ToList();
        var currentIndex = orderedWorks.FindIndex(work => work.Id == selectedWorkId);
        var targetIndex = currentIndex + delta;

        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= orderedWorks.Count)
            return;

        var currentWork = orderedWorks[currentIndex];
        var targetWork = orderedWorks[targetIndex];
        var currentOrder = GetSequenceOrder(currentWork);
        var targetOrder = GetSequenceOrder(targetWork);

        RunTransaction(_store, "CostSim Work 순서 변경", () =>
        {
            TrackMutate(_store, _store.Works, currentWork.Id, work =>
            {
                GetOrCreateProps(work).SequenceOrder = targetOrder;
            });
            TrackMutate(_store, _store.Works, targetWork.Id, work =>
            {
                GetOrCreateProps(work).SequenceOrder = currentOrder;
            });
            ApplyFlowTreeOrderTracked(flowId, autoBoundaryRoles: true);
        });
        _store.EmitRefreshAndHistory();

        AppendActivity($"Work 순서 변경: {currentWork.LocalName} {(delta < 0 ? "↑" : "↓")}");
        RefreshAll(currentWork.Id);
    }

    private void UpdateWorkProperties(
        Guid workId,
        string operationCode,
        double durationSeconds,
        int workerCount,
        double laborCostPerHour,
        double equipmentCostPerHour,
        double overheadCostPerHour,
        double utilityCostPerHour,
        double yieldRate,
        double defectRate,
        string transactionLabel,
        bool emitHistory)
        => UpdateWorkProperties(
            _store,
            workId,
            operationCode,
            durationSeconds,
            workerCount,
            laborCostPerHour,
            equipmentCostPerHour,
            overheadCostPerHour,
            utilityCostPerHour,
            yieldRate,
            defectRate,
            transactionLabel,
            emitHistory);

    private static void UpdateWorkProperties(
        DsStore store,
        Guid workId,
        string operationCode,
        double durationSeconds,
        int workerCount,
        double laborCostPerHour,
        double equipmentCostPerHour,
        double overheadCostPerHour,
        double utilityCostPerHour,
        double yieldRate,
        double defectRate,
        string transactionLabel,
        bool emitHistory)
        => CostSimStoreHelper.UpdateWorkProperties(
            store,
            workId,
            operationCode,
            durationSeconds,
            workerCount,
            laborCostPerHour,
            equipmentCostPerHour,
            overheadCostPerHour,
            utilityCostPerHour,
            yieldRate,
            defectRate,
            transactionLabel,
            emitHistory);

    private void UpdateWorkSourceFlag(Guid workId, bool isSource, bool emitHistory)
    {
        RunTransaction(_store, "CostSim Source 플래그 변경", () =>
        {
            TrackMutate(_store, _store.Works, workId, work =>
            {
                var role = work.TokenRole;
                role = isSource ? role | TokenRole.Source : role & ~TokenRole.Source;
                work.TokenRole = role;
            });
        });

        if (emitHistory)
            _store.EmitRefreshAndHistory();
    }

    private void RecalculateSelectedWork(Guid workId)
    {
        RunTransaction(_store, "CostSim 선택 Work 재계산", () =>
        {
            TrackMutate(_store, _store.Works, workId, ApplyCalculatedTotals);
        });
        _store.EmitRefreshAndHistory();
    }

    private void RecalculateAllCostsCore(bool emitHistory)
    {
        var workIds = _store.Works.Keys.ToList();
        if (workIds.Count == 0)
            return;

        RunTransaction(_store, "CostSim 전체 원가 재계산", () =>
        {
            foreach (var workId in workIds)
            {
                TrackMutate(_store, _store.Works, workId, ApplyCalculatedTotals);
            }
        });

        if (emitHistory)
            _store.EmitRefreshAndHistory();
    }

    private static void ApplyCalculatedTotals(Work work)
        => CostSimStoreHelper.ApplyCalculatedTotals(work);

    private SimulationExecutionResult RunSimulationCore()
    {
        EnsureSequenceOrdersAllFlows(emitHistory: false);
        ApplyTreeOrderDirect(ResolveAllFlowIds(), autoBoundaryRoles: true);

        var index = SimIndexModule.build(_store, 50);
        var validationText = BuildValidationSummary(index);

        using var engine = new EventDrivenEngine(index, RuntimeMode.Simulation);
        var records = new List<StateChangeRecord>();
        var startTime = DateTime.UtcNow;

        engine.WorkStateChanged += (_, args) =>
        {
            lock (records)
            {
                records.Add(new StateChangeRecord(
                    nodeId: args.WorkGuid.ToString(),
                    nodeName: args.WorkName,
                    nodeType: "Work",
                    systemId: ReadOption(index.WorkSystemName.TryFind(args.WorkGuid)),
                    state: args.NewState.ToString(),
                    timestamp: startTime + args.Clock));
            }
        };

        var sourceIds = index.TokenSourceGuids.ToList();
        if (sourceIds.Count == 0)
            throw new InvalidOperationException("TokenRole.Source Work가 없습니다. Flow 첫 Work 또는 선택 Work를 Source로 지정하세요.");

        var selectedSource = sourceIds.Count == 1 ? sourceIds[0] : Guid.Empty;
        var autoStartSources = sourceIds.Count != 1;

        engine.Start();
        engine.SetAllFlowStates(FlowTag.Pause);

        var steps = 0;
        while (steps < 10000 && engine.CanAdvanceStep(selectedSource, autoStartSources))
        {
            steps++;
            if (!engine.StepWithSourcePriming(selectedSource, autoStartSources))
                break;
        }

        engine.Stop();

        var orderedRecords = records.OrderBy(record => record.Timestamp).ToList();
        var endTime = orderedRecords.Count == 0
            ? startTime
            : orderedRecords.Max(record => record.Timestamp);

        var report = ReportService.fromStateChanges(startTime, endTime, orderedRecords);
        var reportText = BuildReportText(report, validationText, steps);

        return new SimulationExecutionResult(report, reportText, steps, orderedRecords.Count);
    }

    private string BuildValidationSummary(SimIndex index)
    {
        var summary = new StringBuilder();
        var unreset = GraphValidator.findUnresetWorks(index);
        var deadlock = GraphValidator.findDeadlockCandidates(index);
        var sourceCandidates = GraphValidator.findSourceCandidates(index);
        var sourcesWithPreds = GraphValidator.findSourcesWithPredecessors(index);
        var groupNoIgnore = GraphValidator.findGroupWorksWithoutIgnore(index);
        var unreachable = GraphValidator.findTokenUnreachableWorks(index);

        summary.AppendLine("[Validation]");
        summary.AppendLine("ExecutionBasis       : Tree Sequence");
        summary.AppendLine($"unresetWorks         : {unreset.Length}");
        summary.AppendLine($"deadlockCandidates   : {deadlock.Length}");
        summary.AppendLine($"sourceCandidates     : {sourceCandidates.Length}");
        summary.AppendLine($"sourcesWithPreds     : {sourcesWithPreds.Length}");
        summary.AppendLine($"groupWithoutIgnore   : {groupNoIgnore.Length}");
        summary.AppendLine($"tokenUnreachable     : {unreachable.Length}");

        return summary.ToString();
    }

    private string BuildReportText(SimulationReport report, string validationText, int stepCount)
    {
        var currency = ResolveCurrency();
        var builder = new StringBuilder();
        builder.AppendLine(validationText.TrimEnd());
        builder.AppendLine();
        builder.AppendLine("[Simulation]");
        builder.AppendLine($"StartTime            : {report.Metadata.StartTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"EndTime              : {report.Metadata.EndTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"TotalDuration        : {report.Metadata.TotalDuration}");
        builder.AppendLine($"WorkCount            : {report.Metadata.WorkCount}");
        builder.AppendLine($"CallCount            : {report.Metadata.CallCount}");
        builder.AppendLine($"StepCount            : {stepCount}");
        builder.AppendLine();
        builder.AppendLine("[Ordered Work Summary]");

        foreach (var work in _store.Works.Values.OrderBy(work => ResolveSystemName(work)).ThenBy(work => ResolveFlowName(work)).ThenBy(GetSequenceOrder))
        {
            var costs = CalculateCosts(work);
            builder.AppendLine($"{GetSequenceOrder(work),2} | {work.Name,-28} | Cost={costs.TotalCost,10:N0} {currency} | Unit={costs.UnitCost,10:N0}");
        }

        return builder.ToString();
    }

    private void SaveStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".aasx":
                AasxExporter.exportFromStore(_store, path, "https://costsim.local/", false);
                break;
            default:
                _store.SaveToFile(path);
                break;
        }
    }

    private static void LoadStore(DsStore store, string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".aasx":
                AasxImporter.importIntoStore(store, path);
                break;
            default:
                store.LoadFromFile(path);
                break;
        }
    }

    private void RefreshSummary()
    {
        var allRows = _store.Works.Values.Select(BuildWorkRow).ToList();
        var totalCost = allRows.Sum(row => row.TotalCost);
        var totalDuration = allRows.Sum(row => row.DurationSeconds);
        var sourceCount = allRows.Count(row => row.IsSource);
        var averageUnitCost = allRows.Count == 0 ? 0.0 : allRows.Average(row => row.UnitCost);
        var currency = ResolveCurrency();
        var scope = _selectedNode is null ? "All Works" : $"{_selectedNode.EntityKind} / {_selectedNode.Name}";

        var builder = new StringBuilder();
        builder.AppendLine($"Currency            : {currency}");
        builder.AppendLine($"Projects            : {_store.Projects.Count}");
        builder.AppendLine($"Systems             : {_store.Systems.Count}");
        builder.AppendLine($"Flows               : {_store.Flows.Count}");
        builder.AppendLine($"Works               : {_store.Works.Count}");
        builder.AppendLine($"Source Works        : {sourceCount}");
        builder.AppendLine($"Total Duration      : {totalDuration:N1} sec");
        builder.AppendLine($"Total Cost          : {totalCost:N0} {currency}");
        builder.AppendLine($"Avg Unit Cost       : {averageUnitCost:N0} {currency}");
        builder.AppendLine($"Tree Order Flows    : {_store.Flows.Count}");
        builder.AppendLine($"Arrow Count         : {_store.ArrowWorks.Count}");
        builder.AppendLine($"Current Scope       : {scope}");
        builder.AppendLine($"Current File        : {_currentFilePath ?? "(unsaved)"}");
        builder.AppendLine($"Report Ready        : {(_lastReport is null ? "No" : "Yes")}");

        ProjectSummaryTextBox.Text = builder.ToString();
        RefreshOutputPanel();
    }

    private void RefreshDocumentLabels()
    {
        FooterFileTextBlock.Text = _currentFilePath ?? "(unsaved JSON/AASX)";
    }

    private void SetExplorerExpansion(bool isExpanded)
    {
        ExplorerTreeView.UpdateLayout();
        foreach (var item in ExplorerTreeView.Items)
        {
            if (ExplorerTreeView.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem treeItem)
                SetTreeItemExpansion(treeItem, isExpanded);
        }
    }

    private static void SetTreeItemExpansion(TreeViewItem item, bool isExpanded)
    {
        item.IsExpanded = isExpanded;
        item.UpdateLayout();

        foreach (var child in item.Items)
        {
            if (item.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem childItem)
                SetTreeItemExpansion(childItem, isExpanded);
        }
    }

    private TreeNodeItem? ResolveContextMenuTreeNode(DependencyObject? originalSource)
        => FindVisualParent<TreeViewItem>(originalSource)?.DataContext as TreeNodeItem;

    private LibraryCatalogNode? ResolveContextMenuLibraryNode(DependencyObject? originalSource)
        => FindVisualParent<TreeViewItem>(originalSource)?.DataContext as LibraryCatalogNode;

    private static WorkCostRow? ResolveContextMenuRow(DependencyObject? originalSource)
        => FindVisualParent<DataGridRow>(originalSource)?.Item as WorkCostRow;

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
                return target;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static void SetMenuVisibility(FrameworkElement element, bool visible)
        => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static void CollapseDuplicateSeparators(ContextMenu menu)
    {
        var previousWasVisibleSeparator = true;

        foreach (var item in menu.Items)
        {
            if (item is Separator separator)
            {
                var shouldShow = !previousWasVisibleSeparator;
                separator.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                previousWasVisibleSeparator = shouldShow;
            }
            else if (item is FrameworkElement element && element.Visibility == Visibility.Visible)
            {
                previousWasVisibleSeparator = false;
            }
        }

        for (var index = menu.Items.Count - 1; index >= 0; index--)
        {
            if (menu.Items[index] is not Separator separator)
                break;

            separator.Visibility = Visibility.Collapsed;
        }
    }

    private string ResolveEntityNameForEdit(TreeNodeItem node)
    {
        return node.EntityKind switch
        {
            EntityKind.Project when _store.Projects.TryGetValue(node.Id, out var project) => project.Name,
            EntityKind.System when _store.Systems.TryGetValue(node.Id, out var system) => system.Name,
            EntityKind.Flow when _store.Flows.TryGetValue(node.Id, out var flow) => flow.Name,
            EntityKind.Work when _store.Works.TryGetValue(node.Id, out var work) => work.LocalName,
            _ => node.Name
        };
    }

    private Guid? ResolveProjectIdForSelection()
    {
        if (_selectedNode is null)
            return _store.Projects.Keys.FirstOrDefault();

        return _selectedNode.EntityKind switch
        {
            EntityKind.Project => _selectedNode.Id,
            EntityKind.System => ResolveProjectIdForSystem(_selectedNode.Id),
            EntityKind.Flow => _store.Flows.TryGetValue(_selectedNode.Id, out var flow) ? ResolveProjectIdForSystem(flow.ParentId) : null,
            EntityKind.Work => _store.Works.TryGetValue(_selectedNode.Id, out var work) ? ResolveProjectIdForFlow(work.ParentId) : null,
            _ => _store.Projects.Keys.FirstOrDefault()
        };
    }

    private Guid? ResolveSystemIdForSelection()
    {
        if (_selectedNode is null)
            return _store.Systems.Keys.FirstOrDefault();

        return _selectedNode.EntityKind switch
        {
            EntityKind.System => _selectedNode.Id,
            EntityKind.Flow => _store.Flows.TryGetValue(_selectedNode.Id, out var flow) ? flow.ParentId : null,
            EntityKind.Work => _store.Works.TryGetValue(_selectedNode.Id, out var work) && _store.Flows.TryGetValue(work.ParentId, out var parentFlow) ? parentFlow.ParentId : null,
            EntityKind.Project => _store.Projects.TryGetValue(_selectedNode.Id, out var project) ? project.ActiveSystemIds.FirstOrDefault() : null,
            _ => null
        };
    }

    private Guid? ResolveFlowIdForSelection()
    {
        if (_selectedNode is null)
            return _store.Flows.Keys.FirstOrDefault();

        return _selectedNode.EntityKind switch
        {
            EntityKind.Flow => _selectedNode.Id,
            EntityKind.Work => _store.Works.TryGetValue(_selectedNode.Id, out var work) ? work.ParentId : null,
            EntityKind.System => GetFlowsInSystem(_selectedNode.Id).Select(flow => (Guid?)flow.Id).FirstOrDefault(),
            EntityKind.Project => GetWorksInProject(_selectedNode.Id).Select(work => (Guid?)work.ParentId).FirstOrDefault(),
            _ => null
        };
    }

    private Guid? ResolveSelectedFlowForOrder()
    {
        return _selectedNode?.EntityKind switch
        {
            EntityKind.Flow => _selectedNode.Id,
            EntityKind.Work => _store.Works.TryGetValue(_selectedNode.Id, out var work) ? work.ParentId : null,
            _ => null
        };
    }

    private IEnumerable<Guid> ResolveScopedFlowIds()
    {
        if (_selectedNode is null)
            return ResolveAllFlowIds();

        return _selectedNode.EntityKind switch
        {
            EntityKind.Project => GetWorksInProject(_selectedNode.Id).Select(work => work.ParentId).Distinct(),
            EntityKind.System => GetFlowsInSystem(_selectedNode.Id).Select(flow => flow.Id),
            EntityKind.Flow => [_selectedNode.Id],
            EntityKind.Work when _store.Works.TryGetValue(_selectedNode.Id, out var work) => [work.ParentId],
            _ => ResolveAllFlowIds()
        };
    }

    private List<Guid> ResolveAllFlowIds()
        => _store.Flows.Values.OrderBy(flow => flow.Name, StringComparer.CurrentCultureIgnoreCase).Select(flow => flow.Id).ToList();

    private Guid? ResolveProjectIdForFlow(Guid flowId)
    {
        if (!_store.Flows.TryGetValue(flowId, out var flow))
            return null;
        return ResolveProjectIdForSystem(flow.ParentId);
    }

    private Guid? ResolveProjectIdForSystem(Guid systemId)
        => _store.Projects.Values.FirstOrDefault(project => project.ActiveSystemIds.Contains(systemId) || project.PassiveSystemIds.Contains(systemId))?.Id;

    private IEnumerable<Flow> GetFlowsInSystem(Guid systemId)
        => GetFlowsInSystem(_store, systemId);

    private static IEnumerable<Flow> GetFlowsInSystem(DsStore store, Guid systemId)
        => store.Flows.Values.Where(flow => flow.ParentId == systemId);

    private IEnumerable<Work> GetWorksInProject(Guid projectId)
    {
        if (!_store.Projects.TryGetValue(projectId, out var project))
            return [];

        var systemIds = GetProjectSystemIds(project);
        return systemIds.SelectMany(GetWorksInSystem);
    }

    private IEnumerable<Work> GetWorksInSystem(Guid systemId)
        => GetFlowsInSystem(systemId).SelectMany(flow => GetOrderedWorksInFlow(flow.Id));

    private IEnumerable<Work> GetOrderedWorksInFlow(Guid flowId)
        => GetOrderedWorksInFlow(_store, flowId);

    private static IEnumerable<Work> GetOrderedWorksInFlow(DsStore store, Guid flowId)
        => CostSimStoreHelper.GetOrderedWorksInFlow(store, flowId);

    private int CountFlows(Guid systemId) => GetFlowsInSystem(systemId).Count();

    private int CountWorks(Guid flowId) => _store.Works.Values.Count(work => work.ParentId == flowId);

    private string ResolveSystemName(Work work)
        => ResolveSystemName(_store, work);

    private static string ResolveSystemName(DsStore store, Work work)
    {
        if (!store.Flows.TryGetValue(work.ParentId, out var flow))
            return "(unknown)";
        return store.Systems.TryGetValue(flow.ParentId, out var system) ? system.Name : "(unknown)";
    }

    private string ResolveFlowName(Work work)
        => ResolveFlowName(_store, work);

    private static string ResolveFlowName(DsStore store, Work work)
        => store.Flows.TryGetValue(work.ParentId, out var flow) ? flow.Name : "(unknown)";

    private static IEnumerable<Guid> GetProjectSystemIds(Project project)
        => project.ActiveSystemIds.Concat(project.PassiveSystemIds).Distinct();

    private IEnumerable<string> GetSystemNamesInProject(Guid projectId)
    {
        if (!_store.Projects.TryGetValue(projectId, out var project))
            return [];

        return GetProjectSystemIds(project)
            .Select(systemId => _store.Systems.TryGetValue(systemId, out var system) ? system.Name : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>();
    }

    private IEnumerable<string> GetWorkNamesInFlow(Guid flowId)
        => _store.Works.Values.Where(work => work.ParentId == flowId).Select(work => work.LocalName);

    private WorkCostRow BuildWorkRow(Work work)
    {
        var props = GetExistingProps(work);
        var costs = CalculateCosts(work);

        return new WorkCostRow
        {
            WorkId = work.Id,
            SequenceOrder = GetSequenceOrder(work),
            SystemName = ResolveSystemName(work),
            FlowName = ResolveFlowName(work),
            WorkName = work.LocalName,
            OperationCode = ReadOption(props?.OperationCode),
            DurationSeconds = costs.DurationSeconds,
            WorkerCount = props?.WorkerCount ?? 1,
            LaborCostPerHour = props?.LaborCostPerHour ?? 0.0,
            EquipmentCostPerHour = props?.EquipmentCostPerHour ?? 0.0,
            OverheadCostPerHour = props?.OverheadCostPerHour ?? 0.0,
            UtilityCostPerHour = props?.UtilityCostPerHour ?? 0.0,
            YieldRate = props?.YieldRate ?? 1.0,
            DefectRate = props?.DefectRate ?? 0.0,
            TotalCost = costs.TotalCost,
            UnitCost = costs.UnitCost,
            IsSource = work.TokenRole.HasFlag(TokenRole.Source)
        };
    }

    private static WorkCostSnapshot CalculateCosts(Work work)
    {
        var props = GetExistingProps(work);
        if (props is null)
            return WorkCostSnapshot.Empty;

        var durationSeconds = props.Duration is { } duration ? duration.Value.TotalSeconds : 0.0;
        var workerCount = Math.Max(1, props.WorkerCount);
        var laborCost = CostAnalysisHelpers.calculateLaborCost(props.LaborCostPerHour, durationSeconds, workerCount);
        var equipmentCost = (props.EquipmentCostPerHour / 3600.0) * durationSeconds;
        var overheadCost = (props.OverheadCostPerHour / 3600.0) * durationSeconds;
        var utilityCost = CostAnalysisHelpers.calculateUtilityCost(props.UtilityCostPerHour, durationSeconds);
        var totalCost = laborCost + equipmentCost + overheadCost + utilityCost;
        var effectiveYield = Math.Max(0.01, props.YieldRate * (1.0 - props.DefectRate));
        var unitCost = totalCost / effectiveYield;

        return new WorkCostSnapshot(
            durationSeconds,
            laborCost,
            equipmentCost,
            overheadCost,
            utilityCost,
            totalCost,
            unitCost,
            effectiveYield);
    }

    private static CostAnalysisWorkProperties? GetExistingProps(Work work)
        => CostSimStoreHelper.GetExistingProps(work);

    private static CostAnalysisWorkProperties GetOrCreateProps(Work work)
        => CostSimStoreHelper.GetOrCreateProps(work);

    private void EnsureSystemCostSettings(Guid systemId, bool emitHistory)
        => EnsureSystemCostSettings(_store, systemId, emitHistory);

    private static void EnsureSystemCostSettings(DsStore store, Guid systemId, bool emitHistory)
    {
        RunTransaction(store, "CostSim System 기본설정", () =>
        {
            TrackMutate(store, store.Systems, systemId, system =>
            {
                var props = system.GetCostAnalysisProperties() is { } simProps
                    ? simProps.Value
                    : new CostAnalysisSystemProperties();

                props.EnableCostSimulation = true;
                props.DefaultCurrency = "KRW";
                system.SystemType = system.SystemType ?? FSharpOption<string>.Some("Line");

                system.SetCostAnalysisProperties(props);
            });
        });

        if (emitHistory)
            store.EmitRefreshAndHistory();
    }

    private void EnsureSequenceOrdersAllFlows(bool emitHistory)
    {
        var flowIds = ResolveAllFlowIds();
        if (flowIds.Count == 0)
            return;

        if (emitHistory)
        {
            RunTransaction(_store, "CostSim SequenceOrder 정규화", () =>
            {
                foreach (var flowId in flowIds)
                    NormalizeFlowOrderTracked(flowId);
            });
            _store.EmitRefreshAndHistory();
            return;
        }

        foreach (var flowId in flowIds)
            NormalizeFlowOrderDirect(flowId);
    }

    private void NormalizeFlowOrderTracked(Guid flowId)
    {
        var orderedWorks = GetOrderedWorksInFlow(flowId).ToList();
        if (orderedWorks.Count == 0)
            return;

        var nextOrder = SequenceStep;
        foreach (var work in orderedWorks)
        {
            if (GetSequenceOrder(work) != nextOrder)
            {
                TrackMutate(_store, _store.Works, work.Id, target =>
                {
                    GetOrCreateProps(target).SequenceOrder = nextOrder;
                });
            }

            nextOrder += SequenceStep;
        }
    }

    private void NormalizeFlowOrderDirect(Guid flowId)
    {
        var orderedWorks = GetOrderedWorksInFlow(flowId).ToList();
        if (orderedWorks.Count == 0)
            return;

        var hasInvalidOrder = orderedWorks.Any(work => GetSequenceOrder(work) <= 0)
                              || orderedWorks.Select(GetSequenceOrder).Distinct().Count() != orderedWorks.Count;
        if (!hasInvalidOrder)
            return;

        var nextOrder = SequenceStep;
        foreach (var work in orderedWorks)
        {
            GetOrCreateProps(work).SequenceOrder = nextOrder;
            nextOrder += SequenceStep;
        }
    }

    private void ApplyTreeOrderDirect(IEnumerable<Guid> flowIds, bool autoBoundaryRoles)
    {
        foreach (var flowId in flowIds.Distinct())
            ApplyFlowTreeOrderDirect(flowId, autoBoundaryRoles);
    }

    private void ApplyFlowTreeOrderTracked(Guid flowId, bool autoBoundaryRoles)
    {
        if (!_store.Flows.TryGetValue(flowId, out _))
            return;

        var orderedWorks = GetOrderedWorksInFlow(flowId).ToList();
        if (orderedWorks.Count == 0)
            return;

        if (autoBoundaryRoles)
            EnsureFlowBoundaryRolesTracked(orderedWorks);

        _store.TrackSyncOrderedWorkChain(orderedWorks.Select(work => work.Id), ArrowType.Start);
    }

    private void ApplyFlowTreeOrderDirect(Guid flowId, bool autoBoundaryRoles)
    {
        if (!_store.Flows.TryGetValue(flowId, out _))
            return;

        var orderedWorks = GetOrderedWorksInFlow(flowId).ToList();
        if (orderedWorks.Count == 0)
            return;

        if (autoBoundaryRoles)
            EnsureFlowBoundaryRolesDirect(orderedWorks);

        _store.SyncOrderedWorkChainDirect(orderedWorks.Select(work => work.Id), ArrowType.Start);
    }

    private void EnsureFlowBoundaryRolesTracked(IReadOnlyList<Work> orderedWorks)
    {
        if (orderedWorks.Count == 0)
            return;

        if (!orderedWorks.Any(work => work.TokenRole.HasFlag(TokenRole.Source)))
        {
            TrackMutate(_store, _store.Works, orderedWorks[0].Id, work =>
            {
                work.TokenRole |= TokenRole.Source;
            });
        }

        if (!orderedWorks.Any(work => work.TokenRole.HasFlag(TokenRole.Sink)))
        {
            TrackMutate(_store, _store.Works, orderedWorks[^1].Id, work =>
            {
                work.TokenRole |= TokenRole.Sink;
            });
        }
    }

    private static void EnsureFlowBoundaryRolesDirect(IReadOnlyList<Work> orderedWorks)
    {
        if (orderedWorks.Count == 0)
            return;

        if (!orderedWorks.Any(work => work.TokenRole.HasFlag(TokenRole.Source)))
            orderedWorks[0].TokenRole |= TokenRole.Source;

        if (!orderedWorks.Any(work => work.TokenRole.HasFlag(TokenRole.Sink)))
            orderedWorks[^1].TokenRole |= TokenRole.Sink;
    }

    private static void EnableSinkFlagDirect(Work work, bool enabled)
    {
        work.TokenRole = enabled ? work.TokenRole | TokenRole.Sink : work.TokenRole & ~TokenRole.Sink;
    }

    private static int GetSequenceOrder(Work work)
        => CostSimStoreHelper.GetSequenceOrder(work);

    private static int GetSequenceSortKey(Work work)
    {
        var sequence = GetSequenceOrder(work);
        return sequence <= 0 ? int.MaxValue : sequence;
    }

    private static void SetSequenceOrderDirect(Work work, int sequenceOrder)
        => CostSimStoreHelper.SetSequenceOrder(work, sequenceOrder);

    private int GetNextSequenceOrder(Guid flowId)
    {
        var maxOrder = GetOrderedWorksInFlow(flowId).Select(GetSequenceOrder).DefaultIfEmpty(0).Max();
        return ((maxOrder / SequenceStep) + 1) * SequenceStep;
    }

    private string ResolveCurrency()
    {
        foreach (var system in _store.Systems.Values)
        {
            if (system.GetCostAnalysisProperties() is { } props && !string.IsNullOrWhiteSpace(props.Value.DefaultCurrency))
                return props.Value.DefaultCurrency;
        }

        return "KRW";
    }

    private string? PromptForText(string title, string label, string initialValue)
    {
        var dialog = new TextPromptDialog(title, label, initialValue)
        {
            Owner = this
        };

        return dialog.ShowDialog() == true ? dialog.ResultText : null;
    }

    private string? PromptForDocumentPath(bool saveMode)
    {
        const string filter = "DS2 Document|*.json;*.aasx|JSON|*.json|AASX|*.aasx";

        if (saveMode)
        {
            var dialog = new SaveFileDialog
            {
                Filter = filter,
                FileName = string.IsNullOrWhiteSpace(_currentFilePath) ? "costsim-project.json" : Path.GetFileName(_currentFilePath),
                DefaultExt = ".json"
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        var openDialog = new OpenFileDialog
        {
            Filter = filter
        };
        return openDialog.ShowDialog(this) == true ? openDialog.FileName : null;
    }

    private string? PromptForReportPath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "HTML Report|*.html|CSV Report|*.csv",
            FileName = "costsim-report.html",
            DefaultExt = ".html"
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string? PromptForExcelReportPath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel Report|*.xlsx",
            FileName = "costsim-report.xlsx",
            DefaultExt = ".xlsx"
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void ExportCurrentReport(string path, string activityLabel, string statusLabel)
    {
        if (_lastReport is null)
            return;

        var result = ReportService.exportAuto(_lastReport, path);
        switch (result)
        {
            case ExportResult.Success:
                AppendActivity($"{activityLabel}: {path}");
                SetStatus($"{statusLabel}: {path}");
                break;
            case ExportResult.Error:
                ShowError($"리포트 저장 실패: {result}");
                break;
        }
    }

    private void AppendActivity(string message)
    {
        _outputBuffer.AppendActivity(message);
        RefreshOutputPanel();
    }

    private void SetStatus(string message)
    {
        _outputBuffer.SetStatus(message);
        StatusTextBlock.Text = _outputBuffer.StatusMessage;
        RefreshOutputPanel();
    }

    private void RefreshOutputPanel()
    {
        if (OutputTextBox is null)
            return;

        OutputTextBox.Text = _outputBuffer.BuildOutputText();
    }

    private void ShowInfo(string message)
        => MessageBox.Show(this, message, "CostSim", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowError(string message)
        => MessageBox.Show(this, message, "CostSim", MessageBoxButton.OK, MessageBoxImage.Error);

    private static bool TryParseDouble(string text, out double value)
    {
        var styles = NumberStyles.Float | NumberStyles.AllowThousands;
        return double.TryParse(text, styles, CultureInfo.CurrentCulture, out value)
               || double.TryParse(text, styles, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseInt(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
               || int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static string ReadOption(FSharpOption<string>? option)
        => CostSimStoreHelper.ReadOption(option);

    private static void RunTransaction(DsStore store, string label, Action action)
        => CostSimStoreHelper.RunTransaction(store, label, action);

    private static void TrackMutate<TEntity>(
        DsStore store,
        Dictionary<Guid, TEntity> dict,
        Guid id,
        Action<TEntity> mutate)
        where TEntity : DsEntity
        => CostSimStoreHelper.TrackMutate(store, dict, id, mutate);

    private static FSharpOption<string>? ToOption(string text)
        => CostSimStoreHelper.ToOption(text);

    private readonly record struct WorkEditorValues(
        string Name,
        string OperationCode,
        double DurationSeconds,
        int WorkerCount,
        double LaborCostPerHour,
        double EquipmentCostPerHour,
        double OverheadCostPerHour,
        double UtilityCostPerHour,
        double YieldRate,
        double DefectRate,
        bool IsSource);

    private readonly record struct WorkCostSnapshot(
        double DurationSeconds,
        double LaborCost,
        double EquipmentCost,
        double OverheadCost,
        double UtilityCost,
        double TotalCost,
        double UnitCost,
        double EffectiveYield)
    {
        public static WorkCostSnapshot Empty => new(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0);
    }

    private readonly record struct SimulationExecutionResult(
        SimulationReport Report,
        string ReportText,
        int StepCount,
        int EventCount);
}
