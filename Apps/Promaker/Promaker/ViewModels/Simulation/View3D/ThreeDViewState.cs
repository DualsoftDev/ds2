using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.View3D;

namespace Promaker.ViewModels;

/// <summary>
/// WPF 3D 뷰 어댑터 (Layer 3).
/// SceneEngine(Layer 1)에서 씬 데이터를 가져와 WebView2(Layer 2)로 전달하고,
/// 시뮬레이션 상태 변경을 실시간으로 반영한다.
/// </summary>
public partial class ThreeDViewState : ObservableObject
{
    private const string SceneId = "promaker";
    private const double MinFloorSize = 100.0;
    private const double FloorSizeMargin = 40.0;
    private const int DefaultColor = 0x888888;

    private static readonly Dictionary<Status4, int> StatePriority = new()
    {
        [Status4.Going] = 4,
        [Status4.Homing] = 3,
        [Status4.Finish] = 2,
        [Status4.Ready] = 1
    };

    private SceneEngine? _engine;
    private Func<string, Task>? _sendToWebView;
    private Action<Guid, EntityKind>? _onDeviceSelected;
    private Action<Guid, string>? _onApiDefSelected;
    private Action? _onEmptySpaceSelected;
    private Action<string, Dictionary<string, object>>? _onDeviceInfoRequested;

    private readonly Dictionary<Guid, Guid> _callToDevice = new();
    private readonly Dictionary<Guid, Guid> _callToApiDef = new();
    private readonly Dictionary<Guid, Guid> _deviceToSystem = new();
    private readonly Dictionary<Guid, Status4> _apiDefStateCache = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _deviceToApiDefs = new();

    // Batched state updates — collect per-frame, flush once
    private readonly Dictionary<Guid, string> _pendingApiDefStates = new();
    private readonly Dictionary<Guid, string> _pendingDeviceStates = new();
    private bool _flushScheduled;
    private DateTime _lastFlushTime = DateTime.MinValue;
    private const int FlushIntervalMs = 33; // ~30fps cap for 3D view updates

    /// <summary>커스텀 모델 레지스트리 (View3DWindow에서 주입)</summary>
    private CustomModelRegistry? _customModelRegistry;

    [ObservableProperty] private bool _hasScene;

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    public void SetWebViewSender(Func<string, Task>? sender) => _sendToWebView = sender;

    public void SetCustomModelRegistry(CustomModelRegistry? registry) => _customModelRegistry = registry;

    public void SetSelectionCallbacks(
        Action<Guid, EntityKind>? onDeviceSelected,
        Action<Guid, string>? onApiDefSelected,
        Action? onEmptySpaceSelected,
        Action<string, Dictionary<string, object>>? onDeviceInfoRequested = null)
    {
        (_onDeviceSelected, _onApiDefSelected, _onEmptySpaceSelected, _onDeviceInfoRequested) =
            (onDeviceSelected, onApiDefSelected, onEmptySpaceSelected, onDeviceInfoRequested);
    }

    public async Task BuildScene(DsStore store, Guid projectId)
    {
        if (_sendToWebView == null) return;

        // F# CustomNames를 현재 레지스트리와 동기화 (삭제된 모델이 이전 세션의 잔재로 남지 않도록)
        if (_customModelRegistry != null)
            Ds2.View3D.DevicePresets.setCustomNames(_customModelRegistry.GetRegisteredNames());

        // 디버그: 커스텀 모델 등록 상태 확인
        var customNames = Ds2.View3D.DevicePresets.CustomNames;
        System.Diagnostics.Debug.WriteLine($"[3D BuildScene] CustomNames registered: [{string.Join(", ", customNames)}]");

        _engine = CreateSceneEngine(store);
        var scene = _engine.BuildDeviceScene(SceneId, projectId);

        // 디버그: 각 디바이스의 ModelType 확인
        foreach (var d in scene.Devices)
            System.Diagnostics.Debug.WriteLine($"[3D BuildScene] Device '{d.Name}': SystemType={d.SystemType}, ModelType={d.ModelType}");

        ClearCaches();
        BuildMappings(store, scene);

        var floorSize = CalculateFloorSize(scene.Devices, scene.FlowZones);
        await SendSceneInitMessage(scene.FlowZones, scene.Devices, floorSize);
        // fitAll 메시지 제거 — JS 가 devices 추가 완료 후 자체 fitCameraToDevices 수행

        HasScene = true;
    }

    /// <summary>
    /// "전체 재배치" — 저장된 layout 을 무시하고 F# 자동 배치를 강제 실행한 뒤 Scene 전체를 재렌더링.
    /// Device 좌표와 Flow Zone 을 단일 F# 경로에서 일괄 계산해 불일치를 방지.
    /// </summary>
    public async Task RebuildWithAutoLayout(DsStore store, Guid projectId)
    {
        if (_sendToWebView == null) return;

        if (_customModelRegistry != null)
            Ds2.View3D.DevicePresets.setCustomNames(_customModelRegistry.GetRegisteredNames());

        _engine = CreateSceneEngine(store);
        var scene = _engine.BuildDeviceSceneAutoLayout(SceneId, projectId);

        ClearCaches();
        BuildMappings(store, scene);

        var floorSize = CalculateFloorSize(scene.Devices, scene.FlowZones);
        await SendSceneInitMessage(scene.FlowZones, scene.Devices, floorSize);
        // fitAll 메시지 제거 — JS 가 devices 추가 완료 후 자체 fitCameraToDevices 수행

        HasScene = true;
    }

