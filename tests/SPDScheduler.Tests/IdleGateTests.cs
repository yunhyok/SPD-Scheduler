using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class IdleGateTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity External = new(400, Epoch, "external-powersi.exe");

    public static IEnumerable<(string Name, Func<Task> Test)> All()
    {
        yield return Test("No external process allows immediate launch", NoProcesses);
        yield return Test("Idle inference requires the full continuous interval", FullInterval);
        yield return Test("CPU uses one-core percentage rather than machine percentage", OneCoreCpu);
        yield return Test("CPU load is accumulated across the process tree", AggregateCpu);
        yield return Test("A large stable memory allocation may be inferred idle", StableLargeMemory);
        yield return Test("Memory variation resets the continuous idle window", MemoryVariation);
        yield return Test("Memory variation may equal the configured limit", ExactMemoryThreshold);
        yield return Test("The I/O threshold must be strictly below the limit", ExactIoThreshold);
        yield return Test("Incomplete and inaccessible snapshots reset idle inference", MissingAccess);
        yield return Test("PID reuse and process-set changes reset idle inference", IdentityChanges);
        yield return Test("Long sampling gaps reset idle inference", SampleGap);
        yield return Test("Minute polling accumulates the configured idle interval", MinutePolling);
        yield return Test("Minute polling rejects missed observations", MissedMinutePolling);
        yield return Test("Backward process counters reset idle inference", CounterReset);
        yield return Test("Explicit running/suspended/license/error evidence blocks idle", BusySignals);
        yield return Test("Exclusions compare complete identities rather than PID alone", ExactExclusion);
    }

    private static (string, Func<Task>) Test(string name, Action test) =>
        (name, () => { test(); return Task.CompletedTask; });

    private static ProcessSample Sample(ProcessIdentity? identity = null, double cpu = 0,
        long memory = 100 * 1024 * 1024, ulong io = 0, bool accessible = true,
        ProcessSignal signal = ProcessSignal.None) =>
        new(identity ?? External, 0, cpu, memory, io, accessible, signal);

    private static GateDecision At(IdleGate gate, int seconds, ProcessSample[] samples,
        bool complete = true, IReadOnlyCollection<ProcessIdentity>? excluded = null) =>
        gate.Evaluate(new ProcessSnapshot(Epoch.AddSeconds(seconds), samples, complete), new AppSettings(), excluded);

    private static GateDecision QuietUntil(IdleGate gate, int end, int start = 0, ProcessSample? sample = null)
    {
        GateDecision decision = new(false, false, "Not sampled");
        for (var second = start; second <= end; second += 2) decision = At(gate, second, [sample ?? Sample()]);
        return decision;
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

    private static void MinutePolling()
    {
        foreach (int minutes in new[] { 1, 5 })
        {
            var settings = new AppSettings { PollIntervalMinutes = minutes, IdleSeconds = minutes * 180 };
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
        var settings = new AppSettings { PollIntervalMinutes = 5, IdleSeconds = 900 };
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
