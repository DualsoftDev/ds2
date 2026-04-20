using Microsoft.JSInterop;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    // ===== Undo / Redo =====
    private const int MaxUndoHistory = 50;

    private record UndoEntry(string Json, string Description, List<string> ExplorerJsonPaths);

    private readonly Stack<UndoEntry> _undoStack = new();
    private readonly Stack<UndoEntry> _redoStack = new();

    private bool CanUndo => _undoStack.Count > 0;
    private bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// 현재 상태를 undo 스택에 저장. 모든 데이터 변경 직전에 호출.
    /// </summary>
    private void PushUndo(string description)
    {
        if (string.IsNullOrEmpty(_currentJson)) return;

        var pathJsonPaths = _explorerPath.Select(n => n.JsonPath).ToList();
        _undoStack.Push(new UndoEntry(_currentJson, description, pathJsonPaths));
        _redoStack.Clear();

        // 스택 크기 제한
        if (_undoStack.Count > MaxUndoHistory)
            TrimStack(_undoStack, MaxUndoHistory);
    }

    private async Task Undo()
    {
        if (!CanUndo) return;

        var entry = _undoStack.Pop();

        // 현재 상태를 redo 스택에 저장
        var currentPaths = _explorerPath.Select(n => n.JsonPath).ToList();
        _redoStack.Push(new UndoEntry(_currentJson, entry.Description, currentPaths));

        await RestoreState(entry);
        SetStatus($"실행 취소: {entry.Description}", "info");
    }

    private async Task Redo()
    {
        if (!CanRedo) return;

        var entry = _redoStack.Pop();

        // 현재 상태를 undo 스택에 저장
        var currentPaths = _explorerPath.Select(n => n.JsonPath).ToList();
        _undoStack.Push(new UndoEntry(_currentJson, entry.Description, currentPaths));

        await RestoreState(entry);
        SetStatus($"다시 실행: {entry.Description}", "info");
    }

    private async Task RestoreState(UndoEntry entry)
    {
        _currentEnv = Converter.JsonToEnvironment(entry.Json);
        await SyncJsonToEditorAsync(entry.Json);

        // 트리 재구성 (nav 스택은 유지하지 않음)
        if (_currentEnv is not null)
            _treeNodes = TreeBuilder.BuildTree(_currentEnv);
        _selectedNode = null;

        // Explorer 경로 복원
        _explorerPath.Clear();
        _navBack.Clear();
        _navForward.Clear();
        RestoreExplorerPath(entry.ExplorerJsonPaths);
    }

    /// <summary>
    /// JsonPath 목록으로 explorer 경로를 복원
    /// </summary>
    private void RestoreExplorerPath(List<string> jsonPaths)
    {
        foreach (var jp in jsonPaths)
        {
            var node = FindNodeByJsonPath(_treeNodes, jp);
            if (node is not null)
                _explorerPath.Add(node);
            else
                break; // 경로가 끊기면 거기까지만 복원
        }
    }

    private void ClearUndoHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    private static void TrimStack<T>(Stack<T> stack, int maxSize)
    {
        if (stack.Count <= maxSize) return;
        var items = stack.ToArray();
        stack.Clear();
        // Stack.ToArray()는 top-first 순서 → 앞쪽이 최신
        for (var i = maxSize - 1; i >= 0; i--)
            stack.Push(items[i]);
    }

    // ===== Keyboard shortcuts (JS → Blazor) =====

    [JSInvokable]
    public async Task OnUndoKeyboard()
    {
        await Undo();
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnRedoKeyboard()
    {
        await Redo();
        StateHasChanged();
    }
}