    public async Task BuildWorkScene(DsStore store, Guid flowId)
    {
        if (_sendToWebView == null) return;

        _engine = CreateSceneEngine(store);
        ClearCaches();

        await SendAsync(new
        {
            type = "init",
            config = new
            {
                works = Array.Empty<object>(),  // WorkScene not yet implemented
                flowZones = Array.Empty<object>(),
                floorSize = MinFloorSize
            }
        });

        HasScene = true;
    }

    public void Reset()
    {
        _engine = null;
        _pendingApiDefStates.Clear();
        _pendingDeviceStates.Clear();
        _apiDefStateCache.Clear();   // 이전 시뮬레이션의 Going 상태 잔류 방지
        _flushScheduled = false;
        // HasScene, 매핑 캐시(_callToDevice 등)는 유지 — 씬 자체는 살아있으며 다음 시뮬레이션에서도 유효
        // ClearCaches / HasScene=false는 BuildScene 재호출 시에만 초기화
        _ = SendAsync(new { type = "resetDeviceStates" });
    }

    public void OnSelectionMessage(string method, JsonElement[] args)
    {
        try
        {
            switch (method)
            {
                case "OnDeviceSelected" when TryParseGuid(args, 0, out var deviceId):
                    HandleDeviceSelected(deviceId);
                    // Parse deviceData if available (args[1])
                    if (args.Length > 1 && args[1].ValueKind == JsonValueKind.Object)
                    {
                        var deviceData = ParseDeviceData(args[1]);
                        var deviceName = deviceData.GetValueOrDefault("name", deviceId.ToString()) as string;
                        _onDeviceInfoRequested?.Invoke(deviceName ?? "Unknown", deviceData);
                    }
                    break;
                case "OnApiDefSelected" when TryParseGuid(args, 0, out var devId)
                    && args.Length > 1 && args[1].GetString() is { } apiName:
                    HandleApiDefSelected(devId, apiName);
                    break;
                case "OnEmptySpaceSelected":
                case "OnEmptySpaceClicked":
                    HandleEmptySpaceSelected();
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[3DView] Selection error: {ex.Message}");
        }
    }

    private Dictionary<string, object> ParseDeviceData(JsonElement deviceDataElement)
    {
        var result = new Dictionary<string, object>();
        foreach (var prop in deviceDataElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.ToString()
            };
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private Helpers
    // ─────────────────────────────────────────────────────────────────────────


    private void HandleDeviceSelected(Guid deviceId)
    {
        _engine?.Select(SelectionEvent.NewDeviceSelected(deviceId));
        if (_deviceToSystem.TryGetValue(deviceId, out var systemId))
            _onDeviceSelected?.Invoke(systemId, EntityKind.System);
    }

    private void HandleApiDefSelected(Guid deviceId, string apiName)
    {
        _engine?.Select(SelectionEvent.NewApiDefSelected(deviceId, apiName));
        _onApiDefSelected?.Invoke(deviceId, apiName);
    }

    private void HandleEmptySpaceSelected()
    {
        _engine?.ClearSelection();
        _onEmptySpaceSelected?.Invoke();
    }

    private bool CanSendMessage() => _sendToWebView != null && HasScene;

    private async Task SendAsync(object payload)
    {
        if (_sendToWebView == null) return;
        try
        {
            await _sendToWebView(JsonSerializer.Serialize(payload));
        }
        catch { /* WebView not ready */ }
    }

    private static bool TryParseGuid(JsonElement[] args, int index, out Guid result)
    {
        result = Guid.Empty;
        return args.Length > index && Guid.TryParse(args[index].GetString(), out result);
    }

    private static Status4 MergeStates(Status4 current, Status4 incoming) =>
        StatePriority.GetValueOrDefault(incoming, 1) > StatePriority.GetValueOrDefault(current, 1)
            ? incoming : current;

    private static string ToStateCode(Status4 s) => s switch
    {
        Status4.Going => "G",
        Status4.Finish => "F",
        Status4.Homing => "H",
        _ => "R"
    };

    private static object[] ToJsFlowZones(IEnumerable<FlowZone> zones) =>
        zones.Select(z => new
        {
            flowName = z.FlowName,
            centerX = z.CenterX,
            centerZ = z.CenterZ,
            sizeX = z.SizeX,
            sizeZ = z.SizeZ,
            color = ParseColorToInt(z.Color)
        }).ToArray();

    private static int ParseColorToInt(string colorHex)
    {
        try { return Convert.ToInt32(colorHex.TrimStart('#'), 16); }
        catch { return DefaultColor; }
    }

    private static string GetLayoutDirectory()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dualsoft", "Promaker", "3DLayouts");
        Directory.CreateDirectory(path);
        return path;
    }
}
