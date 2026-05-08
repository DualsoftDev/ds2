using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.Runtime.IO;

namespace Promaker.ViewModels.Manual;

/// <summary>
/// 수동 컨트롤러 다이얼로그 ViewModel.
/// SignalIOMap 의 매핑 + Store 의 Call 정보를 결합해 Device 단위로 그룹화한 ApiCall 행을 노출.
/// hub 의 OnTagChanged 이벤트를 받아 각 행의 LED 상태를 실시간 갱신.
/// </summary>
public partial class ManualControlState : ObservableObject
{
    public ObservableCollection<DeviceGroupVm> DeviceGroups { get; } = new();

    /// <summary>주소(=HubAddress) → ApiCallControlVm 들. 한 주소가 여러 행에 걸칠 수 있어 list.
    /// (보통 OUT 1:1 이지만 IN 은 여러 ApiCall 이 같은 IN 주소를 공유 가능.)</summary>
    private readonly Dictionary<string, List<ApiCallControlVm>> _byAddress
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly SimulationPanelState _sim;
    private bool _attached;

    [ObservableProperty] private string _statusText = "준비됨";
    [ObservableProperty] private bool _isHubConnected;

    public ManualControlState(SimulationPanelState sim, DsStore store, SignalIOMap ioMap)
    {
        _sim = sim;
        BuildRows(store, ioMap);
        EmergencyOffAllCommand = new AsyncRelayCommand(EmergencyOffAllAsync);
    }

    private void BuildRows(DsStore store, SignalIOMap ioMap)
    {
        // SignalIOMap.Mappings 의 각 SignalMapping = 하나의 ApiCall 매핑.
        // CallGuid 로 Store 에서 Call 을 찾아 DevicesAlias / ApiName 분리.
        var groups = new Dictionary<string, DeviceGroupVm>(StringComparer.Ordinal);

        foreach (var m in ioMap.Mappings)
        {
            // OUT/IN 주소가 둘 다 비면 표시 가치 없음 — skip.
            var hasOut = !string.IsNullOrWhiteSpace(m.OutAddress);
            var hasIn  = !string.IsNullOrWhiteSpace(m.InAddress);
            if (!hasOut && !hasIn) continue;

            string deviceName = "(미매핑)";
            string actionName = m.OutAddress.Length > 0 ? m.OutAddress : m.InAddress;
            string callName = actionName;

            if (store.Calls.TryGetValue(m.CallGuid, out var call))
            {
                deviceName = string.IsNullOrEmpty(call.DevicesAlias) ? "(미매핑)" : call.DevicesAlias;
                actionName = string.IsNullOrEmpty(call.ApiName) ? actionName : call.ApiName;
                callName = call.Name;
            }

            if (!groups.TryGetValue(deviceName, out var group))
            {
                group = new DeviceGroupVm(deviceName);
                groups[deviceName] = group;
            }

            var row = new ApiCallControlVm(
                callName: callName,
                actionName: actionName,
                outAddress: m.OutAddress,
                inAddress: m.InAddress,
                writeTag: _sim.WriteTagFromManualAsync);
            group.Calls.Add(row);

            if (hasOut)
                AddByAddress(m.OutAddress, row);
            if (hasIn)
                AddByAddress(m.InAddress, row);
        }

        // 그룹은 디바이스 이름 알파벳 순.
        foreach (var g in groups.Values.OrderBy(g => g.DeviceName, StringComparer.Ordinal))
            DeviceGroups.Add(g);
    }

    private void AddByAddress(string addr, ApiCallControlVm row)
    {
        if (!_byAddress.TryGetValue(addr, out var list))
        {
            list = new();
            _byAddress[addr] = list;
        }
        list.Add(row);
    }

    /// <summary>다이얼로그가 열릴 때 호출 — Hub broadcast 구독, 초기 값 로드.</summary>
    public async Task AttachAsync()
    {
        if (_attached) return;
        _sim.HubTagBroadcast += OnHubTag;
        _sim.AttachManualControlState(this);
        _attached = true;
        StatusText = "Hub broadcast 구독 — 초기 값 조회 중";

        // 초기 OUT/IN 값을 hub 캐시에서 한 번 가져옴.
        foreach (var (addr, rows) in _byAddress)
        {
            var v = await _sim.QueryTagFromManualAsync(addr);
            foreach (var row in rows)
            {
                if (string.Equals(row.OutAddress, addr, StringComparison.OrdinalIgnoreCase)) row.OutValue = v == "true" || v == "1";
                if (string.Equals(row.InAddress,  addr, StringComparison.OrdinalIgnoreCase)) row.InValue  = v == "true" || v == "1";
            }
        }

        IsHubConnected = true;
        StatusText = $"수동 운전 활성 — {DeviceGroups.Count}개 디바이스 / {DeviceGroups.Sum(g => g.Calls.Count)}개 ApiCall";
    }

    /// <summary>다이얼로그가 닫힐 때 호출 — broadcast 구독 해제.</summary>
    public void Detach()
    {
        if (!_attached) return;
        _sim.HubTagBroadcast -= OnHubTag;
        _sim.DetachManualControlState(this);
        _attached = false;
        IsHubConnected = false;
        StatusText = "닫힘";
    }

    private void OnHubTag(string address, string value, string source)
    {
        if (!_byAddress.TryGetValue(address, out var rows)) return;
        // hub 의 broadcast 는 background 스레드에서 옴 — UI 업데이트는 dispatcher 에 위임.
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            foreach (var row in rows) row.OnHubTag(address, value);
        });
    }

    public IAsyncRelayCommand EmergencyOffAllCommand { get; }

    /// <summary>비상 OFF — 모든 OUT 주소에 false 송출 (현재 상태 무관 강제).</summary>
    private async Task EmergencyOffAllAsync()
    {
        StatusText = "비상 OFF — 모든 OUT 주소를 false 로 송출 중...";
        var tasks = new List<Task>();
        foreach (var group in DeviceGroups)
            foreach (var row in group.Calls)
                if (row.HasOut)
                    tasks.Add(row.ForceOffCommand.ExecuteAsync(null));
        try { await Task.WhenAll(tasks); }
        catch { /* 개별 실패는 row 내부에서 LastWriteStatus 로 표시 */ }
        StatusText = "비상 OFF 완료";
    }
}
