using System;
using System.Globalization;
using log4net.Core;

namespace Promaker.ViewModels.Logging;

/// <summary>
/// 앱 전역 log4net 출력의 GUI 표시용 단일 엔트리. reference equality 보존을 위해 record 가 아닌 sealed class.
/// </summary>
public sealed class AppLogEntry
{
    /// <summary>화면/clipboard 출력 SSOT. XAML 은 `Display` property 를 binding 하고, clipboard 는 ToString() 사용.</summary>
    public const string Format = "[{0:HH:mm:ss.fff}] {1,-5} {2} — {3}";

    public AppLogEntry(long seq, DateTime timestamp, Level level, string logger, string message)
    {
        Seq = seq;
        Timestamp = timestamp;
        Level = level;
        Logger = logger;
        Message = message;
    }

    public long Seq { get; }
    public DateTime Timestamp { get; }
    public Level Level { get; }
    public string Logger { get; }
    public string Message { get; }

    /// <summary>화면 / clipboard 공통 표시 문자열. XAML 은 `Text="{Binding Display}"` 로 단순 binding.</summary>
    public string Display =>
        string.Format(CultureInfo.InvariantCulture, Format, Timestamp, Level.Name, Logger, Message);

    public override string ToString() => Display;
}
