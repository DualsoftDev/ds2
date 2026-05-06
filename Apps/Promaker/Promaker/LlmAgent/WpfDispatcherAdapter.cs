using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Ds2.LlmAgent;

namespace Promaker.LlmAgent;

/// <summary>
/// F# <see cref="IUiDispatcher"/> 의 WPF Dispatcher 어댑터.
///
/// 결정 8: 모든 marshalling 은 <see cref="DispatcherPriority.Background"/> + async 만.
/// sync <see cref="Dispatcher.Invoke"/> 진입 시 큰 RebuildAll 진행 중 stream 처리 thread block →
/// AssistantDelta 표시 frozen + EditorEvent coalescing 깨짐.
/// </summary>
public sealed class WpfDispatcherAdapter : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcherAdapter(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<T> InvokeAsync<T>(Func<T> action) =>
        _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;

    public Task InvokeAsync(Action action) =>
        _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
}
