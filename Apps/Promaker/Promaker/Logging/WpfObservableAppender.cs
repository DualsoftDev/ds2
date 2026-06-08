using System;
using System.Globalization;
using log4net.Appender;
using log4net.Core;
using log4net.Util;
using Promaker.ViewModels.Logging;

namespace Promaker.Logging;

/// <summary>
/// log4net.config 의 root appender 로 등록되어 모든 LoggingEvent 를 GUI VM (AppLogState) 으로 전달.
/// LoggingEvent 의 lazy property (LocationInformation/ThreadName 등) 는 caller thread context 에 의존하므로
/// Append 안에서 즉시 AppLogEntry 로 snapshot 후 marshal 한다 — raw event 를 큐 너머로 넘기지 말 것.
/// </summary>
public sealed class WpfObservableAppender : AppenderSkeleton
{
    protected override void Append(LoggingEvent e)
    {
        try
        {
            // e.Level 은 log4net 상 거의 항상 non-null 이나 annotation 상 nullable. null 이면 표시 의미 없음 → skip.
            if (e.Level is null) return;

            var loggerShort = ShortenLogger(e.LoggerName);
            var entry = new AppLogEntry(
                AppLogState.Instance.NextSeq(),
                e.TimeStamp,
                e.Level,
                loggerShort,
                e.RenderedMessage ?? string.Empty);
            AppLogState.Instance.Enqueue(entry);
        }
        catch (Exception ex)
        {
            // boundary catch — Append 실패가 caller 로 cascade 되어 추가 Log.Fatal 을 유발하면 deadlock/loop 위험.
            // CLAUDE.md "꼭 필요한 경우만 catch" 의 boundary 해당.
            ErrorHandler?.Error("WpfObservableAppender.Append failed", ex);
        }
    }

    // RollingFile layout 의 %logger{1} 과 동일 정책으로 GUI 도 단축. raw FQN 은 GUI 가독성 저하.
    private static string ShortenLogger(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var i = name.LastIndexOf('.');
        return i < 0 ? name : name.Substring(i + 1);
    }
}

/// <summary>
/// Promaker 파일 로그용 RollingFileAppender.
/// 날짜 rolling 직전/직후 marker 를 각 파일 본문에 남겨, sidecar 없이 파일 단위 경계를 판별한다.
/// </summary>
public sealed class PromakerRollingFileAppender : RollingFileAppender
{
    private DateTime _activeDate = DateTime.Now.Date;

    public override void ActivateOptions()
    {
        _activeDate = DateTime.Now.Date;
        base.ActivateOptions();
    }

    protected override void AdjustFileBeforeAppend()
    {
        var previousDate = _activeDate;
        var currentDate = DateTime.Now.Date;
        var dateRolled = currentDate > previousDate;

        if (dateRolled)
            WriteDateRollEndMarker(previousDate, currentDate);

        base.AdjustFileBeforeAppend();

        if (!dateRolled) return;

        _activeDate = currentDate;
        WriteRunBeginMarker(previousDate, currentDate);
    }

    private void WriteDateRollEndMarker(DateTime previousDate, DateTime currentDate)
    {
        WriteMarker(
            $"PROMAKER_LOG_DATE_ROLL_END runId={App.RunId} pid={Environment.ProcessId} " +
            $"previousDate={FormatDate(previousDate)} currentDate={FormatDate(currentDate)} reason=date");
    }

    private void WriteRunBeginMarker(DateTime previousDate, DateTime currentDate)
    {
        WriteMarker(
            $"PROMAKER_RUN_BEGIN runId={App.RunId} pid={Environment.ProcessId} " +
            $"startedAt={App.RunStartedAt.ToString("O", CultureInfo.InvariantCulture)} " +
            $"baseDir=\"{AppContext.BaseDirectory}\" reason=date-roll " +
            $"previousDate={FormatDate(previousDate)} currentDate={FormatDate(currentDate)}");
    }

    private void WriteMarker(string message)
    {
        if (QuietWriter is null) return;

        var properties = new PropertiesDictionary();
        properties["PromakerRunId"] = App.RunId;

        var data = new LoggingEventData
        {
            LoggerName   = typeof(PromakerRollingFileAppender).FullName ?? nameof(PromakerRollingFileAppender),
            Level        = Level.Info,
            Message      = message,
            TimeStampUtc = DateTime.UtcNow,
            Properties   = properties,
        };

        RenderLoggingEvent(QuietWriter, new LoggingEvent(data));
    }

    private static string FormatDate(DateTime date) =>
        date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
