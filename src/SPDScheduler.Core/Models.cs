namespace SPDScheduler.Core;

public enum JobStatus { Queued, Starting, Running, Saving, Completed, Failed, NeedsAttention }

public sealed class JobItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = "";
    public long Order { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? ScheduleTimeZone { get; set; }
    public JobStatus Status { get; set; }
    public RunRecord? Run { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string FileName => Path.GetFileName(FilePath);
}

public sealed record ProcessIdentity(int Pid, DateTimeOffset StartedAt, string ImagePath)
{
    public bool Equals(ProcessIdentity? other) => other is not null && Pid == other.Pid &&
        StartedAt == other.StartedAt && StringComparer.OrdinalIgnoreCase.Equals(ImagePath, other.ImagePath);
    public override int GetHashCode() => HashCode.Combine(Pid, StartedAt,
        StringComparer.OrdinalIgnoreCase.GetHashCode(ImagePath ?? ""));
}

public sealed class RunRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DirectoryPath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public ProcessIdentity? Process { get; set; }
    public List<ProcessIdentity> Children { get; set; } = [];
    public bool StartedObserved { get; set; }
    public bool SaveRequested { get; set; }
    public DateTimeOffset? CleanupRequestedAt { get; set; }
    public string? FailureReason { get; set; }
    public string? Workflow { get; set; }
}

public sealed class CompletionRule
{
    public string Workflow { get; set; } = "";
    public string SpdPattern { get; set; } = "";
    public string StartedPattern { get; set; } = "";
    public string CompletedPattern { get; set; } = "";
}

public sealed class AppSettings
{
    public int ResourcePolicyVersion { get; set; } = 1;
    public bool AllowBbsPostProcessing { get; set; } = true;
    public string PowerSiPath { get; set; } = "";
    public int PollIntervalMinutes { get; set; } = 1;
    public int IdleSeconds { get; set; } = 60;
    public double IdleCpuPercent { get; set; } = 50;
    public int IdleMemoryMiB { get; set; } = 128;
    public int IdleIoKiBPerSecond { get; set; } = 64;
    public int StartTimeoutMinutes { get; set; } = 10;
    public List<CompletionRule> CompletionRules { get; set; } = [];
}

public sealed class QueueDocument
{
    public int SchemaVersion { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<JobItem> Jobs { get; set; } = [];
}

public enum ProcessSignal { None, Running, Suspended, LicenseWaiting, Completed, Error }

public sealed record ProcessSample(
    ProcessIdentity Identity, int ParentPid, double CpuSeconds, long PrivateBytes,
    ulong IoBytes, bool Accessible = true, ProcessSignal Signal = ProcessSignal.None,
    string? Evidence = null, ulong OtherIoBytes = 0);

public sealed record ProcessSnapshot(DateTimeOffset CapturedAt,
    IReadOnlyList<ProcessSample> Processes, bool Complete = true, string? Error = null,
    int LogicalProcessorCount = 1, TimeSpan ObservationDuration = default);

public sealed record GateDecision(bool CanStart, bool InferredIdle, string Reason);

public enum RunStage { Starting, Running, ReadyToSave, Saving, Succeeded, StartFailed, RuntimeFailed, SaveFailed, Unknown, ObservationPending }

public sealed record RunObservation(RunStage Stage, string Detail, bool ProcessAlive,
    bool HasActivity = false);

public enum CleanupState { Closed, Pending, Unsafe }
public sealed record CleanupResult(CleanupState State, string Detail);

public interface IProcessProbe
{
    Task<ProcessSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

public interface IPowerSiRunner
{
    void ValidateConfiguration(AppSettings settings);
    Task<ProcessIdentity> LaunchAsync(JobItem job, AppSettings settings, CancellationToken cancellationToken);
    Task<RunObservation> InspectAsync(JobItem job, AppSettings settings, ProcessSnapshot snapshot, CancellationToken cancellationToken);
    Task RequestSaveAndExitAsync(JobItem job, CancellationToken cancellationToken);
    Task<CleanupResult> CloseFailedRunAsync(JobItem job, ProcessSnapshot snapshot, CancellationToken cancellationToken);
}
