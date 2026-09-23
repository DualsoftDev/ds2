// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using System.Threading.Channels;
using log4net;
using log4net.Config;

namespace DSPilot.Infrastructure;

/// <summary>
/// DSPilot 의 <see cref="ILogger"/> 출력을 공유 폴더의 파일로 남긴다 — Agent 의 promaker-agent.log 와 같은 규약.
/// <para>
/// 왜 필요한가: DSPilot 은 콘솔 로거만 달고 있었는데, Windows 서비스/systemd 로 기동되면 stdout 이 어디에도
/// 붙지 않아 로그가 통째로 증발한다. 2026-09-22 현장 장애 때 "모델 적재가 30회 실패하고 엔진이 초기화되지
/// 않는다"는 결정적 예외가 서비스 모드에서는 아무 데도 안 남아, 서비스를 멈추고 콘솔로 재기동한 뒤에야
/// 원인이 보였다. 사후 추적이 가능하려면 파일로 남아야 한다.
/// </para>
/// <para>
/// 구현: 파일 I/O 는 log4net 의 RollingFileAppender 가 맡고(회전·보존 정책은 log4net.config),
/// 호출 스레드는 <b>절대 막지 않는다</b>. 로그 호출은 큐에 넣기만 하고 전용 백그라운드 태스크가 비운다.
/// 큐가 차면 기다리지 않고 버린다 — 콘솔 로거에 DropWrite 를 준 것과 같은 이유다. Hub 신호 소비 스레드가
/// 로그 I/O 에 붙잡히면 신호 채널이 포화되어 데이터가 유실된다(구미 실기: 오후 신호 약 42% 유실).
/// </para>
/// 설정(<c>Logging:SharedFile</c>): <c>Enabled</c>(기본 true) · <c>MinimumLevel</c>(기본 Information) ·
/// <c>Directory</c>(기본 <see cref="SharedPaths.DsPilotLogDirectory"/>).
/// </summary>
public static class SharedFileLogging
{
    /// <summary>log4net.config 의 전용 appender 에 물려 있는 로거 이름. 이 이름으로 나간 것만 dspilot.log 로 간다.</summary>
    private const string SinkLoggerName = "DSPilot.Runtime";

    /// <summary>실제로 쓰이는 로그 폴더. 초기화 전이거나 실패했으면 null.</summary>
    public static string? LogDirectory { get; private set; }

    /// <summary>공유 폴더 파일 로거를 등록한다. 폴더 확보나 log4net 설정에 실패하면 조용히 건너뛴다(콘솔은 유지).</summary>
    public static ILoggingBuilder AddSharedFile(this ILoggingBuilder builder, IConfiguration configuration)
    {
        var section = configuration.GetSection("Logging:SharedFile");

        if (bool.TryParse(section["Enabled"], out var enabled) && !enabled) return builder;

        var directory = Initialize(section["Directory"]);
        if (directory is null) return builder;

        var minimum = ParseLevel(section["MinimumLevel"]) ?? LogLevel.Information;
        builder.AddProvider(new SharedFileLoggerProvider(SinkLoggerName, minimum));
        return builder;
    }

    /// <summary>로그 폴더를 확보하고 log4net 을 설정한다. 성공 시 최종 폴더, 실패 시 null.</summary>
    private static string? Initialize(string? directoryOverride)
    {
        var dir = string.IsNullOrWhiteSpace(directoryOverride)
            ? SharedPaths.DsPilotLogDirectory
            : Environment.ExpandEnvironmentVariables(directoryOverride.Trim());

        if (!TryEnsureDirectory(ref dir)) return null;

        // log4net.config 의 %property{LogDirectory} 가 이 값을 읽는다 — XmlConfigurator 보다 먼저 넣어야 한다.
        GlobalContext.Properties["LogDirectory"] = dir;

        var cfg = new FileInfo(Path.Combine(AppContext.BaseDirectory, "log4net.config"));
        if (!cfg.Exists) return null;

        try
        {
            XmlConfigurator.Configure(LogManager.GetRepository(typeof(SharedFileLogging).Assembly), cfg);
        }
        catch
        {
            return null;
        }

        LogDirectory = dir;
        return dir;
    }

