namespace SPDScheduler.Core;

public sealed class SchedulerEngine
{
    private readonly QueueStore _store;
    private readonly IPowerSiRunner _runner;
    private readonly IProcessProbe _probe;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly IdleGate _gate = new();
    private readonly HashSet<Guid> _resolved = [];
    private bool _initialized;
    private bool _persistenceFailed;
    private ProcessSnapshot? _monitoringSnapshot;
    private string? _ownedObservationDetail;
    public QueueDocument Document { get; private set; } = new();
    public IReadOnlyList<JobItem> Jobs => Document.Jobs;
    public AppSettings Settings => Document.Settings;
    public bool IsArmed { get; private set; }
    public string StatusText { get; private set; } = "일시정지 · 목록을 확인한 뒤 시작하세요.";
    public string MonitoringDetails => Jobs.Any(IsActive) ? OwnedMonitoringDetails() : _gate.Details;
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public event EventHandler? Changed;

    public SchedulerEngine(QueueStore store, IPowerSiRunner runner, IProcessProbe probe, TimeProvider? clock = null)
    {
        _store = store; _runner = runner; _probe = probe; _clock = clock ?? TimeProvider.System;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized) return Task.CompletedTask;
        Document = _store.Load();
        IsArmed = false;
        foreach (var job in Jobs.Where(IsActive))
        {
            if (job.Run?.Process is null)
            {
                job.Status = JobStatus.NeedsAttention;
                job.LastError = "이전 실행의 PID가 기록되지 않았습니다. 프로세스를 확인한 후 건너뛰기 또는 재시도하세요.";
            }
        }
        _initialized = true;
        Save();
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> AddFiles(IEnumerable<string> paths)
    {
        var rejected = new List<string>();
        var order = Jobs.Count == 0 ? 0 : Jobs.Max(j => j.Order) + 1;
        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!string.Equals(Path.GetExtension(fullPath), ".spd", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                { rejected.Add(path); continue; }
                if (Jobs.Any(j => (j.Status == JobStatus.Queued || IsActive(j)) &&
                    string.Equals(j.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)))
                { rejected.Add(path + " (이미 대기/실행 중)"); continue; }
                Document.Jobs.Add(new JobItem { FilePath = fullPath, Order = order++ });
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { rejected.Add(path + " (" + ex.Message + ")"); }
        }
        Save();
        return rejected;
    }

    public void Remove(Guid id)
    {
        var job = Get(id);
        if (IsActive(job)) throw new InvalidOperationException("실행 상태가 정리되지 않은 작업은 삭제할 수 없습니다.");
        Document.Jobs.Remove(job);
        Save();
    }

    public void ResolveManually(Guid id, Guid? expectedRunId, bool remove)
    {
        if (!_tick.Wait(0)) throw new InvalidOperationException("상태 확인 중입니다. 확인이 끝난 뒤 다시 시도하세요.");
        try
        {
            var job = Get(id);
            if (job.Status is not (JobStatus.NeedsAttention or JobStatus.Failed) || job.Run?.Id != expectedRunId)
                throw new InvalidOperationException("작업 상태가 변경되었습니다. 목록을 다시 확인한 뒤 수동 정리하세요.");
            IsArmed = false;
            _gate.Reset();
            _resolved.Remove(id);
            string action = remove ? "목록에서 제거" : "완료 처리";
            _store.AppendLog($"사용자 수동 정리 요청 · {action} · 이전 상태: {job.Status} · 실행 ID: {job.Run?.Id.ToString() ?? "없음"} · " +
                $"PID: {job.Run?.Process?.Pid.ToString() ?? "없음"} · 이전 오류: {job.LastError ?? "없음"} · PowerSI 명령 없음", id);
            var previousStatus = job.Status;
            var previousFinishedAt = job.FinishedAt;
            bool previousManual = job.CompletedManually;
            int index = Document.Jobs.IndexOf(job);
            if (remove) Document.Jobs.RemoveAt(index);
            else
            {
                job.Status = JobStatus.Completed;
                job.CompletedManually = true;
                job.FinishedAt = _clock.GetUtcNow();
            }
            StatusText = "수동 정리 · " + action + " · 후속 작업은 시작을 누르면 진행합니다.";
            try { Save(); }
            catch
            {
                if (remove) Document.Jobs.Insert(index, job);
                else
                {
                    job.Status = previousStatus;
                    job.CompletedManually = previousManual;
                    job.FinishedAt = previousFinishedAt;
                }
                throw;
            }
        }
        finally { _tick.Release(); }
    }

