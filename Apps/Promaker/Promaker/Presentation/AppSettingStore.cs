using System;
using System.IO;

namespace Promaker.Presentation;

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
}
