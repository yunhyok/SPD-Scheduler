using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SPDScheduler.Core;

namespace SPDScheduler.Windows;

public sealed class WindowsProcessProbe : IProcessProbe
{
    private readonly Dictionary<int, ProcessIdentity> tracked = [];

    public async Task<ProcessSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var snapshot = await Task.Run(() => Capture(cancellationToken), cancellationToken).ConfigureAwait(false);
        var samples = snapshot.Processes.ToArray();
        for (int i = 0; i < samples.Length; i++)
        {
            if (!IsPowerSi(samples[i].Identity.ImagePath)) continue;
            var status = await WindowsTextWorker.ReadAsync(samples[i].Identity, cancellationToken).ConfigureAwait(false);
            var signal = ClassifyStatus(status);
            samples[i] = samples[i] with { Signal = signal, Evidence = signal == ProcessSignal.None ? null : status };
        }
        return snapshot with { Processes = samples };
    }

    internal static ProcessSignal ClassifyStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return ProcessSignal.None;
        if (status.Contains("suspended", StringComparison.OrdinalIgnoreCase)) return ProcessSignal.Suspended;
        if (status.Contains("license", StringComparison.OrdinalIgnoreCase)) return ProcessSignal.LicenseWaiting;
        if (status.Split('\n').Any(line => line.Trim().Equals("Simulation Done.", StringComparison.Ordinal))) return ProcessSignal.Completed;
        if (status.Contains("Simulation", StringComparison.OrdinalIgnoreCase)) return ProcessSignal.Running;
        return ProcessSignal.None;
    }

    private ProcessSnapshot Capture(CancellationToken cancellationToken)
    {
        lock (tracked)
        {
            var rows = new Dictionary<int, (int Parent, string Name)>();
            var handle = CreateToolhelp32Snapshot(2, 0);
            if (handle == new IntPtr(-1)) return new(DateTimeOffset.UtcNow, [], false, "Process enumeration failed.");
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                if (!Process32First(handle, ref entry)) return new(DateTimeOffset.UtcNow, [], false, "Process enumeration failed.");
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Pid <= int.MaxValue && entry.ParentPid <= int.MaxValue)
                        rows[(int)entry.Pid] = ((int)entry.ParentPid, entry.Name);
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
                } while (Process32Next(handle, ref entry));
                if (Marshal.GetLastWin32Error() != 18) return new(DateTimeOffset.UtcNow, [], false, "Process enumeration incomplete.");
            }
            finally { CloseHandle(handle); }

            var relevant = rows.Where(pair => IsPowerSi(pair.Value.Name)).Select(pair => pair.Key).ToHashSet();
            foreach (var old in tracked.Values.ToArray())
            {
                if (!rows.ContainsKey(old.Pid)) { tracked.Remove(old.Pid); continue; }
                try
                {
                    using var process = Process.GetProcessById(old.Pid);
                    if (SameIdentity(old, ReadIdentity(process))) relevant.Add(old.Pid);
                    else tracked.Remove(old.Pid);
                }
                catch (ArgumentException) { tracked.Remove(old.Pid); }
                catch (InvalidOperationException) { relevant.Add(old.Pid); }
                catch (System.ComponentModel.Win32Exception) { relevant.Add(old.Pid); }
                catch (IOException) { relevant.Add(old.Pid); }
            }
            bool added;
            do
            {
                added = false;
                foreach (var row in rows)
                    if (relevant.Contains(row.Value.Parent) && relevant.Add(row.Key)) added = true;
            } while (added);

            var samples = new List<ProcessSample>();
            var complete = true;
            foreach (int pid in relevant.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var identity = ReadIdentity(process);
                    if (process.HasExited) continue;
                    if (!GetProcessIoCounters(process.Handle, out var io)) throw new IOException("Process I/O is unavailable.");
                    samples.Add(new(identity, rows.GetValueOrDefault(pid).Parent,
                        process.TotalProcessorTime.TotalSeconds, process.PrivateMemorySize64,
                        checked(io.ReadBytes + io.WriteBytes + io.OtherBytes)));
                    tracked[pid] = identity;
                }
                catch (ArgumentException) { tracked.Remove(pid); }
                catch (InvalidOperationException) { complete = false; }
                catch (System.ComponentModel.Win32Exception) { complete = false; }
                catch (IOException) { complete = false; }
                catch (OverflowException) { complete = false; }
            }
            return new(DateTimeOffset.UtcNow, samples, complete,
                complete ? null : "A PowerSI process identity or resource counter could not be read.");
        }
    }

    internal static bool IsPowerSi(string name) =>
        Path.GetFileNameWithoutExtension(name).Equals("powersi", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(name).Equals("pwrsi", StringComparison.OrdinalIgnoreCase);

    internal static ProcessIdentity ReadIdentity(Process process) => new(process.Id,
        new DateTimeOffset(process.StartTime.ToUniversalTime()),
        Path.GetFullPath(process.MainModule?.FileName ?? throw new IOException("Process image is unavailable.")));

    internal static bool SameIdentity(ProcessIdentity first, ProcessIdentity second) =>
        first.Pid == second.Pid && first.StartedAt == second.StartedAt &&
        string.Equals(first.ImagePath, second.ImagePath, StringComparison.OrdinalIgnoreCase);

    internal static bool IdentityMatches(ProcessIdentity identity)
        => IdentityState(identity).Alive;

    internal static (bool Alive, bool Readable) IdentityState(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return (!process.HasExited && SameIdentity(identity, ReadIdentity(process)), true);
        }
        catch (ArgumentException) { return (false, true); }
        catch (InvalidOperationException) { return (false, false); }
        catch (System.ComponentModel.Win32Exception) { return (false, false); }
        catch (IOException) { return (false, false); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, Pid;
        internal UIntPtr Heap;
        internal uint Module, Threads, ParentPid;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
