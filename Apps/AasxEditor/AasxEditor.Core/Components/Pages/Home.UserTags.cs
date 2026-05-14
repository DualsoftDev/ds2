using System.Text;
using System.Text.Json;
using AasCore.Aas3_1;
using AasxEditor.Models;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Env = AasCore.Aas3_1.Environment;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    // ===== User Tag Editor State =====
    private bool _showUserTagEditor;
    private string _userTagName = "";
    private string _userTagLogLevel = "Info";
    private string _userTagAddress = "";
    private string _userTagValueType = "Bit";
    private int _editingUserTagIndex = -1; // -1 = new, >= 0 = editing existing
    private bool _csvImportReplace; // true = 교체, false = 추가
    private bool _showCsvReplaceConfirm;
    private AasTreeNode? _csvReplaceTargetNode;
    private AasTreeNode? _csvTargetNode; // CSV 작업 대상 노드 (추가/교체 공용)

    private static readonly string[] PlcValueTypes =
        ["Bit", "Byte", "Word", "DWord", "Int16", "Int32", "Real", "String"];

    private static readonly string[] UserTagLogLevels =
        ["Info", "Warning", "Error"];

    /// <summary>
    /// 현재 선택된 노드가 UserTags SML인지 판별
    /// </summary>
    private bool IsUserTagsSml =>
        _selectedNode is { NodeType: "SML" } &&
        _selectedNode.Label == "UserTags";

    /// <summary>
    /// 현재 UserTags의 자식 Property 값 목록 파싱
    /// </summary>
    private List<(string Name, string LogLevel, string Tag, string ValueType)> GetUserTags()
    {
        if (_selectedNode?.Children is null) return [];
        return _selectedNode.Children
            .Select(c => c.Properties.GetValueOrDefault("value") ?? "")
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(ParseUserTag)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();
    }

    private static (string Name, string LogLevel, string Tag, string ValueType)? ParseUserTag(string encoded)
    {
        var parts = encoded.Split('|');
        if (parts.Length >= 4)
            return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), parts[3].Trim());
        return null;
    }

    private static string FormatUserTag(string name, string logLevel, string tag, string valueType)
        => $"{name}|{logLevel}|{tag}|{valueType}";

    // ===== UI Handlers =====

    private void OnAddUserTag()
    {
        _editingUserTagIndex = -1;
        _userTagName = "";
        _userTagLogLevel = "Info";
        _userTagAddress = "";
        _userTagValueType = "Bit";
        _showUserTagEditor = true;
    }

    private void OnEditUserTag(int index)
    {
        var tags = GetUserTags();
        if (index < 0 || index >= tags.Count) return;
        var (name, level, tag, vt) = tags[index];
        _editingUserTagIndex = index;
        _userTagName = name;
        _userTagLogLevel = level;
        _userTagAddress = tag;
        _userTagValueType = vt;
        _showUserTagEditor = true;
    }

    private async Task OnDeleteUserTag(int index)
    {
        if (_selectedNode is null || string.IsNullOrWhiteSpace(_currentJson)) return;

        var children = _selectedNode.Children;
        if (index < 0 || index >= children.Count) return;

        PushUndo("사용자 태그 삭제");

        var smlPath = _selectedNode.JsonPath;
        var updatedJson = RemoveSmlItem(_currentJson, smlPath, index);
        if (updatedJson is null) return;

        _currentEnv = Converter.JsonToEnvironment(updatedJson);
        await SyncJsonToEditorAsync(updatedJson);
        RebuildTree();
        RestoreUserTagNode(smlPath);

        SetStatus("사용자 태그가 삭제되었습니다", "success");
    }

    private async Task OnSaveUserTag()
    {
        if (string.IsNullOrWhiteSpace(_userTagName) || string.IsNullOrWhiteSpace(_userTagAddress))
        {
            SetStatus("태그 이름과 태그 주소를 입력하세요", "error");
            return;
        }

        if (_selectedNode is null || string.IsNullOrWhiteSpace(_currentJson)) return;

        PushUndo(_editingUserTagIndex >= 0 ? "사용자 태그 수정" : "사용자 태그 추가");

        var encoded = FormatUserTag(
            _userTagName.Trim(),
            _userTagLogLevel,
            _userTagAddress.Trim(),
            _userTagValueType);
        var smlPath = _selectedNode.JsonPath;

        string? updatedJson;
        if (_editingUserTagIndex >= 0)
            updatedJson = UpdateSmlItem(_currentJson, smlPath, _editingUserTagIndex, encoded);
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
        RestoreUserTagNode(smlPath);

        _showUserTagEditor = false;
        SetStatus(_editingUserTagIndex >= 0 ? "사용자 태그가 수정되었습니다" : "사용자 태그가 추가되었습니다", "success");
    }

    /// <summary>
    /// RebuildTree 후 UserTags SML 노드를 다시 선택하고 Explorer 경로를 복원
    /// </summary>
    private void RestoreUserTagNode(string smlJsonPath)
    {
        var restoredNode = FindNodeByJsonPath(_treeNodes, smlJsonPath);
        if (restoredNode is null) return;
        SelectNode(restoredNode);
        var path = new List<AasTreeNode>();
        if (FindPathToNode(_treeNodes, restoredNode, path))
            _explorerPath = path;
    }

    // ===== CSV Export / Import =====

    private async Task OnExportUserTagCsv(AasTreeNode tagNode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("이름,로그 레벨,태그 주소,값 타입");

        foreach (var child in tagNode.Children)
        {
            var val = child.Properties.GetValueOrDefault("value") ?? "";
            var parsed = ParseUserTag(val);
            if (parsed is null) continue;
            var (name, level, tag, vt) = parsed.Value;
            sb.AppendLine($"{CsvEscape(name)},{CsvEscape(level)},{CsvEscape(tag)},{CsvEscape(vt)}");
        }

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
        await JS.InvokeVoidAsync("MonacoInterop.downloadFile", "UserTags.csv", base64);
        SetStatus("CSV 내보내기 완료", "success");
    }

    private static string CsvEscape(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private void OnImportUserTagCsv(bool replace, AasTreeNode tagNode)
    {
        _csvTargetNode = tagNode;
        _csvImportReplace = replace;
    }

    private Task OnCsvAddFileSelected(InputFileChangeEventArgs e)
    {
        _csvTargetNode = _selectedNode;
        _csvImportReplace = false;
        return OnCsvFileSelected(e);
    }

    private Task OnCsvAddForNode(InputFileChangeEventArgs e, AasTreeNode node)
    {
        _csvTargetNode = node;
        _csvImportReplace = false;
        return OnCsvFileSelected(e);
    }

    private Task OnCsvReplaceFileSelected(InputFileChangeEventArgs e)
    {
        _csvImportReplace = true;
        _showCsvReplaceConfirm = false;
        return OnCsvFileSelected(e);
    }

    private async Task OnCsvFileSelected(InputFileChangeEventArgs e)
    {
        try
        {
            var file = e.File;
            if (file is null) return;

            _showCsvReplaceConfirm = false;

            var targetNode = _csvTargetNode ?? _selectedNode;
            if (targetNode is null || string.IsNullOrWhiteSpace(_currentJson)) return;
            SelectNode(targetNode);

            using var reader = new StreamReader(file.OpenReadStream(maxAllowedSize: 5 * 1024 * 1024));
            var content = await reader.ReadToEndAsync();
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .ToList();

            // 헤더 행 건너뛰기
            var startIdx = 0;
            if (lines.Count > 0 && (lines[0].Contains("이름") || lines[0].StartsWith("Name", StringComparison.OrdinalIgnoreCase)))
                startIdx = 1;

            var entries = new List<(string Name, string LogLevel, string Tag, string ValueType)>();
            for (var i = startIdx; i < lines.Count; i++)
            {
                var parts = CsvParseLine(lines[i]);
                if (parts.Count < 4) continue;
                var name = parts[0].Trim();
                var level = parts[1].Trim();
                var tag = parts[2].Trim();
                var vt = parts[3].Trim();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(tag)) continue;
                if (string.IsNullOrWhiteSpace(level)) level = "Info";
                if (string.IsNullOrWhiteSpace(vt)) vt = "Bit";
                entries.Add((name, level, tag, vt));
            }

            if (entries.Count == 0)
            {
                SetStatus("CSV에 유효한 데이터가 없습니다", "error");
                return;
            }

            var csvAction = _csvImportReplace ? "교체" : "추가";
            PushUndo($"CSV {csvAction} ({entries.Count}건)");

            var smlPath = targetNode.JsonPath;

            string? updatedJson;
            if (_csvImportReplace)
                updatedJson = ReplaceSmlItems(_currentJson, smlPath, entries.Select(e2 => FormatUserTag(e2.Name, e2.LogLevel, e2.Tag, e2.ValueType)).ToList());
            else
                updatedJson = AddSmlItems(_currentJson, smlPath, entries.Select(e2 => FormatUserTag(e2.Name, e2.LogLevel, e2.Tag, e2.ValueType)).ToList());

            if (updatedJson is null)
            {
                SetStatus("JSON 반영 실패", "error");
                return;
            }

            _currentEnv = Converter.JsonToEnvironment(updatedJson);
            await SyncJsonToEditorAsync(updatedJson);
            RebuildTree();
            RestoreUserTagNode(smlPath);

            _csvTargetNode = null;
            SetStatus($"CSV {csvAction} 완료: {entries.Count}건", "success");
        }
        catch (Exception ex) { SetStatus($"CSV 가져오기 오류: {ex.Message}", "error"); }
    }

    private static List<string> CsvParseLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    { current.Append('"'); i++; }
                    else
                        inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
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

    /// <summary>
    /// SubmodelElementList에 여러 Property 항목 일괄 추가
    /// </summary>
    private static string? AddSmlItems(string json, string smlPath, List<string> values)
    {
        try
        {
            var result = json;
            foreach (var v in values)
            {
                result = AddSmlItem(result, smlPath, v);
                if (result is null) return null;
            }
            return result;
        }
        catch { return null; }
    }

    /// <summary>
    /// SubmodelElementList의 기존 항목을 모두 제거하고 새 항목들로 교체
    /// </summary>
    private static string? ReplaceSmlItems(string json, string smlPath, List<string> values)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                WriteSmlWithReplacedItems(w, doc.RootElement, smlPath, values, "");
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
                        w.WriteStartArray();
                        foreach (var item in prop.Value.EnumerateArray())
                            item.WriteTo(w);

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

    private static void WriteSmlWithReplacedItems(Utf8JsonWriter w, JsonElement elem, string targetPath, List<string> values, string currentPath)
    {
        switch (elem.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                var hasValue = false;
                foreach (var prop in elem.EnumerateObject())
                {
                    var pp = string.IsNullOrEmpty(currentPath) ? prop.Name : $"{currentPath}.{prop.Name}";
                    w.WritePropertyName(prop.Name);

                    if (pp == $"{targetPath}.value" && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        hasValue = true;
                        w.WriteStartArray();
                        foreach (var v in values)
                        {
                            w.WriteStartObject();
                            w.WriteString("modelType", "Property");
                            w.WriteString("valueType", "xs:string");
                            w.WriteString("value", v);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    else
                    {
                        WriteSmlWithReplacedItems(w, prop.Value, targetPath, values, pp);
                    }
                }
                if (currentPath == targetPath && !hasValue)
                {
                    w.WritePropertyName("value");
                    w.WriteStartArray();
                    foreach (var v in values)
                    {
                        w.WriteStartObject();
                        w.WriteString("modelType", "Property");
                        w.WriteString("valueType", "xs:string");
                        w.WriteString("value", v);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                break;

            case JsonValueKind.Array:
                w.WriteStartArray();
                var idx = 0;
                foreach (var item in elem.EnumerateArray())
                {
                    WriteSmlWithReplacedItems(w, item, targetPath, values, $"{currentPath}[{idx}]");
                    idx++;
                }
                w.WriteEndArray();
                break;

            default:
                elem.WriteTo(w);
                break;
        }
    }

    // ===== AASX 로드 시 누락된 UserTags 자동 생성 =====

    /// <summary>
    /// SequenceLogging 서브모델의 SystemProperties SMC에 UserTags SML이 없으면 빈 SML을 추가
    /// </summary>
    private static void EnsureUserTags(Env env)
    {
        if (env.Submodels is null) return;

        foreach (var sm in env.Submodels)
        {
            if (sm.IdShort is null || !sm.IdShort.Contains("Logging", StringComparison.OrdinalIgnoreCase))
                continue;
            if (sm.SubmodelElements is null) continue;

            foreach (var elem in sm.SubmodelElements)
            {
                if (elem is not SubmodelElementCollection sysPropsSmc) continue;
                if (sysPropsSmc.IdShort is null || !sysPropsSmc.IdShort.Contains("SystemProperties")) continue;
                if (sysPropsSmc.Value is null) continue;

                foreach (var sysElem in sysPropsSmc.Value)
                {
                    if (sysElem is not SubmodelElementCollection sysSmc) continue;
                    EnsureUserTagsInSmc(sysSmc);
                }
            }
        }
    }

    private static void EnsureUserTagsInSmc(SubmodelElementCollection smc)
    {
        smc.Value ??= new List<ISubmodelElement>();

        var hasUserTags = smc.Value.Any(e => e.IdShort == "UserTags");
        if (hasUserTags) return;

        var sml = new SubmodelElementList(
            typeValueListElement: AasSubmodelElements.Property,
            valueTypeListElement: DataTypeDefXsd.String);
        sml.IdShort = "UserTags";
        sml.Value = new List<ISubmodelElement>();

        smc.Value.Add(sml);
    }
}
