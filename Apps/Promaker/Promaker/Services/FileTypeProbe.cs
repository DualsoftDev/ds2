using System;
using System.IO;
using Promaker.Presentation;

namespace Promaker.Services;

/// <summary>
/// 파일 확장자 분류 helpers. MainViewModel partial / ExternalFileChangeWatcher 등 여러 곳에서 동일 분기를
/// 재구현하지 않도록 single source. Pure static — store / VM 의존 없음.
/// </summary>
internal static class FileTypeProbe
{
    public static bool HasExtension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);

    public static bool IsAasx(string path) => HasExtension(path, FileExtensions.Aasx);

    public static bool IsMermaid(string path) => HasExtension(path, FileExtensions.Mermaid);

    public static bool IsYaml(string path) =>
        HasExtension(path, FileExtensions.Yaml) || HasExtension(path, FileExtensions.YamlAlt);
}
