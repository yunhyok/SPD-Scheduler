using System.Reflection;
using SPDScheduler.Core;
using SPDScheduler.Windows;

namespace SPDScheduler.Tests;

internal static class ProcessTreeTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("Reused parent PID cannot adopt older unrelated processes", OlderUnrelatedChild);
        yield return Test("Validated descendants include every generation", ValidDescendants);
        yield return Test("Tracked descendants survive parent exit but never PID reuse", TrackedOrphan);
        yield return Test("Unreadable roots and children keep the process snapshot incomplete", UnreadableCandidates);
        yield return Test("Only system error reporters are omitted from PowerDC process trees", WindowsErrorReporting);
        yield return Test("A root name from a stale enumeration cannot adopt a reused PID", ReusedRoot);
        yield return Test("An exited candidate is removed without adopting its children", ExitedCandidate);
        yield return Test("Active status always overrides a stale completion label", MixedStatus);
    }

    private static (string, Func<Task>) Test(string name, Action test) =>
        (name, () => { test(); return Task.CompletedTask; });

    private static ProcessSample Sample(int pid, int parent, int started, string image = "solver.exe") =>
        new(new ProcessIdentity(pid, Epoch.AddSeconds(started), image), parent, 0, 1024, 0);

    private static ProcessSnapshot Select(WindowsProcessProbe probe, ProcessSample[] samples,
        Func<int, ProcessSample?>? reader = null, Dictionary<int, (int Parent, string Name)>? rows = null)
    {
        rows ??= samples.ToDictionary(sample => sample.Identity.Pid,
            sample => (sample.ParentPid, Path.GetFileName(sample.Identity.ImagePath)));
        reader ??= pid => samples.Single(sample => sample.Identity.Pid == pid);
        return (ProcessSnapshot)typeof(WindowsProcessProbe).GetMethod("SelectProcesses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(probe, [rows, reader, CancellationToken.None])!;
    }

    private static void OlderUnrelatedChild()
    {
        var root = Sample(100, 1, 20, "PowerSI.exe");
        var unrelated = Sample(101, 100, 10);
        var unrelatedChild = Sample(102, 101, 30);
        var snapshot = Select(new(), [root, unrelated, unrelatedChild], pid =>
        {
            Check.That(pid != 102, "An excluded branch must not be inspected or included");
            return pid == 100 ? root : unrelated;
        });
        Check.That(snapshot.Complete, "A proven stale parent relationship is not an unknown state");
        Check.Equal(root, snapshot.Processes.Single(), "Only the actual PowerSI root belongs to the tree");
    }

    private static void ValidDescendants()
    {
        var root = Sample(100, 1, 20, "PowerSI.exe");
        var child = Sample(101, 100, 20);
        var grandchild = Sample(102, 101, 30);
        var reads = new Dictionary<int, int>();
        var samples = new[] { grandchild, child, root, Sample(999, 1, 0) };
        var snapshot = Select(new(), samples, pid =>
        {
            Check.That(pid != 999, "Unrelated protected processes must not make the snapshot incomplete");
            reads[pid] = reads.GetValueOrDefault(pid) + 1;
            return samples.Single(sample => sample.Identity.Pid == pid);
        });
        Check.That(snapshot.Complete && snapshot.Processes.Count == 3, "Every valid generation should be included regardless of enumeration order");
        Check.That(reads.Values.All(count => count == 1), "Resource counters must be sampled once per capture");
    }

    private static void TrackedOrphan()
    {
        var probe = new WindowsProcessProbe();
        var root = Sample(100, 1, 10, "PowerSI.exe");
        var child = Sample(101, 100, 20);
        Select(probe, [root, child]);
        Check.Equal(child, Select(probe, [child]).Processes.Single(), "A validated live child must remain observed after its parent exits");
        var reused = Sample(101, 1, 40, "unrelated.exe");
        var snapshot = Select(probe, [reused, Sample(102, 101, 50)]);
        Check.That(snapshot.Complete && snapshot.Processes.Count == 0, "A reused child PID must lose its old ownership and descendants");
    }

    private static void UnreadableCandidates()
    {
        var root = Sample(100, 1, 10, "PowerSI.exe");
        var child = Sample(101, 100, 20);
        foreach (int unreadable in new[] { 100, 101 })
        {
            var snapshot = Select(new(), [root, child], pid =>
                pid == unreadable ? throw new IOException("Synthetic unreadable process") : root);
            Check.That(!snapshot.Complete, "An unreadable root or reachable child must block launch");
            Check.That(snapshot.Error?.Contains($"PID {unreadable}") == true && snapshot.Error.Contains("Synthetic unreadable process"),
                "The operator can identify which process failed and why");
        }
        var probe = new WindowsProcessProbe();
        Select(probe, [root, child]);
        Check.That(!Select(probe, [child], _ => throw new IOException("Synthetic unreadable orphan")).Complete,
            "An unreadable known orphan must block launch");
        Check.Equal(child, Select(probe, [child]).Processes.Single(), "Temporary unreadability must preserve the known orphan identity");
    }

    private static void WindowsErrorReporting()
    {
        var method = typeof(WindowsProcessProbe).GetMethod("IsWindowsErrorReporter", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Reporter(string path) => (bool)method.Invoke(null, [path])!;
        var dc = Sample(100, 1, 10, "PowerDC.exe");
        var wer = Sample(101, 100, 11, Path.Combine(Environment.SystemDirectory, "WerFault.exe"));
        var diagnostic = Sample(102, 101, 12, "protected.exe");
        var solver = Sample(103, 100, 13, @"C:\Cadence\WerFault.exe");
        ProcessSample[] all = [dc, wer, diagnostic, solver];
        var snapshot = Select(new(), all, pid =>
        {
            if (pid == 102) throw new UnauthorizedAccessException("No access to the reporting subtree");
            var sample = all.Single(item => item.Identity.Pid == pid);
            return Reporter(sample.Identity.ImagePath) ? null : sample;
        });
        Check.That(Reporter(Path.Combine(Environment.SystemDirectory, "WerFaultSecure.exe")) &&
            snapshot.Complete && snapshot.Processes.SequenceEqual(new[] { dc, solver }),
            "The GUI and non-system lookalike remain monitored, including when a report's own children are protected");
        var gate = new IdleGate();
        var settings = new AppSettings();
        Check.That(!gate.Evaluate(snapshot, settings).CanStart, "An open external GUI still needs the full idle interval");
        var quiet = snapshot with { CapturedAt = snapshot.CapturedAt.AddSeconds(settings.IdleSeconds) };
        Check.That(gate.Evaluate(quiet, settings).CanStart &&
            gate.Confirm(quiet with { CapturedAt = quiet.CapturedAt.AddMilliseconds(1) }, settings).CanStart,
            "Error reporting alone must not block launch after verified idle and a final recheck");
    }

    private static void ReusedRoot()
    {
        var reused = Sample(100, 1, 30, "unrelated.exe");
        var rows = new Dictionary<int, (int Parent, string Name)>
        { [100] = (1, "PowerSI.exe"), [101] = (100, "solver.exe") };
        var snapshot = Select(new(), [reused], pid =>
        {
            Check.Equal(100, pid, "A reused non-PowerSI root must not seed traversal");
            return reused;
        }, rows);
        Check.That(snapshot.Complete && snapshot.Processes.Count == 0, "Root image must be validated against the live identity");
    }

    private static void ExitedCandidate()
    {
        var root = Sample(100, 1, 10, "PowerSI.exe");
        var child = Sample(101, 100, 20);
        var snapshot = Select(new(), [root, child], _ => null);
        Check.That(snapshot.Complete && snapshot.Processes.Count == 0, "A confirmed exited root cannot seed a live tree");
    }

    private static void MixedStatus()
    {
        ProcessSignal Classify(string status) => (ProcessSignal)typeof(WindowsProcessProbe)
            .GetMethod("ClassifyStatus", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [status])!;
        Check.Equal(ProcessSignal.Completed, Classify("Simulation Done.\r\n"), "An exact completion label should remain completed");
        Check.Equal(ProcessSignal.Running, Classify("Simulation Done.\nSimulation running"), "Fresh active status must override stale done text");
        Check.Equal(ProcessSignal.Suspended, Classify("Simulation Done.\nSimulation is suspended."), "Suspension must block even with a done label");
        Check.Equal(ProcessSignal.LicenseWaiting, Classify("Simulation Done.\nWaiting for license"), "License waiting must block even with a done label");
    }
}
