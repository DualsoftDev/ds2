using Microsoft.JSInterop;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    [JSInvokable] public void OnDragEnterPage() { _isDragOver = true; StateHasChanged(); }
    [JSInvokable] public void OnDragLeavePage() { _isDragOver = false; StateHasChanged(); }
    [JSInvokable] public void OnDropNotify(string message) => SetStatus(message, "error");

    [JSInvokable]
    public async Task OnDropFiles(string[] fileNames)
    {
        _pendingDropFileNames = fileNames;

        // 아직 아무 파일도 안 연 첫 상태에서는 "추가" 가 의미 없으므로
        // 방식 선택 다이얼로그를 건너뛰고 바로 "새로 열기" 로 로드.
        if (!_contentLoaded)
        {
            await TriggerDropInput("#input-file-open");
            return;
        }

        _showDropChoice = true;
        StateHasChanged();
    }

    private async Task OnDropChoiceOpen() => await TriggerDropInput("#input-file-open");
    private async Task OnDropChoiceAdd() => await TriggerDropInput("#input-file-add");

    private async Task TriggerDropInput(string selector)
    {
        _showDropChoice = false;
        StateHasChanged();
        await JS.InvokeVoidAsync("DropZone.triggerInputFile", selector);
    }
}
