using AasxEditor.Models;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    private void OpenBatchEdit() { _batchNewValue = ""; _showBatchEdit = true; }
    private void CloseBatchEdit() => _showBatchEdit = false;

    private async Task OnBatchApply()
    {
        if (string.IsNullOrEmpty(_batchNewValue)) { SetStatus("값을 입력하세요", "error"); return; }
        if (_currentEnv is null) { SetStatus("로드된 파일이 없습니다", "error"); return; }

        try
        {
            var targetIds = _searchResults.Select(r => r.IdShort).ToHashSet();
            var changed = UpdateEnvironmentValues(_currentEnv, targetIds, _batchNewValue);

            var updatedJson = Converter.EnvironmentToJson(_currentEnv);

            _showBatchEdit = false;
            StateHasChanged();

            await SyncJsonToEditorAsync(updatedJson);

            if (_currentFileId > 0)
            {
                await MetadataStore.UpdateJsonContentAsync(_currentFileId, updatedJson);
                await MetadataStore.BatchUpdateValueAsync(new AasSearchQuery { Text = _searchText }, _batchNewValue);
            }

            RebuildTree();

            SetStatus($"일괄 편집 완료: {changed}건 변경됨", "success");
            await OnSearch();
        }
        catch (Exception ex) { SetStatus($"일괄 편집 오류: {ex.Message}", "error"); }
    }

    private int UpdateEnvironmentValues(AasCore.Aas3_0.Environment env, HashSet<string> targetIdShorts, string newValue)
    {
        if (env.Submodels is null) return 0;
        return env.Submodels
            .Where(sm => sm.SubmodelElements is not null)
            .Sum(sm => UpdateElementValues(sm.SubmodelElements!, targetIdShorts, newValue));
    }

    private int UpdateElementValues(List<AasCore.Aas3_0.ISubmodelElement> elements, HashSet<string> targetIdShorts, string newValue)
    {
        var count = 0;
        foreach (var elem in elements)
        {
            if (elem.IdShort is not null && targetIdShorts.Contains(elem.IdShort))
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

            var children = elem switch
            {
                AasCore.Aas3_0.SubmodelElementCollection smc when smc.Value is { Count: > 0 } => smc.Value,
                AasCore.Aas3_0.SubmodelElementList sml when sml.Value is { Count: > 0 } => sml.Value,
                AasCore.Aas3_0.Entity ent when ent.Statements is { Count: > 0 }
                    => ent.Statements.Cast<AasCore.Aas3_0.ISubmodelElement>().ToList(),
                _ => null
            };
            if (children is not null) count += UpdateElementValues(children, targetIdShorts, newValue);
        }
        return count;
    }
}
