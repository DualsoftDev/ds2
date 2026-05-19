using System;
using Ds2.Core;

namespace Promaker.ViewModels;

/// <summary>
/// Device Scene 전용 이벤트 핸들러.
/// Call 상태 변경 → Device 상태 업데이트 흐름을 처리한다.
/// </summary>
public class DeviceSceneEventHandler : ISceneEventHandler
{
    private readonly ThreeDViewState _threeDViewState;

    public DeviceSceneEventHandler(ThreeDViewState threeDViewState)
    {
        _threeDViewState = threeDViewState ?? throw new ArgumentNullException(nameof(threeDViewState));
    }

    /// <summary>
    /// Work 상태 변경 이벤트 - Device Scene에서는 무시.
    /// </summary>
    public void OnWorkStateChanged(Guid workId, Status4 newState)
    {
        // Device Scene은 Call 이벤트만 처리
    }

    /// <summary>
    /// Call 상태 변경 이벤트 - ThreeDViewState로 전달하여 Device 상태 업데이트.
    /// </summary>
    public void OnCallStateChanged(Guid callId, Status4 newState)
    {
        _threeDViewState.OnCallStateChanged(callId, newState);
    }

    /// <summary>
    /// 시뮬레이션 리셋 - ThreeDViewState 상태 초기화.
    /// </summary>
    public void Reset()
    {
        _threeDViewState.Reset();
    }
}
