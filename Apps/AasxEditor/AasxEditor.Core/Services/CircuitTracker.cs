namespace AasxEditor.Services;

public class CircuitTracker
{
    private readonly HashSet<string> _circuits = [];
    private readonly object _lock = new();

    public event Action? OnChanged;

    public int Count
    {
        get { lock (_lock) return _circuits.Count; }
    }

    public void Connect(string circuitId)
    {
        lock (_lock) _circuits.Add(circuitId);
        OnChanged?.Invoke();
    }

    public void Disconnect(string circuitId)
    {
        lock (_lock) _circuits.Remove(circuitId);
        OnChanged?.Invoke();
    }
}
