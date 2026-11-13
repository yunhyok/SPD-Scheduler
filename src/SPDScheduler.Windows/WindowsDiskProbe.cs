using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SPDScheduler.Windows;

internal static class WindowsDiskProbe
{
    internal static Task<(double ActivePercent, string Detail)> CaptureAsync(CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            nint query = 0;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Verify(PdhOpenQueryW(null, 0, out query));
                Verify(PdhAddEnglishCounterW(query, @"\PhysicalDisk(*)\% Idle Time", 0, out var counter));
                Verify(PdhCollectQueryData(query));
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                Verify(PdhCollectQueryData(query));
                uint bytes = 0;
                var status = PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out _, 0);
                if (status != 0x800007D2) Verify(status); // PDH_MORE_DATA is the expected size query result.
                if (bytes == 0) throw new InvalidOperationException("물리 디스크 카운터 없음");
                var buffer = Marshal.AllocHGlobal(checked((int)bytes));
                try
                {
                    Verify(PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out var count, buffer));
                    var items = new List<(string Name, uint Status, double IdlePercent)>();
                    for (int i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<CounterItem>(buffer + i * Marshal.SizeOf<CounterItem>());
                        items.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Status, item.Value.Value));
                    }
                    return Evaluate(items);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OverflowException)
            {
                return (double.NaN, "디스크 활성 시간 조회 실패 · " + ex.Message);
            }
            finally { if (query != 0) PdhCloseQuery(query); }
        }, cancellationToken);

    internal static (double ActivePercent, string Detail) Evaluate(IEnumerable<(string Name, uint Status, double IdlePercent)> counters)
    {
        // shortcut: guard every local disk; map project/temp volumes only if unrelated-disk waits become a problem.
        var disks = counters.Where(item => !item.Name.Equals("_Total", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (disks.Length == 0 || disks.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Status > 1 ||
            !double.IsFinite(item.IdlePercent) || item.IdlePercent < 0 || item.IdlePercent > 100))
            return (double.NaN, "물리 디스크 카운터 누락 또는 유효하지 않은 측정값");
        return (disks.Max(item => 100 - item.IdlePercent),
            string.Join(" · ", disks.Select(item => $"디스크 {item.Name}: {100 - item.IdlePercent:F1}%")));
    }

    private static void Verify(uint status)
    {
        if (status != 0) throw new Win32Exception(unchecked((int)status), $"PDH 0x{status:X8}");
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public CounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes, out uint count, nint buffer);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