    public void SetSchedule(Guid id, DateTimeOffset? scheduledAt)
    {
        var job = Get(id);
        if (job.Status != JobStatus.Queued) throw new InvalidOperationException("대기 작업만 예약을 변경할 수 있습니다.");
        job.ScheduledAt = scheduledAt;
        job.ScheduleTimeZone = scheduledAt.HasValue ? TimeZoneInfo.Local.Id : null;
        Save();
    }

    public void Retry(Guid id)
    {
        var job = Get(id);
        EnsureResolved(job);
        job.Status = JobStatus.Queued;
        job.Run = null;
        job.CompletedManually = false;
        job.LastError = null;
        job.FinishedAt = null;
        _resolved.Remove(id);
        Save();
        _store.AppendLog("사용자가 재시도를 요청했습니다.", id);
    }

    public void Skip(Guid id)
    {
        var job = Get(id);
        EnsureResolved(job);
        job.Status = JobStatus.Failed;
        job.LastError = "사용자가 건너뛰었습니다.";
        job.FinishedAt = _clock.GetUtcNow();
        Save();
        _store.AppendLog(job.LastError, id);
    }

    private void EnsureResolved(JobItem job)
    {
        if (job.Status is not (JobStatus.Failed or JobStatus.NeedsAttention))
            throw new InvalidOperationException("실패 또는 확인 필요 작업만 재시도/건너뛰기 할 수 있습니다.");
        if (job.Status == JobStatus.NeedsAttention && !_resolved.Contains(job.Id))
            throw new InvalidOperationException("관련 PowerSI가 종료되었는지 확인되지 않았습니다. PowerSI를 직접 확인하고 잠시 기다리세요.");
    }

