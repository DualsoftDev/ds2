// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DSPilot.Models;

namespace DSPilot.Services.EmailBriefing;

/// <summary>
/// 일일 브리핑 메일 스케줄러. 설정(<see cref="EmailBriefingSettings"/>)의 지정 시각·요일에 "어제" 브리핑을
/// 자동 발송한다. 특정 벽시계 시각 발화 + 날짜 워터마크 멱등(하루 1회)을 결합 — 다운타임으로 발송 시각을
/// 놓친 뒤 재기동해도, 그날 미발송이면 (시각이 지난 상태로) 곧바로 따라잡아 1회 발송한다.
///
/// Singleton + HostedService 로 등록(설정 페이지가 동일 인스턴스로 수동 <see cref="SendNowAsync"/>/<see cref="PreviewAsync"/>
/// 호출). 데이터 수집은 Scoped(BriefingComposer) 라 발송마다 scope 를 연다.
/// </summary>
public sealed class EmailBriefingService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISmtpMailer _mailer;
    private readonly IBriefingApiClient _apiClient;
    private readonly BriefingHtmlRenderer _renderer;
    private readonly AppSettingsService _settings;
    private readonly BriefingRelayOptions _relay;
    private readonly ExternalAccessService _externalAccess;
    private readonly ILogger<EmailBriefingService> _logger;

    public EmailBriefingService(
        IServiceScopeFactory scopeFactory,
        ISmtpMailer mailer,
        IBriefingApiClient apiClient,
        BriefingHtmlRenderer renderer,
        AppSettingsService settings,
        BriefingRelayOptions relay,
        ExternalAccessService externalAccess,
        ILogger<EmailBriefingService> logger)
    {
        _scopeFactory = scopeFactory;
        _mailer = mailer;
        _apiClient = apiClient;
        _renderer = renderer;
        _settings = settings;
        _relay = relay;
        _externalAccess = externalAccess;
        _logger = logger;
    }

    /// <summary>고정 릴레이가 중앙 API 경로를 쓰는지(잠금 + api 모드).</summary>
    private bool UseCentralApi => _relay.Locked && _relay.IsApiMode;

    /// <summary>
    /// 실제 발송에 쓸 설정. 고정 릴레이(<see cref="BriefingRelayOptions.Locked"/>=true, 기본)이면 SMTP/발신/모드를
    /// 회사 릴레이 값으로 덮고(사용자는 활성/수신자/스케줄만), 잠금 해제 시엔 사용자 설정을 그대로 쓴다.
    /// </summary>
    private EmailBriefingSettings EffectiveSendConfig(EmailBriefingSettings user)
    {
        if (!_relay.Locked) return user;
        return new EmailBriefingSettings
        {
            Enabled = user.Enabled,
            Recipients = user.Recipients,
            SendTimeLocal = user.SendTimeLocal,
            Weekdays = user.Weekdays,
            LastSentDate = user.LastSentDate,
            SendMode = "relay",
            SmtpHost = _relay.Host,
            SmtpPort = _relay.Port,
            SmtpUseTls = _relay.UseTls,
            SmtpUser = _relay.User,
            SmtpPassword = _relay.Password,
            FromAddress = _relay.EffectiveFrom,
            FromName = _relay.FromName,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cfg = _settings.LoadSettings().EmailBriefing;

                if (!IsSchedulable(cfg))
                {
                    await SafeDelay(IdlePoll, stoppingToken);
                    continue;
                }

                var now = DateTime.Now;
                var tod = ParseTime(cfg.SendTimeLocal);
                var todayStr = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                // 예정 시각 + 지터(설치별·날짜별 안정 오프셋) — 8시 몰림 분산.
                var todaysFire = FireTimeForDay(now.Date, tod, cfg.SendJitterMinutes);
                var isFireWeekday = cfg.Weekdays.Contains((int)now.DayOfWeek);

                // 오늘 발송해야 하는데 아직 안 함(시각 도달 or 지남) → 지금 발송(정시 + 놓친 경우 따라잡기).
                if (isFireWeekday && now >= todaysFire && cfg.LastSentDate != todayStr)
                {
                    var ok = await TrySendScheduledAsync(todayStr, stoppingToken);
                    if (!ok) await SafeDelay(FailureBackoff, stoppingToken); // 발송 실패 시 tight-loop 방지
                    continue;
                }

                // 다음 발화 시각까지 대기(설정 변경 감지 위해 짧게 끊어서). 지터 반영.
                var next = ComputeNextFire(now, tod, cfg.Weekdays, cfg.SendJitterMinutes);
                _logger.LogDebug("다음 브리핑 발송 예정: {Next:yyyy-MM-dd HH:mm:ss} (지터 {Jitter}분)", next, cfg.SendJitterMinutes);
                var signature = ScheduleSignature(cfg);
                while (!stoppingToken.IsCancellationRequested && DateTime.Now < next)
                {
                    var remaining = next - DateTime.Now;
                    await SafeDelay(remaining < IdlePoll ? remaining : IdlePoll, stoppingToken);
                    // 스케줄 영향 설정이 바뀌면 재계산.
                    if (ScheduleSignature(_settings.LoadSettings().EmailBriefing) != signature) break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "브리핑 스케줄러 루프 오류");
                await SafeDelay(IdlePoll, stoppingToken);
            }
        }
    }

    /// <summary>
    /// 지금 즉시 "어제"(또는 지정일) 브리핑을 발송한다. 설정 페이지의 "테스트 발송"과 스케줄러가 공유하는 단일 경로.
    /// <paramref name="updateWatermark"/>=true 면 성공 시 오늘 날짜를 <see cref="EmailBriefingSettings.LastSentDate"/> 로
    /// 박제(스케줄러 전용) — 수동 테스트(false)는 그날 정규 발송을 막지 않는다.
    /// </summary>
    public async Task<BriefingSendResult> SendNowAsync(bool updateWatermark, DateOnly? targetDay, CancellationToken ct)
    {
        var cfg = EffectiveSendConfig(_settings.LoadSettings().EmailBriefing);
        var recipients = NormalizedRecipients(cfg);
        if (recipients.Count == 0)
            return new BriefingSendResult(false, 0, "수신 메일 주소가 설정되지 않았습니다.");
        if (_relay.Locked && !_relay.CredentialConfigured)
            return new BriefingSendResult(false, 0,
                UseCentralApi ? "발송 API가 설치 시 구성되지 않았습니다(관리자 문의)."
                              : "발송 계정이 설치 시 구성되지 않았습니다(관리자 문의).");
        var isDirect = !UseCentralApi && string.Equals(cfg.SendMode, "direct", StringComparison.OrdinalIgnoreCase);
        if (!UseCentralApi && !isDirect && string.IsNullOrWhiteSpace(cfg.SmtpHost))
            return new BriefingSendResult(false, 0, "SMTP 서버가 설정되지 않았습니다.");
        if (isDirect && string.IsNullOrWhiteSpace(cfg.FromAddress))
            return new BriefingSendResult(false, 0, "direct 발송에는 도메인이 포함된 발신 주소(From)가 필요합니다.");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var composer = scope.ServiceProvider.GetRequiredService<BriefingComposer>();
            var data = await composer.ComposeAsync(targetDay, ct);
            var subject = _renderer.BuildSubject(data);
            var html = _renderer.BuildHtml(data, _externalAccess.ResolveUrl());

            if (UseCentralApi)
                await _apiClient.SendAsync(subject, html, recipients, ct);   // 메일 자격증명 없이 회사 API 로 위임
            else
                await _mailer.SendHtmlAsync(cfg, subject, html, ct);

            if (updateWatermark)
            {
                var todayStr = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _settings.Update(m => m.EmailBriefing.LastSentDate = todayStr);
            }
            return new BriefingSendResult(true, recipients.Count, $"{recipients.Count}명에게 발송했습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "브리핑 발송 실패");
            return new BriefingSendResult(false, recipients.Count, "발송 실패: " + ex.Message);
        }
    }

    /// <summary>발송 없이 본문만 렌더(설정 페이지 미리보기).</summary>
    public async Task<BriefingPreview> PreviewAsync(DateOnly? targetDay, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var composer = scope.ServiceProvider.GetRequiredService<BriefingComposer>();
        var data = await composer.ComposeAsync(targetDay, ct);
        return new BriefingPreview(_renderer.BuildSubject(data), _renderer.BuildHtml(data, _externalAccess.ResolveUrl()));
    }

    // ── 내부 ──
    private async Task<bool> TrySendScheduledAsync(string todayStr, CancellationToken ct)
    {
        _logger.LogInformation("브리핑 정기 발송 시도 ({Day})", todayStr);
        var result = await SendNowAsync(updateWatermark: true, targetDay: null, ct);
        if (result.Sent) _logger.LogInformation("브리핑 정기 발송 성공 — {Msg}", result.Message);
        else _logger.LogWarning("브리핑 정기 발송 실패 — {Msg}", result.Message);
        return result.Sent;
    }

    private bool IsSchedulable(EmailBriefingSettings userCfg)
    {
        var cfg = EffectiveSendConfig(userCfg);
        if (!cfg.Enabled || cfg.Weekdays is not { Count: > 0 } || NormalizedRecipients(cfg).Count == 0)
            return false;
        if (_relay.Locked) return _relay.CredentialConfigured; // 호스트 기본값 상존 — 자격증명만 확인
        // 잠금 해제(사용자 직접 설정): direct = From(도메인) 필요, relay = SMTP 호스트 필요.
        return string.Equals(cfg.SendMode, "direct", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(cfg.FromAddress)
            : !string.IsNullOrWhiteSpace(cfg.SmtpHost);
    }

    /// <summary>
    /// 오늘이 발송 요일이고 오늘 발화 시각(지터 포함)이 이미 지났는지. 설정 저장 시 "당일 따라잡기" 억제 판정용 —
    /// 저장 직후 즉시 발송되는 것을 막고 다음 스케줄부터 발송하게 한다(다운타임 따라잡기는 저장과 무관하므로 유지).
    /// </summary>
    internal static bool IsTodayFirePassed(EmailBriefingSettings cfg, DateTime now)
        => (cfg.Weekdays ?? []).Contains((int)now.DayOfWeek)
           && now >= FireTimeForDay(now.Date, ParseTime(cfg.SendTimeLocal), cfg.SendJitterMinutes);

    private static List<string> NormalizedRecipients(EmailBriefingSettings cfg) =>
        (cfg.Recipients ?? [])
            .Select(r => r?.Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => r!)
            .ToList();

    private static string ScheduleSignature(EmailBriefingSettings cfg) =>
        $"{cfg.Enabled}|{cfg.SendTimeLocal}|{cfg.SendJitterMinutes}|{string.Join(',', (cfg.Weekdays ?? []).OrderBy(x => x))}|{NormalizedRecipients(cfg).Count}|{cfg.SendMode}|{cfg.SmtpHost}|{cfg.FromAddress}";

    private static TimeSpan ParseTime(string? hhmm)
    {
        if (TimeSpan.TryParseExact(hhmm, @"hh\:mm", CultureInfo.InvariantCulture, out var t)
            || TimeSpan.TryParse(hhmm, CultureInfo.InvariantCulture, out t))
            return t;
        return new TimeSpan(8, 0, 0); // 파싱 실패 시 08:00 폴백
    }

    // now 이후 가장 이른 발화 시각(요일 필터 + 지터 반영). 최대 8일 탐색.
    private static DateTime ComputeNextFire(DateTime now, TimeSpan tod, List<int> weekdays, int jitterMinutes)
    {
        for (var i = 0; i <= 7; i++)
        {
            var day = now.Date.AddDays(i);
            var cand = FireTimeForDay(day, tod, jitterMinutes);
            if (cand > now && weekdays.Contains((int)day.DayOfWeek))
                return cand;
        }
        return now.AddMinutes(1); // 방어적 폴백(요일 집합이 비정상일 때)
    }

    // 특정 날짜의 실제 발화 시각 = 예정 시각(tod) + 지터 오프셋. dueToday 판정과 대기 계산이 동일 값을 쓰도록 공용화.
    private static DateTime FireTimeForDay(DateTime day, TimeSpan tod, int jitterMinutes)
        => day.Date + tod + TimeSpan.FromSeconds(JitterOffsetSeconds(day.Date, jitterMinutes));

    // 지터 오프셋(초). 설치(MachineName)+날짜 기반 안정 해시 → 같은 날 같은 값(루프 반복에 흔들림 없음),
    // 설치마다 다른 값(전 고객이 동시 발송하지 않도록 분산). 0~jitterMinutes*60 범위.
    private static int JitterOffsetSeconds(DateTime day, int jitterMinutes)
    {
        if (jitterMinutes <= 0) return 0;
        var seed = Encoding.UTF8.GetBytes($"{Environment.MachineName}|{day:yyyy-MM-dd}");
        var hash = SHA256.HashData(seed);
        var v = BitConverter.ToUInt32(hash, 0);
        return (int)(v % (uint)(jitterMinutes * 60));
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* 종료 */ }
    }
}
