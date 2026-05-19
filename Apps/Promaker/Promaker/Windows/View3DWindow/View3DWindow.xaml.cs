using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.View3D;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker.Windows;

public partial class View3DWindow : Window
{
    private const string VirtualHostName = "promaker.app";
    private const string FacilityHtmlPage = "facility3d.html";

    private readonly ThreeDViewState _vm;
    private readonly Func<Task>? _onReady;
    private DsStore? _store;
    private Guid _projectId;
    private string? _projectDir;
    private bool _isDeviceScene = true;
    private CustomModelRegistry? _customModelRegistry;
    private bool _suppressTreeSelectionEvent;
    // Set true once the WebView page is ready to receive setTheme messages.
    // Theme changes that arrive before this point are applied automatically
    // when the page finishes loading (via OnNavigationCompleted).
    private bool _webViewReady;

    public View3DWindow(ThreeDViewState vm, Func<Task>? onReady = null)
    {
        _vm = vm;
        _onReady = onReady;
        InitializeComponent();
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
        KeyDown += OnKeyDown;
        ThemeManager.ThemeChanged += OnPromakerThemeChanged;
    }

    private void OnPromakerThemeChanged(AppTheme theme)
    {
        PushThemeToWebView(theme);
        // SetResourceReference 로 묶이지 않은 Scene Mode 토글 버튼은 수동 재적용
        UpdateButtonStates();
    }

