using System;
using Ds2.Core;

namespace Promaker.ViewModels;

/// <summary>
/// 시뮬레이션 이벤트를 3D 씬에 전달하는 인터페이스.
/// Event Aggregator 패턴으로 SimulationEngine과 3D View 간 결합도를 낮춘다.
/// </summary>
public interface ISceneEventHandler
{
    /// <summary>
    /// Work 상태 변경 이벤트 (Work Scene에서만 사용).
    /// </summary>
    void OnWorkStateChanged(Guid workId, Status4 newState);

    /// <summary>
    /// Call 상태 변경 이벤트 (Device Scene에서 사용).
    /// Call → ApiDef → TargetDevice 체인을 통해 Device 상태를 업데이트한다.
    /// </summary>
    void OnCallStateChanged(Guid callId, Status4 newState);

    /// <summary>
    /// 시뮬레이션 리셋 이벤트.
    /// 모든 Device/Work의 상태를 Ready로 초기화한다.
    /// </summary>
    void Reset();
}
