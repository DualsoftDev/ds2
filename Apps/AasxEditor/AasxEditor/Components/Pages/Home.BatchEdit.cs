using AasxEditor.Models;
using Microsoft.AspNetCore.Components;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    /// <summary>편집 모드</summary>
    private static readonly (string Mode, string Label)[] BatchEditModes =
    [
        ("overwrite", "값 덮어쓰기"),
        ("findReplace", "찾아 바꾸기"),
        ("prepend", "앞에 추가"),
        ("append", "뒤에 추가"),
    ];

    /// <summary>Value 변경을 지원하는 엔티티 타입</summary>
    private static readonly HashSet<string> ValueEditableTypes = ["Property", "MLP", "Range"];

    private void OpenBatchEdit()
    {
        _batchNewValue = "";
        _batchFindText = "";
        _batchEditMode = "overwrite";
        _batchTypeFilter = "";
        _batchValueTypeFilter = "";
        _batchIdFilter = "";
        _batchSelectedIds = _searchResults
            .Where(r => ValueEditableTypes.Contains(r.EntityType))
            .Select(r => r.Id)
            .ToHashSet();
        _showBatchEdit = true;
    }

    private void CloseBatchEdit() => _showBatchEdit = false;

    /// <summary>검색 결과에 등장하는 고유 엔티티 타입 목록</summary>
    private List<string> GetBatchEntityTypes()
        => _searchResults.Select(r => r.EntityType).Distinct().OrderBy(t => t).ToList();

    /// <summary>검색 결과에 등장하는 고유 ValueType 목록</summary>
    private List<string> GetBatchValueTypes()
        => _searchResults
            .Where(r => r.ValueType is not null)
            .Select(r => r.ValueType!)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

    /// <summary>편집 가능한 엔티티 타입인지</summary>
    private static bool IsEditableType(string entityType)
        => ValueEditableTypes.Contains(entityType);

    /// <summary>필터 조건에 맞는 검색 결과 (보이는 목록)</summary>
    private List<AasEntityRecord> GetFilteredBatchRecords()
    {
        IEnumerable<AasEntityRecord> results = _searchResults;

        if (!string.IsNullOrEmpty(_batchTypeFilter))
            results = results.Where(r => r.EntityType == _batchTypeFilter);

        if (!string.IsNullOrEmpty(_batchValueTypeFilter))
            results = results.Where(r => r.ValueType == _batchValueTypeFilter);

        if (!string.IsNullOrEmpty(_batchIdFilter))
            results = results.Where(r => r.IdShort.Contains(_batchIdFilter, StringComparison.OrdinalIgnoreCase));

        return results.ToList();
    }

    /// <summary>선택되고 편집 가능한 레코드 (실제 적용 대상)</summary>
    private List<AasEntityRecord> GetBatchApplyTargets()
        => _searchResults
            .Where(r => _batchSelectedIds.Contains(r.Id) && IsEditableType(r.EntityType))
            .ToList();

    // ===== 선택 조작 =====

    private void OnBatchToggleItem(long id, bool isChecked)
    {
        if (isChecked) _batchSelectedIds.Add(id);
        else _batchSelectedIds.Remove(id);
    }

    private void OnBatchSelectAllVisible()
    {
        foreach (var r in GetFilteredBatchRecords().Where(r => IsEditableType(r.EntityType)))
            _batchSelectedIds.Add(r.Id);
    }

    private void OnBatchDeselectAllVisible()
    {
        foreach (var r in GetFilteredBatchRecords())
            _batchSelectedIds.Remove(r.Id);
    }

    // ===== 값 계산 =====

    private string ComputeNewValue(string? currentValue)
    {
        var cur = currentValue ?? "";
        return _batchEditMode switch
        {
            "overwrite" => _batchNewValue,
            "findReplace" => cur.Replace(_batchFindText, _batchNewValue),
            "prepend" => _batchNewValue + cur,
            "append" => cur + _batchNewValue,
            _ => _batchNewValue
        };
    }

    private string PreviewNewValue(AasEntityRecord r) => ComputeNewValue(r.Value);

    private bool CanApply()
    {
        if (_batchEditMode == "findReplace")
            return !string.IsNullOrEmpty(_batchFindText);
        return !string.IsNullOrEmpty(_batchNewValue);
    }

    // ===== 적용 =====

    private async Task OnBatchApply()
    {
        if (!CanApply()) { SetStatus("값을 입력하세요", "error"); return; }
        if (_currentEnv is null) { SetStatus("로드된 파일이 없습니다", "error"); return; }

        var targets = GetBatchApplyTargets();
        if (targets.Count == 0) { SetStatus("선택된 대상이 없습니다", "error"); return; }

        try
        {
            var snapshotJson = _currentJson;
            var snapshotPaths = _explorerPath.Select(n => n.JsonPath).ToList();

            var targetJsonPaths = targets.Select(r => r.JsonPath).ToHashSet();
            var currentValues = targets.ToDictionary(r => r.JsonPath, r => r.Value);
            var changed = UpdateEnvironmentValues(_currentEnv, targetJsonPaths, currentValues);

            var desc = _batchEditMode switch
            {
                "findReplace" => $"일괄 찾아바꾸기: '{_batchFindText}' → '{_batchNewValue}'",
                "prepend" => $"일괄 앞에추가: '{_batchNewValue}'",
                "append" => $"일괄 뒤에추가: '{_batchNewValue}'",
                _ => $"일괄 편집: '{_batchNewValue}'"
            };
            _undoStack.Push(new UndoEntry(snapshotJson, desc, snapshotPaths));
            _redoStack.Clear();
            if (_undoStack.Count > MaxUndoHistory) TrimStack(_undoStack, MaxUndoHistory);

            var updatedJson = Converter.EnvironmentToJson(_currentEnv);

            _showBatchEdit = false;
            StateHasChanged();

            await SyncJsonToEditorAsync(updatedJson);

            if (_currentFileId > 0)
            {
                await MetadataStore.UpdateJsonContentAsync(_currentFileId, updatedJson);

                if (_batchEditMode == "overwrite")
                {
                    await MetadataStore.BatchUpdateFieldByIdsAsync(targets.Select(r => r.Id), "Value", _batchNewValue);
                }
                else
                {
                    foreach (var t in targets)
                    {
                        var newVal = ComputeNewValue(t.Value);
                        await MetadataStore.BatchUpdateFieldByIdsAsync([t.Id], "Value", newVal);
                    }
                }
            }

            RebuildTree();
            SetStatus($"일괄 편집 완료: {changed}건 변경됨", "success");
            await OnSearch();
        }
        catch (Exception ex) { SetStatus($"일괄 편집 오류: {ex.Message}", "error"); }
    }

    // ========== Environment 업데이트 ==========

    private int UpdateEnvironmentValues(AasCore.Aas3_0.Environment env, HashSet<string> targetJsonPaths, Dictionary<string, string?> currentValues)
    {
        if (env.Submodels is null) return 0;
        var count = 0;
        for (var si = 0; si < env.Submodels.Count; si++)
        {
            var sm = env.Submodels[si];
            if (sm.SubmodelElements is null) continue;
            count += UpdateElementValues(sm.SubmodelElements, targetJsonPaths, currentValues, $"submodels[{si}].submodelElements");
        }
        return count;
    }

    private int UpdateElementValues(List<AasCore.Aas3_0.ISubmodelElement> elements, HashSet<string> targetJsonPaths, Dictionary<string, string?> currentValues, string basePath)
    {
        var count = 0;
        for (var i = 0; i < elements.Count; i++)
        {
            var elem = elements[i];
            var elemPath = $"{basePath}[{i}]";

            if (targetJsonPaths.Contains(elemPath))
            {
                var newVal = ComputeNewValue(currentValues.GetValueOrDefault(elemPath));
                count += elem switch
                {
                    AasCore.Aas3_0.Property p => Do(() => p.Value = newVal),
                    AasCore.Aas3_0.MultiLanguageProperty mlp => Do(() =>
                    {
                        if (mlp.Value is { Count: > 0 })
                            mlp.Value[0] = new AasCore.Aas3_0.LangStringTextType(mlp.Value[0].Language, newVal);
                        else
                            mlp.Value = [new AasCore.Aas3_0.LangStringTextType("en", newVal)];
                    }),
                    AasCore.Aas3_0.Range r => Do(() => { r.Min = newVal; r.Max = newVal; }),
                    _ => 0
                };
            }

            var children = GetChildren(elem);
            if (children is not null)
                count += UpdateElementValues(children, targetJsonPaths, currentValues, GetChildBasePath(elem, elemPath));
        }
        return count;
    }

    // ========== Helpers ==========

    private static string GetChildBasePath(AasCore.Aas3_0.ISubmodelElement elem, string elemPath) => elem switch
    {
        AasCore.Aas3_0.SubmodelElementCollection => $"{elemPath}.value",
        AasCore.Aas3_0.SubmodelElementList => $"{elemPath}.value",
        AasCore.Aas3_0.Entity => $"{elemPath}.statements",
        _ => $"{elemPath}.value"
    };

    private static List<AasCore.Aas3_0.ISubmodelElement>? GetChildren(AasCore.Aas3_0.ISubmodelElement elem) => elem switch
    {
        AasCore.Aas3_0.SubmodelElementCollection smc when smc.Value is { Count: > 0 } => smc.Value,
        AasCore.Aas3_0.SubmodelElementList sml when sml.Value is { Count: > 0 } => sml.Value,
        AasCore.Aas3_0.Entity ent when ent.Statements is { Count: > 0 }
            => ent.Statements.Cast<AasCore.Aas3_0.ISubmodelElement>().ToList(),
        _ => null
    };
}
