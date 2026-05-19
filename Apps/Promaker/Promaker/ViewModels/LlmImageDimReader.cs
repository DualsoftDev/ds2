using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace Promaker.ViewModels;

/// <summary>
/// 이미지 dimension(width × height) metadata-only 읽기 helper.
/// BitmapCreateOptions.IgnoreColorProfile + BitmapCacheOption.None 으로 pixel data 디코딩 회피.
/// 실패 시 (0, 0) — caller 가 fallback 처리.
/// </summary>
internal static class LlmImageDimReader
{
    public static (int W, int H) TryFromPath(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(fs);
        }
        catch { return (0, 0); }
    }

    public static (int W, int H) TryFromBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return Read(ms);
        }
        catch { return (0, 0); }
    }

    private static (int W, int H) Read(Stream stream)
    {
        var dec = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var f = dec.Frames.FirstOrDefault();
        return f is null ? (0, 0) : (f.PixelWidth, f.PixelHeight);
    }
}
