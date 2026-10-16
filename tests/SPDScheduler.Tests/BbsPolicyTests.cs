using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class BbsPolicyTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity Gui = new(410, Epoch, @"C:\Synthetic\Cadence\bin\PowerSI.exe");
    private static readonly ProcessIdentity OtherGui = new(420, Epoch, @"C:\Synthetic\Cadence\bin\PowerSI.exe");
    private static readonly ProcessIdentity BbsGui = new(460, Epoch, @"C:\Synthetic\Cadence\bin\PowerSI.exe");
    private static readonly ProcessIdentity Bbs = new(430, Epoch.AddSeconds(1), @"C:\Synthetic\Cadence\bin\BbsCon.exe");
    private static readonly ProcessIdentity Console = new(440, Epoch.AddSeconds(2),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "conhost.exe"));

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("Authorized BbsCon overlap excludes only its validated resources", MeasuredOverlap);
        yield return Test("Unrelated solver, image, path and ancestry never inherit the BbsCon exemption", UnrelatedProcesses);
        yield return Test("BbsCon overlap never overrides busy GUI or missing inventory", BusyOrUnknown);
        yield return Test("Other I/O is diagnostic while read/write I/O remains a gate", OtherIo);
        yield return Test("Invalid total, other and read/write counters reset the gate", InvalidIoCounters);
        yield return Test("Only consecutive completed GUI samples exempt that GUI I/O", CompletedGuiIo);
        yield return Test("Completed GUI CPU and memory are still counted", CompletedGuiResources);
        yield return Test("Approved orphan BbsCon survives GUI exit but not PID reuse", RetainedOrphan);
        yield return Test("Final launch check uses the same BbsCon policy and protects other children", FinalConfirmation);
        yield return Test("Disabling and reenabling overlap preserves known identity evidence", TogglePolicy);
    }

    private static (string, Func<Task>) Test(string name, Action action) =>
        (name, () => { action(); return Task.CompletedTask; });

    private static ProcessSample Sample(ProcessIdentity identity, int parent, double seconds,
        double cpu = 0, double ioKiB = 0, double memoryMiBPerMinute = 0,
        ProcessSignal signal = ProcessSignal.None, double otherKiB = 0) =>
        new(identity, parent, cpu * seconds / 100, (long)((100 + memoryMiBPerMinute * seconds / 60) * 1048576),
            (ulong)((ioKiB + otherKiB) * seconds * 1024), Signal: signal,
            OtherIoBytes: (ulong)(otherKiB * seconds * 1024));

    private static ProcessSample[] Measured(double seconds) =>
    [
        Sample(Gui, 0, seconds, cpu: 6, ioKiB: 529, memoryMiBPerMinute: 9.4, signal: ProcessSignal.Completed),
        Sample(OtherGui, 0, seconds, cpu: 0.08, ioKiB: 8),
        Sample(Bbs, BbsGui.Pid, seconds, cpu: 100, ioKiB: 953, memoryMiBPerMinute: 1226.7),
        Sample(Console, Bbs.Pid, seconds, cpu: 20, ioKiB: 100, memoryMiBPerMinute: 256),
        Sample(BbsGui, 0, seconds, memoryMiBPerMinute: 9.8)
    ];

    private static ProcessSnapshot Snapshot(double seconds, ProcessSample[]? samples = null, bool complete = true) =>
        new(Epoch.AddSeconds(seconds), samples ?? Measured(seconds), complete, LogicalProcessorCount: 64);

    private static void MeasuredOverlap()
    {
        foreach (bool allow in new[] { true, false })
        {
            var gate = new IdleGate();
            var settings = new AppSettings { AllowBbsPostProcessing = allow };
            gate.Evaluate(Snapshot(0), settings);
            var decision = gate.Evaluate(Snapshot(60), settings);
            Check.Equal(allow, decision.CanStart, "User-authorized BbsCon overlap should permit the remaining measured GUI load");
            Check.That(gate.Details.Contains("관측 합계 CPU:") && gate.Details.Contains("판정 부하 CPU:"), "Observed and decision loads must both be visible");
            Check.That(gate.Details.Contains("BbsCon.exe") && gate.Details.Contains("conhost.exe"), "Exempt processes must stay visible");
            if (allow) Check.That(decision.Reason.Contains("BbsCon 후처리 병행 허용") && !decision.Reason.Contains("유휴 추정"),
                "Busy post-processing must be described as authorized overlap, not idle");
        }
    }

    private static void UnrelatedProcesses()
    {
        var variants = new[]
        {
            (new ProcessIdentity(450, Epoch.AddSeconds(3), @"C:\Synthetic\Cadence\bin\solver.exe"), Bbs.Pid),
            (Bbs with { Pid = 451, ImagePath = @"C:\Other\BbsCon.exe" }, Gui.Pid),
            (Bbs with { Pid = 452, ImagePath = @"C:\Synthetic\Cadence\bin\BbsConHelper.exe" }, Gui.Pid),
            (Bbs with { Pid = 453 }, 999),
            (Bbs with { Pid = 454, StartedAt = Epoch.AddSeconds(-1) }, Gui.Pid),
            (new ProcessIdentity(455, Epoch.AddSeconds(3), @"C:\Synthetic\Cadence\bin\PowerSI.exe"), Bbs.Pid),
            (Console with { Pid = 456, ImagePath = @"C:\Other\conhost.exe" }, Bbs.Pid),
            (Console with { Pid = 457 }, Gui.Pid)
        };
        foreach (var (identity, parent) in variants)
        {
            var gate = new IdleGate();
            var settings = new AppSettings();
            foreach (int seconds in new[] { 0, 60, 120 })
                Check.That(!gate.Evaluate(Snapshot(seconds, [.. Measured(seconds), Sample(identity, parent, seconds, cpu: 100)]), settings).CanStart,
                    "A name, invalid ancestry, other bin, nested GUI or unrelated child must remain counted");
        }
    }

    private static void BusyOrUnknown()
    {
        foreach (var signal in new[] { ProcessSignal.Running, ProcessSignal.Suspended, ProcessSignal.LicenseWaiting, ProcessSignal.Error })
        {
            var gate = new IdleGate();
            gate.Evaluate(Snapshot(0), new AppSettings());
            var samples = Measured(60); samples[0] = samples[0] with { Signal = signal };
            Check.That(!gate.Evaluate(Snapshot(60, samples), new AppSettings()).CanStart, "Explicit GUI activity must always block");
        }
        foreach (bool incomplete in new[] { true, false })
        {
            var gate = new IdleGate();
            gate.Evaluate(Snapshot(0), new AppSettings());
            var samples = Measured(60);
            if (!incomplete) samples[2] = samples[2] with { Accessible = false };
            Check.That(!gate.Evaluate(Snapshot(60, samples, !incomplete), new AppSettings()).CanStart, "Even exempt BbsCon must remain observable");
        }
    }

    private static void OtherIo()
    {
        foreach (double readWrite in new[] { 8d, 64d })
        {
            var gate = new IdleGate();
            ProcessSnapshot At(int seconds) => Snapshot(seconds, [Sample(Gui, 0, seconds, ioKiB: readWrite, otherKiB: 900)]);
            gate.Evaluate(At(0), new AppSettings());
            Check.Equal(readWrite < 64, gate.Evaluate(At(60), new AppSettings()).CanStart,
                "Large status/other I/O must not hide a real read/write threshold violation");
            Check.That(gate.Details.Contains("기타 900"), "Other I/O must remain diagnostic");
        }
    }

    private static void InvalidIoCounters()
    {
        foreach (var (total, other) in new[] { (200UL, 40UL), (200UL, 160UL), (99UL, 50UL), (100UL, 101UL) })
        {
            var gate = new IdleGate();
            var sample = Sample(Gui, 0, 0) with { IoBytes = 100, OtherIoBytes = 50 };
            gate.Evaluate(Snapshot(0, [sample]), new AppSettings());
            Check.That(!gate.Evaluate(Snapshot(60, [sample with { IoBytes = total, OtherIoBytes = other }]), new AppSettings()).CanStart,
                "Other and derived read/write counters must never wrap or roll backward");
        }
    }

    private static void CompletedGuiIo()
    {
        foreach (var (before, after, expected) in new[]
        {
            (ProcessSignal.Completed, ProcessSignal.Completed, true),
            (ProcessSignal.None, ProcessSignal.Completed, false),
            (ProcessSignal.Completed, ProcessSignal.None, false)
        })
        {
            var gate = new IdleGate();
            gate.Evaluate(Snapshot(0, [Sample(Gui, 0, 0, signal: before)]), new AppSettings());
            Check.Equal(expected, gate.Evaluate(Snapshot(60, [Sample(Gui, 0, 60, ioKiB: 529, signal: after)]), new AppSettings()).CanStart,
                "Only the same GUI's consecutive completion observations exempt its I/O");
        }
    }

    private static void CompletedGuiResources()
    {
        foreach (bool cpu in new[] { true, false })
        {
            var gate = new IdleGate();
            gate.Evaluate(Snapshot(0, [Sample(Gui, 0, 0, signal: ProcessSignal.Completed)]), new AppSettings());
            Check.That(!gate.Evaluate(Snapshot(60, [Sample(Gui, 0, 60, cpu: cpu ? 100 : 0,
                memoryMiBPerMinute: cpu ? 0 : 256, signal: ProcessSignal.Completed)]), new AppSettings()).CanStart,
                "Completed GUI CPU and memory must still obey their limits");
        }
    }

    private static void RetainedOrphan()
    {
        var gate = new IdleGate();
        var settings = new AppSettings();
        gate.Evaluate(Snapshot(0), settings);
        ProcessSample[] Orphan(int seconds) => Measured(seconds).Where(p => p.Identity != BbsGui).ToArray();
        Check.That(!gate.Evaluate(Snapshot(60, Orphan(60)), settings).CanStart, "GUI exit restarts the process-set window");
        Check.That(gate.Evaluate(Snapshot(120, Orphan(120)), settings).CanStart, "Previously validated BbsCon and its console remain authorized after GUI exit");
        var reused = Bbs with { StartedAt = Epoch.AddSeconds(150) };
        foreach (int seconds in new[] { 180, 240 })
        {
            var samples = Orphan(seconds).Select(p => p.Identity == Bbs ? p with { Identity = reused } : p).ToArray();
            Check.That(!gate.Evaluate(Snapshot(seconds, samples), settings).CanStart, "Reused PID must not inherit orphan authorization");
        }
    }

    private static void FinalConfirmation()
    {
        foreach (int change in new[] { 0, 1, 2, 3, 4 })
        {
            var gate = new IdleGate();
            var settings = new AppSettings();
            var child = new ProcessIdentity(450, Epoch.AddSeconds(3), @"C:\Synthetic\Cadence\bin\solver.exe");
            foreach (int seconds in new[] { 0, 60 })
                gate.Evaluate(Snapshot(seconds, [.. Measured(seconds), Sample(child, Gui.Pid, seconds)]), settings);
            var samples = new List<ProcessSample>(Measured(60.2)) { Sample(child, Gui.Pid, change == 1 ? 0.2 : 0, cpu: change == 1 ? 100 : 0) };
            if (change == 2) samples[2] = samples[2] with { Identity = Bbs with { StartedAt = Epoch.AddSeconds(60) } };
            if (change == 3) samples[0] = samples[0] with { Signal = ProcessSignal.Running };
            if (change == 4) samples[2] = samples[2] with { OtherIoBytes = samples[2].IoBytes + 1 };
            Check.Equal(change == 0, gate.Confirm(Snapshot(60.2, samples.ToArray()), settings).CanStart,
                "Exempt post-processing must not loop the final check; a new solver, identity, GUI activity or invalid counter still blocks");
        }
    }

    private static void TogglePolicy()
    {
        var gate = new IdleGate();
        var settings = new AppSettings();
        gate.Evaluate(Snapshot(0), settings);
        Check.That(gate.Evaluate(Snapshot(60), settings).CanStart, "Enabled overlap permits the measured case");
        settings.AllowBbsPostProcessing = false;
        Check.That(!gate.Evaluate(Snapshot(120), settings).CanStart, "Disabling immediately counts BbsCon again");
        settings.AllowBbsPostProcessing = true;
        Check.That(gate.Evaluate(Snapshot(180), settings).CanStart, "Reenabled overlap can use freshly observed low decision load");
    }
}
