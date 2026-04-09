using System.Text.Json;
using AasxEditor.Models;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    // ===== Error Definition Editor State =====
    private bool _showErrorDefEditor;
    private string _errorDefName = "";
    private string _errorDefTag = "";
    private string _errorDefValueType = "Bit";
    private int _editingErrorDefIndex = -1; // -1 = new, >= 0 = editing existing

    private static readonly string[] ErrorValueTypes =
        ["Bit", "Byte", "Word", "DWord", "Int16", "Int32", "Real", "String"];

    /// <summary>
    /// 현재 선택된 노드가 ErrorDefinitions SML인지 판별
    /// </summary>
    private bool IsErrorDefinitionsSml =>
        _selectedNode is { NodeType: "SML" } &&
        _selectedNode.Label == "ErrorDefinitions";

    /// <summary>
    /// 현재 ErrorDefinitions의 자식 Property 값 목록 파싱
    /// </summary>
    private List<(string Name, string Tag, string ValueType)> GetErrorDefinitions()
    {
        if (_selectedNode?.Children is null) return [];
        return _selectedNode.Children
            .Select(c => c.Properties.GetValueOrDefault("value") ?? "")
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(ParseErrorDef)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();
    }

    private static (string Name, string Tag, string ValueType)? ParseErrorDef(string encoded)
    {
        var parts = encoded.Split('|');
        if (parts.Length >= 3)
            return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
        if (parts.Length == 2)
            return (parts[0].Trim(), parts[1].Trim(), "Bit");
        return null;
    }

    private static string FormatErrorDef(string name, string tag, string valueType)
        => $"{name}|{tag}|{valueType}";

    // ===== UI Handlers =====

    private void OnAddErrorDef()
    {
        _editingErrorDefIndex = -1;
        _errorDefName = "";
        _errorDefTag = "";
        _errorDefValueType = "Bit";
        _showErrorDefEditor = true;
    }

    private void OnEditErrorDef(int index)
    {
        var defs = GetErrorDefinitions();
        if (index < 0 || index >= defs.Count) return;
        var (name, tag, vt) = defs[index];
        _editingErrorDefIndex = index;
        _errorDefName = name;
        _errorDefTag = tag;
        _errorDefValueType = vt;
        _showErrorDefEditor = true;
    }

    private async Task OnDeleteErrorDef(int index)
    {
        if (_selectedNode is null || string.IsNullOrWhiteSpace(_currentJson)) return;

        var children = _selectedNode.Children;
        if (index < 0 || index >= children.Count) return;

        // JSON에서 해당 항목 제거
        var smlPath = _selectedNode.JsonPath; // e.g., "submodels[3].submodelElements[0].value[2]"
        var updatedJson = RemoveSmlItem(_currentJson, smlPath, index);
        if (updatedJson is null) return;

        _currentEnv = Converter.JsonToEnvironment(updatedJson);
        await SyncJsonToEditorAsync(updatedJson);
        RebuildTree();

        // 삭제 후 동일 SML 노드 재선택
        var restoredNode = FindNodeByJsonPath(_treeNodes, smlPath);
        if (restoredNode is not null) SelectNode(restoredNode);

        SetStatus("에러 정의가 삭제되었습니다", "success");
    }

    private async Task OnSaveErrorDef()
    {
        if (string.IsNullOrWhiteSpace(_errorDefName) || string.IsNullOrWhiteSpace(_errorDefTag))
        {
            SetStatus("에러 이름과 태그 주소를 입력하세요", "error");
            return;
        }

        if (_selectedNode is null || string.IsNullOrWhiteSpace(_currentJson)) return;

        var encoded = FormatErrorDef(_errorDefName.Trim(), _errorDefTag.Trim(), _errorDefValueType);
        var smlPath = _selectedNode.JsonPath;

        string? updatedJson;
        if (_editingErrorDefIndex >= 0)
            updatedJson = UpdateSmlItem(_currentJson, smlPath, _editingErrorDefIndex, encoded);
        else
            updatedJson = AddSmlItem(_currentJson, smlPath, encoded);

        if (updatedJson is null)
        {
            SetStatus("JSON 반영 실패", "error");
            return;
        }

        _currentEnv = Converter.JsonToEnvironment(updatedJson);
        await SyncJsonToEditorAsync(updatedJson);
        RebuildTree();

        var restoredNode = FindNodeByJsonPath(_treeNodes, smlPath);
        if (restoredNode is not null) SelectNode(restoredNode);

        _showErrorDefEditor = false;
        SetStatus(_editingErrorDefIndex >= 0 ? "에러 정의가 수정되었습니다" : "에러 정의가 추가되었습니다", "success");
    }

    // ===== JSON Manipulation =====

    /// <summary>
    /// SubmodelElementList에 새 Property 항목 추가
    /// </summary>
    private static string? AddSmlItem(string json, string smlPath, string value)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                WriteSmlWithNewItem(w, doc.RootElement, smlPath, value, "");
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return null; }
    }

    /// <summary>
    /// SubmodelElementList의 특정 인덱스 항목 값 수정
    /// </summary>
    private static string? UpdateSmlItem(string json, string smlPath, int index, string newValue)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                WriteSmlWithUpdatedItem(w, doc.RootElement, smlPath, index, newValue, "");
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return null; }
    }

    /// <summary>
    /// SubmodelElementList의 특정 인덱스 항목 제거
    /// </summary>
    private static string? RemoveSmlItem(string json, string smlPath, int index)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                WriteSmlWithRemovedItem(w, doc.RootElement, smlPath, index, "");
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return null; }
    }

    // --- JSON writers ---

    private static void WriteSmlWithNewItem(Utf8JsonWriter w, JsonElement elem, string targetPath, string value, string currentPath)
    {
        switch (elem.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var prop in elem.EnumerateObject())
                {
                    var pp = string.IsNullOrEmpty(currentPath) ? prop.Name : $"{currentPath}.{prop.Name}";
                    w.WritePropertyName(prop.Name);

                    if (pp == $"{targetPath}.value" && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        // SML의 value 배열에 새 Property 추가
                        w.WriteStartArray();
                        foreach (var item in prop.Value.EnumerateArray())
                            item.WriteTo(w);

                        // 새 Property 객체 추가
                        w.WriteStartObject();
                        w.WriteString("modelType", "Property");
                        w.WriteString("valueType", "xs:string");
                        w.WriteString("value", value);
                        w.WriteEndObject();

                        w.WriteEndArray();
                    }
                    else
                    {
                        WriteSmlWithNewItem(w, prop.Value, targetPath, value, pp);
                    }
                }
                // value 배열이 아예 없는 경우 (빈 SML)
                if (currentPath == targetPath && !elem.EnumerateObject().Any(p => p.Name == "value"))
                {
                    w.WritePropertyName("value");
                    w.WriteStartArray();
                    w.WriteStartObject();
                    w.WriteString("modelType", "Property");
                    w.WriteString("valueType", "xs:string");
                    w.WriteString("value", value);
                    w.WriteEndObject();
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                break;

            case JsonValueKind.Array:
                w.WriteStartArray();
                var idx = 0;
                foreach (var item in elem.EnumerateArray())
                {
                    WriteSmlWithNewItem(w, item, targetPath, value, $"{currentPath}[{idx}]");
                    idx++;
                }
                w.WriteEndArray();
                break;

            default:
                elem.WriteTo(w);
                break;
        }
    }

    private static void WriteSmlWithUpdatedItem(Utf8JsonWriter w, JsonElement elem, string targetPath, int targetIndex, string newValue, string currentPath)
    {
        switch (elem.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var prop in elem.EnumerateObject())
                {
                    var pp = string.IsNullOrEmpty(currentPath) ? prop.Name : $"{currentPath}.{prop.Name}";
                    w.WritePropertyName(prop.Name);

                    if (pp == $"{targetPath}.value" && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        w.WriteStartArray();
                        var i = 0;
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (i == targetIndex)
                            {
                                // 해당 인덱스의 Property value만 교체
                                w.WriteStartObject();
                                foreach (var ip in item.EnumerateObject())
                                {
                                    w.WritePropertyName(ip.Name);
                                    if (ip.Name == "value")
                                        w.WriteStringValue(newValue);
                                    else
                                        ip.Value.WriteTo(w);
                                }
                                w.WriteEndObject();
                            }
                            else
                            {
                                item.WriteTo(w);
                            }
                            i++;
                        }
                        w.WriteEndArray();
                    }
                    else
                    {
                        WriteSmlWithUpdatedItem(w, prop.Value, targetPath, targetIndex, newValue, pp);
                    }
                }
                w.WriteEndObject();
                break;

            case JsonValueKind.Array:
                w.WriteStartArray();
                var idx = 0;
                foreach (var item in elem.EnumerateArray())
                {
                    WriteSmlWithUpdatedItem(w, item, targetPath, targetIndex, newValue, $"{currentPath}[{idx}]");
                    idx++;
                }
                w.WriteEndArray();
                break;

            default:
                elem.WriteTo(w);
                break;
        }
    }

    private static void WriteSmlWithRemovedItem(Utf8JsonWriter w, JsonElement elem, string targetPath, int targetIndex, string currentPath)
    {
        switch (elem.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var prop in elem.EnumerateObject())
                {
                    var pp = string.IsNullOrEmpty(currentPath) ? prop.Name : $"{currentPath}.{prop.Name}";
                    w.WritePropertyName(prop.Name);

                    if (pp == $"{targetPath}.value" && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        w.WriteStartArray();
                        var i = 0;
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (i != targetIndex)
                                item.WriteTo(w);
                            i++;
                        }
                        w.WriteEndArray();
                    }
                    else
                    {
                        WriteSmlWithRemovedItem(w, prop.Value, targetPath, targetIndex, pp);
                    }
                }
                w.WriteEndObject();
                break;

            case JsonValueKind.Array:
                w.WriteStartArray();
                var idx = 0;
                foreach (var item in elem.EnumerateArray())
                {
                    WriteSmlWithRemovedItem(w, item, targetPath, targetIndex, $"{currentPath}[{idx}]");
                    idx++;
                }
                w.WriteEndArray();
                break;

            default:
                elem.WriteTo(w);
                break;
        }
    }
}
