using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Promaker.ViewModels.Manual;

/// <summary>"Cylinder1" 같은 디바이스 단위 그룹. 같은 DevicesAlias 의 ApiCall 들을 묶음.</summary>
public partial class DeviceGroupVm : ObservableObject
{
    public string DeviceName { get; }
    public ObservableCollection<ApiCallControlVm> Calls { get; } = new();

    public DeviceGroupVm(string deviceName) { DeviceName = deviceName; }
}
