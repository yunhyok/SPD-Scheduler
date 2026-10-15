using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class IdleGateTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity External = new(400, Epoch, "external-powersi.exe");

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("No external process allows immediate launch", NoProcesses);
        yield return Test("Immediate launch recheck retains the measured interval and rejects meaningful activity", ImmediateConfirmation);
        yield return Test("GUI status-read bursts do not hide renewed solver activity during preflight", ObserverActivity);
        yield return Test("Launch recheck rejects changed or inaccessible process evidence", ChangedConfirmation);
        yield return Test("Fresh completion needs a full quiet poll and every GUI completion", FreshCompletion);
        yield return Test("Stale or lost completion cannot shorten the idle interval", StaleCompletion);
        yield return Test("Fresh completion never overrides a busy BbsCon descendant", FreshCompletionBusyChild);
        yield return Test("Measured GUI background qualifies after the active BbsCon child stops", MeasuredBackground);
        yield return Test("Idle inference requires the full continuous interval", FullInterval);
        yield return Test("CPU uses one-core percentage rather than machine percentage", OneCoreCpu);
        yield return Test("CPU load is accumulated across the process tree", AggregateCpu);
        yield return Test("64 logical CPU display identifies the busy PID without relaxing the gate", MachineCpuDisplay);
        yield return Test("Multiple quiet PowerSI windows allow the queue after sustained idle", MultipleIdleWindows);
        yield return Test("A completed GUI with a busy solver still blocks on a many-core PC", CompletedWithBusySolver);
        yield return Test("A large stable memory allocation may be inferred idle", StableLargeMemory);
        yield return Test("Memory variation resets the continuous idle window", MemoryVariation);
        yield return Test("Memory variation may equal the configured limit", ExactMemoryThreshold);
        yield return Test("The I/O threshold must be strictly below the limit", ExactIoThreshold);
        yield return Test("Incomplete and inaccessible snapshots reset idle inference", MissingAccess);
        yield return Test("PID reuse and process-set changes reset idle inference", IdentityChanges);
        yield return Test("Long sampling gaps reset idle inference", SampleGap);
        yield return Test("Status observation time does not appear to be a missed poll", StatusObservationDelay);
        yield return Test("Minute polling accumulates the configured idle interval", MinutePolling);
        yield return Test("Minute polling rejects missed observations", MissedMinutePolling);
        yield return Test("Backward process counters reset idle inference", CounterReset);
        yield return Test("Explicit running/suspended/license/error evidence blocks idle", BusySignals);
        yield return Test("Exclusions compare complete identities rather than PID alone", ExactExclusion);
    }

    private static (string, Func<Task>) Test(string name, Action test) =>
        (name, () => { test(); return Task.CompletedTask; });

    private static AppSettings StrictSettings() => new() { IdleSeconds = 180, IdleCpuPercent = 1, IdleMemoryMiB = 16 };

    private static ProcessSample Sample(ProcessIdentity? identity = null, double cpu = 0,
        long memory = 100 * 1024 * 1024, ulong io = 0, bool accessible = true,
        ProcessSignal signal = ProcessSignal.None) =>
        new(identity ?? External, 0, cpu, memory, io, accessible, signal);

    private static GateDecision At(IdleGate gate, int seconds, ProcessSample[] samples,
        bool complete = true, IReadOnlyCollection<ProcessIdentity>? excluded = null) =>
        gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), samples, complete), StrictSettings(), excluded);

    private static GateDecision QuietUntil(IdleGate gate, int end, int start = 0, ProcessSample? sample = null)
    {
        GateDecision decision = new(false, false, "Not sampled");
        for (var second = start; second <= end; second += 2) decision = At(gate, second, [sample ?? Sample()]);
        return decision;
    }


    private static void ImmediateConfirmation()
    {
        foreach (var (gap, cpu, expected) in new[] { (0d, 0d, true), (0.001, 0.016, true), (0.2, 0.2, false), (2d, 2d, false) })
        {
            var gate = new IdleGate();
            var settings = StrictSettings();
            foreach (int seconds in new[] { 0, 60, 120, 180 })
                gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), [Sample()]), settings);
            Check.Equal(expected, gate.Confirm(new ProcessSnapshot(Epoch.AddSeconds(180 + gap), [Sample(cpu: cpu)]), settings).CanStart,
                "A counter quantum must not erase idle history; meaningful new CPU must not be diluted into the old interval");
        }
    }

    private static void ObserverActivity()
    {
        foreach (int childActivity in new[] { 0, 1, 2 })
        {
            var gate = new IdleGate();
            var settings = new AppSettings { IdleSeconds = 60, IdleCpuPercent = 50, IdleMemoryMiB = 128 };
            var root = External with { ImagePath = "PowerSI.exe" };
            var child = External with { Pid = 401, ImagePath = "BbsCon.exe" };
            foreach (int seconds in new[] { 0, 60 })
                gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), [Sample(root), Sample(child)]), settings);
            var snapshot = new ProcessSnapshot(Epoch.AddSeconds(63),
                [Sample(root, cpu: 0.3, io: 256 * 1024), Sample(child, cpu: childActivity == 1 ? 3 : 0,
                    io: childActivity == 2 ? 256 * 1024UL : 0)]);
            Check.Equal(childActivity == 0, gate.Confirm(snapshot, settings).CanStart,
                "GUI inspection bursts use the full interval, but resumed BbsCon CPU or I/O must fail the short child check");
        }
    }

    private static void ChangedConfirmation()
    {
        foreach (int change in Enumerable.Range(0, 6))
        {
            var gate = new IdleGate();
            var settings = StrictSettings();
            foreach (int seconds in new[] { 0, 60, 120, 180 })
                gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), [Sample(cpu: 10, io: 100)]), settings);
            var samples = change switch
            {
                0 => new[] { Sample(External with { Pid = 401 }) },
                1 => [Sample(accessible: false)],
                2 => [Sample(cpu: 10, io: 100, signal: ProcessSignal.Running)],
                3 => [Sample(cpu: 9, io: 100)],
                4 => [Sample(cpu: 10, io: 100, memory: 117 * 1024 * 1024)],
                _ => [Sample(cpu: 10, io: 100)]
            };
            Check.That(!gate.Confirm(new ProcessSnapshot(Epoch.AddSeconds(180), samples, Complete: change != 5), settings).CanStart,
                "Identity/access/signal/counter/memory/inventory changes must invalidate the final launch check");
        }
    }

    private static void FreshCompletion()
    {
        var gate = new IdleGate();
        var root = External with { ImagePath = "PowerSI.exe" };
        var other = root with { Pid = 401 };
        GateDecision Observe(int seconds, ProcessSignal first, ProcessSignal second) =>
            gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), [Sample(root, signal: first), Sample(other, signal: second)]), StrictSettings());
        Check.That(!Observe(0, ProcessSignal.Running, ProcessSignal.Running).CanStart, "Running roots block");
        Check.That(!Observe(60, ProcessSignal.Completed, ProcessSignal.Running).CanStart, "Every GUI must complete");
        Check.That(!Observe(120, ProcessSignal.Completed, ProcessSignal.Completed).CanStart, "Completion alone cannot skip the quiet poll");
        Check.That(Observe(180, ProcessSignal.Completed, ProcessSignal.Completed).CanStart, "Fresh completions plus a full quiet poll may shorten custom 180 seconds");
    }

    private static void StaleCompletion()
    {
        foreach (bool loseSignal in new[] { false, true })
        {
            var gate = new IdleGate();
            var root = External with { ImagePath = "PowerSI.exe" };
            GateDecision Observe(int seconds, ProcessSignal signal) =>
                gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), [Sample(root, signal: signal)]), StrictSettings());
            if (loseSignal) Observe(-60, ProcessSignal.Running);
            Observe(0, ProcessSignal.Completed);
            Observe(60, loseSignal ? ProcessSignal.None : ProcessSignal.Completed);
            Check.That(!Observe(120, ProcessSignal.Completed).CanStart, "First-seen Done or a lost status must retain the normal idle interval");
            Check.That(Observe(180, ProcessSignal.Completed).CanStart, "The normal quiet fallback remains available");
        }
    }

    private static void FreshCompletionBusyChild()
    {
        var gate = new IdleGate();
        var root = External with { ImagePath = "PowerSI.exe" };
        var child = External with { Pid = 401, ImagePath = "BbsCon.exe" };
        var settings = new AppSettings { IdleSeconds = 180, IdleCpuPercent = 50, IdleMemoryMiB = 128 };
        for (int seconds = 0; seconds <= 240; seconds += 60)
        {
            var signal = seconds == 0 ? ProcessSignal.Running : ProcessSignal.Completed;
            Check.That(!gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds),
                [Sample(root, signal: signal), Sample(child, cpu: seconds * 0.9995, memory: (long)(seconds / 60d * 1226.7 * 1048576))]), settings).CanStart,
                "A fresh GUI completion cannot override CPU or memory activity in BbsCon");
        }
    }

    private static void MeasuredBackground()
    {
        var gate = new IdleGate();
        var root = External with { ImagePath = "PowerSI.exe" };
        var child = External with { Pid = 401, ImagePath = "BbsCon.exe" };
        var settings = new AppSettings();
        ProcessSample Gui(int seconds) => Sample(root, cpu: seconds * 0.2247,
            memory: (long)((100 + seconds / 60d * 9.4) * 1048576), io: (ulong)(seconds * 46.5 * 1024));
        for (int seconds = 0; seconds <= 120; seconds += 60)
            Check.That(!gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds),
                [Gui(seconds), Sample(child, cpu: seconds * 0.9995, memory: (long)((100 + seconds / 60d * 1226.7) * 1048576))], LogicalProcessorCount: 64), settings).CanStart,
                "The measured active BbsCon must remain blocked");
        Check.That(!gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(180), [Gui(180)], LogicalProcessorCount: 64), settings).CanStart,
            "A departing child restarts observation with the surviving GUI set");
        Check.That(gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(240), [Gui(240)], LogicalProcessorCount: 64), settings).CanStart,
            "Measured GUI background should qualify after one complete quiet minute");
    }

    private static void NoProcesses()
    {
        var decision = At(new IdleGate(), 0, []);
        Check.That(decision.CanStart && !decision.InferredIdle, "No process should not need an inference period");
    }

    private static void FullInterval()
    {
        var gate = new IdleGate();
        Check.That(!QuietUntil(gate, 178).CanStart, "178 seconds is insufficient");
        var decision = At(gate, 180, [Sample()]);
        Check.That(decision.CanStart && decision.InferredIdle, "180 quiet seconds should produce inferred idle");
        Check.That(!string.IsNullOrWhiteSpace(decision.Reason), "Inference needs a visible explanation");
    }

    private static void OneCoreCpu()
    {
        var gate = new IdleGate();
        for (var second = 0; second <= 200; second += 2)
            Check.That(!At(gate, second, [Sample(cpu: second * 0.02)]).CanStart,
                "2% of one core must stay busy even on a many-core PC");
    }

    private static void AggregateCpu()
    {
        var gate = new IdleGate();
        var child = new ProcessIdentity(401, Epoch, "external-child.exe");
        for (var second = 0; second <= 200; second += 2)
            Check.That(!At(gate, second,
                [Sample(cpu: second * 0.006), Sample(child, cpu: second * 0.006)]).CanStart,
                "Two 0.6% processes together exceed the 1% threshold");
    }

    private static void StableLargeMemory()
    {
        var decision = QuietUntil(new IdleGate(), 180, sample: Sample(memory: 1L << 40));
        Check.That(decision.CanStart && decision.InferredIdle, "Absolute memory size is not a busy signal");
    }

    private static void MachineCpuDisplay()
    {
        var gate = new IdleGate();
        var child = new ProcessIdentity(401, Epoch, "synthetic-solver.exe");
        gate.Evaluate(new ProcessSnapshot(Epoch, [Sample(), Sample(child)], LogicalProcessorCount: 64), StrictSettings());
        var decision = gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(60),
            [Sample(cpu: 0.12), Sample(child, cpu: 68.736)], LogicalProcessorCount: 64), StrictSettings());
        Check.That(!decision.CanStart, "Display normalization must not relax the one-core threshold");
        Check.That(decision.Reason.Contains($"{1.1476 * 100 / 64:F2}% (전체)"), "114.76% one-core must display as 1.79% whole-machine");
        Check.That(gate.Details.Contains("PID 401 · synthetic-solver.exe") && gate.Details.Contains($"{114.56:F2}% (1코어)"),
            "Per-process details must identify the source of CPU load");
        Check.That(gate.Details.Contains($"{1d / 64:F4}% (전체 PC)") && gate.Details.Contains("기준 초과: CPU"),
            "Details must show the equivalent whole-PC limit and actual blocker");
    }

    private static void MultipleIdleWindows()
    {
        var gate = new IdleGate();
        for (int seconds = 0; seconds <= 180; seconds += 60)
        {
            var samples = Enumerable.Range(0, 3).Select(i => Sample(External with { Pid = 400 + i },
                cpu: seconds * 0.002, memory: 20L << 30)).ToArray();
            var decision = gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), samples, LogicalProcessorCount: 64), StrictSettings());
            Check.Equal(seconds == 180, decision.CanStart, "Multiple open idle windows with stable large memory may qualify");
        }
    }

    private static void CompletedWithBusySolver()
    {
        var gate = new IdleGate();
        for (int seconds = 0; seconds <= 240; seconds += 60)
        {
            var samples = new[] { Sample(signal: ProcessSignal.Completed),
                Sample(new ProcessIdentity(401, Epoch, "synthetic-solver.exe"), cpu: seconds) };
            Check.That(!gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), samples, LogicalProcessorCount: 64), StrictSettings()).CanStart,
                "One busy solver is only 1.56% machine CPU but must still block, even after a GUI completion label");
        }
    }

    private static void MemoryVariation()
    {
        var gate = new IdleGate();
        QuietUntil(gate, 178);
        var changed = Sample(memory: 117 * 1024 * 1024);
        Check.That(!At(gate, 180, [changed]).CanStart, "17 MiB variation invalidates accumulated quiet time");
        Check.That(!QuietUntil(gate, 358, 182, changed).CanStart, "A reset requires another full interval");
        Check.That(At(gate, 360, [changed]).CanStart, "Stable memory may qualify after a fresh interval");
    }

    private static void ExactMemoryThreshold()
    {
        var gate = new IdleGate();
        QuietUntil(gate, 178);
        Check.That(At(gate, 180, [Sample(memory: 116 * 1024 * 1024)]).CanStart,
            "Exactly 16 MiB variation is within the accepted memory limit");
    }

    private static void ExactIoThreshold()
    {
        var gate = new IdleGate();
        for (var second = 0; second <= 200; second += 2)
            Check.That(!At(gate, second, [Sample(io: (ulong)second * 64 * 1024)]).CanStart,
                "Exactly 64 KiB/s is not below 64 KiB/s");
    }

    private static void MissingAccess()
    {
        foreach (var incomplete in new[] { true, false })
        {
            var gate = new IdleGate();
            QuietUntil(gate, 178);
            var blocked = At(gate, 180, [Sample(accessible: incomplete)], complete: !incomplete);
            Check.That(!blocked.CanStart, "Missing access or incomplete inventory blocks launch");
            Check.That(!At(gate, 182, [Sample()]).CanStart, "Returning access must not reuse old idle time");
            Check.That(!QuietUntil(gate, 360, 184).CanStart, "Full observation restarts after missing data");
            Check.That(At(gate, 362, [Sample()]).CanStart, "Fresh complete interval can infer idle");
        }
    }

    private static void IdentityChanges()
    {
        foreach (var replacement in new[]
        {
            External with { StartedAt = Epoch.AddMinutes(1) },
            External with { ImagePath = "different.exe" },
            External with { Pid = 999 }
        })
        {
            var gate = new IdleGate();
            QuietUntil(gate, 178);
            Check.That(!At(gate, 180, [Sample(replacement)]).CanStart, "Changed identity must restart observation");
            Check.That(!At(gate, 182, [Sample(replacement)]).CanStart, "Two replacement seconds are insufficient");
        }
        var addedChild = new IdleGate();
        QuietUntil(addedChild, 178);
        Check.That(!At(addedChild, 180, [Sample(), Sample(External with { Pid = 401 })]).CanStart,
            "A newly discovered descendant must reset the observation window");
    }

    private static void SampleGap()
    {
        var gate = new IdleGate();
        QuietUntil(gate, 178);
        Check.That(!At(gate, 250, [Sample()]).CanStart, "A monitoring outage cannot count as quiet time");
        Check.That(!At(gate, 252, [Sample()]).CanStart, "Observation restarts after the gap");
    }

    private static void StatusObservationDelay()
    {
        var gate = new IdleGate();
        var settings = new AppSettings { IdleSeconds = 60, IdleCpuPercent = 1, IdleMemoryMiB = 16 };
        ProcessSnapshot Snapshot(int seconds) => new(Epoch.AddSeconds(seconds),
            Enumerable.Range(0, 4).Select(i => Sample(External with { Pid = 400 + i })).ToArray(),
            ObservationDuration: TimeSpan.FromSeconds(12));
        gate.Evaluate(Snapshot(0), settings);
        Check.That(gate.Evaluate(Snapshot(72), settings).CanStart, "A 60-second poll after twelve seconds of status reads is continuous observation");
        Check.That(gate.Confirm(Snapshot(84), settings).CanStart, "The final confirmation must account for its preceding status reads too");
        Check.That(!gate.Evaluate(Snapshot(216), settings).CanStart, "A genuinely missed poll must still reset observation");
    }

    private static void MinutePolling()
    {
        foreach (int minutes in new[] { 1, 5 })
        {
            var settings = new AppSettings { PollIntervalMinutes = minutes, IdleSeconds = minutes * 180, IdleCpuPercent = 1, IdleMemoryMiB = 16 };
            var gate = new IdleGate();
            for (int sample = 0; sample <= 3; sample++)
            {
                int seconds = sample * minutes * 60;
                var result = gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds),
                    [Sample(cpu: seconds * 0.005, io: (ulong)seconds * 1024)]), settings);
                Check.Equal(sample == 3, result.CanStart,
                    "Low CPU/I/O across real minute intervals must accumulate without an artificial gap reset");
            }
        }
    }

    private static void MissedMinutePolling()
    {
        var settings = new AppSettings { PollIntervalMinutes = 5, IdleSeconds = 900, IdleCpuPercent = 1, IdleMemoryMiB = 16 };
        var gate = new IdleGate();
        foreach (int seconds in new[] { 0, 300, 600, 1200 })
            Check.That(!gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), [Sample()]), settings).CanStart,
                "A missed five-minute observation must reset accumulated idle time");
    }

    private static void CounterReset()
    {
        var gate = new IdleGate();
        QuietUntil(gate, 178, sample: Sample(cpu: 100, io: 1000));
        Check.That(!At(gate, 180, [Sample(cpu: 99, io: 999)]).CanStart,
            "Counter rollback invalidates the measurement window");
    }

    private static void BusySignals()
    {
        foreach (var signal in new[] { ProcessSignal.Running, ProcessSignal.Suspended, ProcessSignal.LicenseWaiting, ProcessSignal.Error })
        {
            var gate = new IdleGate();
            QuietUntil(gate, 178);
            Check.That(!At(gate, 180, [Sample(signal: signal)]).CanStart, $"{signal} must override low resource use");
            Check.That(!At(gate, 182, [Sample()]).CanStart, "A busy signal must invalidate accumulated quiet time");
        }
    }

    private static void ExactExclusion()
    {
        var excluded = At(new IdleGate(), 0, [Sample(signal: ProcessSignal.Running)], excluded: [External]);
        Check.That(excluded.CanStart && !excluded.InferredIdle, "The owned identity should be excluded from external gating");
        var reused = External with { StartedAt = Epoch.AddMinutes(1) };
        Check.That(!At(new IdleGate(), 0, [Sample(reused, signal: ProcessSignal.Running)], excluded: [External]).CanStart,
            "An excluded PID must not exclude its later reuse");
    }
}
