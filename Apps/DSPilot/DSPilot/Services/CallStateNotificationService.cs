using System.Reactive.Subjects;
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// Call 상태 변경 알림 서비스 (Reactive Extensions 기반)
/// StateTransition 결과를 구독자들에게 브로드캐스트
/// </summary>
public class CallStateNotificationService
{
    private readonly ILogger<CallStateNotificationService> _logger;
    private readonly Subject<CallStateChangedEvent> _stateChanges = new();

    public IObservable<CallStateChangedEvent> StateChanges => _stateChanges;

    public CallStateNotificationService(ILogger<CallStateNotificationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 상태 변경 알림 발송
    /// </summary>
    public void NotifyStateChanged(string callName, string previousState, string newState, DateTime timestamp)
    {
        var evt = new CallStateChangedEvent
        {
            CallName = callName,
            PreviousState = previousState,
            NewState = newState,
            Timestamp = timestamp
        };

        _logger.LogDebug("Broadcasting state change: {CallName} {PrevState} → {NewState}",
            callName, previousState, newState);

        _stateChanges.OnNext(evt);
    }

    /// <summary>
    /// 서비스 종료 시 호출
    /// </summary>
    public void Complete()
    {
        _stateChanges.OnCompleted();
    }
}
