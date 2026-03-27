using System;
using System.IO;
using Promaker.Presentation;
using Xunit;

namespace Promaker.Tests;

public sealed class AppSettingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Promaker.Tests",
        nameof(AppSettingStoreTests),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadEnumOrDefault_returns_default_when_file_is_missing()
    {
        var path = Path.Combine(_root, "missing.txt");

        var result = AppSettingStore.LoadEnumOrDefault(path, AppTheme.Dark);

        Assert.Equal(AppTheme.Dark, result);
    }

    [Fact]
    public void SaveEnum_and_LoadEnumOrDefault_round_trip_enum_value()
    {
        var path = Path.Combine(_root, "theme.txt");

        AppSettingStore.SaveEnum(path, AppTheme.Light);
        var result = AppSettingStore.LoadEnumOrDefault(path, AppTheme.Dark);

        Assert.Equal(AppTheme.Light, result);
    }

    [Fact]
    public void LoadEnumOrDefault_returns_default_for_invalid_content()
    {
        var path = Path.Combine(_root, "language.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not-an-enum");

        var result = AppSettingStore.LoadEnumOrDefault(path, AppLanguage.Korean);

        Assert.Equal(AppLanguage.Korean, result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
