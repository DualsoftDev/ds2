using Microsoft.JSInterop;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    [JSInvokable] public void OnDragEnterPage() { _isDragOver = true; StateHasChanged(); }
    [JSInvokable] public void OnDragLeavePage() { _isDragOver = false; StateHasChanged(); }
    [JSInvokable] public void OnDropNotify(string message) => SetStatus(message, "error");

    [JSInvokable]
    public void OnDropFiles(string[] fileNames)
    {
        _pendingDropFileNames = fileNames;
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
