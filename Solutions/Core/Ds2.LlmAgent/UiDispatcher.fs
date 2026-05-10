namespace Ds2.LlmAgent

open System
open System.Threading.Tasks

/// Tool handler 가 mutation/read 작업을 UI thread (WPF Dispatcher) 로 marshalling 하기 위한 추상.
///
/// 결정 8: `Ds2.Editor` 의 mutable state (`StoreEditorState.CurrentRecords` / `SuppressEvents` 등) 가
/// lock 없이 사용되므로 background thread 에서 직접 store 변경 시 race / Undo corruption 위험.
///
/// **반드시 async (`InvokeAsync`) + Background priority**. sync `Invoke` 금지 — UI thread 점유 시
/// `await foreach` stream 처리도 block 되어 AssistantDelta 표시가 frozen.
///
/// AssistantDelta 같은 stream-only 이벤트는 본 dispatcher 우회. ChatPanel ViewModel 측 자체
/// `ObservableCollection` 갱신이 SynchronizationContext 로 충분.
type IUiDispatcher =
    /// 결과 반환 작업의 dispatcher marshalling (Background priority).
    abstract member InvokeAsync<'T> : action: Func<'T> -> Task<'T>
    /// 결과 없는 작업의 dispatcher marshalling (Background priority).
    abstract member InvokeAsync : action: Action -> Task
