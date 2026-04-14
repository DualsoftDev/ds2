using Microsoft.JSInterop;

namespace AasxEditor.Components.Pages;

public partial class Home
{
    private readonly string _circuitId = Guid.NewGuid().ToString();

    private void OnCircuitChanged()
    {
        _ = InvokeAsync(UpdateClientCountIndicator);
    }

    private void UpdateClientCountIndicator()
    {
        var count = CircuitTracker.Count;
        var text = count >= 2 ? $"클라이언트 {count}개 접속중.." : "";
        _ = JS.InvokeVoidAsync("ClientCount.update", text);
    }

    [JSInvokable]
    public void OnBeforeUnload()
    {
        CircuitTracker.OnChanged -= OnCircuitChanged;
        CircuitTracker.Disconnect(_circuitId);
    }
}
