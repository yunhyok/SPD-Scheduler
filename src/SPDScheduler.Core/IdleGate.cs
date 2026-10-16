namespace SPDScheduler.Core;

public sealed class IdleGate
{
    private ProcessSnapshot? _previous;
    private DateTimeOffset? _lowSince;
    private readonly Dictionary<ProcessIdentity, (long Min, long Max)> _memory = [];
    private readonly HashSet<ProcessIdentity> _runningRoots = [];
    private readonly Dictionary<ProcessIdentity, DateTimeOffset> _completedRoots = [];
    private readonly HashSet<ProcessIdentity> _approvedBbs = [];
    private ProcessSnapshot? _qualifiedBaseline, _qualifiedCurrent;
    public string Details { get; private set; } = "자동 진행 중 외부 PowerSI를 관측하면 감시 정보가 표시됩니다.";

    public void Reset() { ResetIdle(); _runningRoots.Clear(); _completedRoots.Clear(); }

    private void ResetIdle()
    {
        _previous = null; _lowSince = null; _memory.Clear();
        _qualifiedBaseline = null; _qualifiedCurrent = null;
    }

    public GateDecision Confirm(ProcessSnapshot snapshot, AppSettings settings)
    {
        var qualified = _qualifiedCurrent;
        var baseline = _qualifiedBaseline;
        if (qualified is null || baseline is null) return new(false, false, "유휴 관측을 먼저 완료해야 합니다.");
        var prior = qualified.Processes.ToDictionary(p => p.Identity);
        var seconds = (snapshot.CapturedAt - qualified.CapturedAt).TotalSeconds;
        if (!snapshot.Complete || snapshot.Processes.Any(p => !p.Accessible) ||
            seconds < 0 || seconds > settings.PollIntervalMinutes * 60 + qualified.ObservationDuration.TotalSeconds + 10 ||
            !prior.Keys.ToHashSet().SetEquals(snapshot.Processes.Select(p => p.Identity)) ||
            snapshot.Processes.Any(p => InvalidCounters(p, prior[p.Identity])))
        {
            Reset();
            return new(false, false, "실행 직전 프로세스 또는 관측 정보가 변경되었습니다.");
        }
        var allowedPostProcessing = AllowedPostProcessing(snapshot.Processes, settings);
        var children = snapshot.Processes.Where(p => !IsPowerSi(p.Identity) && !allowedPostProcessing.Contains(p.Identity)).ToArray();
        var cpuDelta = children.Sum(p => p.CpuSeconds - prior[p.Identity].CpuSeconds);
        // Ignore at most two Windows CPU counter ticks per process during a subsecond recheck.
        var cpuAllowance = seconds < 1 ? children.Length * 0.032 : 0;
        // Reading GUI status itself consumes GUI CPU/I/O; its counters use the full measured interval below.
        var ioDelta = children.Sum(p => (double)(ReadWriteBytes(p) - ReadWriteBytes(prior[p.Identity])));
        var shortIoBusy = settings.IdleIoKiBPerSecond == 0 ? ioDelta > 0 :
            ioDelta > 0 && ioDelta >= settings.IdleIoKiBPerSecond * 1024d * seconds;
        if ((cpuDelta > cpuAllowance && (seconds == 0 || cpuDelta / seconds * 100 >= settings.IdleCpuPercent)) || shortIoBusy)
        {
            Reset();
            return new(false, false, "실행 직전 새로운 CPU/I/O 활동이 확인되었습니다.");
        }
        // Reuse the measured interval; an immediate capture must not erase sustained idle time.
        _previous = baseline;
        return EvaluateCore(snapshot, settings, null, qualified.ObservationDuration);
    }

    public GateDecision Evaluate(ProcessSnapshot snapshot, AppSettings settings,
        IReadOnlyCollection<ProcessIdentity>? excluded = null) => EvaluateCore(snapshot, settings, excluded, TimeSpan.Zero);

