namespace DSPilot.Services;

/// <summary>
/// Theme management service for dark/light mode
/// </summary>
public class ThemeService
{
    private bool _isDarkMode = false;
    public event Action? OnThemeChanged;

    public bool IsDarkMode => _isDarkMode;

    public void ToggleTheme()
    {
        _isDarkMode = !_isDarkMode;
        OnThemeChanged?.Invoke();
    }

    public void SetDarkMode(bool enabled)
    {
        if (_isDarkMode != enabled)
        {
            _isDarkMode = enabled;
            OnThemeChanged?.Invoke();
        }
    }

    public string GetThemeClass() => _isDarkMode ? "dark-theme" : "light-theme";
}
