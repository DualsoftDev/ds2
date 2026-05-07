using System;
using System.Diagnostics;
using System.Threading;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase 2 후속 — 결정 5.6 / 1d-5 ChildProcessTracker 회귀 테스트.
/// AddProcess 가 정상 process 에 대해 throw 없이 attach. 실제 cascade kill 은 본 process 종료가 필요해 검증 어려움.
/// </summary>
public sealed class ChildProcessTrackerTests
{
    [Fact]
    public void AddProcess_null_is_no_op()
    {
        // exception 없이 통과
        ChildProcessTracker.AddProcess(null!);
    }

    [Fact]
    public void AddProcess_already_exited_process_is_no_op()
    {
        if (!OperatingSystem.IsWindows()) return;

        var psi = new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(5000);
        Assert.True(proc.HasExited);

        // 이미 종료된 process 라도 throw 없이 silent skip
        ChildProcessTracker.AddProcess(proc);
    }

    [Fact]
    public void AddProcess_attaches_running_process_without_exception()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 1초 자고 종료하는 cmd
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var proc = Process.Start(psi)!;
        try
        {
            // attach — throw 없으면 OK (실제 Win32 attach 결과는 log 로만 노출)
            ChildProcessTracker.AddProcess(proc);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); proc.WaitForExit(2000); } catch { /* best-effort */ }
        }
    }
}
