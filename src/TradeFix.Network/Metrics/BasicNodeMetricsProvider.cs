using System.Diagnostics;
using System.Runtime.InteropServices;
using TradeFix.Shared.Models;

namespace TradeFix.Network.Metrics;

/// <summary>
/// Best-effort local machine metrics using Windows Performance Counters and Win32 memory status.
/// PerformanceCounter's first NextValue() call after construction is unreliable (needs a warm-up
/// sample), which is why counters are created once and reused across calls to <see cref="Sample"/>
/// rather than per-call. GPU utilization uses the "GPU Engine" counter category (Windows 10+,
/// no admin rights required); if it is unavailable (older Windows, missing driver telemetry) this
/// falls back to 0 rather than throwing — see docs/KNOWN_LIMITATIONS.md.
/// </summary>
public sealed class BasicNodeMetricsProvider : INodeMetricsProvider, IDisposable
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter[]? _gpuCounters;
    private bool _initialized;

    public NodeMetrics Sample()
    {
        EnsureInitialized();

        return new NodeMetrics
        {
            CpuPercent = SafeRead(_cpuCounter),
            GpuPercent = SampleGpu(),
            RamPercent = SampleRamPercent(),
            Fps = 0,
            LatencyMs = 0,
            UptimeSeconds = (long)_uptime.Elapsed.TotalSeconds
        };
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue();
        }
        catch
        {
            _cpuCounter = null;
        }

        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames()
                .Where(name => name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            _gpuCounters = instances
                .Select(name =>
                {
                    try
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name);
                        counter.NextValue();
                        return counter;
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(c => c is not null)
                .Select(c => c!)
                .ToArray();
        }
        catch
        {
            _gpuCounters = [];
        }
    }

    private double SampleGpu()
    {
        if (_gpuCounters is not { Length: > 0 })
        {
            return 0;
        }

        return Math.Min(100, _gpuCounters.Sum(SafeRead));
    }

    private static double SafeRead(PerformanceCounter? counter)
    {
        if (counter is null)
        {
            return 0;
        }

        try
        {
            return counter.NextValue();
        }
        catch
        {
            return 0;
        }
    }

    private static double SampleRamPercent()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

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

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        if (_gpuCounters is not null)
        {
            foreach (var counter in _gpuCounters)
            {
                counter.Dispose();
            }
        }
    }
}
