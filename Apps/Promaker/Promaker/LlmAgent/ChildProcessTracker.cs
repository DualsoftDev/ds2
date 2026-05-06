using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using log4net;

namespace Promaker.LlmAgent;

/// <summary>
/// 1d-5 — Promaker 가 종료 / crash 시 Claude CLI 자식 process cascade kill.
///
/// 결정 5.6 / 주의 4. process-wide singleton Job Object 1개에 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 설정.
/// Promaker process 종료 시 OS 가 job 안 모든 process 강제 종료 → orphan / 좀비 방지.
///
/// 결정 4 (c) HTTP MCP 채택으로 자식 Promaker spawn 없음 → 손자 (Claude CLI 가 자체 spawn 한 process) 만 잠재적 cascade 대상.
/// Claude CLI (Node.js) 가 child 에 `CREATE_BREAKAWAY_FROM_JOB` 설정하지 않는 한 손자도 같은 job 에 머무름 — 보통 OK.
/// </summary>
public static class ChildProcessTracker
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ChildProcessTracker));

    private static readonly Lazy<IntPtr> _jobHandle = new(CreateAndConfigureJob, isThreadSafe: true);

    /// <summary>
    /// Process 를 job 에 attach. 실패 시 warn 로그만 — cascade kill 미보장 상태로 진행.
    /// </summary>
    public static void AddProcess(Process process)
    {
        if (process == null) return;
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (process.HasExited) return;
            if (!AssignProcessToJobObject(_jobHandle.Value, process.Handle))
            {
                var err = Marshal.GetLastWin32Error();
                Log.Warn($"AssignProcessToJobObject 실패 (Win32={err}) — pid={process.Id} cascade kill 미보장");
            }
            else
            {
                Log.Debug($"AssignProcessToJobObject 성공 — pid={process.Id}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"AssignProcessToJobObject 예외 — pid={process.Id}", ex);
        }
    }

    private static IntPtr CreateAndConfigureJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateJobObject 실패 (Win32={Marshal.GetLastWin32Error()})");

        var basic = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
        var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = basic };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(ext, ptr, false);
            if (!SetInformationJobObject(handle, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)size))
                throw new InvalidOperationException($"SetInformationJobObject 실패 (Win32={Marshal.GetLastWin32Error()})");
        }
        finally { Marshal.FreeHGlobal(ptr); }

        Log.Info("ChildProcessTracker job object 생성 — JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE");
        return handle;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
