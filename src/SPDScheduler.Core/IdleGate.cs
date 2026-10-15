namespace SPDScheduler.Core;

public sealed class IdleGate
{
    private ProcessSnapshot? _previous;
    private DateTimeOffset? _lowSince;
    private readonly Dictionary<ProcessIdentity, (long Min, long Max)> _memory = [];
    public string Details { get; private set; } = "자동 진행 중 외부 PowerSI를 관측하면 감시 정보가 표시됩니다.";

    public void Reset() { _previous = null; _lowSince = null; _memory.Clear(); }

    public GateDecision Evaluate(ProcessSnapshot snapshot, AppSettings settings,
        IReadOnlyCollection<ProcessIdentity>? excluded = null)
    {
        var processorCount = Math.Max(1, snapshot.LogicalProcessorCount);
        var limits = $"CPU 한도: {settings.IdleCpuPercent:F2}% (1코어 합계) = {settings.IdleCpuPercent / processorCount:F4}% (전체 PC)\r\n" +
            $"I/O 한도: {settings.IdleIoKiBPerSecond} KiB/s · 메모리 변화 한도: {settings.IdleMemoryMiB} MiB · 유휴 유지: {settings.IdleSeconds}초";
        Details = $"측정: {snapshot.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} · 논리 프로세서 {processorCount}개\r\n{limits}\r\n" +
            string.Join("\r\n", snapshot.Processes.Select(p => $"PID {p.Identity.Pid} · {Path.GetFileName(p.Identity.ImagePath)} · 부모 PID {p.ParentPid} · 상태 {p.Signal}"));
        if (!snapshot.Complete)
        {
            Reset();
            Details += "\r\n관측 불완전: " + snapshot.Error;
            return new(false, false, snapshot.Error ?? "PowerSI 프로세스 정보를 확인할 수 없습니다.");
        }
        var processes = snapshot.Processes.Where(p => excluded is null || !excluded.Contains(p.Identity)).ToArray();
        if (processes.Length == 0) { Reset(); return new(true, false, "다른 PowerSI 작업이 없습니다."); }
        var current = snapshot with { Processes = processes };
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
        var blockedBy = new List<string>();
        if (cpu >= settings.IdleCpuPercent) blockedBy.Add("CPU");
        if (ioBusy) blockedBy.Add("I/O");
        if (memoryChange > settings.IdleMemoryMiB * 1048576d) blockedBy.Add("메모리 변화");
        Details = $"측정: {current.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} · 최근 {seconds:F1}초 평균 · 논리 프로세서 {processorCount}개\r\n{limits}\r\n" +
            $"합계 CPU: {cpu / processorCount:F2}% (전체 PC), {cpu:F2}% (1코어) · I/O: {io / 1024:F1} KiB/s · 메모리 변화: {memoryChange / 1048576:F1} MiB\r\n" +
            $"기준 초과: {(blockedBy.Count == 0 ? "없음" : string.Join(", ", blockedBy))}\r\n\r\n" +
            string.Join("\r\n", processes.OrderByDescending(p => p.CpuSeconds - prior[p.Identity].CpuSeconds).Select(p =>
            {
                var processCpu = (p.CpuSeconds - prior[p.Identity].CpuSeconds) / seconds * 100;
                var processIo = (p.IoBytes - prior[p.Identity].IoBytes) / seconds / 1024;
                var memory = _memory[p.Identity];
                return $"PID {p.Identity.Pid} · {Path.GetFileName(p.Identity.ImagePath)} · 부모 PID {p.ParentPid}\r\n" +
                    $"  CPU {processCpu / processorCount:F2}% (전체), {processCpu:F2}% (1코어) · I/O {processIo:F1} KiB/s · 메모리 변화 {(memory.Max - (double)memory.Min) / 1048576:F1} MiB · 상태 {p.Signal}";
            }));
        if (blockedBy.Count != 0)
        {
            _lowSince = null;
            _memory.Clear();
            _previous = current;
            return new(false, false, $"PowerSI 리소스 대기 · CPU {cpu / processorCount:F2}% (전체) · 기준 초과: {string.Join(", ", blockedBy)}");
        }
        _lowSince ??= _previous.CapturedAt;
        _previous = current;
        var elapsed = (current.CapturedAt - _lowSince.Value).TotalSeconds;
        return elapsed >= settings.IdleSeconds
            ? new(true, true, $"유휴 추정 · {settings.IdleSeconds}초 연속 저부하 (오판 가능)")
            : new(false, false, $"유휴 관측 {elapsed:F0}/{settings.IdleSeconds}초 · CPU {cpu / processorCount:F2}% (전체)");
    }
}
