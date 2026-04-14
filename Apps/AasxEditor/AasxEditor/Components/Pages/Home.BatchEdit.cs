using AasxEditor.Models;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    /// <summary>일괄 편집 대상 필드 목록 (IdShort 등 고유 식별자는 제외)</summary>
    private static readonly (string Field, string Label)[] BatchEditableFields =
    [
        ("Value", "값 (Value)"),
        ("SemanticId", "시맨틱 ID"),
        ("ValueType", "값 타입 (ValueType)"),
        ("Category", "카테고리 (Category)"),
    ];

    /// <summary>일괄 편집에서 Value 변경을 지원하는 엔티티 타입</summary>
    private static readonly HashSet<string> ValueEditableTypes = ["Property", "MLP", "Range"];

    private void OpenBatchEdit()
    {
        _batchNewValue = "";
        _batchTargetField = "Value";
        _showBatchEdit = true;
    }

    private void CloseBatchEdit() => _showBatchEdit = false;

    /// <summary>선택된 필드 기준으로 변경 가능한 검색 결과만 필터링</summary>
    private List<AasEntityRecord> GetBatchTargetRecords()
    {
        if (_batchTargetField == "Value")
            return _searchResults.Where(r => ValueEditableTypes.Contains(r.EntityType)).ToList();
        // SemanticId, ValueType, Category 등은 모든 엔티티 타입에 적용 가능
        return _searchResults.ToList();
    }

    /// <summary>변경 불가한 (건너뛸) 검색 결과</summary>
    private List<AasEntityRecord> GetBatchSkippedRecords()
    {
        if (_batchTargetField == "Value")
            return _searchResults.Where(r => !ValueEditableTypes.Contains(r.EntityType)).ToList();
        return [];
    }

    private async Task OnBatchApply()
    {
        if (string.IsNullOrEmpty(_batchNewValue)) { SetStatus("값을 입력하세요", "error"); return; }
        if (_currentEnv is null) { SetStatus("로드된 파일이 없습니다", "error"); return; }

        var targets = GetBatchTargetRecords();
        if (targets.Count == 0) { SetStatus("변경 가능한 대상이 없습니다", "error"); return; }

        try
        {
            // 1. Undo용 JSON 스냅샷 캡처 (변경 전)
            var snapshotJson = _currentJson;
            var snapshotPaths = _explorerPath.Select(n => n.JsonPath).ToList();

            // 2. 인메모리 Environment 변경 (JsonPath 기반 정확 매칭)
            var targetJsonPaths = targets.Select(r => r.JsonPath).ToHashSet();
            int changed;

            if (_batchTargetField == "Value")
                changed = UpdateEnvironmentValues(_currentEnv, targetJsonPaths, _batchNewValue);
            else
                changed = UpdateEnvironmentField(_currentEnv, targetJsonPaths, _batchTargetField, _batchNewValue);

            // 3. 변경 성공 시에만 Undo 스택에 푸시
            _undoStack.Push(new UndoEntry(snapshotJson, $"일괄 편집 ({_batchTargetField}): '{_batchNewValue}'", snapshotPaths));
            _redoStack.Clear();
            if (_undoStack.Count > MaxUndoHistory) TrimStack(_undoStack, MaxUndoHistory);

            var updatedJson = Converter.EnvironmentToJson(_currentEnv);

            _showBatchEdit = false;
            StateHasChanged();

            await SyncJsonToEditorAsync(updatedJson);

            // 4. DB 업데이트 — 검색 결과의 ID 기반으로 정확한 레코드만 변경
            if (_currentFileId > 0)
            {
                await MetadataStore.UpdateJsonContentAsync(_currentFileId, updatedJson);
                var dbField = _batchTargetField switch
                {
                    "Value" => "Value",
                    "SemanticId" => "SemanticId",
                    "ValueType" => "ValueType",
                    "Category" => "Category",
                    _ => "Value"
                };
                await MetadataStore.BatchUpdateFieldByIdsAsync(targets.Select(r => r.Id), dbField, _batchNewValue);
            }

            RebuildTree();

            var skipped = _searchResults.Count - targets.Count;
            var statusMsg = $"일괄 편집 완료: {changed}건 변경됨";
            if (skipped > 0)
                statusMsg += $", {skipped}건 건너뜀 (미지원 타입)";
            SetStatus(statusMsg, "success");

            await OnSearch();
        }
        catch (Exception ex) { SetStatus($"일괄 편집 오류: {ex.Message}", "error"); }
    }

    /// <summary>Value 필드 변경 — JsonPath 기반 정확 매칭</summary>
    private int UpdateEnvironmentValues(AasCore.Aas3_0.Environment env, HashSet<string> targetJsonPaths, string newValue)
    {
        if (env.Submodels is null) return 0;
        var count = 0;
        for (var si = 0; si < env.Submodels.Count; si++)
        {
            var sm = env.Submodels[si];
            if (sm.SubmodelElements is null) continue;
            count += UpdateElementValues(sm.SubmodelElements, targetJsonPaths, newValue, $"submodels[{si}].submodelElements");
        }
        return count;
    }

    private int UpdateElementValues(List<AasCore.Aas3_0.ISubmodelElement> elements, HashSet<string> targetJsonPaths, string newValue, string basePath)
    {
        var count = 0;
        for (var i = 0; i < elements.Count; i++)
        {
            var elem = elements[i];
            var elemPath = $"{basePath}[{i}]";

            if (targetJsonPaths.Contains(elemPath))
            {
                count += elem switch
                {
                    AasCore.Aas3_0.Property p => Do(() => p.Value = newValue),
                    AasCore.Aas3_0.MultiLanguageProperty mlp => Do(() =>
                    {
                        if (mlp.Value is { Count: > 0 })
                            mlp.Value[0] = new AasCore.Aas3_0.LangStringTextType(mlp.Value[0].Language, newValue);
                        else
                            mlp.Value = [new AasCore.Aas3_0.LangStringTextType("en", newValue)];
                    }),
                    AasCore.Aas3_0.Range r => Do(() => { r.Min = newValue; r.Max = newValue; }),
                    _ => 0
                };
            }

            var children = GetChildren(elem);
            if (children is not null)
            {
                var childBasePath = elem switch
                {
                    AasCore.Aas3_0.SubmodelElementCollection => $"{elemPath}.value",
                    AasCore.Aas3_0.SubmodelElementList => $"{elemPath}.value",
                    AasCore.Aas3_0.Entity => $"{elemPath}.statements",
                    _ => $"{elemPath}.value"
                };
                count += UpdateElementValues(children, targetJsonPaths, newValue, childBasePath);
            }
        }
        return count;
    }

    /// <summary>Value 외 필드(SemanticId, Category 등) 변경</summary>
    private int UpdateEnvironmentField(AasCore.Aas3_0.Environment env, HashSet<string> targetJsonPaths, string field, string newValue)
    {
        if (env.Submodels is null) return 0;
        var count = 0;
        for (var si = 0; si < env.Submodels.Count; si++)
        {
            var sm = env.Submodels[si];
            if (sm.SubmodelElements is null) continue;
            count += UpdateElementField(sm.SubmodelElements, targetJsonPaths, field, newValue, $"submodels[{si}].submodelElements");
        }
        return count;
    }

    private int UpdateElementField(List<AasCore.Aas3_0.ISubmodelElement> elements, HashSet<string> targetJsonPaths, string field, string newValue, string basePath)
    {
        var count = 0;
        for (var i = 0; i < elements.Count; i++)
        {
            var elem = elements[i];
            var elemPath = $"{basePath}[{i}]";

            if (targetJsonPaths.Contains(elemPath))
            {
                switch (field)
                {
                    case "SemanticId":
                        elem.SemanticId = new AasCore.Aas3_0.Reference(
                            AasCore.Aas3_0.ReferenceTypes.ExternalReference,
                            [new AasCore.Aas3_0.Key(AasCore.Aas3_0.KeyTypes.GlobalReference, newValue)]);
                        count++;
                        break;
                    case "ValueType":
                        if (elem is AasCore.Aas3_0.Property prop)
                        {
                            if (Enum.TryParse<AasCore.Aas3_0.DataTypeDefXsd>(newValue, true, out var dt))
                            { prop.ValueType = dt; count++; }
                        }
                        break;
                    case "Category":
                        elem.Category = newValue;
                        count++;
                        break;
                }
            }

            var children = GetChildren(elem);
            if (children is not null)
            {
                var childBasePath = elem switch
                {
                    AasCore.Aas3_0.SubmodelElementCollection => $"{elemPath}.value",
                    AasCore.Aas3_0.SubmodelElementList => $"{elemPath}.value",
                    AasCore.Aas3_0.Entity => $"{elemPath}.statements",
                    _ => $"{elemPath}.value"
                };
                count += UpdateElementField(children, targetJsonPaths, field, newValue, childBasePath);
            }
        }
        return count;
    }

    private static List<AasCore.Aas3_0.ISubmodelElement>? GetChildren(AasCore.Aas3_0.ISubmodelElement elem) => elem switch
    {
        AasCore.Aas3_0.SubmodelElementCollection smc when smc.Value is { Count: > 0 } => smc.Value,
        AasCore.Aas3_0.SubmodelElementList sml when sml.Value is { Count: > 0 } => sml.Value,
        AasCore.Aas3_0.Entity ent when ent.Statements is { Count: > 0 }
            => ent.Statements.Cast<AasCore.Aas3_0.ISubmodelElement>().ToList(),
        _ => null
    };
}
