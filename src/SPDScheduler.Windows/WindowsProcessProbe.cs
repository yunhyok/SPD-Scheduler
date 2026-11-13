using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SPDScheduler.Core;

namespace SPDScheduler.Windows;

public sealed class WindowsProcessProbe : IProcessProbe
{
    private readonly Dictionary<int, ProcessIdentity> tracked = [];
    public static int LogicalProcessorCount => Math.Max(1, (int)GetActiveProcessorCount(ushort.MaxValue));

    public async Task<ProcessSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var snapshot = await Task.Run(() => Capture(cancellationToken), cancellationToken).ConfigureAwait(false);
        var samples = snapshot.Processes.ToArray();
        for (int i = 0; i < samples.Length; i++)
        {
            if (!IsSimulationGui(samples[i].Identity.ImagePath)) continue;
            var status = await WindowsTextWorker.ReadAsync(samples[i].Identity, cancellationToken).ConfigureAwait(false);
            var signal = ClassifyStatus(status);
            samples[i] = samples[i] with { Signal = signal, Evidence = StatusEvidence(status) };
        }
        var disk = await WindowsDiskProbe.CaptureAsync(cancellationToken).ConfigureAwait(false);
        // Disk sampling waits one second; refresh inventory so a newly opened GUI cannot slip through.
        var refreshed = await Task.Run(() => Capture(cancellationToken), cancellationToken).ConfigureAwait(false);
        var statusSamples = samples.ToDictionary(sample => sample.Identity);
        samples = refreshed.Processes.Select(sample => statusSamples.TryGetValue(sample.Identity, out var status)
            ? sample with { Signal = sample.Signal == ProcessSignal.None ? status.Signal : sample.Signal, Evidence = sample.Evidence ?? status.Evidence }
            : sample).ToArray();
        return refreshed with { Processes = samples, Complete = snapshot.Complete && refreshed.Complete,
            Error = snapshot.Error ?? refreshed.Error, ObservationDuration = DateTimeOffset.UtcNow - snapshot.CapturedAt,
            DiskActivePercent = disk.ActivePercent, DiskActivityDetail = disk.Detail };
    }

    internal static ProcessSignal ClassifyStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return ProcessSignal.None;
        if (status.Contains("suspended", StringComparison.OrdinalIgnoreCase)) return ProcessSignal.Suspended;
        if (status.Contains("license", StringComparison.OrdinalIgnoreCase)) return ProcessSignal.LicenseWaiting;
        var lines = status.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Any(line => line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Simulation failed", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Solver failed", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Insufficient memory", StringComparison.OrdinalIgnoreCase))) return ProcessSignal.Error;
        if (lines.Any(line => line.Equals("Busy", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("Busy.", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Simulation", StringComparison.OrdinalIgnoreCase) &&
            !line.Equals("Simulation Done.", StringComparison.Ordinal))) return ProcessSignal.Running;
        if (lines.Any(line => line.Equals("Simulation Done.", StringComparison.Ordinal))) return ProcessSignal.Completed;
        return ProcessSignal.None;
    }

    internal static string StatusEvidence(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "상태 표시줄 읽기 실패/지원되지 않음";
        var clean = new string(status.Where(character => !char.IsControl(character) || character == '\n').ToArray());
        var value = string.Join('\n', clean.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(16));
        return value.Length > 1024 ? value[..1024] : value;
    }

    private ProcessSnapshot Capture(CancellationToken cancellationToken)
    {
        lock (tracked)
        {
            var rows = new Dictionary<int, (int Parent, string Name)>();
            var handle = CreateToolhelp32Snapshot(2, 0);
            if (handle == new IntPtr(-1)) return new(DateTimeOffset.UtcNow, [], false, "Process enumeration failed.", LogicalProcessorCount: LogicalProcessorCount);
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                if (!Process32First(handle, ref entry)) return new(DateTimeOffset.UtcNow, [], false, "Process enumeration failed.", LogicalProcessorCount: LogicalProcessorCount);
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Pid <= int.MaxValue && entry.ParentPid <= int.MaxValue)
                        rows[(int)entry.Pid] = ((int)entry.ParentPid, entry.Name);
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
                } while (Process32Next(handle, ref entry));
                if (Marshal.GetLastWin32Error() != 18) return new(DateTimeOffset.UtcNow, [], false, "Process enumeration incomplete.", LogicalProcessorCount: LogicalProcessorCount);
            }
            finally { CloseHandle(handle); }

            return SelectProcesses(rows, pid => ReadSample(pid, rows[pid].Parent), cancellationToken);
        }
    }

    internal ProcessSnapshot SelectProcesses(IReadOnlyDictionary<int, (int Parent, string Name)> rows,
        Func<int, ProcessSample?> readSample, CancellationToken cancellationToken)
    {
        var read = new Dictionary<int, ProcessSample?>();
        var selected = new Dictionary<int, ProcessSample>();
        string? error = null;
        foreach (var old in tracked.Values.ToArray())
        {
            if (!rows.ContainsKey(old.Pid)) { tracked.Remove(old.Pid); continue; }
            var sample = Read(old.Pid);
            if (sample != null && SameIdentity(old, sample.Identity)) selected[old.Pid] = sample;
            else if (sample != null) tracked.Remove(old.Pid);
        }
        foreach (var row in rows.Where(pair => IsSimulationGui(pair.Value.Name)))
        {
            var sample = Read(row.Key);
            if (sample != null && IsSimulationGui(sample.Identity.ImagePath)) selected[row.Key] = sample;
        }
        bool added;
        do
        {
            added = false;
            foreach (var row in rows)
            {
                if (selected.ContainsKey(row.Key) || !selected.TryGetValue(row.Value.Parent, out var parent)) continue;
                var sample = Read(row.Key);
                // A surviving process can retain the PID of an exited parent that Windows has since reused.
                if (sample != null && sample.Identity.StartedAt >= parent.Identity.StartedAt)
                { selected[row.Key] = sample; added = true; }
            }
        } while (added);
        foreach (var sample in selected.Values) tracked[sample.Identity.Pid] = sample.Identity;
        return new(DateTimeOffset.UtcNow, selected.Values.OrderBy(sample => sample.Identity.Pid).ToArray(), error == null,
            error, LogicalProcessorCount: LogicalProcessorCount);

        ProcessSample? Read(int pid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read.TryGetValue(pid, out var cached)) return cached;
            ProcessSample? sample = null;
            try
            {
                sample = readSample(pid);
                if (sample == null) tracked.Remove(pid);
                else if (Path.GetFileName(sample.Identity.ImagePath).Equals("PdcMesh.exe", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(sample.Identity.ImagePath).Equals("PdcSolver.exe", StringComparison.OrdinalIgnoreCase))
                    sample = sample with { Signal = ProcessSignal.Running,
                        Evidence = $"PowerDC 해석 대기 · {Path.GetFileName(sample.Identity.ImagePath)} (PID {pid}) 실행 중" };
            }
            catch (ArgumentException) { tracked.Remove(pid); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or OverflowException)
            {
                error ??= $"프로세스 조회 대기 · {rows[pid].Name} (PID {pid}) · " +
                    (ex is Win32Exception native ? $"Win32 {native.NativeErrorCode}: " : "") + ex.Message;
            }
            read[pid] = sample;
            return sample;
        }
    }

    internal static bool IsSimulationGui(string name) => IsPowerSi(name) || IsPowerDc(name);

    internal static bool IsPowerDc(string name) =>
        Path.GetFileNameWithoutExtension(name).Equals("powerdc", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPowerSi(string name) =>
        Path.GetFileNameWithoutExtension(name).Equals("powersi", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(name).Equals("pwrsi", StringComparison.OrdinalIgnoreCase);

    internal static bool IsWindowsErrorReporter(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        string file = Path.GetFileName(path);
        if (!file.Equals("WerFault.exe", StringComparison.OrdinalIgnoreCase) &&
            !file.Equals("WerFaultSecure.exe", StringComparison.OrdinalIgnoreCase)) return false;
        string? directory = Path.GetDirectoryName(path);
        return string.Equals(directory, Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directory, Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessSample? ReadSample(int pid, int parent)
    {
        // Process.Handle asks for full access, even when Windows permits reading all the required counters.
        using var handle = OpenProcess(0x1000, false, pid);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 87) return null;
            throw new Win32Exception(error);
        }
        try
        {
            string path = ReadImagePath(handle);
            // Windows can attach a protected reporting subtree to a faulted GUI. Its work is not solver activity.
            if (IsWindowsErrorReporter(path)) return null;
            if (!GetProcessTimes(handle, out long created, out _, out long kernel, out long user) ||
                !GetProcessIoCounters(handle, out var io) ||
                !K32GetProcessMemoryInfo(handle, out var memory, (uint)Marshal.SizeOf<MemoryCounters>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var identity = new ProcessIdentity(pid, new DateTimeOffset(DateTime.FromFileTimeUtc(created)), path);
            if (!GetExitCodeProcess(handle, out uint exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (exitCode != 259) return null;
            return new(identity, parent, TimeSpan.FromTicks(kernel + user).TotalSeconds, checked((long)memory.PrivateUsage),
                checked(io.ReadBytes + io.WriteBytes + io.OtherBytes), OtherIoBytes: io.OtherBytes);
        }
        catch (Win32Exception) when (GetExitCodeProcess(handle, out uint code) && code != 259) { return null; }
    }

    internal static bool SameIdentity(ProcessIdentity first, ProcessIdentity second) =>
        first.Pid == second.Pid && first.StartedAt == second.StartedAt &&
        string.Equals(first.ImagePath, second.ImagePath, StringComparison.OrdinalIgnoreCase);

    internal static bool IdentityMatches(ProcessIdentity identity)
        => IdentityState(identity).Alive;

    internal static (bool Alive, bool Readable, string? Detail) IdentityState(ProcessIdentity identity)
    {
        // Identity reads need limited query access; SYNCHRONIZE can be denied on an otherwise readable PID.
        using var handle = OpenProcess(0x1000, false, identity.Pid);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 87) return (false, true, $"PID {identity.Pid}: process no longer exists.");
            return (false, false, IdentityFailure(identity, "OpenProcess", new Win32Exception(error)));
        }
        return EvaluateIdentity(identity, () => HasExited(handle, identity.Pid), () => ReadCreationTime(handle), () => ReadImagePath(handle));
    }

    internal static (bool Alive, bool Readable, string? Detail) EvaluateIdentity(ProcessIdentity identity,
        Func<bool> hasExited, Func<DateTimeOffset> readCreationTime, Func<string> readImagePath)
    {
        string stage = "exit state";
        try
        {
            if (hasExited()) return (false, true, $"PID {identity.Pid}: process exited.");
            stage = "creation time";
            if (readCreationTime() != identity.StartedAt) return (false, true, $"PID {identity.Pid}: PID was reused.");
            stage = "image path";
            bool matches = string.Equals(identity.ImagePath, readImagePath(), StringComparison.OrdinalIgnoreCase);
            stage = "exit state";
            if (hasExited()) return (false, true, $"PID {identity.Pid}: process exited during identity read.");
            return (matches, true, matches ? null : $"PID {identity.Pid}: process image changed.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            // A failed image/time query may race normal process exit; only the pinned handle can prove it.
            try { if (hasExited()) return (false, true, $"PID {identity.Pid}: process exited during identity read."); }
            catch (Exception retry) when (retry is Win32Exception or IOException or InvalidOperationException or ArgumentException) { }
            return (false, false, IdentityFailure(identity, stage, exception));
        }
    }

    private static string IdentityFailure(ProcessIdentity identity, string stage, Exception exception) =>
        $"PID {identity.Pid} ({Path.GetFileName(identity.ImagePath)}), {stage}: " +
        (exception is Win32Exception native ? $"Win32 {native.NativeErrorCode}: " : "") + exception.Message;

    private static bool HasExited(SafeProcessHandle handle, int pid)
    {
        if (!GetExitCodeProcess(handle, out uint code)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (code != 259) return true;
        // A wait distinguishes exit code 259 from STILL_ACTIVE, but must not require extra access for identity reads.
        using var wait = OpenProcess(0x100000, false, pid);
        // shortcut: a query-only process exiting with code 259 stays unconfirmed until its PID disappears; add a stronger exit signal if a solver uses it.
        return !wait.IsInvalid && WaitForSingleObject(wait, 0) == 0;
    }

    private static DateTimeOffset ReadCreationTime(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out long creation, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new DateTimeOffset(DateTime.FromFileTimeUtc(creation));
    }

    private static string ReadImagePath(SafeProcessHandle handle)
    {
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Path.GetFullPath(path.ToString());
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        internal uint Size, PageFaultCount;
        internal nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonpagedPool, NonpagedPool,
            Pagefile, PeakPagefile, PrivateUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort groupNumber);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, out MemoryCounters counters, uint size);
}
