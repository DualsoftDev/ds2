using System;
using System.IO;
using System.Windows;

namespace CostSim.Presentation;

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
        "Dualsoft", "CostSim", "Settings", "theme.txt");

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
        => AppSettingStore.LoadEnumOrDefault(SettingsPath, AppTheme.Dark);

    private static void SaveTheme(AppTheme theme)
        => AppSettingStore.SaveEnum(SettingsPath, theme);

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null &&
               (source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase));
    }
}
