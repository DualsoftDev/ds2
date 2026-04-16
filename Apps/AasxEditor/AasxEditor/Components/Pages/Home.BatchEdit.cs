using AasCore.Aas3_1;
using AasxEditor.Models;
using Environment = AasCore.Aas3_1.Environment;
using Range = AasCore.Aas3_1.Range;

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

    private int UpdateEnvironmentValues(Environment env, HashSet<string> targetIdShorts, string newValue)
    {
        if (env.Submodels is null) return 0;
        return env.Submodels
            .Where(sm => sm.SubmodelElements is not null)
            .Sum(sm => UpdateElementValues(sm.SubmodelElements!, targetIdShorts, newValue));
    }

    private int UpdateElementValues(List<ISubmodelElement> elements, HashSet<string> targetIdShorts, string newValue)
    {
        var count = 0;
        foreach (var elem in elements)
        {
            if (elem.IdShort is not null && targetIdShorts.Contains(elem.IdShort))
            {
                count += elem switch
                {
                    Property p => Do(() => p.Value = newValue),
                    MultiLanguageProperty mlp => Do(() =>
                    {
                        if (mlp.Value is { Count: > 0 })
                            mlp.Value[0] = new LangStringTextType(mlp.Value[0].Language, newValue);
                        else
                            mlp.Value = [new LangStringTextType("en", newValue)];
                    }),
                    Range r => Do(() => { r.Min = newValue; r.Max = newValue; }),
                    _ => 0
                };
            }

            var children = elem switch
            {
                SubmodelElementCollection smc when smc.Value is { Count: > 0 } => smc.Value,
                SubmodelElementList sml when sml.Value is { Count: > 0 } => sml.Value,
                Entity ent when ent.Statements is { Count: > 0 }
                    => ent.Statements.Cast<AasCore.Aas3_1.ISubmodelElement>().ToList(),
                _ => null
            };
            if (children is not null) count += UpdateElementValues(children, targetIdShorts, newValue);
        }
        return count;
    }
}
