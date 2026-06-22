using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Promaker.Shared;

/// <summary>
/// 공유 폴더의 AASX/사이드카 동시 쓰기를 직렬화하는 cross-process 파일 락.
/// named Mutex 는 머신 로컬이라 같은 공유 폴더를 보는 cross-machine(우분투 Agent ↔ Windows Promaker)·
/// cross-process(Promaker WPF ↔ DSPilot 서비스) 동시 쓰기를 못 막는다 → 공유 폴더의 락 파일을
/// 원자적 생성(FileMode.CreateNew)으로 획득한다. 점유자/시각을 기록해 경고 표시 + stale 타임아웃 회수.
///
/// 사용: var ok = SharedWriteLock.TryAcquire("Promaker", out var holder);
///       try { /* 읽기→수정→쓰기 */ } finally { if (ok) SharedWriteLock.Release("Promaker"); }
/// </summary>
public static class SharedWriteLock
{
    /// <summary>점유자가 크래시로 락을 안 풀었을 때 강제 회수 기준(ms). 이 시간 지난 락은 stale 로 간주해 회수.</summary>
    public const int StaleTimeoutMs = 30_000;

    /// <summary>락 점유 정보 — 경고 메시지 표시 + stale 판정용.</summary>
    public readonly record struct LockInfo(string Holder, int Pid, string AtUtc);

    private static readonly JsonSerializerOptions JsonOpts = new();

    /// <summary>락 획득 시도. 성공 시 true. 점유 중이면 false + <paramref name="current"/> 에 현재 점유자(경고 표시용).
    /// stale(타임아웃 초과·손상) 락은 회수 후 재시도한다.</summary>
    public static bool TryAcquire(string holder, out LockInfo current)
    {
        current = default;
        try { Directory.CreateDirectory(SharedPaths.AgentDirectory); } catch { }
        var path = SharedPaths.SharedWriteLockPath;

        ReclaimIfStale(path);

        var me = new LockInfo(holder, Environment.ProcessId, DateTime.UtcNow.ToString("o"));
        try
        {
            // CreateNew = 원자적: 이미 있으면 IOException → 점유 중. (두 프로세스 동시 호출해도 하나만 성공)
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var w = new StreamWriter(fs);
            w.Write(JsonSerializer.Serialize(me, JsonOpts));
            return true;
        }
        catch (IOException)
        {
            current = TryRead(path) ?? new LockInfo("(알 수 없음)", 0, "");
            return false;
        }
    }

    /// <summary>락 해제 — 자기가 잡은 락(holder 일치)만 삭제. 정리 실패는 무시.</summary>
    public static void Release(string holder)
    {
        try
        {
            if (TryRead(SharedPaths.SharedWriteLockPath) is { } i && i.Holder == holder)
                File.Delete(SharedPaths.SharedWriteLockPath);
        }
        catch { /* 정리 실패 무시 */ }
    }

    /// <summary>기존 락이 stale(타임아웃 초과)이거나 손상(파싱 불가)이면 회수(삭제).</summary>
    private static void ReclaimIfStale(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = TryRead(path);
            if (info is { } i
                && DateTime.TryParse(i.AtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                && (DateTime.UtcNow - at.ToUniversalTime()).TotalMilliseconds <= StaleTimeoutMs)
                return;   // 살아있는 락 — 회수 안 함
            File.Delete(path);   // stale 또는 손상 → 회수
        }
        catch { /* 회수 실패 시 다음 TryAcquire 가 IOException 으로 정상 거부 */ }
    }

    private static LockInfo? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(path), JsonOpts);
        }
        catch { return null; }
    }
}
