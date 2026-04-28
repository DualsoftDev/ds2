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
        await SendInitMessage(scene.FlowZones, floorSize);
        await SendDevices(scene.Devices);
        await SendAsync(new { type = "fitAll" });

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

    public void OnWorkStateChanged(Guid workId, Status4 newState)
    {
        // Work state is not visualized in Device Scene (only Call/ApiDef/Device)
    }

    public async Task ShowApiDefConnections(Guid deviceId, string apiDefName,
        IEnumerable<object> outgoing, IEnumerable<object> incoming)
    {
        if (!CanSendMessage()) return;
        await SendAsync(new
        {
            type = "showApiDefConnections",
            deviceId = deviceId.ToString(),
            apiDefName,
            outgoing = outgoing.ToArray(),
            incoming = incoming.ToArray()
        });
    }

    public void OnCallStateChanged(Guid callId, Status4 newState)
    {
        if (!CanSendMessage()) return;

        // Buffer ApiDef state
        if (_callToApiDef.TryGetValue(callId, out var apiDefId))
        {
            _apiDefStateCache[apiDefId] = newState;
            _pendingApiDefStates[apiDefId] = ToStateCode(newState);
        }

        // Buffer Device state (derived from all its ApiDefs)
        if (_callToDevice.TryGetValue(callId, out var deviceId) &&
            _deviceToApiDefs.TryGetValue(deviceId, out var apiDefIds))
        {
            var deviceState = Status4.Ready;
            foreach (var id in apiDefIds)
            {
                if (_apiDefStateCache.GetValueOrDefault(id, Status4.Ready) == Status4.Going)
                {
                    deviceState = Status4.Going;
                    break;
                }
            }
            _pendingDeviceStates[deviceId] = ToStateCode(deviceState);
        }

        ScheduleFlush();
    }

    /// <summary>
    /// UI 스레드 Background 우선순위로 배치 전송 예약.
    /// 최소 FlushIntervalMs 간격으로 쓰로틀링하여 WebView2 과부하를 방지한다.
    /// </summary>
    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;

        var elapsed = (DateTime.UtcNow - _lastFlushTime).TotalMilliseconds;
        if (elapsed >= FlushIntervalMs)
        {
            // 충분히 시간이 지남 — 즉시 flush
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                FlushPendingStates);
        } 
        else
        {
            // 아직 간격 미달 — 남은 시간만큼 지연 후 flush
            var delay = TimeSpan.FromMilliseconds(FlushIntervalMs - elapsed);
            var timer = new System.Windows.Threading.DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = delay
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                FlushPendingStates();
            };
            timer.Start();
        }
    }

    private void FlushPendingStates()
    {
        _flushScheduled = false;
        _lastFlushTime = DateTime.UtcNow;
        if (_sendToWebView == null) return;

        // ApiDef + Device 상태를 단일 메시지로 합쳐서 WebView2 마샬링 횟수를 줄인다
        object[]? apiDefArr = null;
        object[]? deviceArr = null;

        if (_pendingApiDefStates.Count > 0)
        {
            apiDefArr = _pendingApiDefStates
                .Select(kv => (object)new { id = kv.Key.ToString(), state = kv.Value })
                .ToArray();
            _pendingApiDefStates.Clear();
        }

        if (_pendingDeviceStates.Count > 0)
        {
            deviceArr = _pendingDeviceStates
                .Select(kv => (object)new { id = kv.Key.ToString(), state = kv.Value })
                .ToArray();
            _pendingDeviceStates.Clear();
        }

        if (apiDefArr != null || deviceArr != null)
        {
            // fire-and-forget: UI 스레드 블로킹 방지
            _ = SendAsync(new
            {
                type = "batchStateUpdate",
                apiDefStates = apiDefArr,
                deviceStates = deviceArr
            });
        }
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

    private static SceneEngine CreateSceneEngine(DsStore store)
    {
        var layoutDir = GetLayoutDirectory();
        return new SceneEngine(store, new Ds2.View3D.Persistence.JsonFileLayoutStore(layoutDir));
    }

    private void ClearCaches()
    {
        _callToDevice.Clear();
        _callToApiDef.Clear();
        _deviceToSystem.Clear();
        _apiDefStateCache.Clear();
        _deviceToApiDefs.Clear();
    }

    private void BuildMappings(DsStore store, SceneData scene)
    {
        var deviceNameToId = scene.Devices.ToDictionary(d => d.Name, d => d.Id,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (systemId, system) in store.Systems)
        {
            if (deviceNameToId.TryGetValue(system.Name, out var deviceId))
                _deviceToSystem[deviceId] = systemId;
        }

        // Store all ApiDefs for each Device (from SceneData)
        foreach (var deviceNode in scene.Devices)
        {
            var apiDefList = deviceNode.ApiDefs.ToList();
            if (apiDefList.Count > 0)
            {
                _deviceToApiDefs[deviceNode.Id] = new HashSet<Guid>(
                    apiDefList.Select(a => a.Id));
            }
        }

        BuildCallToDeviceMapping(store, deviceNameToId);
    }

    /// <summary>
    /// Call → ApiDef → TargetDevice 체인 추적으로 정확한 매핑 구축.
    /// ApiDef.ParentId = System (Device)이므로 ApiCall을 통해 어느 Device가 호출되는지 추적한다.
    /// </summary>
    private void BuildCallToDeviceMapping(DsStore store, Dictionary<string, Guid> deviceNameToId)
    {
        foreach (var (callId, call) in store.Calls)
        {
            foreach (var apiCall in call.ApiCalls)
            {
                if (apiCall.ApiDefId == null) continue;

                var apiDefId = apiCall.ApiDefId.Value;

                // ApiCall → ApiDef 추적
                if (!store.ApiDefs.TryGetValue(apiDefId, out var apiDef))
                    continue;

                // Call → ApiDef 매핑
                _callToApiDef[callId] = apiDefId;

                // ApiDef.ParentId = TargetSystem (Device)
                var targetSystemId = apiDef.ParentId;
                if (!store.Systems.TryGetValue(targetSystemId, out var targetSystem))
                    continue;

                // System.Name → DeviceId 매핑
                if (deviceNameToId.TryGetValue(targetSystem.Name, out var deviceId))
                {
                    _callToDevice[callId] = deviceId;
                    break;  // 하나의 Call은 하나의 주요 Device에만 매핑
                }
            }
        }
    }

    private static double CalculateFloorSize(IEnumerable<DeviceInfo> devices, IEnumerable<FlowZone> flowZones)
    {
        var posDevices = devices.Where(d => d.Position != null).ToList();
        var flowZonesList = flowZones.ToList();

        if (posDevices.Count == 0 && flowZonesList.Count == 0) return MinFloorSize;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        // Include device positions
        foreach (var d in posDevices)
        {
            var pos = d.Position!.Value;
            minX = Math.Min(minX, pos.X);
            maxX = Math.Max(maxX, pos.X);
            minZ = Math.Min(minZ, pos.Z);
            maxZ = Math.Max(maxZ, pos.Z);
        }

        // Include flow zone bounds
        foreach (var zone in flowZonesList)
        {
            var zoneMinX = zone.CenterX - zone.SizeX / 2;
            var zoneMaxX = zone.CenterX + zone.SizeX / 2;
            var zoneMinZ = zone.CenterZ - zone.SizeZ / 2;
            var zoneMaxZ = zone.CenterZ + zone.SizeZ / 2;

            minX = Math.Min(minX, zoneMinX);
            maxX = Math.Max(maxX, zoneMaxX);
            minZ = Math.Min(minZ, zoneMinZ);
            maxZ = Math.Max(maxZ, zoneMaxZ);
        }

        if (double.IsInfinity(minX)) return MinFloorSize;

        var spanX = maxX - minX;
        var spanZ = maxZ - minZ;
        var span = Math.Max(spanX, spanZ);

        return Math.Max(span + FloorSizeMargin, MinFloorSize);
    }

    private async Task SendInitMessage(IEnumerable<FlowZone> flowZones, double floorSize)
    {
        // 커스텀 JSON 모델 레지스트리 (추후 프로젝트 디렉터리에서 로드 예정)
        var customModels = LoadCustomModelRegistry();

        await SendAsync(new
        {
            type = "init",
            config = new { flowZones = ToJsFlowZones(flowZones), floorSize, customModels }
        });
    }

    /// <summary>
    /// 커스텀 JSON 디바이스 모델 레지스트리를 직렬화 가능한 형태로 반환.
    /// View3DWindow에서 주입된 CustomModelRegistry를 사용한다.
    /// </summary>
    private Dictionary<string, object> LoadCustomModelRegistry()
    {
        return _customModelRegistry?.ToSerializableDictionary()
               ?? new Dictionary<string, object>();
    }

    private async Task SendDevices(IEnumerable<DeviceInfo> devices)
    {
        foreach (var device in devices)
        {
            if (device.Position == null) continue;
            var pos = device.Position.Value;

            await SendAsync(new
            {
                type = "addDevice",
                device = new
                {
                    id = device.Id.ToString(),
                    name = device.Name,
                    flowName = device.FlowName,
                    modelType = device.ModelType,
                    deviceType = device.ModelType,
                    state = "R",
                    isUsedInSimulation = device.IsUsedInSimulation,
                    apiDefs = device.ApiDefs.Select(a => new
                    {
                        id = a.Id.ToString(),
                        name = a.Name,
                        callerCount = a.CallerCount
                    }).ToArray()
                },
                x = pos.X,
                z = pos.Z
            });
        }
    }



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
