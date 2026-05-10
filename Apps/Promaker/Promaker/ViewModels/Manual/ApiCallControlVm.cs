using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Promaker.ViewModels.Manual;

/// <summary>
/// 수동 컨트롤러 다이얼로그의 한 행 — ApiCall(=한 동작) 단위.
/// OutTag 주소로 ON/OFF 명령 송출, InTag 주소의 현재 값을 LED 로 표시.
/// </summary>
public partial class ApiCallControlVm : ObservableObject
{
    /// <summary>"Cylinder1.Forward" 같은 Call 의 풀네임 — 그룹 라벨용 prefix 분리는 부모 VM 이.</summary>
    public string CallName { get; }
    /// <summary>"Forward" — 그룹 안에서 행 라벨로 표시.</summary>
    public string ActionName { get; }
    /// <summary>OUT 코일 주소 — 비어 있으면 ON/OFF 비활성.</summary>
    public string OutAddress { get; }
    /// <summary>IN 응답 주소 — 비어 있으면 LED 항상 회색.</summary>
    public string InAddress { get; }
    public bool HasOut => !string.IsNullOrWhiteSpace(OutAddress);
    public bool HasIn  => !string.IsNullOrWhiteSpace(InAddress);

    /// <summary>현재 OUT 값 — true=ON 송출됨. ON 버튼 강조에 사용.</summary>
    [ObservableProperty] private bool _outValue;
    /// <summary>현재 IN 값 — true=피드백 ON. 녹색 LED 점등.</summary>
    [ObservableProperty] private bool _inValue;
    /// <summary>마지막 OUT 송신 결과. ON/OFF 버튼 옆에 짧게 표시.</summary>
    [ObservableProperty] private string _lastWriteStatus = "";

    private readonly Func<string, string, Task<bool>> _writeTag;

    /// <summary>같은 device group 안의 sibling 들을 mutex 처리 위해 owning group 참조.
    /// ManualControlState 가 row 추가 시 set. null 이면 mutex 없이 단독 toggle.</summary>
    public DeviceGroupVm? Group { get; internal set; }

    public ApiCallControlVm(
        string callName,
        string actionName,
        string outAddress,
        string inAddress,
        Func<string, string, Task<bool>> writeTag)
    {
        CallName = callName;
        ActionName = actionName;
        OutAddress = outAddress ?? "";
        InAddress = inAddress ?? "";
        _writeTag = writeTag;
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => HasOut);
        // 비상 OFF 가 사용하는 별도 OFF-only 명령 — 항상 OFF 송출 (현재 상태 무관).
        ForceOffCommand = new AsyncRelayCommand(() => WriteAsync(false), () => HasOut);
    }

    /// <summary>토글 — ON 으로 가는 경우 같은 device 의 다른 ON 들을 먼저 OFF (mutex).
    /// 양방향 솔레노이드 (ADV+RET) 가 동시에 active 되는 안전 위험 차단.</summary>
    private async Task ToggleAsync()
    {
        var newValue = !OutValue;
        if (newValue && Group is not null)
        {
            // 같은 group 내에서 현재 ON 인 *다른* row 들을 먼저 OFF.
            // 동시 active 방지를 위해 sibling OFF 가 *먼저* 완료된 후 자기 ON.
            foreach (var sibling in Group.Calls)
            {
                if (ReferenceEquals(sibling, this)) continue;
                if (sibling.HasOut && sibling.OutValue)
                {
                    await sibling.WriteAsync(false);
                }
            }
        }
        await WriteAsync(newValue);
    }

    /// <summary>현재 OutValue 의 반대값 송출 (토글). 매뉴얼 다이얼로그 행 버튼이 사용.</summary>
    public IAsyncRelayCommand ToggleCommand { get; }
    /// <summary>항상 OFF 를 송출 — 비상 OFF / 모든 OUT 일괄 끄기용.</summary>
    public IAsyncRelayCommand ForceOffCommand { get; }

    /// <summary>실제 hub.WriteTag 호출 + 결과 반영. mutex 처리에서 sibling 의 OFF 도 이걸 사용.</summary>
    internal async Task WriteAsync(bool value)
    {
        var ok = await _writeTag(OutAddress, value ? "true" : "false");
        if (ok)
        {
            OutValue = value;
            LastWriteStatus = $"송신 {(value ? "ON" : "OFF")} ✓";
        }
        else
        {
            LastWriteStatus = $"송신 실패 — Hub 연결 확인";
        }
    }

    /// <summary>외부 OnTagChanged 가 매칭되는 주소를 가져왔을 때 LED/표시값 갱신.</summary>
    public void OnHubTag(string address, string value)
    {
        var v = ParseBool(value);
        if (string.Equals(address, InAddress, StringComparison.OrdinalIgnoreCase))
            InValue = v;
        if (string.Equals(address, OutAddress, StringComparison.OrdinalIgnoreCase))
            OutValue = v;
    }

    public void SetInitialValues(string outValue, string inValue)
    {
        OutValue = ParseBool(outValue);
        InValue  = ParseBool(inValue);
    }

    private static bool ParseBool(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var t = s.Trim().ToLowerInvariant();
        return t == "true" || t == "1";
    }
}
