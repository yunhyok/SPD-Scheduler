using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class ProcessIdentityTests
{
    private static readonly Func<ProcessIdentity, (bool Alive, bool Readable, string? Detail)> State =
        typeof(WindowsProcessProbe).GetMethod("IdentityState", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<ProcessIdentity, (bool, bool, string?)>>();
    private static readonly Func<ProcessIdentity, Func<bool>, Func<DateTimeOffset>, Func<string>, (bool Alive, bool Readable, string? Detail)> Evaluate =
        typeof(WindowsProcessProbe).GetMethod("EvaluateIdentity", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<ProcessIdentity, Func<bool>, Func<DateTimeOffset>, Func<string>, (bool, bool, string?)>>();
    private static readonly Func<int, int, ProcessSample?> Read = typeof(WindowsProcessProbe)
        .GetMethod("ReadSample", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<int, int, ProcessSample?>>();
    private static readonly ProcessIdentity Synthetic = new(123, DateTimeOffset.UnixEpoch.AddDays(1), @"C:\SyntheticOnly\solver.exe");

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("Pinned native identity matches this test process and rejects reused identities", CurrentProcess);
        yield return Test("An absent synthetic PID is readable and no longer alive", AbsentProcess);
        yield return Test("A reused PID is rejected before querying its inaccessible image", ReusedProcess);
        yield return Test("Exit racing image reads is resolved only when exit is proven", ExitedDuringRead);
        yield return Test("Unreadable live identities retain native errors and block progress", UnreadableProcess);
        yield return Test("Identity remains readable when Windows denies SYNCHRONIZE but allows limited query", LimitedQueryAccess);
        yield return Test("Exit code 259 is not mistaken for a live process when waiting is allowed", StillActiveExitCode);
    }

    private static (string, Func<Task>) Test(string name, Action action) =>
        (name, () => { action(); return Task.CompletedTask; });

    private static void CurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        var sample = Read(process.Id, 0)!;
        Check.That(sample.PrivateBytes > 0 && sample.CpuSeconds >= 0 && sample.IoBytes >= sample.OtherIoBytes,
            "Limited-query handle must return usable counters");
        var identity = sample.Identity;
        Check.Equal(new DateTimeOffset(process.StartTime.ToUniversalTime()), identity.StartedAt, "Native and persisted .NET creation timestamps must agree");
        Check.Equal(Path.GetFullPath(Environment.ProcessPath!), identity.ImagePath, "Limited-query image path must match the test apphost");
        var current = State(identity);
        Check.That(current.Alive && current.Readable, "This test process identity must be readable and alive");
        foreach (var different in new[] { identity with { StartedAt = identity.StartedAt.AddTicks(-1) },
            identity with { ImagePath = @"C:\SyntheticOnly\different.exe" } })
        {
            var state = State(different);
            Check.That(!state.Alive && state.Readable, "Readable creation/image mismatches must prove the old identity is gone");
        }
    }

    private static void AbsentProcess()
    {
        var state = State(Synthetic with { Pid = int.MaxValue });
        Check.That(!state.Alive && state.Readable, "A nonexistent PID must not become an unreadable live identity");
    }

    private static void ReusedProcess()
    {
        var state = Evaluate(Synthetic, () => false, () => Synthetic.StartedAt.AddSeconds(1),
            () => throw new InvalidOperationException("A reused process image must never be queried"));
        Check.That(!state.Alive && state.Readable && state.Detail!.Contains("reused"), "Creation mismatch alone proves PID reuse even if replacement image access would fail");
    }

    private static void ExitedDuringRead()
    {
        foreach (Exception error in new Exception[] { new Win32Exception(299), new IOException("Image unavailable"), new InvalidOperationException("Process exited") })
        {
            int checks = 0;
            var state = Evaluate(Synthetic, () => ++checks > 1, () => Synthetic.StartedAt, () => throw error);
            Check.That(!state.Alive && state.Readable, "The same pinned handle proving exit must recover a racing image failure");
        }
        var exited = Evaluate(Synthetic, () => true, () => throw new Exception("Exited process time must not be queried"),
            () => throw new Exception("Exited process image must not be queried"));
        Check.That(!exited.Alive && exited.Readable, "A signaled handle must be sufficient to establish exit");
    }

    private static void UnreadableProcess()
    {
        var state = Evaluate(Synthetic, () => false, () => Synthetic.StartedAt, () => throw new Win32Exception(5));
        Check.That(!state.Alive && !state.Readable, "Access denied on a matching live identity must stay unknown");
        Check.That(state.Detail!.Contains("PID 123") && state.Detail.Contains("image path") && state.Detail.Contains("Win32 5"),
            "An unreadable identity must name the PID, failing stage and native error");
        var time = Evaluate(Synthetic, () => false, () => throw new Win32Exception(5), () => Synthetic.ImagePath);
        Check.That(!time.Readable && time.Detail!.Contains("creation time"), "Unavailable creation time must never prove absence or reuse");
        var wait = Evaluate(Synthetic, () => throw new Win32Exception(5), () => Synthetic.StartedAt, () => Synthetic.ImagePath);
        Check.That(!wait.Readable && wait.Detail!.Contains("exit state"), "Failure to read the exit state must stay unknown");
    }

    private static void LimitedQueryAccess()
    {
        using var process = Process.GetCurrentProcess();
        var identity = Read(process.Id, 0)!.Identity;
        using var handle = OpenProcess(0x60000, false, process.Id); // Preserve a READ_CONTROL/WRITE_DAC handle before restricting this test process.
        Check.That(!handle.IsInvalid, "Test process security must be readable and restorable");
        GetKernelObjectSecurity(handle, 4, null, 0, out uint needed);
        var original = new byte[needed];
        Check.That(GetKernelObjectSecurity(handle, 4, original, needed, out _), "Back up the test process DACL");
        var restricted = new RawSecurityDescriptor("D:(D;;0x100000;;;WD)(A;;0x1000;;;WD)");
        var descriptor = new byte[restricted.BinaryLength];
        restricted.GetBinaryForm(descriptor, 0);
        Check.That(OpenProcessToken(new IntPtr(-1), 0x28, out var token), "Open this test process privilege token");
        using var ownedToken = token;
        Check.That(LookupPrivilegeValue(null, "SeDebugPrivilege", out var debug), "Find the privilege that can bypass process DACLs");
        var disabledDebug = new TokenPrivilege { Count = 1, Luid = debug };
        Check.That(AdjustTokenPrivileges(token, false, ref disabledDebug, Marshal.SizeOf<TokenPrivilege>(), out var previousDebug, out _),
            "Disable debug privilege for the access-denied reproduction");
        try
        {
            Check.That(SetKernelObjectSecurity(handle, 4, descriptor), "Restrict only this test process");
            using var denied = OpenProcess(0x101000, false, process.Id);
            Check.That(denied.IsInvalid && Marshal.GetLastWin32Error() == 5, "Reproduce the former OpenProcess access-denied request");
            var state = State(identity);
            Check.That(state.Alive && state.Readable, "Limited query access must still verify the exact live identity");
            var reused = State(identity with { StartedAt = identity.StartedAt.AddTicks(-1) });
            Check.That(!reused.Alive && reused.Readable, "Reject the retired identity even when synchronization access is denied");
        }
        finally
        {
            try { Check.That(SetKernelObjectSecurity(handle, 4, original), "Restore the original test process DACL"); }
            finally
            {
                if (previousDebug.Count != 0)
                    Check.That(AdjustTokenPrivileges(token, false, ref previousDebug, Marshal.SizeOf<TokenPrivilege>(), out _, out _),
                        "Restore the test process debug privilege");
            }
        }
    }

    private static void StillActiveExitCode()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        using var process = Process.Start(start)!;
        var identity = Read(process.Id, 0)!.Identity;
        process.StandardInput.WriteLine("exit 259");
        Check.That(process.WaitForExit(5_000), "Synthetic process must exit promptly");
        Check.Equal(259, process.ExitCode, "Exercise the reserved STILL_ACTIVE value as an actual exit code");
        var state = State(identity);
        Check.That(!state.Alive && state.Readable, "A signaled process is exited regardless of its exit code");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeProcessHandle handle, uint info, byte[]? descriptor, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeProcessHandle handle, uint info, byte[] descriptor);
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivilege state, int length, out TokenPrivilege previous, out int required);
}
