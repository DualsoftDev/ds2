using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// s6-r24 작업 2 — `LlmConfig.ModifyWithLock` (MJ1 multi-instance race 본질 해결) 회귀 차단.
///
/// scope: 단일 thread 정상 동작 / 다중 thread 동시 mutate 누적 정합 / lock 충돌 backoff retry.
/// fixture = 임시 path override (TestConfigPathOverride). [Collection] 으로 직렬화 — 같은 override path 의
/// cross-test race 차단.
/// </summary>
[Collection("LlmConfigOverride")]
public sealed class LlmConfigModifyWithLockTests : IDisposable
{
    private readonly string _tempPath;

    public LlmConfigModifyWithLockTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"lh-test-llm-{Guid.NewGuid():N}.json");
        LlmConfig.TestConfigPathOverride = _tempPath;
    }

    public void Dispose()
    {
        LlmConfig.TestConfigPathOverride = null;
        TryDelete(_tempPath);
        TryDelete(_tempPath + ".lock");
        TryDelete(_tempPath + ".bak");
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    [Fact]
    public void ModifyWithLock_single_thread_round_trip()
    {
        var result = LlmConfig.ModifyWithLock(cfg =>
        {
            cfg.VisionCostGate.DailyTokenCap = 5000;
            cfg.VisionCostGate.TokensUsedToday = 1234;
            cfg.VisionCostGate.LastResetUtc = "2026-05-19";
        });

        Assert.Equal(5000, result.VisionCostGate.DailyTokenCap);
        Assert.Equal(1234, result.VisionCostGate.TokensUsedToday);
        // 재 load 결과도 동일 박제 (disk persist 정합).
        var reloaded = LlmConfig.LoadFrom(_tempPath);
        Assert.Equal(1234, reloaded.VisionCostGate.TokensUsedToday);
    }

    [Fact]
    public void ModifyWithLock_concurrent_increments_all_accumulate()
    {
        // 4 thread × 50 increment = 200 의 누적 (정확). lock 미동작 시 race 로 일부 손실 (< 200).
        // 부하 ↓ 의도 — backoff cap (5회 / max 3.1s) 안에서 모든 thread 가 lock 획득 가능 정합.
        // 본 fact 의 SSOT = "총합 일치" — race 차단 검증. extreme 부하 SSOT 는 별 turn (정책 의도 외).
        const int threadCount = 4;
        const int perThread = 50;
        const int total = threadCount * perThread;

        // 초기 박제.
        LlmConfig.ModifyWithLock(cfg =>
        {
            cfg.VisionCostGate.DailyTokenCap = 1_000_000;
            cfg.VisionCostGate.TokensUsedToday = 0;
        });

        var tasks = new Task[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    LlmConfig.ModifyWithLock(cfg =>
                    {
                        cfg.VisionCostGate.TokensUsedToday += 1;
                    });
                }
            });
        }
        Task.WaitAll(tasks);

        var final = LlmConfig.LoadFrom(_tempPath);
        Assert.Equal(total, final.VisionCostGate.TokensUsedToday);
    }

    [Fact]
    public void ModifyWithLock_lock_throws_on_argument_null()
    {
        Assert.Throws<ArgumentNullException>(() => LlmConfig.ModifyWithLock(null!));
    }

    /// <summary>**R8-M5 (s6-r76, external review backlog)** — Save() 가 cross-process file lock 통과
    /// (이전: in-process lock 만). EffectiveConfigPath 박제 정합 — TestConfigPathOverride 가 production
    /// path 침투 차단. lock 파일 생성 박제.</summary>
    [Fact]
    public void Save_uses_effective_path_and_file_lock()
    {
        var cfg = new LlmConfig();
        cfg.VisionCostGate.DailyTokenCap = 7777;
        cfg.Save();
        Assert.True(File.Exists(_tempPath), "Save() 가 EffectiveConfigPath 박제 (override 통과)");
        // WithFileLock 진입 자체가 lock file 을 OpenOrCreate — 호출 후 파일 잔존 (released 상태).
        Assert.True(File.Exists(_tempPath + ".lock"), "cross-process file lock 파일 박제");
        var reloaded = LlmConfig.LoadFrom(_tempPath);
        Assert.Equal(7777, reloaded.VisionCostGate.DailyTokenCap);
    }

    [Fact]
    public void ModifyWithLock_cap_disk_SSOT_preserved_on_delta_accumulate_pattern()
    {
        // 시나리오: process A 가 cap=5000 박제 후 token 100 누적.
        // 동시에 process B (본 fact 가 직접 ModifyWithLock 로 시뮬레이션) 가 cap=10000 으로 변경.
        // 본 fact 가 검증: KbManagerDialog 의 delta 누적 패턴 정합 — caller 는 cap 을 mutate 안 하고
        // TokensUsedToday 만 delta 합산하면 cap 은 disk SSOT (latest 의 값) 보존.

        // 초기 cap=5000, used=100.
        LlmConfig.ModifyWithLock(cfg =>
        {
            cfg.VisionCostGate.DailyTokenCap = 5000;
            cfg.VisionCostGate.TokensUsedToday = 100;
        });

        // process B: cap=10000 로 변경 (used 무관).
        LlmConfig.ModifyWithLock(cfg =>
        {
            cfg.VisionCostGate.DailyTokenCap = 10000;
        });

        // process A 가 늦게 finally 진입 — delta 50 누적 (used 100 → 150) 패턴.
        // 핵심: cap 은 mutate 안 함, used 만 delta 합산.
        LlmConfig.ModifyWithLock(cfg =>
        {
            cfg.VisionCostGate.TokensUsedToday += 50;   // delta 누적 (KbManagerDialog finally 패턴)
        });

        var final = LlmConfig.LoadFrom(_tempPath);
        // cap 은 B 의 변경 보존, used 는 A 의 delta 누적.
        Assert.Equal(10000, final.VisionCostGate.DailyTokenCap);
        Assert.Equal(150, final.VisionCostGate.TokensUsedToday);
    }
}
