using System.IO.Compression;

namespace Promaker.Shared;

/// <summary>Cheap ZIP-bomb/size preflight shared by direct Agent activation and network upload.</summary>
public static class AasxPackageSafety
{
    public const int MaxEntries = 10_000;
    public const long MaxEntryBytes = 256L * 1024 * 1024;
    public const long MaxExpandedBytes = 512L * 1024 * 1024;
    public const double MaxCompressionRatio = 200.0;

    public static bool TryValidate(string path, out string error)
    {
        error = "";
        try
        {
            using var package = ZipFile.OpenRead(path);
            if (package.Entries.Count > MaxEntries)
            {
                error = "AASX package contains too many entries.";
                return false;
            }
            long expandedTotal = 0;
            foreach (var entry in package.Entries)
            {
                expandedTotal = checked(expandedTotal + entry.Length);
                if (entry.Length > MaxEntryBytes || expandedTotal > MaxExpandedBytes)
                {
                    error = "AASX expanded content exceeds the supported limit.";
                    return false;
                }
                if (entry.Length > 10L * 1024 * 1024
                    && entry.CompressedLength > 0
                    && entry.Length / (double)entry.CompressedLength > MaxCompressionRatio)
                {
                    error = $"AASX entry '{entry.FullName}' has an unsafe compression ratio.";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"AASX package structure is invalid: {ex.Message}";
            return false;
        }
    }
}
