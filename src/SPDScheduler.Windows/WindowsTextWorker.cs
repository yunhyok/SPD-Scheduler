using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using SPDScheduler.Core;

namespace SPDScheduler.Windows;

public static class WindowsTextWorker
{
    private const string Argument = "--read-powersi-status";

    // UIA providers can hang; only this disposable helper process calls them.
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != Argument) return null;
        try
        {
            if (args.Length != 4 || !int.TryParse(args[1], out int pid) ||
                !long.TryParse(args[2], out long ticks)) return 2;
            var identity = new ProcessIdentity(pid, new DateTimeOffset(ticks, TimeSpan.Zero),
                Encoding.UTF8.GetString(Convert.FromBase64String(args[3])));
            if (!WindowsProcessProbe.IsSimulationGui(identity.ImagePath) || !WindowsProcessProbe.IdentityMatches(identity)) return 3;
            using var process = Process.GetProcessById(pid);
            IntPtr window = process.MainWindowHandle;
            if (window == IntPtr.Zero) return 4;
            GetWindowThreadProcessId(window, out uint actualPid);
            if (actualPid != (uint)pid || IsHungAppWindow(window)) return 4;
            var text = new List<string>();
            var nativeBars = new Dictionary<IntPtr, int>();
            int nativeCount = 0;
            bool enumerationComplete = true;
            EnumChildWindows(window, (child, ignored) =>
            {
                if (++nativeCount > 2048) { enumerationComplete = false; return false; }
                GetWindowThreadProcessId(child, out uint childPid);
                if (childPid != (uint)pid) return true;
                var className = new StringBuilder(256);
                GetClassName(child, className, className.Capacity);
                if (!className.ToString().Equals("msctls_statusbar32", StringComparison.OrdinalIgnoreCase)) return true;
                // SB_GETPARTS with no buffer returns only a count; SB_GETTEXT needs target-process memory.
                if (SendMessageTimeout(child, 0x0406, UIntPtr.Zero, IntPtr.Zero, 0x23, 150, out var parts) == IntPtr.Zero || parts.ToUInt64() > 64)
                { enumerationComplete = false; return false; }
                nativeBars[child] = (int)parts.ToUInt64();
                if (nativeBars.Count > 16) { enumerationComplete = false; return false; }
                var value = new StringBuilder(1025);
                if (SendMessageTimeout(child, 0x000D, new UIntPtr((uint)value.Capacity), value, 0x23, 150, out _) != IntPtr.Zero && value.Length > 0)
                {
                    if (value.Length == value.Capacity - 1) { enumerationComplete = false; return false; }
                    text.Add(value.ToString());
                }
                return true;
            }, IntPtr.Zero);
            if (!enumerationComplete) return 5;
            foreach (var bar in nativeBars)
                ReadBar(AutomationElement.FromHandle(bar.Key), bar.Value);
            var root = AutomationElement.FromHandle(window);
            var bars = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar));
            if (bars.Count > 16) return 5;
            for (int i = 0; i < bars.Count; i++)
            {
                if (!nativeBars.ContainsKey(new IntPtr(bars[i].Current.NativeWindowHandle))) ReadBar(bars[i], 0);
            }
            var status = NormalizeStatusText(text);
            if (status == null) return 5;
            if (!WindowsProcessProbe.IdentityMatches(identity)) return 3;
            Console.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(status)));
            return 0;

            void ReadBar(AutomationElement bar, int parts)
            {
                if (bar.Current.ProcessId != pid) throw new InvalidDataException("Status bar ownership changed.");
                var children = bar.FindAll(TreeScope.Children, Condition.TrueCondition);
                if (children.Count > 64 || parts > 1 && children.Count < parts)
                    throw new InvalidDataException("Status bar panes could not be completely read.");
                // Parent text can concatenate the same panes; read the individual current values when available.
                if (children.Count == 0) Add(bar);
                for (int i = 0; i < children.Count; i++) Add(children[i]);
            }

            void Add(AutomationElement element)
            {
                if (element.Current.ProcessId != pid) throw new InvalidDataException("Status pane ownership changed.");
                AddValue(element.Current.Name);
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) AddValue(((ValuePattern)value).Current.Value);
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var range)) AddValue(((TextPattern)range).DocumentRange.GetText(1025));
            }

            void AddValue(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (value.Length > 1024) throw new InvalidDataException("Status pane text was truncated.");
                if (!text.Contains(value)) text.Add(value);
                if (text.Count > 64) throw new InvalidDataException("Too many status values.");
            }
        }
        catch { return 6; }
    }

    internal static string? NormalizeStatusText(IEnumerable<string?> values)
    {
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value == null) continue;
            if (value.Length > 1024 || value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')) return null;
            foreach (var line in value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (lines.Add(line) && lines.Count > 64) return null;
        }
        return string.Join('\n', lines);
    }

    internal static async Task<string?> ReadAsync(ProcessIdentity identity, CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable(AppContext.BaseDirectory);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(identity.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(identity.StartedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(identity.ImagePath)));
        using var worker = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (!worker.Start()) throw new IOException("Simulation status reader could not start.");
            var error = worker.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var output = new StringBuilder();
            var buffer = new char[2048];
            int length;
            while ((length = await worker.StandardOutput.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + length > 90_000) return null;
                output.Append(buffer, 0, length);
            }
            await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (worker.ExitCode is >= 2 and <= 6) return null;
            if (worker.ExitCode != 0) throw new IOException($"Simulation status reader is unavailable (exit {worker.ExitCode}). Reinstall the complete SPD Scheduler package.");
            return Encoding.UTF8.GetString(Convert.FromBase64String(output.ToString()));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception exception) { throw new IOException("Simulation status reader could not start. Reinstall the complete SPD Scheduler package.", exception); }
        catch (FormatException) { return null; }
        finally
        {
            // This is our read-only helper, never the Cadence process.
            try { if (!worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
    }

    internal static string ResolveExecutable(string baseDirectory)
    {
        string executable = Path.Combine(baseDirectory, "SPDScheduler.StatusReader.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Simulation status reader is missing. Reinstall the complete SPD Scheduler package.", executable);
        return executable;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsHungAppWindow(IntPtr window);
    private delegate bool EnumerateWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr window, EnumerateWindow callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr buffer, uint flags, uint timeout, out UIntPtr result);
}
