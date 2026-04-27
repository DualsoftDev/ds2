using AasxEditor.Models;
using AasxEditor.Services;
using Microsoft.JSInterop;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    // 노드 JsonPath → 편집기 모드. AASX 로드 시 1회 채워지고, 사용자가 토글하면 갱신됨.
    private readonly Dictionary<string, ValueEditorMode> _editorModeCache = new(StringComparer.Ordinal);

    // 현재 인라인 Monaco 편집기 호스트 ID. JSON 모드인 노드가 선택됐을 때만 마운트.
    private const string InlineJsonEditorId = "inline-json-editor";
    private string? _mountedInlineJsonPath;
    private bool _inlineEditorDirty;
    private bool _inlineEditorInvalid;

    private void ResetEditorModeCache()
    {
        _editorModeCache.Clear();
        _mountedInlineJsonPath = null;
        _inlineEditorDirty = false;
        _inlineEditorInvalid = false;
    }

    /// <summary>AASX 로드 직후 1회: 모든 String Property 값을 스캔해 JSON 여부 판정.</summary>
    private void ScanEditorModesForTree(IEnumerable<AasTreeNode> roots)
    {
        foreach (var node in EnumerateNodes(roots))
            _editorModeCache[node.JsonPath] = DetectInitialMode(node);
    }

    private static IEnumerable<AasTreeNode> EnumerateNodes(IEnumerable<AasTreeNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var d in EnumerateNodes(root.Children))
                yield return d;
        }
    }

    private static ValueEditorMode DetectInitialMode(AasTreeNode node)
    {
        // String 타입 leaf만 대상
        if (node.Children.Count > 0) return ValueEditorMode.Text;
        if (!node.Properties.ContainsKey("value")) return ValueEditorMode.Text;
        var vt = node.Properties.GetValueOrDefault("valueType") ?? "String";
        if (vt is "Boolean" or "Int" or "Double" or "Duration") return ValueEditorMode.Text;

        var hint = PropertyDescriptions.GetEditorHint(node.Label);
        if (hint == PropertyDescriptions.EditorHint.ForceJson) return ValueEditorMode.Json;
        if (hint == PropertyDescriptions.EditorHint.ForceText) return ValueEditorMode.Text;

        var val = node.Properties.GetValueOrDefault("value") ?? "";
        return JsonValueDetector.LooksLikeJson(val) ? ValueEditorMode.Json : ValueEditorMode.Text;
    }

    private ValueEditorMode GetEditorMode(AasTreeNode node)
    {
        if (_editorModeCache.TryGetValue(node.JsonPath, out var mode)) return mode;
        // 새로 추가된 노드는 첫 조회 시 1회 감지 후 캐시
        var detected = DetectInitialMode(node);
        _editorModeCache[node.JsonPath] = detected;
        return detected;
    }

    private async Task SetEditorMode(AasTreeNode node, ValueEditorMode mode)
    {
        var prev = GetEditorMode(node);
        if (prev == mode) return;

        // JSON → Text 전환: 미반영 변경 있으면 그대로 두고 내려서 사용자가 잃지 않게.
        if (prev == ValueEditorMode.Json && _mountedInlineJsonPath == node.JsonPath)
            await DisposeInlineJsonEditorAsync();

        _editorModeCache[node.JsonPath] = mode;
        _inlineEditorDirty = false;
        _inlineEditorInvalid = false;
    }

    private async Task EnsureInlineJsonEditorAsync(AasTreeNode node, string value)
    {
        if (_mountedInlineJsonPath == node.JsonPath) return;
        await DisposeInlineJsonEditorAsync();
        var pretty = JsonValueDetector.TryFormat(value);
        try
        {
            await JS.InvokeVoidAsync("InlineJsonEditor.attach", InlineJsonEditorId, pretty, _dotnetRef);
            _mountedInlineJsonPath = node.JsonPath;
        }
        catch { }
    }

    private async Task DisposeInlineJsonEditorAsync()
    {
        if (_mountedInlineJsonPath is null) return;
        try { await JS.InvokeVoidAsync("InlineJsonEditor.dispose"); } catch { }
        _mountedInlineJsonPath = null;
    }

    /// <summary>매 렌더 후: 현재 선택된 노드/모드와 마운트 상태를 동기화.</summary>
    private async Task SyncInlineJsonEditorAsync()
    {
        var sel = _selectedNode;
        var shouldMount = sel is not null
            && _centerTab == "explorer"
            && sel.Children.Count == 0
            && sel.Properties.ContainsKey("value")
            && (sel.Properties.GetValueOrDefault("valueType") ?? "String") is "String"
            && GetEditorMode(sel) == ValueEditorMode.Json;

        if (!shouldMount)
        {
            if (_mountedInlineJsonPath is not null)
                await DisposeInlineJsonEditorAsync();
            return;
        }

        if (_mountedInlineJsonPath != sel!.JsonPath)
        {
            var val = sel.Properties.GetValueOrDefault("value") ?? "";
            await EnsureInlineJsonEditorAsync(sel, val);
        }
    }

    [JSInvokable]
    public Task OnInlineJsonChanged(bool isValid)
    {
        _inlineEditorDirty = true;
        _inlineEditorInvalid = !isValid;
        InvokeAsync(StateHasChanged);
        return Task.CompletedTask;
    }

    private async Task ApplyInlineJsonAsync(AasTreeNode node)
    {
        if (_mountedInlineJsonPath != node.JsonPath) return;
        string raw;
        try { raw = await JS.InvokeAsync<string>("InlineJsonEditor.getValue"); }
        catch { return; }

        if (!JsonValueDetector.IsValid(raw))
        {
            SetStatus("JSON 구문 오류 — 저장하지 않았습니다", "error");
            _inlineEditorInvalid = true;
            return;
        }

        // 저장 시 minify(공백 제거)해서 기존 저장 형식과 맞춤
        string toSave;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            toSave = System.Text.Json.JsonSerializer.Serialize(doc.RootElement);
        }
        catch { toSave = raw; }

        await OnCardValueChanged(node, toSave);
        _inlineEditorDirty = false;
        _inlineEditorInvalid = false;
    }

    private async Task ResetInlineJsonAsync(AasTreeNode node)
    {
        var current = node.Properties.GetValueOrDefault("value") ?? "";
        var pretty = JsonValueDetector.TryFormat(current);
        try { await JS.InvokeVoidAsync("InlineJsonEditor.setValue", pretty); } catch { }
        _inlineEditorDirty = false;
        _inlineEditorInvalid = false;
    }
}
