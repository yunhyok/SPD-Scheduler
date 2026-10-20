using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using SPDScheduler.Core;

namespace SPDScheduler.Windows;

public sealed class PowerSiRunner : IPowerSiRunner
{
    private readonly ConcurrentDictionary<Guid, EvidenceState> evidence = new();
    private readonly Func<ProcessIdentity, (bool Alive, bool Readable, string? Detail)> identityState;
    private readonly Func<ProcessIdentity, CancellationToken, Task<string?>> readStatus;

    public PowerSiRunner() : this(WindowsProcessProbe.IdentityState, WindowsTextWorker.ReadAsync) { }

    internal PowerSiRunner(Func<ProcessIdentity, (bool Alive, bool Readable, string? Detail)> identityState,
        Func<ProcessIdentity, CancellationToken, Task<string?>> readStatus)
    {
        this.identityState = identityState;
        this.readStatus = readStatus;
    }

    private sealed class EvidenceState
    {
        internal bool SawNonCompletedUi, Started, Completed;
        internal bool CompletedViaUi;
        internal DateTimeOffset? CompletionAt;
        internal readonly Dictionary<int, (double Cpu, ulong Io)> Counters = [];
        internal CompletionRule? Rule;
        internal bool RuleChecked;
        internal bool RuleStarted;
        internal string? ObservedLog;
    }
    private sealed record Freshness(Guid RunId, bool SawNonCompletedUi, bool Started, bool RuleStarted);
    public sealed record LogEvidence(bool Started, bool Completed, bool NewerStart, bool Blocked, bool Reliable);

    public void ValidateConfiguration(AppSettings settings) =>
        settings.PowerSiPath = PowerSiDiscovery.ResolveExecutablePath(settings.PowerSiPath);

