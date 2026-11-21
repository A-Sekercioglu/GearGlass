using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GearGlass.Services;

/// <summary>
/// Reads CPU (GetSystemTimes), memory (GlobalMemoryStatusEx) and GPU usage
/// (PDH "GPU Engine" 3D utilization counters). Not thread-safe for concurrent
/// Read calls; the HUD polls it sequentially.
/// </summary>
public sealed partial class SystemMetricsService : IDisposable
{
    private const string GpuCounterPath = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusValidData = 0x00000000;
    private const uint PdhCstatusNewData = 0x00000001;
    private const int ItemSize = 24;
    private const int ItemStatusOffset = 8;
    private const int ItemValueOffset = 16;

    private readonly object _sync = new();
    private long _previousIdle;
    private long _previousTotal;
    private nint _gpuQuery;
    private nint _gpuCounter;
    private string? _gpuStatus;
    private bool _disposed;

    public SystemMetricsService()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            _previousIdle = idle;
            _previousTotal = kernel + user;
        }

        InitializeGpuCounters();
    }

    public SystemSnapshot Read()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var cpu = ReadCpuPercent();
            var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref memory))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var gpu = ReadGpuPercent();
            return new SystemSnapshot(
                cpu,
                gpu,
                memory.TotalPhys - memory.AvailPhys,
                memory.TotalPhys,
                _gpuStatus);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_gpuQuery != nint.Zero)
            {
                PdhCloseQuery(_gpuQuery);
                _gpuQuery = nint.Zero;
                _gpuCounter = nint.Zero;
            }
        }
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // Kernel time already includes idle time.
        var total = kernel + user;
        var idleDelta = idle - _previousIdle;
        var totalDelta = total - _previousTotal;
        _previousIdle = idle;
        _previousTotal = total;

        if (totalDelta <= 0)
        {
            return 0;
        }

        return Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
    }

    private void InitializeGpuCounters()
    {
        try
        {
            if (PdhOpenQuery(null, nint.Zero, out _gpuQuery) != 0)
            {
                _gpuQuery = nint.Zero;
                _gpuStatus = "GPU sayaçları açılamadı";
                return;
            }

            if (PdhAddEnglishCounter(_gpuQuery, GpuCounterPath, nint.Zero, out _gpuCounter) != 0)
            {
                PdhCloseQuery(_gpuQuery);
                _gpuQuery = nint.Zero;
                _gpuCounter = nint.Zero;
                _gpuStatus = "GPU sayaçları desteklenmiyor";
                return;
            }

            // Rate counters need a baseline sample before the first formatted read.
            PdhCollectQueryData(_gpuQuery);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            _gpuQuery = nint.Zero;
            _gpuCounter = nint.Zero;
            _gpuStatus = "GPU sayaçları kullanılamıyor";
        }
    }

    private double? ReadGpuPercent()
    {
        if (_gpuQuery == nint.Zero || _gpuCounter == nint.Zero)
        {
            return null;
        }

        if (PdhCollectQueryData(_gpuQuery) != 0)
        {
            return null;
        }

        uint bufferSize = 0;
        var status = PdhGetFormattedCounterArray(
            _gpuCounter, PdhFmtDouble | PdhFmtNoCap100, ref bufferSize, out _, nint.Zero);
        if (status != PdhMoreData || bufferSize == 0)
        {
            // No 3D engine instances yet (e.g. nothing is rendering): report idle.
            return 0;
        }

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(
                _gpuCounter, PdhFmtDouble | PdhFmtNoCap100, ref bufferSize, out var itemCount, buffer);
            if (status != 0)
            {
                return null;
            }

            var perAdapter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < itemCount; i++)
            {
                var item = buffer + i * ItemSize;
                var counterStatus = (uint)Marshal.ReadInt32(item, ItemStatusOffset);
                if (counterStatus != PdhCstatusValidData && counterStatus != PdhCstatusNewData)
                {
                    continue;
                }

                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? string.Empty;
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, ItemValueOffset));
                var match = AdapterLuid().Match(name);
                var adapter = match.Success ? match.Value : string.Empty;
                perAdapter[adapter] = perAdapter.GetValueOrDefault(adapter) + value;
            }

            return perAdapter.Count == 0 ? 0 : Math.Clamp(perAdapter.Values.Max(), 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [GeneratedRegex(@"luid_0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+")]
    private static partial Regex AdapterLuid();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(nint query, string counterPath, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
