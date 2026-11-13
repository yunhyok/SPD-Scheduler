using SPDScheduler.Core;
using SPDScheduler.Windows;
using System.Reflection;

namespace SPDScheduler.Tests;

internal static class DiskGateTests
{
    private static readonly Type DiskProbe = typeof(WindowsProcessProbe).Assembly.GetType("SPDScheduler.Windows.WindowsDiskProbe")!;
    private static readonly Func<IEnumerable<(string, uint, double)>, (double ActivePercent, string Detail)> EvaluateCounters =
        DiskProbe.GetMethod("Evaluate", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<IEnumerable<(string, uint, double)>, (double, string)>>();
    private static readonly Func<CancellationToken, Task<(double ActivePercent, string Detail)>> CaptureDisk =
        DiskProbe.GetMethod("CaptureAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<CancellationToken, Task<(double, string)>>>();
    private static readonly DateTimeOffset Epoch = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly AppSettings Settings = new();
    private static ProcessSnapshot At(int seconds, double active, params ProcessSample[] processes) =>
        new(Epoch.AddSeconds(seconds), processes, DiskActivePercent: active);

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return ("Physical disk saturation overrides completed GUI and BbsCon overlap", () => Run(Exemptions));
        yield return ("Busy disk does not discard validated BbsCon ancestry before GUI exit", () => Run(BusyOrphan));
        yield return ("Disk recovery survives process exit/reset and requires a fresh quiet window", () => Run(Recovery));
        yield return ("Disk preflight blocks fresh saturation and unreadable counters", () => Run(Preflight));
        yield return ("Incomplete process inventory still retains observed disk saturation", () => Run(IncompleteInventory));
        yield return ("Disk counter uses busiest physical instance and rejects invalid data", () => Run(Counters));
        yield return ("Owned completion and next launch wait for disk post-processing", () => OwnedCompletion(false));
        yield return ("Paused queue still finishes owned disk recovery without launching", () => OwnedCompletion(true));
        yield return ("A busy disk at final queue preflight cannot launch a job", LaunchPreflight);
        yield return ("Native disk capture returns valid data or an explicit unavailable result", NativeCapture);
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }

    private static void Exemptions()
    {
        var gui = new ProcessIdentity(1, Epoch, @"C:\Synthetic\PowerSI.exe");
        var bbs = new ProcessIdentity(2, Epoch.AddSeconds(1), @"C:\Synthetic\BbsCon.exe");
        ProcessSample[] samples = [new(gui, 0, 0, 100, 0, Signal: ProcessSignal.Completed), new(bbs, 1, 0, 100, 0)];
        foreach (var processes in new[] { Array.Empty<ProcessSample>(), samples[..1], samples })
        foreach (var active in new[] { 80d, 99d, double.NaN, double.PositiveInfinity, -1d, 101d })
        {
            var gate = new IdleGate();
            gate.Evaluate(At(0, 0, processes), Settings);
            Check.That(!gate.Evaluate(At(60, active, processes), Settings).CanStart, "Disk must block even with zero CPU and completion/parallel exemptions");
            Check.That(gate.Details.Contains("디스크"), "Show the blocking metric");
        }
    }

    private static void Recovery()
    {
        var gate = new IdleGate();
        Check.That(!gate.Evaluate(At(0, 99), Settings).CanStart, "Busy disk blocks an empty process list");
        gate.Reset();
        Check.That(!gate.Evaluate(At(60, 0), Settings).CanStart, "Process exit/reset cannot erase disk recovery");
        Check.That(!gate.Evaluate(At(119, 0), Settings).CanStart, "Wait the full configured idle period");
        Check.That(gate.Evaluate(At(120, 79.9), Settings).CanStart, "Quiet disk eventually releases the queue");
        gate.Evaluate(At(180, 99), Settings);
        gate.Evaluate(At(240, 0), Settings);
        Check.That(!gate.Evaluate(At(360, 0), Settings).CanStart, "Missed observation restarts recovery");
        Check.That(gate.Evaluate(At(420, 0), Settings).CanStart, "Fresh recovery after missed poll");
        gate.Evaluate(At(480, 99), Settings);
        gate.Evaluate(At(540, 0), Settings);
        Check.That(!gate.Evaluate(At(600, 99), Settings).CanStart, "Renewed load clears earlier quiet time");
        Check.That(!gate.Evaluate(At(660, 0), Settings).CanStart, "Renewed load needs a new interval");
        Check.That(gate.Evaluate(At(720, 0), Settings).CanStart, "Second recovery can finish");
    }

    private static void BusyOrphan()
    {
        var gui = new ProcessIdentity(1, Epoch, @"C:\Synthetic\PowerSI.exe");
        var bbs = new ProcessIdentity(2, Epoch.AddSeconds(1), @"C:\Synthetic\BbsCon.exe");
        ProcessSample Child(int seconds) => new(bbs, gui.Pid, seconds, 100, (ulong)seconds * 1024 * 1024);
        var gate = new IdleGate();
        gate.Evaluate(At(0, 99, new(gui, 0, 0, 100, 0), Child(0)), Settings);
        foreach (int seconds in new[] { 60, 120 }) gate.Evaluate(At(seconds, 0, Child(seconds)), Settings);
        Check.That(gate.Evaluate(At(180, 0, Child(180)), Settings).CanStart,
            "An approved orphan stays eligible for overlap once the physical disk is quiet");
    }

    private static void Preflight()
    {
        foreach (var active in new[] { 99d, double.NaN })
        {
            var gate = new IdleGate();
            Check.That(gate.Evaluate(At(0, 0), Settings).CanStart, "Idle host can qualify immediately");
            Check.That(!gate.Confirm(At(1, active), Settings).CanStart, "Final capture must use fresh disk data");
            gate.Reset();
            Check.That(!gate.Evaluate(At(60, 0), Settings).CanStart, "Failed preflight starts recovery even after engine reset");
            Check.That(gate.Evaluate(At(120, 0), Settings).CanStart, "Recovery can requalify");
        }
    }

    private static void Counters()
    {
        var result = EvaluateCounters([("_Total", 0, 90), ("0 C:", 0, 99), ("1 D:", 1, 1)]);
        Check.Equal(99d, result.ActivePercent, "An idle disk or aggregate must not dilute the busy disk");
        foreach (var bad in new[] { ("1 D:", 2U, 50d), ("1 D:", 0U, double.NaN), ("1 D:", 0U, -1d), ("1 D:", 0U, 101d) })
            Check.That(double.IsNaN(EvaluateCounters([("0 C:", 0, 99), bad]).ActivePercent), "One unreadable disk invalidates the observation");
        Check.That(double.IsNaN(EvaluateCounters([("_Total", 0, 90)]).ActivePercent), "Aggregate alone cannot prove disk idle");
        Check.That(double.IsNaN(EvaluateCounters([]).ActivePercent), "No counters cannot prove disk idle");
    }

    private static void IncompleteInventory()
    {
        var gate = new IdleGate();
        Check.That(!gate.Evaluate(At(0, 99) with { Complete = false }, Settings).CanStart, "Incomplete inventory blocks launch");
        Check.That(!gate.Evaluate(At(60, 0), Settings).CanStart, "Disk saturation from an incomplete process capture still requires recovery");
        Check.That(gate.Evaluate(At(120, 0), Settings).CanStart, "Complete quiet recovery resumes");
    }

    private static async Task OwnedCompletion(bool pause)
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        var first = rig.Add("first.spd");
        var next = rig.Add("next.spd");
        rig.Engine.Start();
        await rig.TickAsync();
        if (pause) rig.Engine.Pause();
        rig.Probe.DiskActivePercent = 99;
        rig.Runner.Observation = new(RunStage.ReadyToSave, "Simulation Done.", true);
        await rig.TickAsync();
        Check.Equal(0, rig.Runner.SaveRequests.Count, "Completion text cannot save/exit during disk post-processing");
        Check.That(!first.Run!.SaveRequested && rig.Engine.MonitoringDetails.Contains("디스크 후처리 대기"), "No persisted save intent until disk clears");
        rig.Probe.DiskActivePercent = 0;
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.TickAsync();
        Check.Equal(0, rig.Runner.SaveRequests.Count, "First quiet sample is insufficient");
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.TickAsync();
        Check.Equal(first.Id, rig.Runner.SaveRequests.Single(), "Fresh completion is saved after quiet recovery");
        rig.Runner.Observation = new(RunStage.Succeeded, "Saved and closed", false);
        rig.Probe.Processes.Clear();
        rig.Probe.DiskActivePercent = 95;
        await rig.TickAsync();
        Check.Equal(JobStatus.Saving, first.Status, "Cache flushing after process exit still postpones completion");
        Check.Equal(1, rig.Runner.Launched.Count, "No next job during flushing");
        rig.Probe.DiskActivePercent = 0;
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.TickAsync();
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.TickAsync(2);
        Check.Equal(JobStatus.Completed, first.Status, "Saved run completes after disk recovery");
        Check.Equal(pause ? first.Id : next.Id, rig.Runner.Launched.Last(), "Next job resumes only when the queue is armed");
    }

    private static async Task LaunchPreflight()
    {
        using var rig = new TestRig();
        await rig.InitializeAsync();
        rig.Add("queued.spd");
        rig.Probe.BeforeCaptureAsync = capture => { if (capture == 2) rig.Probe.DiskActivePercent = 99; return Task.CompletedTask; };
        rig.Engine.Start();
        await rig.TickAsync();
        Check.Equal(0, rig.Runner.Launched.Count, "Disk saturation after qualification blocks process creation");
        Check.That(rig.Engine.IsArmed, "Temporary saturation does not disarm the queue");
    }

    private static async Task NativeCapture()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sample = await CaptureDisk(cancellation.Token);
        Check.That(double.IsNaN(sample.ActivePercent) ? !string.IsNullOrWhiteSpace(sample.Detail) :
            sample.ActivePercent is >= 0 and <= 100 && sample.Detail.Contains("디스크"), "Native failure must be explicit, never false idle");
        Console.WriteLine($"Disk probe: {sample.ActivePercent:F1}% · {sample.Detail}");
    }
}