    public async Task<ProcessIdentity> LaunchAsync(JobItem job, AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfiguration(settings);
        string executable = settings.PowerSiPath;
        string spd = Path.GetFullPath(job.FilePath);
        if (!File.Exists(spd) || !Path.GetExtension(spd).Equals(".spd", StringComparison.OrdinalIgnoreCase)) throw new IOException("SPD file is unavailable.");
        var run = job.Run ??= new RunRecord();
        if (run.Process != null) throw new InvalidOperationException("This run already has a process identity.");
        if (string.IsNullOrWhiteSpace(run.DirectoryPath))
            run.DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SPDScheduler", "runs", run.Id.ToString("N"));
        run.DirectoryPath = Path.GetFullPath(run.DirectoryPath);
        Directory.CreateDirectory(run.DirectoryPath);
        if (Directory.EnumerateFileSystemEntries(run.DirectoryPath).Any()) throw new IOException("The run folder must be new and empty.");
        run.CreatedAt = DateTimeOffset.UtcNow;
        var script = Path.Combine(run.DirectoryPath, "run.tcl");
        await File.WriteAllTextAsync(script, CreateScript(run.Id, spd, run.DirectoryPath), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(spd)!, UseShellExecute = false, CreateNoWindow = false
        };
        // shortcut: GUI -tcl startup is unverified; validate it on a separately authorized PC.
        start.ArgumentList.Add("-tcl");
        start.ArgumentList.Add(script);
        // Native per-run log files survive scheduler exit without a redirected pipe dependency.
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        try { if (!process.Start()) throw new IOException("PowerSI did not start."); }
        catch { process.Dispose(); throw; }
        ProcessIdentity identity;
        try { identity = new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), executable); }
        catch
        {
            run.FailureReason = "PowerSI was launched but its identity could not be established; manual inspection required.";
            throw;
        }
        run.Process = identity;
        evidence[run.Id] = new();
        return identity;
    }

    public async Task<RunObservation> InspectAsync(JobItem job, AppSettings settings, ProcessSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = job.Run ?? throw new InvalidOperationException("Run record is missing.");
        if (run.Process == null) return new(RunStage.Unknown, "Run process identity is missing.", false);
        RefreshChildren(run, snapshot);
        var owned = snapshot.Processes.Where(p => IsOwned(run, p.Identity)).ToArray();
        var identities = run.Children.Prepend(run.Process).Select(identity => (Identity: identity, State: identityState(identity))).ToArray();
        bool identitiesReadable = identities.All(identity => identity.State.Readable);
        bool alive = !identitiesReadable || owned.Length != 0 || identities.Any(identity => identity.State.Alive);
        bool activity = false;
        var state = evidence.GetOrAdd(run.Id, _ => LoadEvidence(run));
        InvalidateCompletion(run, state);
        if (!identities[0].State.Readable) return PendingIdentity();
        foreach (var sample in owned)
        {
            if (state.Counters.TryGetValue(sample.Identity.Pid, out var before))
                activity |= sample.CpuSeconds > before.Cpu || sample.IoBytes > before.Io;
            state.Counters[sample.Identity.Pid] = (sample.CpuSeconds, sample.IoBytes);
        }
        var markers = ReadMarkers(run);
        if (markers.Contains("EXIT_FAILED")) return new(RunStage.Unknown, "Saving succeeded but PowerSI rejected exit; inspect this run's error.txt and GUI.", alive, activity);
        if (markers.Contains("SAVED"))
        {
            if (!identitiesReadable) return PendingIdentity();
            if (!snapshot.Complete) return new(RunStage.Saving, "Save is confirmed; process exit cannot yet be confirmed.", true, activity);
            return new(alive ? RunStage.Saving : RunStage.Succeeded,
                alive ? "Save is confirmed; waiting for this run and its solver processes to exit." : "Save confirmed and owned processes exited.", alive, activity);
        }
        if (markers.Contains("SAVE_FAILED")) return new(RunStage.SaveFailed, "PowerSI rejected saving. The process is preserved.", alive, activity);
        if (markers.Contains("START_REJECTED"))
            return new(state.Started || run.StartedObserved ? RunStage.RuntimeFailed : RunStage.StartFailed,
                "The current-run Tcl command reported an error; see this run's error.txt.", alive, activity);
        if (markers.Contains("BEGIN_FAILED")) return new(RunStage.RuntimeFailed, "begin simulation failed; solver activity is uncertain and the process is preserved.", alive, activity);
        if (TokenMatches(run, "SAVE_EXIT", "control.txt")) return identitiesReadable
            ? new(RunStage.Saving, "Save/exit was requested; waiting for SAVED and process exit.", alive, activity) : PendingIdentity();
        if (!alive) return new(RunStage.Unknown, "Process exited without a current-run save confirmation.", false, activity);
        if (!markers.Contains("OPENED")) return identitiesReadable
            ? new(RunStage.Starting, "Waiting for the new GUI to execute its Tcl script.", true, activity) : PendingIdentity();

        var status = await readStatus(run.Process, cancellationToken).ConfigureAwait(false);
        bool uiCompleted = false;
        var uiSignal = WindowsProcessProbe.ClassifyStatus(status);
        if (status != null)
        {
            var lines = status.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool done = lines.Any(line => line.Equals("Simulation Done.", StringComparison.Ordinal));
            bool simulation = lines.Any(line => line.StartsWith("Simulation", StringComparison.Ordinal) && !line.Equals("Simulation Done.", StringComparison.Ordinal));
            if (simulation && !done && markers.Contains("BEGIN_REQUESTED")) { state.SawNonCompletedUi = true; state.Started = true; }
            if (done && state.SawNonCompletedUi && markers.Contains("BEGIN_REQUESTED"))
                uiCompleted = true;
            if (uiSignal is ProcessSignal.Running or ProcessSignal.Suspended or ProcessSignal.LicenseWaiting or ProcessSignal.Error)
            {
                run.StartedObserved |= state.Started;
                await PersistEvidenceAsync(run, state, cancellationToken).ConfigureAwait(false);
                if (!identitiesReadable) return PendingIdentity();
                return new(RunStage.Running, "PowerSI reports an active or blocked simulation; no save or next job will start.", true, activity);
            }
        }

        if (!state.RuleChecked)
        {
            state.Rule = await FindRuleAsync(job.FilePath, settings.CompletionRules, cancellationToken).ConfigureAwait(false);
            state.RuleChecked = true;
            run.Workflow = state.Rule?.Workflow;
        }
        string output = ReadTail(Path.Combine(run.DirectoryPath, "output.log"));
        var log = new LogEvidence(state.RuleStarted, false, false, false, true);
        if (state.Rule != null && markers.Contains("BEGIN_REQUESTED"))
        {
            log = EvaluateLogEvidence(output, state.Rule, state.RuleStarted);
            if (log.Reliable && log.Started) { state.RuleStarted = true; state.Started = true; }
        }
        state.CompletedViaUi = uiCompleted && log.Reliable && !log.NewerStart && !log.Blocked;
        state.Completed = state.CompletedViaUi || log.Completed;
        run.StartedObserved |= state.Started;
        await PersistEvidenceAsync(run, state, cancellationToken).ConfigureAwait(false);
        if (!identitiesReadable) return PendingIdentity();
        if (state.Completed && snapshot.Complete)
        {
            state.CompletionAt = DateTimeOffset.UtcNow;
            state.ObservedLog = output;
            await WriteTokenAsync(run, "COMPLETION_OBSERVED", "completion.txt", cancellationToken).ConfigureAwait(false);
            return new(RunStage.ReadyToSave, "Fresh current-run completion evidence was observed.", true, activity);
        }
        return new(state.Started ? RunStage.Running : RunStage.Starting,
            markers.Contains("BEGIN_RETURNED") ? "begin simulation returned; completion is not yet confirmed." : "Waiting for fresh simulation status or a matching workflow log rule.", true, activity);

        RunObservation PendingIdentity()
        {
            string detail = string.Join("; ", identities.Where(item => !item.State.Readable).Select(item =>
                $"PID {item.Identity.Pid} ({Path.GetFileName(item.Identity.ImagePath)}): {item.State.Detail ?? "조회 실패"}"));
            return new(RunStage.ObservationPending, "프로세스 확인 재시도 대기 · " + detail, true, activity);
        }
    }

    public async Task RequestSaveAndExitAsync(JobItem job, CancellationToken cancellationToken)
    {
        var run = job.Run ?? throw new InvalidOperationException("Run record is missing.");
        if (TokenMatches(run, "SAVE_EXIT", "control.txt")) { run.SaveRequested = true; return; }
        if (!TokenMatches(run, "COMPLETION_OBSERVED", "completion.txt")) throw new InvalidOperationException("Fresh completion evidence is required before saving and exiting.");
        if (run.Process == null) throw new InvalidOperationException("Owned PowerSI identity is missing.");
        var identity = identityState(run.Process);
        // Without a delivered control token, the next inspection must observe completion again.
        if (!identity.Readable) return;
        if (!identity.Alive) throw new InvalidOperationException("Owned PowerSI identity changed.");
        if (!evidence.TryGetValue(run.Id, out var state) || !state.Completed || state.CompletionAt == null ||
            DateTimeOffset.UtcNow - state.CompletionAt > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Completion evidence must be observed again before saving.");
        var status = await readStatus(run.Process, cancellationToken).ConfigureAwait(false);
        if (WindowsProcessProbe.ClassifyStatus(status) is ProcessSignal.Running or ProcessSignal.Suspended or ProcessSignal.LicenseWaiting or ProcessSignal.Error)
        {
            InvalidateCompletion(run, state);
            throw new InvalidOperationException("PowerSI no longer reports completion; its process is preserved.");
        }
        if (state.CompletedViaUi && (status == null || !status.Split('\n').Any(line => line.Trim().Equals("Simulation Done.", StringComparison.Ordinal))))
        {
            InvalidateCompletion(run, state);
            throw new InvalidOperationException("The completion status could not be confirmed immediately before saving.");
        }
        string output = ReadTail(Path.Combine(run.DirectoryPath, "output.log"));
        if (state.Rule != null)
        {
            var log = EvaluateLogEvidence(output, state.Rule, state.RuleStarted);
            if (!log.Reliable || log.NewerStart || log.Blocked ||
                (!state.CompletedViaUi && (!log.Completed || output != state.ObservedLog)))
            {
                InvalidateCompletion(run, state);
                throw new InvalidOperationException("The current-run log changed or no longer confirms completion; observe it again before saving.");
            }
        }
        await WriteTokenAsync(run, "SAVE_EXIT", "control.txt", cancellationToken).ConfigureAwait(false);
        run.SaveRequested = true;
    }

    public async Task<CleanupResult> CloseFailedRunAsync(JobItem job, ProcessSnapshot snapshot, CancellationToken cancellationToken)
    {
        var run = job.Run ?? throw new InvalidOperationException("Run record is missing.");
        RefreshChildren(run, snapshot);
        bool alive = snapshot.Processes.Any(p => IsOwned(run, p.Identity));
        var identities = (run.Process == null ? run.Children : run.Children.Prepend(run.Process)).Select(identityState).ToArray();
        alive |= identities.Any(identity => identity.Alive);
        if (run.Children.Select(identityState).Any(identity => identity.Alive || !identity.Readable))
            return new(CleanupState.Unsafe, "An owned solver child is alive or unreadable; cleanup is blocked.");
        if (!alive && snapshot.Complete && identities.All(identity => identity.Readable)) return new(CleanupState.Closed, "All owned processes have exited.");
        if (!ReadMarkers(run).Contains("START_REJECTED") || run.StartedObserved || run.SaveRequested)
            return new(CleanupState.Unsafe, "No confirmed rejected start; the PowerSI process is preserved.");
        if (snapshot.Processes.Any(p => run.Children.Any(child => WindowsProcessProbe.SameIdentity(child, p.Identity))))
            return new(CleanupState.Unsafe, "An owned solver child remains; cleanup waits for manual confirmation.");
        if (!snapshot.Complete || run.Process == null || !identityState(run.Process).Alive)
            return new(CleanupState.Unsafe, "Process ownership cannot be confirmed.");
        if (ReadMarkers(run).Contains("CLEANUP_FAILED")) return new(CleanupState.Unsafe, "PowerSI rejected no-save cleanup; inspect its GUI.");
        await WriteTokenAsync(run, "ABORT_NO_SAVE", "control.txt", cancellationToken).ConfigureAwait(false);
        run.CleanupRequestedAt ??= DateTimeOffset.UtcNow;
        return new(CleanupState.Pending, "Requested no-save exit of this run only; waiting for process exit.");
    }

    private static bool IsOwned(RunRecord run, ProcessIdentity identity) =>
        run.Process != null && WindowsProcessProbe.SameIdentity(run.Process, identity) ||
        run.Children.Any(child => WindowsProcessProbe.SameIdentity(child, identity));

    private static void RefreshChildren(RunRecord run, ProcessSnapshot snapshot)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var sample in snapshot.Processes)
            {
                if (IsOwned(run, sample.Identity)) continue;
                var parent = snapshot.Processes.FirstOrDefault(p => p.Identity.Pid == sample.ParentPid);
                if (parent != null && IsOwned(run, parent.Identity) && sample.Identity.StartedAt >= parent.Identity.StartedAt)
                { run.Children.Add(sample.Identity); changed = true; }
            }
        } while (changed);
    }

    private static HashSet<string> ReadMarkers(RunRecord run)
    {
        string prefix = run.Id.ToString("N") + " ";
        return ReadTail(Path.Combine(run.DirectoryPath, "events.log")).Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).Select(line => line[prefix.Length..]).ToHashSet(StringComparer.Ordinal);
    }

    private static EvidenceState LoadEvidence(RunRecord run)
    {
        try
        {
            string path = Path.Combine(run.DirectoryPath, "freshness.json");
            if (new FileInfo(path).Length > 4096) return new();
            var saved = JsonSerializer.Deserialize<Freshness>(File.ReadAllText(path));
            return saved?.RunId == run.Id ? new EvidenceState
            { SawNonCompletedUi = saved.SawNonCompletedUi, Started = saved.Started, RuleStarted = saved.RuleStarted } : new();
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
        catch (JsonException) { return new(); }
    }

    private static async Task PersistEvidenceAsync(RunRecord run, EvidenceState state, CancellationToken cancellationToken)
    {
        string path = Path.Combine(run.DirectoryPath, "freshness.json");
        string text = JsonSerializer.Serialize(new Freshness(run.Id, state.SawNonCompletedUi, state.Started, state.RuleStarted));
        if (File.Exists(path) && await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) == text) return;
        await File.WriteAllTextAsync(path + ".tmp", text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(path + ".tmp", path, true);
    }

    private static string ReadTail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 256 * 1024) stream.Seek(-256 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    internal static async Task<CompletionRule?> FindRuleAsync(string spd, IEnumerable<CompletionRule> rules, CancellationToken cancellationToken)
    {
        // SpdPattern matches one metadata line; an overlong line or an uncertain match rejects rule selection.
        const int maxLineCharacters = 1024 * 1024;
        var candidates = rules.Where(r => !string.IsNullOrWhiteSpace(r.SpdPattern) && !string.IsNullOrWhiteSpace(r.StartedPattern) && !string.IsNullOrWhiteSpace(r.CompletedPattern)).ToArray();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidates.Length == 0) return null;
            var patterns = candidates.Select(rule => new Regex(rule.SpdPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))).ToArray();
            var matched = new HashSet<int>();
            await using var stream = new FileStream(spd, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var buffer = new char[8192];
            var line = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                int offset = 0;
                while (offset < count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int newline = Array.IndexOf(buffer, '\n', offset, count - offset);
                    int length = (newline < 0 ? count : newline) - offset;
                    if (line.Length + length > maxLineCharacters) return null;
                    line.Append(buffer, offset, length);
                    if (newline < 0) break;
                    MatchLine();
                    if (matched.Count > 1) return null;
                    line.Clear();
                    offset = newline + 1;
                }
            }
            if (line.Length > 0) MatchLine();
            return matched.Count == 1 ? candidates[matched.Single()] : null;

            void MatchLine()
            {
                string text = line.ToString().TrimEnd('\r');
                for (int i = 0; i < patterns.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!matched.Contains(i) && patterns[i].IsMatch(text)) matched.Add(i);
                    if (matched.Count > 1) return;
                }
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static void InvalidateCompletion(RunRecord run, EvidenceState state)
    {
        state.Completed = false;
        state.CompletedViaUi = false;
        state.CompletionAt = null;
        state.ObservedLog = null;
        if (!string.IsNullOrWhiteSpace(run.DirectoryPath)) File.Delete(Path.Combine(run.DirectoryPath, "completion.txt"));
    }

    public static LogEvidence EvaluateLogEvidence(string output, CompletionRule rule, bool previouslyStarted)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rule.StartedPattern) || string.IsNullOrWhiteSpace(rule.CompletedPattern)) return new(previouslyStarted, false, false, false, false);
            int start = Last(rule.StartedPattern);
            int complete = Last(rule.CompletedPattern);
            int blocked = Last(@"(?i)Simulation\s+is\s+suspended\.|Insufficient memory|license[^\r\n]*(?:waiting|unavailable|denied)|waiting[^\r\n]*license");
            bool started = previouslyStarted || start >= 0;
            return new(started, started && complete >= 0 && complete > start && complete > blocked,
                start >= 0 && start >= complete, blocked >= 0 && blocked >= complete, true);

            int Last(string pattern)
            {
                var matches = Regex.Matches(output, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                return matches.Count == 0 ? -1 : matches[^1].Index;
            }
        }
        catch (ArgumentException) { return new(previouslyStarted, false, false, false, false); }
        catch (RegexMatchTimeoutException) { return new(previouslyStarted, false, false, false, false); }
    }

    private static bool TokenMatches(RunRecord run, string token, string file) =>
        ReadTail(Path.Combine(run.DirectoryPath, file)).Trim().Equals(token + " " + run.Id.ToString("N"), StringComparison.Ordinal);

    private static async Task WriteTokenAsync(RunRecord run, string token, string file, CancellationToken cancellationToken)
    {
        string path = Path.Combine(run.DirectoryPath, file);
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, token + " " + run.Id.ToString("N"), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    public static string CreateScript(Guid runId, string spdPath, string runDirectory)
    {
        static string Encoded(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return $$$"""
            set ::scheduler_id { {{{runId:N}}} }
            set ::scheduler_id [string trim $::scheduler_id]
            set ::scheduler_spd [encoding convertfrom utf-8 [binary decode base64 { {{{Encoded(spdPath)}}} }]]
            set ::scheduler_dir [encoding convertfrom utf-8 [binary decode base64 { {{{Encoded(runDirectory)}}} }]]
            proc scheduler_mark {name} {
                set f [open [file join $::scheduler_dir events.log] a]
                fconfigure $f -encoding utf-8 -translation lf
                puts $f "$::scheduler_id $name"
                close $f
            }
            proc scheduler_error {message} {
                set f [open [file join $::scheduler_dir error.txt] a]
                fconfigure $f -encoding utf-8 -translation lf
                puts $f $message
                close $f
            }
            proc scheduler_poll {} {
                set command ""
                set path [file join $::scheduler_dir control.txt]
                if {[file exists $path]} {
                    set f [open $path r]
                    set command [string trim [read $f 256]]
                    close $f
                }
                if {$command eq "SAVE_EXIT $::scheduler_id" && !$::scheduler_rejected} {
                    if {[catch {sigrity::save {!}} message]} {
                        scheduler_error $message
                        scheduler_mark SAVE_FAILED
                        return
                    }
                    scheduler_mark SAVED
                    if {[catch {sigrity::exit -noSave {!}} message]} {scheduler_error $message; scheduler_mark EXIT_FAILED}
                    return
                }
                if {$command eq "ABORT_NO_SAVE $::scheduler_id" && $::scheduler_rejected} {
                    scheduler_mark EXIT_NO_SAVE_REQUESTED
                    if {[catch {sigrity::exit -noSave {!}} message]} {
                        scheduler_error $message
                        scheduler_mark CLEANUP_FAILED
                        return
                    }
                    return
                }
                after 500 scheduler_poll
            }
            set ::scheduler_rejected 0
            if {[catch {
                sigrity::open document $::scheduler_spd {!}
                sigrity::update option -caseLogFolder $::scheduler_dir -outputWindowLogName {output.log} -runtimeErrorLogName {runtime-errors.log} {!}
                scheduler_mark OPENED
            } message]} {
                set ::scheduler_rejected 1
                scheduler_error $message
                scheduler_mark START_REJECTED
            }
            if {!$::scheduler_rejected} {
                scheduler_mark BEGIN_REQUESTED
                if {[catch {sigrity::begin simulation {!}} message]} {
                    scheduler_error $message
                    scheduler_mark BEGIN_FAILED
                } else {scheduler_mark BEGIN_RETURNED}
            }
            after 500 scheduler_poll
            vwait ::scheduler_done
            """;
    }
}
