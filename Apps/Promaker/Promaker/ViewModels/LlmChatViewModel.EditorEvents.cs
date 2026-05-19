using System;
using System.Windows.Threading;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

/// <summary>
/// LlmChatViewModel partial — Editor event 구독 + LLM apply self-loop guard. Store 의 ObserveEvents 결과를
/// dispatcher thread 로 marshalling 후 _editorDigest 에 누적. _isLlmApplyingPlan window 안에서는 skip.
/// </summary>
public partial class LlmChatViewModel
{
    private void SubscribeEditorEvents()
    {
        _editorSubscription?.Dispose();
        var observable = (IObservable<EditorEvent>)_store.ObserveEvents();
        _editorSubscription = observable.Subscribe(new EditorEventObserver(OnEditorEvent));
    }

    private void OnEditorEvent(EditorEvent evt)
    {
        // dispatcher thread 도착 시 sync — _isLlmApplyingPlan 윈도가 ApplyImportPlan 의 sync emit 과 동일 stack frame
        // 에서 검사되므로 self-loop guard 정확. marshalling 경로 (else) 는 BG/non-dispatcher thread 에서 store 가 mutate 되는
        // 가설적 경로 — 결정 8 (mutation 은 dispatcher 경유) 가 깨지지 않는 한 사용자 GUI 직접 동작에서만 발생하므로
        // self-loop 와 무관 (LLM 자기 turn 의 ApplyImportPlan 은 항상 dispatcher 위라 sync 분기로 들어옴).
        if (_wpfDispatcher.CheckAccess()) HandleEditorEventOnDispatcher(evt);
        else _wpfDispatcher.InvokeAsync(() => HandleEditorEventOnDispatcher(evt), DispatcherPriority.Background);
    }

    private void HandleEditorEventOnDispatcher(EditorEvent evt)
    {
        if (_isLlmApplyingPlan) return;
        _editorDigest.Record(evt);
    }
}

file sealed class EditorEventObserver : IObserver<EditorEvent>
{
    private readonly Action<EditorEvent> _onNext;

    public EditorEventObserver(Action<EditorEvent> onNext) => _onNext = onNext;

    public void OnNext(EditorEvent value) => _onNext(value);
    public void OnCompleted() { }
    public void OnError(Exception error) { /* swallow — provider 변경/dispose 시 emit 종료 가능 */ }
}
