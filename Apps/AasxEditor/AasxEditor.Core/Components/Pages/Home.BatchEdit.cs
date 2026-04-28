using AasCore.Aas3_1;
using AasxEditor.Models;
using Microsoft.AspNetCore.Components.Web;
using Environment = AasCore.Aas3_1.Environment;
using Range = AasCore.Aas3_1.Range;

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

    /// <summary>ValueType → 카테고리 매핑</summary>
    private static readonly (string Category, string Label, HashSet<string> Types)[] ValueTypeCategories =
    [
        ("bit", "Bit", ["Boolean"]),
        ("number", "Number", ["Byte", "Short", "Int", "Long", "Integer",
            "UnsignedByte", "UnsignedShort", "UnsignedInt", "UnsignedLong",
            "Int16", "Int32", "Int64", "UInt16", "UInt32", "UInt64",
            "Float", "Double", "Decimal", "Real",
            "NonNegativeInteger", "PositiveInteger", "NonPositiveInteger", "NegativeInteger"]),
        ("string", "String", ["String", "LangString", "AnyUri", "Base64Binary",
            "HexBinary", "Date", "DateTime", "Time", "Duration",
            "GYearMonth", "GYear", "GMonthDay", "GDay", "GMonth"]),
    ];

    /// <summary>Value 변경을 지원하는 엔티티 타입</summary>
    private static readonly HashSet<string> ValueEditableTypes = ["Property", "MLP", "Range"];

    /// <summary>ValueType을 카테고리(bit/number/string)로 분류</summary>
    private static string GetValueTypeCategory(string? valueType)
    {
        if (string.IsNullOrEmpty(valueType)) return "string";
        foreach (var (cat, _, types) in ValueTypeCategories)
        {
            if (types.Contains(valueType)) return cat;
        }
        return "string";
    }

    private void OpenBatchEdit()
    {
        _batchNewValue = "";
        _batchFindText = "";
        _batchEditMode = "overwrite";
        _batchTypeFilter = "";
        _batchValueTypeFilter = "bit";
        _batchIdFilter = "";
        _batchHighlightedIds = [];
        _batchLastClickedId = 0;
        _batchSortColumn = "";
        _batchSortAsc = true;
        ReselectForCurrentFilter();
        _showBatchEdit = true;
    }

    private void CloseBatchEdit() => _showBatchEdit = false;

    /// <summary>현재 필터에 맞는 편집 가능한 항목만 선택하고 나머지는 해제</summary>
    private void ReselectForCurrentFilter()
    {
        _batchSelectedIds = GetFilteredBatchRecords()
            .Where(r => IsEditableType(r.EntityType))
            .Select(r => r.Id)
            .ToHashSet();
    }

    /// <summary>ValueType 카테고리 필터 변경 시 — 선택 초기화 + 새 값 초기화</summary>
    private void OnBatchValueTypeFilterChanged(string newFilter)
    {
        _batchValueTypeFilter = newFilter;
        _batchHighlightedIds.Clear();
        // 새 값 초기화 — 카테고리에 맞는 기본값
        _batchNewValue = newFilter == "bit" ? "false" : "";
        // 편집 모드도 카테고리에 맞게 조정
        if (newFilter == "bit") _batchEditMode = "overwrite";
        ReselectForCurrentFilter();
    }

    /// <summary>검색 결과에 등장하는 고유 엔티티 타입 목록</summary>
    private List<string> GetBatchEntityTypes()
        => _searchResults.Select(r => r.EntityType).Distinct().OrderBy(t => t).ToList();

    /// <summary>카테고리별 건수 계산</summary>
    private List<(string Category, string Label, int Count)> GetBatchValueTypeCategories()
    {
        var result = new List<(string, string, int)>();
        foreach (var (cat, label, types) in ValueTypeCategories)
        {
            var cnt = _searchResults.Count(r => r.ValueType is not null && types.Contains(r.ValueType));
            if (cnt > 0) result.Add((cat, label, cnt));
        }
        return result;
    }

    /// <summary>편집 가능한 엔티티 타입인지</summary>
    private static bool IsEditableType(string entityType)
        => ValueEditableTypes.Contains(entityType);

    /// <summary>헤더 클릭 시 정렬 토글</summary>
    private void OnBatchSortToggle(string column)
    {
        if (_batchSortColumn == column)
            _batchSortAsc = !_batchSortAsc;
        else
        {
            _batchSortColumn = column;
            _batchSortAsc = true;
        }
    }

    /// <summary>필터 + 정렬 조건에 맞는 검색 결과 (보이는 목록)</summary>
    private List<AasEntityRecord> GetFilteredBatchRecords()
    {
        IEnumerable<AasEntityRecord> results = _searchResults;

        if (!string.IsNullOrEmpty(_batchTypeFilter))
            results = results.Where(r => r.EntityType == _batchTypeFilter);

        if (!string.IsNullOrEmpty(_batchValueTypeFilter))
            results = results.Where(r => GetValueTypeCategory(r.ValueType) == _batchValueTypeFilter);

        if (!string.IsNullOrEmpty(_batchIdFilter))
            results = results.Where(r => r.IdShort.Contains(_batchIdFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(_batchSortColumn))
        {
            Func<AasEntityRecord, string> keySelector = _batchSortColumn switch
            {
                "EntityType" => r => r.EntityType,
                "IdShort" => r => r.IdShort,
                "ValueType" => r => r.ValueType ?? "",
                "Value" => r => r.Value ?? "",
                _ => r => r.IdShort
            };
            results = _batchSortAsc
                ? results.OrderBy(keySelector, StringComparer.OrdinalIgnoreCase)
                : results.OrderByDescending(keySelector, StringComparer.OrdinalIgnoreCase);
        }

        return results.ToList();
    }

    /// <summary>선택되고 편집 가능한 레코드 (실제 적용 대상)</summary>
    private List<AasEntityRecord> GetBatchApplyTargets()
        => _searchResults
            .Where(r => _batchSelectedIds.Contains(r.Id) && IsEditableType(r.EntityType))
            .ToList();

    // ===== 체크박스 조작 =====

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

    // ===== 행 클릭 선택 (Ctrl/Shift) =====

    private void OnBatchRowClick(long id, MouseEventArgs e)
    {
        var filtered = GetFilteredBatchRecords();
        var rec = filtered.FirstOrDefault(r => r.Id == id);
        if (rec is null || !IsEditableType(rec.EntityType)) return;

        if (e.ShiftKey && _batchLastClickedId != 0)
        {
            var ids = filtered.Select(r => r.Id).ToList();
            var fromIdx = ids.IndexOf(_batchLastClickedId);
            var toIdx = ids.IndexOf(id);
            if (fromIdx >= 0 && toIdx >= 0)
            {
                var start = Math.Min(fromIdx, toIdx);
                var end = Math.Max(fromIdx, toIdx);
                for (var i = start; i <= end; i++)
                {
                    if (IsEditableType(filtered[i].EntityType))
                        _batchHighlightedIds.Add(ids[i]);
                }
            }
        }
        else if (e.CtrlKey || e.MetaKey)
        {
            if (!_batchHighlightedIds.Remove(id))
                _batchHighlightedIds.Add(id);
            _batchLastClickedId = id;
        }
        else
        {
            _batchHighlightedIds = [id];
            _batchLastClickedId = id;
        }
    }

    private void OnBatchCheckHighlighted()
    {
        foreach (var id in _batchHighlightedIds)
            _batchSelectedIds.Add(id);
        _batchHighlightedIds.Clear();
    }

    private void OnBatchUncheckHighlighted()
    {
        foreach (var id in _batchHighlightedIds)
            _batchSelectedIds.Remove(id);
        _batchHighlightedIds.Clear();
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

    private int UpdateEnvironmentValues(AasCore.Aas3_1.Environment env, HashSet<string> targetJsonPaths, Dictionary<string, string?> currentValues)
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

    private int UpdateElementValues(List<AasCore.Aas3_1.ISubmodelElement> elements, HashSet<string> targetJsonPaths, Dictionary<string, string?> currentValues, string basePath)
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
                    AasCore.Aas3_1.Property p => Do(() => p.Value = newVal),
                    AasCore.Aas3_1.MultiLanguageProperty mlp => Do(() =>
                    {
                        if (mlp.Value is { Count: > 0 })
                            mlp.Value[0] = new AasCore.Aas3_1.LangStringTextType(mlp.Value[0].Language, newVal);
                        else
                            mlp.Value = [new AasCore.Aas3_1.LangStringTextType("en", newVal)];
                    }),
                    AasCore.Aas3_1.Range r => Do(() => { r.Min = newVal; r.Max = newVal; }),
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

    private static string GetChildBasePath(AasCore.Aas3_1.ISubmodelElement elem, string elemPath) => elem switch
    {
        AasCore.Aas3_1.SubmodelElementCollection => $"{elemPath}.value",
        AasCore.Aas3_1.SubmodelElementList => $"{elemPath}.value",
        AasCore.Aas3_1.Entity => $"{elemPath}.statements",
        _ => $"{elemPath}.value"
    };

    private static List<AasCore.Aas3_1.ISubmodelElement>? GetChildren(AasCore.Aas3_1.ISubmodelElement elem) => elem switch
    {
        AasCore.Aas3_1.SubmodelElementCollection smc when smc.Value is { Count: > 0 } => smc.Value,
        AasCore.Aas3_1.SubmodelElementList sml when sml.Value is { Count: > 0 } => sml.Value,
        AasCore.Aas3_1.Entity ent when ent.Statements is { Count: > 0 }
            => ent.Statements.Cast<AasCore.Aas3_1.ISubmodelElement>().ToList(),
        _ => null
    };
}
