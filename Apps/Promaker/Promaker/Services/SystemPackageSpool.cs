using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Promaker.Services;

/// <summary>
/// 인스턴스 간 시스템 복사의 <b>파일 채널</b> — OS 클립보드가 막혀도 Ctrl+C/Ctrl+V 가 성립하게 하는
/// 정본 저장소. Windows 클립보드는 전역 배타 자원이라 클립보드 관리자·원격 데스크톱·보안 에이전트가
/// 점유/차단하면 앱이 손쓸 수 없이 실패한다(CLIPBRD_E_CANT_OPEN). 그래서 복사는 <b>항상</b> 이 스풀에
/// 먼저 쓰고, 클립보드 게시는 best-effort 로 강등한다 — 사용자에게 클립보드 기록 끄기 같은 환경
/// 조치를 요구하지 않는다는 뜻.
///
/// payload 는 SystemPackageClipboard 봉투 JSON 원문 그대로 (별도 wire format 없음).
/// meta 는 폴백 확인 다이얼로그를 오버레이 진입 전에 띄우려고 분리한 소용량 사이드카 —
/// UI 스레드에서 즉시 읽어도 부담 없다.
///
/// "가장 최근 복사가 이긴다" 규약 그대로 파일 1쌍을 덮어쓰므로 누적되지 않는다.
/// </summary>
public static class SystemPackageSpool
{
    private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(SystemPackageSpool));

    /// <summary>폴백 유효 기간 — 이보다 오래된 스풀은 "없음"으로 취급(옛 복사분 오붙임 방지).</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    private static string PayloadPath => Path.Combine(SettingsPaths.ClipboardSpoolDir, "last-package.json");
    private static string MetaPath => Path.Combine(SettingsPaths.ClipboardSpoolDir, "last-package.meta.json");

    /// <summary>
    /// 스풀 사이드카 — 폴백 확인 다이얼로그 문구용(무엇을·언제 복사했는지) + 채널 우선순위 판정용.
    /// ClipboardPublished=false 이고 ClipboardSequence 가 현재 시퀀스와 같으면
    /// "클립보드에는 이 복사분이 없고 그 뒤로 클립보드가 바뀌지도 않았다" = 클립보드에 남은
    /// 패키지는 확실히 더 낡은 것 → 스풀이 정본(확인 없이 스풀 사용).
    /// </summary>
    public sealed record SpoolMeta(
        DateTime WrittenAtUtc, List<string> Names, string AppVersion,
        bool ClipboardPublished, uint ClipboardSequence);

    /// <summary>
    /// 봉투 payload 를 스풀에 기록 — 클립보드 시도보다 <b>먼저</b> 호출한다(파일 채널이 정본).
    /// 임시 파일 → Move 로 교체해 부분 기록된 파일이 읽히지 않게 하고, 옛 meta 를 먼저 지워
    /// "새 payload + 옛 meta" 짝이 잠시라도 읽히지 않게 한다(TryReadMeta 가 둘 다 요구).
    /// 실패는 예외 대신 false — 클립보드가 살아 있으면 복사는 여전히 성립하므로 사용자 작업을
    /// 막지 않는다(로그로만 흔적).
    /// </summary>
    public static bool TryWritePayload(string envelopeJson)
    {
        try
        {
            Directory.CreateDirectory(SettingsPaths.ClipboardSpoolDir);
            if (File.Exists(MetaPath))
                File.Delete(MetaPath);
            _cacheStampMs = -1;
            WriteAtomic(PayloadPath, envelopeJson);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"System package spool payload write failed — {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 클립보드 게시 결과까지 확정한 뒤 meta 를 기록해 스풀을 "보이게" 만든다(커밋).
    /// TryWritePayload 성공 후에만 호출.
    /// </summary>
    public static bool TryCommitMeta(
        IReadOnlyList<string> names, string appVersion, bool clipboardPublished, uint clipboardSequence)
    {
        try
        {
            var meta = new SpoolMeta(
                DateTime.UtcNow, [.. names], appVersion, clipboardPublished, clipboardSequence);
            WriteAtomic(MetaPath, JsonSerializer.Serialize(meta));
            _cacheStampMs = -1;   // 방금 쓴 복사분이 같은 인스턴스에서도 즉시 보이게 무효화
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"System package spool meta commit failed — {ex.Message}");
            return false;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// TTL 안의 스풀 메타를 읽는다(없거나 만료/손상이면 null). 저비용 — CanExecute 폴링 및
    /// 폴백 확인 다이얼로그 문구 구성에 쓴다.
    /// </summary>
    public static SpoolMeta? TryReadMeta()
    {
        try
        {
            if (!File.Exists(MetaPath) || !File.Exists(PayloadPath))
                return null;
            var meta = JsonSerializer.Deserialize<SpoolMeta>(File.ReadAllText(MetaPath));
            if (meta is null || meta.Names is null)
                return null;
            return DateTime.UtcNow - meta.WrittenAtUtc <= Ttl ? meta : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"System package spool meta read failed — {ex.Message}");
            return null;
        }
    }

    private static long _cacheStampMs = -1;
    private static bool _cachedHasPackage;

    /// <summary>
    /// CanExecute 폴링용 저비용 체크 — CommandManager 는 입력마다 재질의하므로 2초 캐시로
    /// 파일 IO 를 억제한다(클립보드 쪽 시퀀스 번호 캐시와 같은 역할).
    /// </summary>
    public static bool HasFreshPackage()
    {
        var now = Environment.TickCount64;
        if (_cacheStampMs >= 0 && now - _cacheStampMs < 2000)
            return _cachedHasPackage;
        _cacheStampMs = now;
        _cachedHasPackage = TryReadMeta() is not null;
        return _cachedHasPackage;
    }

    /// <summary>TTL 안의 스풀 봉투 원문을 읽는다(없거나 만료/실패면 null).</summary>
    public static string? TryReadPayload()
    {
        if (TryReadMeta() is null)
            return null;
        try
        {
            return File.ReadAllText(PayloadPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"System package spool payload read failed — {ex.Message}");
            return null;
        }
    }
}