    public void Start()
    {
        SettingsValidator.Validate(Settings);
        if (Jobs.Any(j => j.Status == JobStatus.NeedsAttention))
            throw new InvalidOperationException("확인 필요 작업을 먼저 정리하세요.");
        try { _runner.ValidateConfiguration(Settings); }
        catch (Exception ex)
        {
            IsArmed = false;
            StatusText = "실행 설정 확인 필요 · " + ex.Message;
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
        Save();
        _persistenceFailed = false;
        IsArmed = true;
        StatusText = "자동 진행 활성화 · PowerSI 상태를 확인합니다.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        IsArmed = false;
        StatusText = "일시정지 · 현재 시뮬레이션은 계속 진행합니다.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateSettings(AppSettings settings)
    {
        SettingsValidator.Validate(settings);
        if (Jobs.Any(IsActive)) throw new InvalidOperationException("실행 중인 작업을 정리한 뒤 설정을 변경하세요.");
        Document.Settings = settings;
        _gate.Reset();
        Save();
    }

    public void Save()
    {
        try { _store.Save(Document); }
        catch
        {
            IsArmed = false;
            _persistenceFailed = true;
            StatusText = "목록 저장 실패 · 디스크와 접근 권한을 확인하세요.";
            throw;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (!await _tick.WaitAsync(0, cancellationToken)) return;
        try
        {
            if (!_initialized) await InitializeAsync(cancellationToken);
            if (_persistenceFailed) return;
            var snapshot = await _probe.CaptureAsync(cancellationToken);
            _monitoringSnapshot = snapshot;
            _ownedObservationDetail = null;
            LastCheckedAt = _clock.GetUtcNow();
            RefreshResolved(snapshot);
            var active = Jobs.Where(IsActive).ToArray();
            if (active.Length > 1)
            {
                foreach (var item in active) Attention(item, "여러 미정리 실행이 있습니다. PowerSI를 확인하세요.");
                Save();
                return;
            }
            if (active.Length == 1)
            {
                await MonitorAsync(active[0], snapshot, cancellationToken);
                Save();
                return;
            }
            if (!IsArmed) return;
            var now = _clock.GetUtcNow();
            var next = Jobs.Where(j => j.Status == JobStatus.Queued && (!j.ScheduledAt.HasValue || j.ScheduledAt <= now))
                .OrderBy(j => j.ScheduledAt.HasValue ? 0 : 1)
                .ThenBy(j => j.ScheduledAt ?? DateTimeOffset.MaxValue).ThenBy(j => j.Order).FirstOrDefault();
            if (next is null)
            {
                int failures = Jobs.Count(j => j.Status == JobStatus.Failed);
                StatusText = Jobs.Any(j => j.Status == JobStatus.Queued) ? "예약 시간 대기 중" :
                    failures > 0 ? $"대기 작업 없음 · 실패 {failures}건 · 실패 상태를 더블클릭하면 원인을 볼 수 있습니다." : "목록 완료 · 새 파일을 추가할 수 있습니다.";
                return;
            }
            var decision = _gate.Evaluate(snapshot, Settings);
            StatusText = decision.Reason;
            if (!decision.CanStart) return;
            if (!File.Exists(next.FilePath))
            {
                Finish(next, false, "SPD 파일이 없거나 접근할 수 없습니다.");
                Save();
                return;
            }
            // Repeat the process snapshot immediately before crossing the launch boundary.
            var finalSnapshot = await _probe.CaptureAsync(cancellationToken);
            _monitoringSnapshot = finalSnapshot;
            LastCheckedAt = _clock.GetUtcNow();
            if (!IsArmed || !Document.Jobs.Contains(next) || next.Status != JobStatus.Queued ||
                (next.ScheduledAt.HasValue && next.ScheduledAt > _clock.GetUtcNow())) return;
            var finalDecision = _gate.Confirm(finalSnapshot, Settings);
            if (!finalDecision.CanStart)
            {
                _gate.Reset();
                StatusText = finalDecision.Reason;
                return;
            }
            // Recheck shared configuration before consuming a queued job or creating its run record.
            _runner.ValidateConfiguration(Settings);
            next.Run = new RunRecord { CreatedAt = now };
            next.Run.DirectoryPath = Path.Combine(_store.DataDirectory, "runs", next.Run.Id.ToString("N"));
            next.Status = JobStatus.Starting;
            next.LastError = null;
            Save();
            _store.AppendLog("시작 준비 · " + decision.Reason, next.Id);
            try
            {
                next.Run.Process = await _runner.LaunchAsync(next, Settings, cancellationToken);
                Save();
                StatusText = "PowerSI 시작 확인 중 · " + next.FileName;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (next.Run.Process is null && next.Run.FailureReason is null) Finish(next, false, "실행 실패: " + ex.Message);
                else Attention(next, "프로세스 생성 후 오류가 발생했습니다: " + ex.Message);
                Save();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            IsArmed = false;
            StatusText = "확인 필요 · " + ex.Message;
            try { _store.AppendLog(StatusText); } catch (IOException) { }
        }
        finally
        {
            _tick.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task MonitorAsync(JobItem job, ProcessSnapshot snapshot, CancellationToken cancellationToken)
    {
        var run = job.Run;
        if (run?.Process is null)
        {
            Attention(job, job.LastError ?? "실행 PID가 없어 자동 복구할 수 없습니다.");
            return;
        }
        _resolved.Remove(job.Id);
        if (!snapshot.Complete)
        {
            StatusText = "PowerSI 관측 대기 · " + (snapshot.Error ?? "정보 부족");
            return;
        }
        if (run.FailureReason is not null)
        {
            await CleanupAsync(job, snapshot, cancellationToken);
            return;
        }
        var observation = await _runner.InspectAsync(job, Settings, snapshot, cancellationToken);
        _ownedObservationDetail = observation.Detail;
        if (!observation.ProcessAlive) _resolved.Add(job.Id);
        StatusText = observation.Detail;
        switch (observation.Stage)
        {
            case RunStage.ObservationPending:
                if (job.LastError != observation.Detail) _store.AppendLog(observation.Detail, job.Id);
                job.LastError = observation.Detail;
                break;
            case RunStage.Starting:
                if (_clock.GetUtcNow() - run.CreatedAt >= TimeSpan.FromMinutes(Settings.StartTimeoutMinutes))
                    Attention(job, "시작 확인 시간이 지났습니다. 현재 작업의 시작 상태를 계속 재확인하며 후속 작업은 대기합니다. 계산/라이선스/로그 상태를 직접 확인하세요. " + observation.Detail, pauseQueue: false);
                break;
            case RunStage.Running:
                run.StartedObserved = true;
                job.Status = JobStatus.Running;
                job.LastError = null;
                break;
            case RunStage.ReadyToSave:
                job.Status = JobStatus.Saving;
                job.LastError = null;
                if (!run.SaveRequested)
                {
                    run.SaveRequested = true;
                    Save();
                }
                // Token writing is idempotent, including a crash between persist and delivery.
                await _runner.RequestSaveAndExitAsync(job, cancellationToken);
                break;
            case RunStage.Saving:
                job.Status = JobStatus.Saving;
                job.LastError = null;
                break;
            case RunStage.Succeeded:
                if (observation.ProcessAlive) Attention(job, "저장 후 PowerSI/solver 종료가 확인되지 않았습니다.");
                else Finish(job, true, observation.Detail);
                break;
            case RunStage.StartFailed:
            case RunStage.RuntimeFailed:
                run.FailureReason = observation.Detail;
                run.CleanupRequestedAt = _clock.GetUtcNow();
                Save();
                await CleanupAsync(job, snapshot, cancellationToken);
                break;
            case RunStage.SaveFailed:
                Attention(job, "저장 실패 · " + observation.Detail);
                break;
            case RunStage.Unknown:
                Attention(job, observation.Detail);
                break;
        }
    }

    private async Task CleanupAsync(JobItem job, ProcessSnapshot snapshot, CancellationToken cancellationToken)
    {
        var result = await _runner.CloseFailedRunAsync(job, snapshot, cancellationToken);
        _ownedObservationDetail = result.Detail;
        if (result.State != CleanupState.Closed) _resolved.Remove(job.Id);
        StatusText = result.Detail;
        if (result.State == CleanupState.Closed)
            Finish(job, false, job.Run!.FailureReason ?? result.Detail);
        else if (result.State == CleanupState.Unsafe ||
                 _clock.GetUtcNow() - (job.Run!.CleanupRequestedAt ?? _clock.GetUtcNow()) > TimeSpan.FromSeconds(30))
            Attention(job, "실패 작업 정리 확인 필요 · " + result.Detail);
    }

    private void RefreshResolved(ProcessSnapshot snapshot)
    {
        _resolved.Clear();
        if (!snapshot.Complete || snapshot.Processes.Any(p => !p.Accessible)) return;
        foreach (var job in Jobs.Where(j => j.Status == JobStatus.NeedsAttention))
        {
            if (job.Run?.Process is null)
            {
                if (snapshot.Processes.Count == 0) _resolved.Add(job.Id);
            }
            else if (!snapshot.Processes.Any(p => p.Identity == job.Run.Process || job.Run.Children.Contains(p.Identity)))
                _resolved.Add(job.Id);
        }
    }

    private void Attention(JobItem job, string detail, bool pauseQueue = true)
    {
        if (job.Status != JobStatus.NeedsAttention || job.LastError != detail) _store.AppendLog(detail, job.Id);
        job.Status = JobStatus.NeedsAttention;
        job.LastError = detail;
        if (pauseQueue) IsArmed = false;
        StatusText = "확인 필요 · " + detail;
    }

    private string OwnedMonitoringDetails()
    {
        var snapshot = _monitoringSnapshot;
        var lines = new List<string>
        {
            snapshot is null ? "현재 실행 감시 · 아직 프로세스 관측 없음" :
                $"현재 실행 감시 · 측정: {snapshot.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}",
            snapshot is null ? "" : snapshot.Complete ? "프로세스 관측 완료" : "관측 불완전: " + (snapshot.Error ?? "정보 부족"),
            "현재 실행 관측: " + (_ownedObservationDetail ?? "이번 측정에서 실행 상태를 확인하지 못했습니다.")
        };
        foreach (var job in Jobs.Where(IsActive))
        {
            lines.Add($"실행 ID: {job.Run?.Id.ToString() ?? "없음"} · 작업 상태: {job.Status} · PID: {job.Run?.Process?.Pid.ToString() ?? "없음"}");
            if (job.Run is not { } run) continue;
            var identities = run.Children.ToHashSet();
            if (run.Process is not null) identities.Add(run.Process);
            foreach (var identity in identities.OrderBy(identity => identity.Pid))
            {
                var sample = snapshot?.Processes.FirstOrDefault(process => process.Identity == identity);
                lines.Add(sample is null ? $"PID {identity.Pid} · 이번 관측에 없음{(snapshot?.Complete == true ? "" : " (종료 여부 확인 불가)")}" :
                    $"PID {identity.Pid} · {Path.GetFileName(identity.ImagePath)} · 부모 PID {sample.ParentPid} · 상태 {sample.Signal} · 접근 {(sample.Accessible ? "가능" : "불가")}\r\n  근거: {sample.Evidence ?? "없음"}");
            }
        }
        return string.Join("\r\n", lines);
    }

    private void Finish(JobItem job, bool success, string detail)
    {
        job.Status = success ? JobStatus.Completed : JobStatus.Failed;
        job.LastError = success ? null : detail;
        job.FinishedAt = _clock.GetUtcNow();
        _store.AppendLog((success ? "완료 · " : "실패 · ") + detail, job.Id);
        _gate.Reset();
        StatusText = (success ? "완료 · " : "실패 · ") + job.FileName;
    }

    private JobItem Get(Guid id) => Jobs.FirstOrDefault(j => j.Id == id) ?? throw new ArgumentException("작업을 찾을 수 없습니다.");
    private static bool IsActive(JobItem job) => job.Status is JobStatus.Starting or JobStatus.Running or JobStatus.Saving or JobStatus.NeedsAttention;
}
