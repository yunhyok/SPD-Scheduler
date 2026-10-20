using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
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
    private static readonly Func<Process, ProcessIdentity> Read = typeof(WindowsProcessProbe)
        .GetMethod("ReadIdentity", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<Process, ProcessIdentity>>();
    private static readonly ProcessIdentity Synthetic = new(123, DateTimeOffset.UnixEpoch.AddDays(1), @"C:\SyntheticOnly\solver.exe");

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("Pinned native identity matches this test process and rejects reused identities", CurrentProcess);
        yield return Test("An absent synthetic PID is readable and no longer alive", AbsentProcess);
        yield return Test("A reused PID is rejected before querying its inaccessible image", ReusedProcess);
        yield return Test("Exit racing image reads is resolved only when exit is proven", ExitedDuringRead);
        yield return Test("Unreadable live identities retain native errors and block progress", UnreadableProcess);
    }

    private static (string, Func<Task>) Test(string name, Action action) =>
        (name, () => { action(); return Task.CompletedTask; });

    private static void CurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        var identity = Read(process);
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
}
