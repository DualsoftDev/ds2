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
using Promaker.ViewModels;

namespace Promaker.Windows;

public partial class View3DWindow : Window
{
    private const string VirtualHostName = "promaker.app";
    private const string FacilityHtmlPage = "facility3d.html";

    private static class UIColors
    {
        public static readonly Color ActiveBlue = (Color)ColorConverter.ConvertFromString("#3b82f6");
        public static readonly Color InactiveGray = (Color)ColorConverter.ConvertFromString("#475569");
        public static readonly Color InactiveTextGray = (Color)ColorConverter.ConvertFromString("#94a3b8");
    }

    private readonly ThreeDViewState _vm;
    private readonly Func<Task>? _onReady;
    private DsStore? _store;
    private Guid _projectId;
    private bool _isDeviceScene = true;

    public View3DWindow(ThreeDViewState vm, Func<Task>? onReady = null)
    {
        _vm = vm;
        _onReady = onReady;
        InitializeComponent();
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
        KeyDown += OnKeyDown;
    }

    public void SetSceneData(DsStore store, Guid projectId)
    {
        _store = store;
        _projectId = projectId;
        BuildDeviceTree();
    }

    private void BuildDeviceTree()
    {
        if (_store == null) return;

        DeviceTree.Items.Clear();

        var project = _store.Projects.Values.FirstOrDefault(p => p.Id == _projectId);
        if (project == null) return;

        // Group devices by Flow
        var allSystems = _store.Systems.Values.ToList();
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
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94a3b8")),
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 2)
            };

            var valueText = new TextBlock
            {
                Text = value?.ToString() ?? "N/A",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e2e8f0")),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };

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
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e2e8f0")!),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        InfoContent.Children.Add(summary);

        AddConnectionGroup("Outgoing", outgoing, "→ ", "#10b981");
        AddConnectionGroup("Incoming", incoming, "← ", "#f59e0b");
    }

    private void AddConnectionGroup(string title, IReadOnlyList<ConnectionItem> items,
        string prefix, string prefixColor)
    {
        if (items.Count == 0) return;

        InfoContent.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94a3b8")!),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 2)
        });

        foreach (var item in items)
        {
            var tb = new TextBlock { FontSize = 11, Margin = new Thickness(4, 1, 0, 1) };
            tb.Inlines.Add(new Run(prefix)
            {
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(prefixColor)!),
                FontWeight = FontWeights.Bold
            });
            tb.Inlines.Add(new Run(item.Display)
            {
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e2e8f0")!)
            });
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
        if (e.NewValue is not TreeViewItem item || item.Tag == null) return;

        // Use ValueTuple pattern matching
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

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView3D.EnsureCoreWebView2Async();

            // wwwroot 폴더를 가상 호스트에 매핑
            var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            WebView3D.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);

            // JS → C# 콜백 수신 (선택 이벤트)
            WebView3D.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // C# → JS 전송 델리게이트 주입
            _vm.SetWebViewSender(async json =>
            {
                if (WebView3D.CoreWebView2 != null)
                    await WebView3D.CoreWebView2.ExecuteScriptAsync(
                        $"handleFromCSharp({json})");
            });

            // 페이지 로드 완료 후 씬 빌드
            WebView3D.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            // facility3d.html 로드
            WebView3D.Source = new Uri($"https://{VirtualHostName}/{FacilityHtmlPage}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] WebView2 init failed: {ex.Message}");
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        WebView3D.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        if (_onReady != null)
        {
            try { await _onReady(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[View3DWindow] onReady failed: {ex.Message}");
            }
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;

            var doc = JsonDocument.Parse(raw);
            var method = doc.RootElement.GetProperty("method").GetString() ?? "";
            var argsEl = doc.RootElement.GetProperty("args");
            var args = argsEl.EnumerateArray().ToArray();

            _vm.OnSelectionMessage(method, args);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] WebMessage parse error: {ex.Message}");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        try { _ = WebView3D.CoreWebView2?.ExecuteScriptAsync("if(window.Ds2Sound)Ds2Sound.stopAll();"); }
        catch { }
        if (WebView3D.CoreWebView2 != null)
        {
            WebView3D.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
        _vm.SetWebViewSender(null);
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

    private static void ApplyButtonState(Button button, bool isActive)
    {
        button.Background = new SolidColorBrush(isActive ? UIColors.ActiveBlue : UIColors.InactiveGray);
        button.Foreground = new SolidColorBrush(isActive ? Colors.White : UIColors.InactiveTextGray);
    }
}
