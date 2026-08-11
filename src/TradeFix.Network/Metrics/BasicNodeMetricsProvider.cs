using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TradeFix.Shared.Models;

namespace TradeFix.Network.Metrics;

/// <summary>
/// Best-effort local machine metrics using Win32 memory/CPU-time APIs — deliberately NOT
/// <c>System.Diagnostics.PerformanceCounter</c> (removed 2026-08-11; see below).
///
/// This class used to read CPU% via <c>PerformanceCounter("Processor", "% Processor Time",
/// "_Total")</c> and GPU% via the "GPU Engine" counter category. On a real render-node PC, this
/// crashed the entire Agent process every ~9-14 seconds (right on the first post-connect
/// heartbeat) with <c>System.AccessViolationException</c> deep inside
/// <c>PerformanceCounterLib.GetPerformanceData</c> — confirmed via Windows' own "Application
/// Error"/".NET Runtime" event log entries after the app got proper crash logging (see
/// docs/PROGRESS.md). The PC's performance-counter registry data was corrupted (evidenced by
/// adjacent Microsoft-Windows-Perflib errors for an unrelated service in the same event log —
/// this is a known, fairly common Windows environmental issue, normally fixed with `lodctr /R`,
/// but not something this app can require an end user to run). Critically,
/// <c>AccessViolationException</c> is a corrupted-state exception that modern .NET (Core/5+)
/// simply does not let managed code catch — the existing <c>try/catch</c> around every
/// <c>PerformanceCounter</c> call did nothing, because the runtime never gives a catch block the
/// chance to run. The only real fix is not touching that subsystem at all, hence Win32 APIs here
/// instead: <c>GetSystemTimes</c> for CPU (kernel32, no registry/perflib involvement) and
/// <c>GlobalMemoryStatusEx</c> for RAM (already used, unaffected — it was only ever
/// PerformanceCounter that touched the corrupted subsystem). GPU utilization has no equivalent
/// non-PerformanceCounter Win32 API, so it's simply reported as 0 rather than risk the same crash
/// class recurring on another machine with corrupted perf-counter data — see
/// docs/KNOWN_LIMITATIONS.md.
/// </summary>
public sealed class BasicNodeMetricsProvider : INodeMetricsProvider
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private (ulong Idle, ulong Kernel, ulong User)? _lastCpuSample;

    public NodeMetrics Sample()
    {
        return new NodeMetrics
        {
            CpuPercent = SampleCpuPercent(),
            GpuPercent = 0,
            RamPercent = SampleRamPercent(),
            Fps = 0,
            LatencyMs = 0,
            UptimeSeconds = (long)_uptime.Elapsed.TotalSeconds
        };
    }

    /// <summary>System-wide CPU load via the delta between two <c>GetSystemTimes</c> samples
    /// (idle/kernel/user tick counts since boot). The first call after startup has no prior
    /// sample to diff against, so it reports 0 — the next call (2s later, per the heartbeat
    /// interval) has a real delta.</summary>
    private double SampleCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleTicks = ToUInt64(idle);
        var kernelTicks = ToUInt64(kernel);
        var userTicks = ToUInt64(user);

        if (_lastCpuSample is not { } last)
        {
            _lastCpuSample = (idleTicks, kernelTicks, userTicks);
            return 0;
        }

        _lastCpuSample = (idleTicks, kernelTicks, userTicks);

        var idleDelta = idleTicks - last.Idle;
        var totalDelta = (kernelTicks - last.Kernel) + (userTicks - last.User);
        if (totalDelta == 0)
        {
            return 0;
        }

        var busy = totalDelta - idleDelta;
        return Math.Clamp(busy * 100.0 / totalDelta, 0, 100);
    }

    private static ulong ToUInt64(FILETIME time) =>
        ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    private static double SampleRamPercent()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
