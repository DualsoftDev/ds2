namespace DSPilot.Services;

public class DashboardEditService
{
    public bool IsEditing { get; private set; }
    public event Action? OnChanged;

    public void Toggle()
    {
        IsEditing = !IsEditing;
        OnChanged?.Invoke();
    }
}
