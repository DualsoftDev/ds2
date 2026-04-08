using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CostSim;

internal static class CostSimPathService
{
    public static string LibraryPathSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "CostSim", "Settings", "library-path.txt");

    public static string DefaultLibraryRootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dualsoft", "CostSim", "Library");

    public static string DemoLibraryRootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dualsoft", "CostSim", "DemoLibrary");

    public static string ResolvePreferredLibraryPath(string? requestedPath)
    {
        var trimmed = requestedPath?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return EnsureDirectory(DefaultLibraryRootPath);

        try
        {
            var fullPath = Path.GetFullPath(trimmed);
            var documentsPath = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            if (PathsEqual(fullPath, documentsPath))
                return EnsureDirectory(DefaultLibraryRootPath);

            return fullPath;
        }
        catch
        {
            return EnsureDirectory(DefaultLibraryRootPath);
        }
    }

    public static IEnumerable<string> EnumerateLibraryFiles(string libraryPath)
    {
        EnsureDirectory(libraryPath);

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(libraryPath, "*.*", enumerationOptions)
            .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Path.GetExtension(path), ".aasx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase);
    }

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
