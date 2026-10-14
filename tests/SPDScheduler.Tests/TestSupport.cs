using SPDScheduler.Core;

namespace SPDScheduler.Tests;

internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message) =>
        That(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, actual {actual}");

    public static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan amount) => Now += amount;
}

internal sealed class FakeProbe(TestClock clock) : IProcessProbe
{
    public List<ProcessSample> Processes { get; } = [];
    public bool Complete { get; set; } = true;
    public Func<int, Task>? BeforeCaptureAsync { get; set; }
    private int captures;
    public async Task<ProcessSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        if (BeforeCaptureAsync is not null) await BeforeCaptureAsync(++captures);
        return new ProcessSnapshot(clock.Now, Processes.ToArray(), Complete);
    }
}

internal sealed class FakeRunner(FakeProbe probe, TestClock clock) : IPowerSiRunner
{
    public List<Guid> Launched { get; } = [];
    public List<Guid> SaveRequests { get; } = [];
    public List<ProcessIdentity> CleanupIdentities { get; } = [];
    public RunObservation Observation { get; set; } = new(RunStage.Running, "Synthetic running evidence", true, true);
    public CleanupState Cleanup { get; set; } = CleanupState.Closed;
    public bool ThrowAfterSpawn { get; set; }
    public Action<JobItem>? BeforeLaunch { get; set; }
    public Action<JobItem>? BeforeSave { get; set; }

    public Task<ProcessIdentity> LaunchAsync(JobItem job, AppSettings settings, CancellationToken cancellationToken)
    {
        BeforeLaunch?.Invoke(job);
        Launched.Add(job.Id);
        var identity = new ProcessIdentity(10_000 + Launched.Count, clock.Now,
            Path.Combine(Path.GetTempPath(), "SPDScheduler.Tests", "SyntheticPowerSI.exe"));
        probe.Processes.Add(new ProcessSample(identity, 0, 0, 100 * 1024 * 1024, 0));
        if (ThrowAfterSpawn)
        {
            job.Run!.FailureReason = "Synthetic process exists but its identity could not be delivered";
            throw new IOException("Synthetic failure after process creation but before identity delivery");
        }
        return Task.FromResult(identity);
    }

    public Task<RunObservation> InspectAsync(JobItem job, AppSettings settings, ProcessSnapshot snapshot, CancellationToken cancellationToken) =>
        Task.FromResult(Observation);

    public Task RequestSaveAndExitAsync(JobItem job, CancellationToken cancellationToken)
    {
        BeforeSave?.Invoke(job);
        SaveRequests.Add(job.Id);
        return Task.CompletedTask;
    }

    public Task<CleanupResult> CloseFailedRunAsync(JobItem job, ProcessSnapshot snapshot, CancellationToken cancellationToken)
    {
        Check.That(job.Run?.Process is not null, "Cleanup must have an owned process identity");
        var identity = job.Run!.Process!;
        CleanupIdentities.Add(identity);
        if (Cleanup == CleanupState.Closed) probe.Processes.RemoveAll(sample => sample.Identity == identity);
        return Task.FromResult(new CleanupResult(Cleanup, "Synthetic cleanup"));
    }
}

internal sealed class TestRig : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "SPDScheduler.Tests", Guid.NewGuid().ToString("N"));
    public TestClock Clock { get; } = new();
    public QueueStore Store { get; }
    public FakeProbe Probe { get; }
    public FakeRunner Runner { get; }
    public SchedulerEngine Engine { get; }

    public TestRig()
    {
        Directory.CreateDirectory(DirectoryPath);
        Store = new QueueStore(Path.Combine(DirectoryPath, "state"));
        Probe = new FakeProbe(Clock);
        Runner = new FakeRunner(Probe, Clock);
        Engine = new SchedulerEngine(Store, Runner, Probe, Clock);
    }

    public async Task InitializeAsync(QueueDocument? document = null)
    {
        if (document is not null)
        {
            foreach (var run in document.Jobs.Select(job => job.Run).OfType<RunRecord>())
                if (string.IsNullOrEmpty(run.DirectoryPath)) run.DirectoryPath = Path.Combine(Store.DataDirectory, "runs", run.Id.ToString("N"));
            Store.Save(document);
        }
        await Engine.InitializeAsync();
    }

    public JobItem Add(string name)
    {
        var path = CreateFile(name);
        Check.Equal(0, Engine.AddFiles([path]).Count, "Synthetic SPD should be accepted");
        return Engine.Jobs.Single(job => job.FilePath == path);
    }

    public string CreateFile(string name)
    {
        var path = Path.Combine(DirectoryPath, name);
        File.WriteAllText(path, "Synthetic fixture; never open in PowerSI.");
        return path;
    }

    public async Task TickAsync(int count = 1)
    {
        for (var index = 0; index < count; index++) await Engine.TickAsync();
    }

    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