    private GateDecision EvaluateCore(ProcessSnapshot snapshot, AppSettings settings,
        IReadOnlyCollection<ProcessIdentity>? excluded, TimeSpan confirmationDelay)
    {
        _qualifiedBaseline = null; _qualifiedCurrent = null;
        var processorCount = Math.Max(1, snapshot.LogicalProcessorCount);
        var limits = $"CPU 한도: {settings.IdleCpuPercent:F2}% (1코어 합계) = {settings.IdleCpuPercent / processorCount:F4}% (전체 PC)\r\n" +
            $"읽기/쓰기 I/O 한도: {settings.IdleIoKiBPerSecond} KiB/s · 메모리 변화 한도: {settings.IdleMemoryMiB} MiB · 유휴 유지: {settings.IdleSeconds}초";
        Details = $"측정: {snapshot.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} · 논리 프로세서 {processorCount}개\r\n{limits}\r\n" +
            string.Join("\r\n", snapshot.Processes.Select(p => $"PID {p.Identity.Pid} · {Path.GetFileName(p.Identity.ImagePath)} · 부모 PID {p.ParentPid} · 상태 {p.Signal}\r\n  근거: {p.Evidence?.Replace("\r", "").Replace("\n", "\r\n    ") ?? "없음"}"));
        if (!snapshot.Complete)
        {
            Reset();
            Details += "\r\n관측 불완전: " + snapshot.Error;
            return new(false, false, snapshot.Error ?? "PowerSI 프로세스 정보를 확인할 수 없습니다.");
        }
        var processes = snapshot.Processes.Where(p => excluded is null || !excluded.Contains(p.Identity)).ToArray();
        if (processes.Length == 0)
        {
            Reset(); _approvedBbs.Clear(); _qualifiedBaseline = snapshot; _qualifiedCurrent = snapshot;
            return new(true, false, "다른 PowerSI 작업이 없습니다.");
        }
        var current = snapshot with { Processes = processes };
        if (processes.Any(p => !p.Accessible || p.OtherIoBytes > p.IoBytes))
        {
            Reset();
            return new(false, false, "PowerSI 접근 권한 또는 관측 정보가 부족합니다.");
        }
        var allowedPostProcessing = AllowedPostProcessing(processes, settings);
        var parallel = allowedPostProcessing.Count > 0 ? "BbsCon 후처리 병행 허용 · " : "";
        Details += $"\r\nBbsCon 후처리 병행: {(settings.AllowBbsPostProcessing ? "허용" : "차단")} · 판정 제외 프로세스: {allowedPostProcessing.Count}개";
        var roots = processes.Where(p => IsPowerSi(p.Identity)).ToArray();
        var rootIdentities = roots.Select(p => p.Identity).ToHashSet();
        _runningRoots.RemoveWhere(identity => !rootIdentities.Contains(identity));
        foreach (var identity in _completedRoots.Keys.Where(identity => !rootIdentities.Contains(identity)).ToArray()) _completedRoots.Remove(identity);
        foreach (var root in roots)
        {
            if (root.Signal == ProcessSignal.Running) { _runningRoots.Add(root.Identity); _completedRoots.Remove(root.Identity); }
            else if (root.Signal == ProcessSignal.Completed)
            {
                if (_runningRoots.Remove(root.Identity)) _completedRoots[root.Identity] = current.CapturedAt;
            }
            else { _runningRoots.Remove(root.Identity); _completedRoots.Remove(root.Identity); }
        }
        var busy = processes.FirstOrDefault(p => p.Signal is ProcessSignal.Running or ProcessSignal.Suspended or ProcessSignal.LicenseWaiting or ProcessSignal.Error);
        if (busy is not null)
        {
            ResetIdle();
            return new(false, false, busy.Evidence ?? $"PowerSI {busy.Identity.Pid}: {busy.Signal}");
        }
        // A completion label can precede child-process flushing; still require sustained quiet.
        if (_previous is null || !_previous.Processes.Select(p => p.Identity).ToHashSet().SetEquals(processes.Select(p => p.Identity)))
        {
            ResetIdle();
            _previous = current;
            return new(false, false, parallel.Length > 0 ? parallel + "다음 작업 시작 부하를 관측합니다." : "PowerSI 유휴 상태를 관측합니다.");
        }
        var seconds = (current.CapturedAt - _previous.CapturedAt).TotalSeconds;
        if (seconds <= 0 || seconds > settings.PollIntervalMinutes * 60 + _previous.ObservationDuration.TotalSeconds + confirmationDelay.TotalSeconds + 10)
        {
            Reset();
            _previous = current;
            return new(false, false, "관측 간격이 끊겨 유휴 시간을 다시 측정합니다.");
        }
        var prior = _previous.Processes.ToDictionary(p => p.Identity);
        if (processes.Any(p => InvalidCounters(p, prior[p.Identity])))
        {
            Reset(); _previous = current;
            return new(false, false, "프로세스 카운터가 변경되어 다시 관측합니다.");
        }
        var decisionProcesses = processes.Where(p => !allowedPostProcessing.Contains(p.Identity)).ToArray();
        var observedCpu = processes.Sum(p => p.CpuSeconds - prior[p.Identity].CpuSeconds) / seconds * 100;
        var observedIo = processes.Sum(p => (double)(p.IoBytes - prior[p.Identity].IoBytes)) / seconds;
        var observedOtherIo = processes.Sum(p => (double)(p.OtherIoBytes - prior[p.Identity].OtherIoBytes)) / seconds;
        var cpu = decisionProcesses.Sum(p => p.CpuSeconds - prior[p.Identity].CpuSeconds) / seconds * 100;
        bool CompletedGuiIo(ProcessSample p) => IsPowerSi(p.Identity) && p.Signal == ProcessSignal.Completed &&
            prior[p.Identity].Signal == ProcessSignal.Completed;
        var io = decisionProcesses.Where(p => !CompletedGuiIo(p))
            .Sum(p => (double)(ReadWriteBytes(p) - ReadWriteBytes(prior[p.Identity]))) / seconds;
        foreach (var process in processes)
        {
            var baseline = _memory.GetValueOrDefault(process.Identity, (Min: prior[process.Identity].PrivateBytes, Max: prior[process.Identity].PrivateBytes));
            _memory[process.Identity] = (Math.Min(baseline.Min, process.PrivateBytes), Math.Max(baseline.Max, process.PrivateBytes));
        }
        var observedMemoryChange = _memory.Values.Sum(value => (double)value.Max - value.Min);
        var memoryChange = decisionProcesses.Sum(p => (double)_memory[p.Identity].Max - _memory[p.Identity].Min);
        var ioBusy = settings.IdleIoKiBPerSecond == 0 ? io > 0 : io >= settings.IdleIoKiBPerSecond * 1024d;
        var blockedBy = new List<string>();
        if (cpu >= settings.IdleCpuPercent) blockedBy.Add("CPU");
        if (ioBusy) blockedBy.Add("I/O");
        if (memoryChange > settings.IdleMemoryMiB * 1048576d) blockedBy.Add("메모리 변화");
        Details = $"측정: {current.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} · 최근 {seconds:F1}초 평균 · 논리 프로세서 {processorCount}개\r\n{limits}\r\n" +
            $"관측 합계 CPU: {observedCpu / processorCount:F2}% (전체 PC), {observedCpu:F2}% (1코어) · I/O 전체: {observedIo / 1024:F1} KiB/s (기타: {observedOtherIo / 1024:F1}) · 메모리 변화: {observedMemoryChange / 1048576:F1} MiB\r\n" +
            $"판정 부하 CPU: {cpu / processorCount:F2}% (전체 PC), {cpu:F2}% (1코어) · 읽기/쓰기 I/O: {io / 1024:F1} KiB/s · 메모리 변화: {memoryChange / 1048576:F1} MiB\r\n" +
            $"BbsCon 후처리 병행: {(settings.AllowBbsPostProcessing ? "허용" : "차단")} · 판정 제외 프로세스: {allowedPostProcessing.Count}개\r\n" +
            $"기준 초과: {(blockedBy.Count == 0 ? "없음" : string.Join(", ", blockedBy))}\r\n\r\n" +
            string.Join("\r\n", processes.OrderByDescending(p => p.CpuSeconds - prior[p.Identity].CpuSeconds).Select(p =>
            {
                var processCpu = (p.CpuSeconds - prior[p.Identity].CpuSeconds) / seconds * 100;
                var processIo = (p.IoBytes - prior[p.Identity].IoBytes) / seconds / 1024;
                var processOtherIo = (p.OtherIoBytes - prior[p.Identity].OtherIoBytes) / seconds / 1024;
                var memory = _memory[p.Identity];
                var policy = allowedPostProcessing.Contains(p.Identity) ? "BbsCon 후처리 병행: CPU/메모리/I/O 판정 제외 (계속 관측)" :
                    CompletedGuiIo(p) ? "동일 GUI 완료 상태 연속 확인: I/O만 판정 제외" : "CPU/메모리/읽기·쓰기 I/O 판정 포함";
                return $"PID {p.Identity.Pid} · {Path.GetFileName(p.Identity.ImagePath)} · 부모 PID {p.ParentPid}\r\n" +
                    $"  CPU {processCpu / processorCount:F2}% (전체), {processCpu:F2}% (1코어) · I/O 전체 {processIo:F1} KiB/s (읽기·쓰기 {processIo - processOtherIo:F1}, 기타 {processOtherIo:F1}) · 메모리 변화 {(memory.Max - (double)memory.Min) / 1048576:F1} MiB · 상태 {p.Signal}\r\n" +
                    $"  판정: {policy}\r\n" +
                    $"  근거: {p.Evidence?.Replace("\r", "").Replace("\n", "\r\n    ") ?? "없음"}";
            }));
        if (blockedBy.Count != 0)
        {
            _lowSince = null;
            _memory.Clear();
            _previous = current;
            return new(false, false, $"{parallel}PowerSI 리소스 대기 · 판정 CPU {cpu / processorCount:F2}% (전체) · 기준 초과: {string.Join(", ", blockedBy)}");
        }
        _lowSince ??= _previous.CapturedAt;
        var measuredBaseline = _previous;
        _previous = current;
        var elapsed = (current.CapturedAt - _lowSince.Value).TotalSeconds;
        var completed = roots.Length > 0 && roots.All(root => root.Signal == ProcessSignal.Completed &&
            _completedRoots.TryGetValue(root.Identity, out var completedAt) &&
            (current.CapturedAt - completedAt).TotalSeconds >= settings.PollIntervalMinutes * 60);
        var required = completed ? Math.Min(settings.IdleSeconds, settings.PollIntervalMinutes * 60) : settings.IdleSeconds;
        if (elapsed >= required) { _qualifiedBaseline = measuredBaseline; _qualifiedCurrent = current; }
        return elapsed >= required
            ? new(true, true, parallel.Length > 0 ? $"{parallel}판정 대상 {required}초 연속 저부하" : completed ? $"완료 전이 확인 · {required}초 연속 저부하 (외부 유휴 추정)" : $"유휴 추정 · {settings.IdleSeconds}초 연속 저부하 (오판 가능)")
            : new(false, false, $"{parallel}판정 대상 관측 {elapsed:F0}/{settings.IdleSeconds}초 · CPU {cpu / processorCount:F2}% (전체)");
    }

