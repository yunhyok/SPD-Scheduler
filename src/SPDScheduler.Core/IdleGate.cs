namespace SPDScheduler.Core;

public sealed class IdleGate
{
    private ProcessSnapshot? _previous;
    private DateTimeOffset? _lowSince;
    private readonly Dictionary<ProcessIdentity, (long Min, long Max)> _memory = [];

    public void Reset() { _previous = null; _lowSince = null; _memory.Clear(); }

    public GateDecision Evaluate(ProcessSnapshot snapshot, AppSettings settings,
        IReadOnlyCollection<ProcessIdentity>? excluded = null)
    {
        if (!snapshot.Complete)
        {
            Reset();
            return new(false, false, snapshot.Error ?? "PowerSI 프로세스 정보를 확인할 수 없습니다.");
        }
        var processes = snapshot.Processes.Where(p => excluded is null || !excluded.Contains(p.Identity)).ToArray();
        if (processes.Length == 0) { Reset(); return new(true, false, "다른 PowerSI 작업이 없습니다."); }
        var current = new ProcessSnapshot(snapshot.CapturedAt, processes);
        if (processes.Any(p => !p.Accessible))
        {
            Reset();
            return new(false, false, "PowerSI 접근 권한 또는 관측 정보가 부족합니다.");
        }
        var busy = processes.FirstOrDefault(p => p.Signal is ProcessSignal.Running or ProcessSignal.Suspended or ProcessSignal.LicenseWaiting or ProcessSignal.Error);
        if (busy is not null)
        {
            Reset();
            return new(false, false, busy.Evidence ?? $"PowerSI {busy.Identity.Pid}: {busy.Signal}");
        }
        // A completion label can precede child-process flushing; still require sustained quiet.
        if (_previous is null || !_previous.Processes.Select(p => p.Identity).ToHashSet().SetEquals(processes.Select(p => p.Identity)))
        {
            Reset();
            _previous = current;
            return new(false, false, "PowerSI 유휴 상태를 관측합니다.");
        }
        var seconds = (current.CapturedAt - _previous.CapturedAt).TotalSeconds;
        if (seconds <= 0 || seconds > settings.PollIntervalMinutes * 60 + 10)
        {
            Reset();
            _previous = current;
            return new(false, false, "관측 간격이 끊겨 유휴 시간을 다시 측정합니다.");
        }
        var prior = _previous.Processes.ToDictionary(p => p.Identity);
        if (processes.Any(p => p.CpuSeconds < prior[p.Identity].CpuSeconds || p.IoBytes < prior[p.Identity].IoBytes))
        {
            Reset(); _previous = current;
            return new(false, false, "프로세스 카운터가 변경되어 다시 관측합니다.");
        }
        var cpu = processes.Sum(p => p.CpuSeconds - prior[p.Identity].CpuSeconds) / seconds * 100;
        var io = processes.Sum(p => (double)(p.IoBytes - prior[p.Identity].IoBytes)) / seconds;
        foreach (var process in processes)
        {
            var baseline = _memory.GetValueOrDefault(process.Identity, (Min: prior[process.Identity].PrivateBytes, Max: prior[process.Identity].PrivateBytes));
            _memory[process.Identity] = (Math.Min(baseline.Min, process.PrivateBytes), Math.Max(baseline.Max, process.PrivateBytes));
        }
        var memoryChange = _memory.Values.Sum(value => (double)value.Max - value.Min);
        var ioBusy = settings.IdleIoKiBPerSecond == 0 ? io > 0 : io >= settings.IdleIoKiBPerSecond * 1024d;
        if (cpu >= settings.IdleCpuPercent || ioBusy || memoryChange > settings.IdleMemoryMiB * 1048576d)
        {
            _lowSince = null;
            _memory.Clear();
            _previous = current;
            return new(false, false, $"PowerSI 사용 중 · CPU {cpu:F2}% (1코어 기준), I/O {io / 1024:F1} KiB/s");
        }
        _lowSince ??= _previous.CapturedAt;
        _previous = current;
        var elapsed = (current.CapturedAt - _lowSince.Value).TotalSeconds;
        return elapsed >= settings.IdleSeconds
            ? new(true, true, $"유휴 추정 · {settings.IdleSeconds}초 연속 저부하 (오판 가능)")
            : new(false, false, $"유휴 관측 {elapsed:F0}/{settings.IdleSeconds}초 · CPU {cpu:F2}% (1코어 기준)");
    }
}