    private void PushThemeToWebView(AppTheme theme)
    {
        if (!_webViewReady || WebView3D?.CoreWebView2 == null) return;
        var keyword = theme == AppTheme.Light ? "light" : "dark";
        var script = $"handleFromCSharp({{ type: 'setTheme', theme: '{keyword}' }})";
        try { _ = WebView3D.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] setTheme push failed: {ex.Message}");
        }
    }

    public void SetSceneData(DsStore store, Guid projectId, string? projectFilePath = null)
    {
        _store = store;
        _projectId = projectId;
        _projectDir = projectFilePath != null ? Path.GetDirectoryName(projectFilePath) : null;
        BuildDeviceTree();
        InitCustomModelRegistry();
    }

    /// <summary>커스텀 모델 레지스트리를 외부에서 접근할 수 있게 제공</summary>
    public CustomModelRegistry? GetCustomModelRegistry() => _customModelRegistry;

    private void BuildDeviceTree()
    {
        if (_store == null) return;

        DeviceTree.Items.Clear();

        var project = _store.Projects.Values.FirstOrDefault(p => p.Id == _projectId);
        if (project == null) return;

        // 실제 설비(Passive)만 조회 — F# Core 의 단일 진원지 Queries.passiveSystemsOf 재사용
        var allSystems = Queries.passiveSystemsOf(project.Id, _store);
        var systemFlowMap = new Dictionary<Guid, List<Guid>>();

        // Find flows that contain each system
        foreach (var flow in _store.Flows.Values)
        {
            foreach (var work in _store.Works.Values.Where(w => w.ParentId == flow.Id))
            {
                if (!systemFlowMap.ContainsKey(work.ParentId))
                    systemFlowMap[work.ParentId] = new List<Guid>();
                if (!systemFlowMap[work.ParentId].Contains(flow.Id))
                    systemFlowMap[work.ParentId].Add(flow.Id);
            }
        }

        // Group systems by their primary flow
        var flowGroups = new Dictionary<Guid, List<DsSystem>>();
        foreach (var system in allSystems)
        {
            if (systemFlowMap.TryGetValue(system.Id, out var flowIds) && flowIds.Any())
            {
                var primaryFlowId = flowIds.First();
                if (!flowGroups.ContainsKey(primaryFlowId))
                    flowGroups[primaryFlowId] = new List<DsSystem>();
                flowGroups[primaryFlowId].Add(system);
            }
        }

        foreach (var (flowId, systems) in flowGroups)
        {
            var flow = _store.Flows.GetValueOrDefault(flowId);
            if (flow == null) continue;

            var flowNode = new TreeViewItem
            {
                Header = $"📁 {flow.Name}",
                Tag = (Type: "Flow", Id: flowId)
            };

            foreach (var system in systems.OrderBy(s => s.Name))
            {
                var deviceNode = new TreeViewItem
                {
                    Header = $"📦 {system.Name}",
                    Tag = (Type: "Device", Id: system.Id, Name: system.Name)
                };
                flowNode.Items.Add(deviceNode);
            }

            DeviceTree.Items.Add(flowNode);
        }

        // Unassigned devices
        var assignedSystemIds = flowGroups.Values.SelectMany(l => l.Select(s => s.Id)).ToHashSet();
        var unassignedSystems = allSystems.Where(s => !assignedSystemIds.Contains(s.Id)).ToList();

        if (unassignedSystems.Any())
        {
            var unassignedNode = new TreeViewItem
            {
                Header = "📁 Unassigned",
                Tag = (Type: "Flow", Id: Guid.Empty)
            };

            foreach (var system in unassignedSystems.OrderBy(s => s.Name))
            {
                var deviceNode = new TreeViewItem
                {
                    Header = $"📦 {system.Name}",
                    Tag = (Type: "Device", Id: system.Id, Name: system.Name)
                };
                unassignedNode.Items.Add(deviceNode);
            }

            DeviceTree.Items.Add(unassignedNode);
        }
    }

    public void ShowDeviceInfo(string deviceName, Dictionary<string, object> deviceData)
    {
        InfoContent.Children.Clear();
        InfoTitle.Text = $"장비: {deviceName}";

        foreach (var (key, value) in deviceData)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            var label = new TextBlock
            {
                Text = key,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 2)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");

            var valueText = new TextBlock
            {
                Text = value?.ToString() ?? "N/A",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
            valueText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");

            panel.Children.Add(label);
            panel.Children.Add(valueText);
            InfoContent.Children.Add(panel);
        }
    }

    public void ShowConnectionInfo(string apiDefLabel,
        IReadOnlyList<ConnectionItem> outgoing,
        IReadOnlyList<ConnectionItem> incoming)
    {
        InfoContent.Children.Clear();
        InfoTitle.Text = $"연결: {apiDefLabel}";

        var summary = new TextBlock
        {
            Text = $"→ Out {outgoing.Count}   ← In {incoming.Count}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        InfoContent.Children.Add(summary);

        // 연결 방향 색상은 의미가 있으므로 테마 리소스의 Green/Orange 사용
        AddConnectionGroup("Outgoing", outgoing, "→ ", "GreenAccentBrush");
        AddConnectionGroup("Incoming", incoming, "← ", "OrangeAccentBrush");
    }

    private void AddConnectionGroup(string title, IReadOnlyList<ConnectionItem> items,
        string prefix, string prefixBrushKey)
    {
        if (items.Count == 0) return;

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 2)
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        InfoContent.Children.Add(titleBlock);

        foreach (var item in items)
        {
            var tb = new TextBlock { FontSize = 11, Margin = new Thickness(4, 1, 0, 1) };
            var prefixRun = new Run(prefix) { FontWeight = FontWeights.Bold };
            prefixRun.SetResourceReference(Run.ForegroundProperty, prefixBrushKey);
            tb.Inlines.Add(prefixRun);

            var bodyRun = new Run(item.Display);
            bodyRun.SetResourceReference(Run.ForegroundProperty, "PrimaryTextBrush");
            tb.Inlines.Add(bodyRun);

            InfoContent.Children.Add(tb);
        }
    }

    private void ToggleLeftPanel_Click(object sender, RoutedEventArgs e)
    {
        ToggleLeftPanel();
    }

    private void ToggleLeftPanel()
    {
        var isVisible = LeftPanelColumn.Width.Value > 0;

        // GridLength cannot be animated with DoubleAnimation, so just toggle immediately
        LeftPanelColumn.Width = new GridLength(isVisible ? 0 : 280);
    }

    private void DeviceTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelectionEvent) return;
        if (e.NewValue is not TreeViewItem item || item.Tag == null) return;

        if (item.Tag is ValueTuple<string, Guid, string> deviceTag && deviceTag.Item1 == "Device")
        {
            var deviceName = deviceTag.Item3;
            if (!string.IsNullOrEmpty(deviceName))
            {
                _ = WebView3D.CoreWebView2?.ExecuteScriptAsync(
                    $"Ev23DViewer.selectDeviceByName('scene3d', '{deviceName}')");
            }
        }
    }

    /// <summary>
    /// 3D 뷰에서 디바이스 클릭 시 Tree의 해당 노드를 선택/확장한다.
    /// </summary>
    public void SelectDeviceInTree(Guid systemId)
    {
        foreach (TreeViewItem flowNode in DeviceTree.Items)
        {
            foreach (TreeViewItem deviceNode in flowNode.Items)
            {
                if (deviceNode.Tag is ValueTuple<string, Guid, string> tag
                    && tag.Item1 == "Device" && tag.Item2 == systemId)
                {
                    _suppressTreeSelectionEvent = true;
                    try
                    {
                        flowNode.IsExpanded = true;
                        deviceNode.IsSelected = true;
                        deviceNode.BringIntoView();
                    }
                    finally
                    {
                        _suppressTreeSelectionEvent = false;
                    }
                    return;
                }
            }
        }
    }

    private void CameraMode_Checked(object sender, RoutedEventArgs e)
    {
        // Skip if WebView2 is not initialized yet
        if (WebView3D?.CoreWebView2 == null) return;

        // Disable edit mode
        _ = WebView3D.CoreWebView2.ExecuteScriptAsync(
            "Ev23DViewer.setEditMode('scene3d', false)");
    }

    private void MoveMode_Checked(object sender, RoutedEventArgs e)
    {
        // Skip if WebView2 is not initialized yet
        if (WebView3D?.CoreWebView2 == null) return;

        // Enable device edit mode
        _ = WebView3D.CoreWebView2.ExecuteScriptAsync(
            "Ev23DViewer.setEditMode('scene3d', true)");
    }

    private void GridSnap_Changed(object sender, RoutedEventArgs e)
    {
        if (WebView3D?.CoreWebView2 == null) return;

        var enabled = GridSnapCheck.IsChecked == true;
        _ = WebView3D.CoreWebView2.ExecuteScriptAsync(
            $"Ev23DViewer.setGridSnap('scene3d', {(enabled ? "true" : "false")})");
    }


    private async void RebuildSceneIfReady()
    {
        if (_store == null || !_vm.HasScene) return;
        try
        {
            await _vm.BuildScene(_store, _projectId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] Scene rebuild failed: {ex.Message}");
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.F2:
                // Toggle left panel visibility
                ToggleLeftPanel();
                e.Handled = true;
                break;

            case System.Windows.Input.Key.F4:
                // F4: Device Move Mode
                MoveModeRadio.IsChecked = true;
                e.Handled = true;
                break;

            case System.Windows.Input.Key.F5:
                // F5: Camera Mode
                CameraModeRadio.IsChecked = true;
                e.Handled = true;
                break;

            case System.Windows.Input.Key.F:
                // Fit camera to all devices
                if (WebView3D?.CoreWebView2 != null)
                {
                    _ = WebView3D.CoreWebView2.ExecuteScriptAsync(
                        "Ev23DViewer.fitAll('scene3d')");
                }
                e.Handled = true;
                break;
        }
    }

    private async void DeviceSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDeviceScene || _store == null) return;

        try
        {
            await _vm.BuildScene(_store, _projectId);
            _isDeviceScene = true;
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] Failed to build device scene: {ex.Message}");
        }
    }

    private async void WorkSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDeviceScene || _store == null) return;

        try
        {
            // Get first flow for WorkGraph scene
            var firstFlow = Queries.allFlows(_store).FirstOrDefault();
            if (firstFlow != null)
            {
                await _vm.BuildWorkScene(_store, firstFlow.Id);
                _isDeviceScene = false;
                UpdateButtonStates();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[View3DWindow] No flows found for Work scene");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] Failed to build work scene: {ex.Message}");
        }
    }

    private void UpdateButtonStates()
    {
        ApplyButtonState(DeviceSceneButton, _isDeviceScene);
        ApplyButtonState(WorkSceneButton, !_isDeviceScene);
    }

    /// <summary>
    /// 활성 버튼은 액센트 배경 + 액센트 위 텍스트, 비활성 버튼은 BorderBrush 위
    /// SecondaryText 로 표시. 두 테마(다크/라이트) 모두에서 충분한 대비를 확보한다.
    /// </summary>
    private void ApplyButtonState(Button button, bool isActive)
    {
        if (isActive)
        {
            button.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
            button.SetResourceReference(Control.ForegroundProperty, "AccentTextBrush");
        }
        else
        {
            button.SetResourceReference(Control.BackgroundProperty, "BorderBrush");
            button.SetResourceReference(Control.ForegroundProperty, "SecondaryTextBrush");
        }
    }
}
