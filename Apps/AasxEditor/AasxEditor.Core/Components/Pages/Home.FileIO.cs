using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    // ===== File Open / Add =====
    private async Task OnFileOpen(InputFileChangeEventArgs e)
        => await LoadFilesAsync(e.GetMultipleFiles(10), isNewOpen: true);

    private async Task OnFileAdd(InputFileChangeEventArgs e)
        => await LoadFilesAsync(e.GetMultipleFiles(10), isNewOpen: false);

    private async Task LoadFilesAsync(IReadOnlyList<IBrowserFile> files, bool isNewOpen)
    {
        try
        {
            if (isNewOpen) await ResetForNewOpenAsync();

            foreach (var file in files)
            {
                SetStatus($"파일 읽는 중: {file.Name}...", "info");
                StateHasChanged();

                using var stream = file.OpenReadStream(maxAllowedSize: 200 * 1024 * 1024);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var originalBytes = ms.ToArray();

                var env = Converter.ReadEnvironmentFromBytes(originalBytes);
                if (env is null) { SetStatus($"{file.Name}: 읽기 실패", "error"); continue; }

                EnsureErrorDefinitions(env);
                var json = Converter.EnvironmentToJson(env);
                await ApplyEnvironmentAsync(env, json, file.Name);
                _isExternalAasx = !IsDsAasx(env);
                await RegisterInDbAsync(file.Name, env, json, originalBytes);
            }

            _loadedFiles = await MetadataStore.GetFilesAsync();
            SetStatus($"로드 완료 ({_loadedFiles.Count}개 파일)", "success");
        }
        catch (Exception ex) { SetStatus($"오류: {ex.Message}", "error"); }
    }

    private async Task RestoreFromDbAsync()
    {
        try
        {
            _loadedFiles = await MetadataStore.GetFilesAsync();
            if (_loadedFiles.Count == 0) return;

            var lastFile = _loadedFiles[0];
            var json = await MetadataStore.GetJsonContentAsync(lastFile.Id);
            if (string.IsNullOrWhiteSpace(json)) return;

            var env = Converter.JsonToEnvironment(json);
            if (env is null) return;

            EnsureErrorDefinitions(env);
            json = Converter.EnvironmentToJson(env);
            await ApplyEnvironmentAsync(env, json, lastFile.FileName);
            _currentFileId = lastFile.Id;
            _isExternalAasx = !IsDsAasx(env);
            SetStatus($"DB에서 복원됨: {lastFile.FileName}", "success");
        }
        catch { }
    }

    private async Task ApplyEnvironmentAsync(AasCore.Aas3_1.Environment env, string json, string fileName)
    {
        _contentLoaded = true;
        _fileName = fileName;
        _currentEnv = env;
        await SyncJsonToEditorAsync(json);
        RebuildTree();
        ClearUndoHistory();
    }

    private async Task RegisterInDbAsync(string fileName, AasCore.Aas3_1.Environment env, string json, byte[]? originalBytes)
    {
        var shellCount = env.AssetAdministrationShells?.Count ?? 0;
        var submodelCount = env.Submodels?.Count ?? 0;
        var fileRecord = await MetadataStore.AddFileAsync(fileName, fileName, shellCount, submodelCount, json, originalBytes);
        _currentFileId = fileRecord.Id;
        var entities = EntityExtractor.Extract(env);
        await MetadataStore.AddEntitiesAsync(_currentFileId, entities);
    }

    /// <summary>DS가 생성한 AASX인지 판별 (semanticId "dualsoft.com" 포함 여부로 판정).</summary>
    private static bool IsDsAasx(AasCore.Aas3_1.Environment env)
    {
        if (env.Submodels is null) return false;
        foreach (var sm in env.Submodels)
        {
            var keys = sm.SemanticId?.Keys;
            if (keys is null) continue;
            foreach (var k in keys)
                if (!string.IsNullOrEmpty(k.Value) && k.Value.Contains("dualsoft.com", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    private async Task ResetForNewOpenAsync()
    {
        await ClearDbAsync();
        _searchResults.Clear();
        _searchText = "";
    }

    private async Task ClearDbAsync()
    {
        try
        {
            var files = await MetadataStore.GetFilesAsync();
            foreach (var f in files)
            {
                await MetadataStore.RemoveEntitiesByFileAsync(f.Id);
                await MetadataStore.RemoveFileAsync(f.Id);
            }
        }
        catch { }
    }

    // ===== Save =====
    private async Task OnSaveAasx() => await SaveAasxAs(_fileName ?? "output.aasx");

    private void OnSaveAsAasx()
    {
        _saveAsName = _fileName ?? "output.aasx";
        _showSaveAs = true;
    }

    private async Task OnSaveAsConfirm()
    {
        if (string.IsNullOrWhiteSpace(_saveAsName)) return;
        var name = _saveAsName.Trim();
        if (!name.EndsWith(".aasx", StringComparison.OrdinalIgnoreCase)) name += ".aasx";
        _showSaveAs = false;
        await SaveAasxAs(name);
    }

    private async Task SaveAasxAs(string outputName)
    {
        try
        {
            if (_currentEnv is null) { SetStatus("저장할 내용이 없습니다", "error"); return; }

            SetStatus("AASX 생성 중...", "info");
            StateHasChanged();

            // 외부 AASX의 경우 원본 ZIP 엔트리(썸네일·첨부파일·커스텀 관계)를 보존하기 위해 원본 바이트를 전달
            byte[]? originalBytes = null;
            if (_currentFileId > 0)
                originalBytes = await MetadataStore.GetOriginalBytesAsync(_currentFileId);

            var aasxBytes = Converter.WriteEnvironmentToBytes(_currentEnv, originalBytes);
            var base64 = Convert.ToBase64String(aasxBytes);
            await JS.InvokeVoidAsync("MonacoInterop.downloadFile", outputName, base64);

            _fileName = outputName;
            SetStatus($"저장 완료: {outputName} ({aasxBytes.Length / 1024}KB)", "success");
        }
        catch (Exception ex) { SetStatus($"저장 오류: {ex.Message}", "error"); }
    }

    // ===== Toolbar =====
    private async Task OnFormat()
    {
        await JS.InvokeVoidAsync("MonacoInterop.formatDocument");
        SetStatus("정렬 완료", "success");
    }

    private void OnValidate()
    {
        if (string.IsNullOrWhiteSpace(_currentJson)) { SetStatus("내용이 없습니다", "error"); return; }
        var (isValid, error) = Converter.ValidateJson(_currentJson);
        SetStatus(isValid ? "유효한 AAS JSON 입니다." : $"검증 실패: {error}", isValid ? "success" : "error");
    }
}