    private static bool IsPowerSi(ProcessIdentity identity) =>
        Path.GetFileNameWithoutExtension(identity.ImagePath).Equals("powersi", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(identity.ImagePath).Equals("pwrsi", StringComparison.OrdinalIgnoreCase);

    private static ulong ReadWriteBytes(ProcessSample sample) => sample.IoBytes - sample.OtherIoBytes;

    private static bool InvalidCounters(ProcessSample sample, ProcessSample prior) =>
        sample.OtherIoBytes > sample.IoBytes || prior.OtherIoBytes > prior.IoBytes ||
        sample.CpuSeconds < prior.CpuSeconds || sample.IoBytes < prior.IoBytes || sample.OtherIoBytes < prior.OtherIoBytes ||
        ReadWriteBytes(sample) < ReadWriteBytes(prior);

    private HashSet<ProcessIdentity> AllowedPostProcessing(IReadOnlyList<ProcessSample> processes, AppSettings settings)
    {
        var byPid = processes.ToDictionary(p => p.Identity.Pid);
        var identities = processes.Select(p => p.Identity).ToHashSet();
        _approvedBbs.RemoveWhere(identity => !identities.Contains(identity));
        foreach (var bbs in processes.Where(p => Path.GetFileName(p.Identity.ImagePath).Equals("BbsCon.exe", StringComparison.OrdinalIgnoreCase)))
        {
            if (!Path.IsPathFullyQualified(bbs.Identity.ImagePath)) continue;
            var child = bbs;
            var visited = new HashSet<int> { child.Identity.Pid };
            while (byPid.TryGetValue(child.ParentPid, out var parent) && visited.Add(parent.Identity.Pid) &&
                child.Identity.StartedAt >= parent.Identity.StartedAt)
            {
                if (IsPowerSi(parent.Identity))
                {
                    if (Path.IsPathFullyQualified(parent.Identity.ImagePath) &&
                        StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(bbs.Identity.ImagePath), Path.GetDirectoryName(parent.Identity.ImagePath)))
                        _approvedBbs.Add(bbs.Identity);
                    break;
                }
                child = parent;
            }
        }
        if (!settings.AllowBbsPostProcessing) return [];
        // Keep approved BbsCon identities after GUI exit; a missing/reused identity must be approved again.
        var allowed = _approvedBbs.ToHashSet();
        bool added;
        do
        {
            added = false;
            foreach (var process in processes)
            {
                if (allowed.Contains(process.Identity) || !IsConhost(process.Identity) ||
                    !byPid.TryGetValue(process.ParentPid, out var parent) || !allowed.Contains(parent.Identity) ||
                    process.Identity.StartedAt < parent.Identity.StartedAt) continue;
                allowed.Add(process.Identity); added = true;
            }
        } while (added);
        return allowed;
    }

    private static bool IsConhost(ProcessIdentity identity)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return StringComparer.OrdinalIgnoreCase.Equals(identity.ImagePath, Path.Combine(windows, "System32", "conhost.exe")) ||
            StringComparer.OrdinalIgnoreCase.Equals(identity.ImagePath, Path.Combine(windows, "SysWOW64", "conhost.exe"));
    }
}
