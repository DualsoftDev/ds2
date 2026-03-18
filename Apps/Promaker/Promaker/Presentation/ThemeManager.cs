using System;
using System.IO;
using System.Windows;

namespace Promaker.Presentation;

public enum AppTheme
{
    Dark,
    Light
}

public static class ThemeManager
{
    private static readonly Uri DarkThemeUri = new("Themes/Theme.Dark.xaml", UriKind.Relative);
    private static readonly Uri LightThemeUri = new("Themes/Theme.Light.xaml", UriKind.Relative);
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Promaker",
        "theme.txt");

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;
    public static event Action<AppTheme>? ThemeChanged;

    public static void ApplySavedTheme()
    {
        ApplyTheme(LoadSavedTheme(), persist: false);
    }

    public static void ToggleTheme()
    {
        ApplyTheme(CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
    }

    public static void ApplyTheme(AppTheme theme, bool persist = true)
    {
        CurrentTheme = theme;

        if (Application.Current is { } app)
        {
            var dictionaries = app.Resources.MergedDictionaries;
            var themeDictionary = new ResourceDictionary
            {
                Source = theme == AppTheme.Dark ? DarkThemeUri : LightThemeUri
            };

            var themeIndex = -1;
            for (var i = 0; i < dictionaries.Count; i++)
            {
                if (IsThemeDictionary(dictionaries[i]))
                {
                    themeIndex = i;
                    break;
                }
            }

            if (themeIndex >= 0)
            {
                dictionaries[themeIndex] = themeDictionary;
            }
            else
            {
                dictionaries.Insert(0, themeDictionary);
            }
        }

        if (persist)
        {
            SaveTheme(theme);
        }

        ThemeChanged?.Invoke(theme);
    }

    private static AppTheme LoadSavedTheme()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return AppTheme.Dark;
            }

            var raw = File.ReadAllText(SettingsPath).Trim();
            return Enum.TryParse<AppTheme>(raw, ignoreCase: true, out var theme)
                ? theme
                : AppTheme.Dark;
        }
        catch
        {
            return AppTheme.Dark;
        }
    }

    private static void SaveTheme(AppTheme theme)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(SettingsPath, theme.ToString());
        }
        catch
        {
            // Ignore theme persistence failures.
        }
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null &&
               (source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase));
    }
}
