using AasxEditor.Models;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    // ===== Tree actions =====
    private async Task OnNodeDblClick(AasTreeNode node)
    {
        SelectNode(node);
        if (_centerTab == "explorer")
            NavigateExplorerToNode(node);
        else
            await JS.InvokeVoidAsync("MonacoInterop.revealJsonPath", node.JsonPath);
    }

    private void ToggleNode(AasTreeNode node) => node.IsExpanded = !node.IsExpanded;

    private async Task OnGoToJson()
    {
        if (_selectedNode?.JsonPath is { } path)
        {
            _centerTab = "code";
            StateHasChanged();
            await Task.Yield();
            await JS.InvokeVoidAsync("MonacoInterop.revealJsonPath", path);
        }
    }

    private void SwitchTab(string tab) => _centerTab = tab;

    // ===== Explorer navigation =====
    private void ExplorerDrillDown(AasTreeNode node)
    {
        SelectNode(node);
        if (node.Children.Count > 0) _explorerPath.Add(node);
    }

    private void ExplorerGoRoot() => _explorerPath.Clear();
    private void ExplorerGoTo(int index) => _explorerPath = _explorerPath.Take(index + 1).ToList();

    private void NavigateExplorerToNode(AasTreeNode target)
    {
        _explorerPath.Clear();
        var path = new List<AasTreeNode>();
        if (FindPathToNode(_treeNodes, target, path))
            _explorerPath = path;
    }

    private bool FindPathToNode(List<AasTreeNode> nodes, AasTreeNode target, List<AasTreeNode> path)
    {
        foreach (var node in nodes)
        {
            if (node == target) return true;
            if (node.Children.Count > 0)
            {
                path.Add(node);
                if (FindPathToNode(node.Children, target, path)) return true;
                path.RemoveAt(path.Count - 1);
            }
        }
        return false;
    }

    // ===== Properties editing =====
    private void OnPropFieldChanged(string key, string? newValue)
    {
        if (_selectedNode is not null)
        {
            _selectedNode.Properties[key] = newValue;
            _propsDirty = true;
        }
    }

    private async Task OnApplyPropChanges()
    {
        if (_selectedNode is null || string.IsNullOrWhiteSpace(_currentJson)) return;
        try
        {
            var updatedJson = ApplyPropertyChanges(_currentJson, _selectedNode);
            _currentEnv = Converter.JsonToEnvironment(updatedJson);
            await SyncJsonToEditorAsync(updatedJson);
            _propsDirty = false;
            SetStatus("속성 변경이 JSON에 반영되었습니다", "success");
        }
        catch (Exception ex) { SetStatus($"반영 실패: {ex.Message}", "error"); }
    }

    private string ApplyPropertyChanges(string json, AasTreeNode node)
    {
        var doc = System.Text.Json.JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            WriteWithChanges(writer, doc.RootElement, node.JsonPath, node.Properties);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private void WriteWithChanges(System.Text.Json.Utf8JsonWriter writer, System.Text.Json.JsonElement element,
        string targetPath, Dictionary<string, string?> changes, string currentPath = "")
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    var propPath = string.IsNullOrEmpty(currentPath) ? prop.Name : $"{currentPath}.{prop.Name}";
                    writer.WritePropertyName(prop.Name);
                    if (currentPath == targetPath && changes.TryGetValue(prop.Name, out var newVal) && newVal is not null)
                        writer.WriteStringValue(newVal);
                    else
                        WriteWithChanges(writer, prop.Value, targetPath, changes, propPath);
                }
                writer.WriteEndObject();
                break;
            case System.Text.Json.JsonValueKind.Array:
                writer.WriteStartArray();
                var idx = 0;
                foreach (var item in element.EnumerateArray())
                {
                    WriteWithChanges(writer, item, targetPath, changes, $"{currentPath}[{idx}]");
                    idx++;
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    // ===== Search =====
    private async Task OnSearch()
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return;
        _searchResults = await MetadataStore.SearchAsync(new AasSearchQuery { Text = _searchText });
        SetStatus($"검색 완료: {_searchResults.Count}건", "success");
    }

    private async Task OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await OnSearch();
    }

    private void ClearSearch()
    {
        _searchResults.Clear();
        _searchText = "";
    }

    private void OnSearchResultClick(AasEntityRecord record)
    {
        var node = FindNodeByJsonPath(_treeNodes, record.JsonPath);
        if (node is null) return;

        SelectNode(node);
        ExpandParentsOf(_treeNodes, node);
        if (_centerTab == "explorer") NavigateExplorerToNode(node);
    }

    private AasTreeNode? FindNodeByJsonPath(List<AasTreeNode> nodes, string jsonPath)
    {
        foreach (var node in nodes)
        {
            if (node.JsonPath == jsonPath) return node;
            var found = FindNodeByJsonPath(node.Children, jsonPath);
            if (found is not null) return found;
        }
        return null;
    }

    private bool ExpandParentsOf(List<AasTreeNode> nodes, AasTreeNode target)
    {
        foreach (var node in nodes)
        {
            if (node == target) return true;
            if (node.Children.Count > 0 && ExpandParentsOf(node.Children, target))
            {
                node.IsExpanded = true;
                return true;
            }
        }
        return false;
    }
}
