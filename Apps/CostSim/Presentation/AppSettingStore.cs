using System;
using System.IO;

namespace CostSim.Presentation;

internal static class AppSettingStore
{
    internal static TEnum LoadEnumOrDefault<TEnum>(string settingsPath, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        try
        {
            if (!File.Exists(settingsPath))
                return defaultValue;

            var raw = File.ReadAllText(settingsPath).Trim();
            return Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value)
                ? value
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static void SaveEnum<TEnum>(string settingsPath, TEnum value)
        where TEnum : struct, Enum
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(settingsPath, value.ToString());
        }
        catch
        {
            // Ignore persistence failures.
        }
    }

    internal static bool LoadBoolOrDefault(string settingsPath, bool defaultValue)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return defaultValue;

            var raw = File.ReadAllText(settingsPath).Trim();
            return bool.TryParse(raw, out var value)
                ? value
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static void SaveBool(string settingsPath, bool value)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(settingsPath, value.ToString());
        }
        catch
        {
            // Ignore persistence failures.
        }
    }

    internal static string LoadStringOrDefault(string settingsPath, string defaultValue)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return defaultValue;

            var value = File.ReadAllText(settingsPath).Trim();
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static void SaveString(string settingsPath, string value)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(settingsPath, value ?? string.Empty);
        }
        catch
        {
            // Ignore persistence failures.
        }
    }
}