    /// <summary>공유 폴더를 못 만들면(권한·드라이브 부재) 설치 폴더 아래로 물러난다 — 로그가 없는 것보단 낫다.</summary>
    private static bool TryEnsureDirectory(ref string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch { /* 아래 폴백 */ }

        try
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(fallback);
            dir = fallback;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LogLevel? ParseLevel(string? value)
        => Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : null;
}

/// <summary>ILogger → log4net 브리지. 큐잉·드롭 정책은 <see cref="SharedFileLogging"/> 주석 참조.</summary>
internal sealed class SharedFileLoggerProvider : ILoggerProvider
{
    /// <summary>미처리 항목 상한. 넘으면 새 로그를 버린다(호출 스레드는 절대 대기시키지 않는다).</summary>
    private const int MaxPending = 8192;

    private readonly ILog _sink;
    private readonly Channel<Entry> _queue;
    private readonly Task _writer;
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private int _pending;
    private int _dropped;

    internal SharedFileLoggerProvider(string sinkLoggerName, LogLevel minimum)
    {
        Minimum = minimum;
        _sink = LogManager.GetLogger(typeof(SharedFileLoggerProvider).Assembly, sinkLoggerName);
        _queue = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true });
        _writer = Task.Run(WriteLoopAsync);

        // ★ILoggingBuilder.AddProvider 는 인스턴스로 등록하므로 DI 컨테이너도 LoggerFactory 도 이 객체를
        //   Dispose 해 주지 않는다. 종료 훅이 없으면 서비스 중지 순간 큐에 남아 있던 로그 — 하필 종료 원인을
        //   담고 있을 그 줄들 — 이 그대로 사라진다.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private void OnProcessExit(object? sender, EventArgs e) => Drain();

    /// <summary>큐를 닫고 writer 가 비울 때까지 잠시 기다린다. 중복 호출 안전.</summary>
    private void Drain()
    {
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(3)); }
        catch { /* 종료 중 — 남은 로그는 포기 */ }
    }

    internal LogLevel Minimum { get; }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new SharedFileLogger(this, name));

    internal void Enqueue(LogLevel level, string category, string message, Exception? exception)
    {
        if (Volatile.Read(ref _pending) >= MaxPending)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        Interlocked.Increment(ref _pending);
        var entry = new Entry(level, category, message, exception, Environment.CurrentManagedThreadId);
        if (!_queue.Writer.TryWrite(entry)) Interlocked.Decrement(ref _pending);
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var entry))
            {
                Interlocked.Decrement(ref _pending);
                Emit(entry);
            }
            ReportDrops();
        }
        ReportDrops();
    }

    /// <summary>버린 건수는 조용히 삼키지 않고 파일에 남긴다 — 로그가 비는 구간을 "조용했다"로 오독하지 않도록.</summary>
    private void ReportDrops()
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0) _sink.Warn($"[SharedFileLogger] 큐 포화로 로그 {dropped}건을 버렸습니다.");
    }

    private void Emit(Entry e)
    {
        // 스레드 번호는 호출 시점 것을 실어 나른다 — 기록은 전용 writer 스레드가 하므로 %thread 는 의미가 없다.
        var line = $"[{e.ThreadId}] {e.Category} - {e.Message}";
        try
        {
            switch (e.Level)
            {
                case LogLevel.Trace:
                case LogLevel.Debug: _sink.Debug(line, e.Exception); break;
                case LogLevel.Information: _sink.Info(line, e.Exception); break;
                case LogLevel.Warning: _sink.Warn(line, e.Exception); break;
                case LogLevel.Error: _sink.Error(line, e.Exception); break;
                default: _sink.Fatal(line, e.Exception); break;
            }
        }
        catch { /* 로깅 실패가 앱을 멈추게 하지 않는다 */ }
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Drain();
    }

    private readonly record struct Entry(
        LogLevel Level, string Category, string Message, Exception? Exception, int ThreadId);

    private sealed class SharedFileLogger : ILogger
    {
        private readonly SharedFileLoggerProvider _owner;
        private readonly string _category;

        internal SharedFileLogger(SharedFileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _owner.Minimum;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;

            _owner.Enqueue(logLevel, _category, message, exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
