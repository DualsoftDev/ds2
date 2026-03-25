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

                using var stream = file.OpenReadStream(maxAllowedSize: 50 * 1024 * 1024);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);

                var env = Converter.ReadEnvironmentFromBytes(ms.ToArray());
                if (env is null) { SetStatus($"{file.Name}: 읽기 실패", "error"); continue; }

                var json = Converter.EnvironmentToJson(env);
                await ApplyEnvironmentAsync(env, json, file.Name);
                await RegisterInDbAsync(file.Name, env, json);
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

            await ApplyEnvironmentAsync(env, json, lastFile.FileName);
            _currentFileId = lastFile.Id;
            SetStatus($"DB에서 복원됨: {lastFile.FileName}", "success");
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

            var aasxBytes = Converter.WriteEnvironmentToBytes(_currentEnv);
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
