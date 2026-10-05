using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;

namespace Promaker.ViewModels;

/// <summary>
/// 여러 씬 뷰(3D 배치 뷰 · 그래픽 정보뷰 …)가 같은 시뮬레이션 이벤트를 동시에 받도록
/// <see cref="ISceneEventHandler"/> 를 fan-out 하는 Composite.
///
/// 기존에는 <c>_sceneEventHandler</c> 가 단일 필드여서 시뮬 시작마다
/// <c>InitSceneEventHandler()</c> 가 새 <see cref="DeviceSceneEventHandler"/> 로 덮어썼고,
/// 다른 뷰가 붙을 자리가 없었다. 이 Composite 이 그 자리를 대신 차지하고,
/// 3D 배치 뷰는 <see cref="ReplaceSingleton{T}"/> 으로 "타입당 1개" 슬롯을 계속 유지한다.
/// → 3D 배치 뷰의 기존 동작(시뮬 시작마다 새 handler 1개)은 그대로, 다른 구독만 보존된다.
/// </summary>
public sealed class CompositeSceneEventHandler : ISceneEventHandler
{
    private readonly object _gate = new();
    private readonly List<ISceneEventHandler> _handlers = [];

    /// <summary>등록된 핸들러 수 (진단용).</summary>
    public int Count { get { lock (_gate) return _handlers.Count; } }

    public void Add(ISceneEventHandler? handler)
    {
        if (handler is null || ReferenceEquals(handler, this)) return;
        lock (_gate)
        {
            if (!_handlers.Contains(handler))
                _handlers.Add(handler);
        }
    }

    public void Remove(ISceneEventHandler? handler)
    {
        if (handler is null) return;
        lock (_gate) _handlers.Remove(handler);
    }

    /// <summary>
    /// 해당 타입의 기존 핸들러를 모두 제거하고 새 인스턴스 1개로 교체한다.
    /// (단일 필드 시절의 "덮어쓰기" 의미를 타입 범위로 좁힌 것)
    /// </summary>
    public void ReplaceSingleton<T>(T handler) where T : ISceneEventHandler
    {
        lock (_gate)
        {
            _handlers.RemoveAll(h => h is T);
            if (handler is not null)
                _handlers.Add(handler);
        }
    }

    public void Clear()
    {
        lock (_gate) _handlers.Clear();
    }

    private ISceneEventHandler[] Snapshot()
    {
        lock (_gate) return _handlers.ToArray();
    }

    // 한 뷰의 예외가 다른 뷰(특히 기존 3D 배치 뷰)로 번지지 않도록 개별 격리.
    private static void SafeInvoke(ISceneEventHandler handler, Action<ISceneEventHandler> action)
    {
        try { action(handler); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CompositeSceneEventHandler] {handler.GetType().Name} threw: {ex.Message}");
        }
    }

    public void OnWorkStateChanged(Guid workId, Status4 newState)
    {
        foreach (var h in Snapshot())
            SafeInvoke(h, x => x.OnWorkStateChanged(workId, newState));
    }

    public void OnCallStateChanged(Guid callId, Status4 newState)
    {
        foreach (var h in Snapshot())
            SafeInvoke(h, x => x.OnCallStateChanged(callId, newState));
    }

    public void Reset()
    {
        foreach (var h in Snapshot())
            SafeInvoke(h, x => x.Reset());
    }
}
