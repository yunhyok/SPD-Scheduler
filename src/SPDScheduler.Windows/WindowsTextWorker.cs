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
            if (!WindowsProcessProbe.IsPowerSi(identity.ImagePath) || !WindowsProcessProbe.IdentityMatches(identity)) return 3;
            using var process = Process.GetProcessById(pid);
            IntPtr window = process.MainWindowHandle;
            if (window == IntPtr.Zero) return 4;
            GetWindowThreadProcessId(window, out uint actualPid);
            if (actualPid != (uint)pid || IsHungAppWindow(window)) return 4;
            var text = new List<string>();
            int nativeCount = 0;
            EnumChildWindows(window, (child, ignored) =>
            {
                if (++nativeCount > 2048) return false;
                var className = new StringBuilder(256);
                GetClassName(child, className, className.Capacity);
                if (!className.ToString().Equals("msctls_statusbar32", StringComparison.OrdinalIgnoreCase)) return true;
                var value = new StringBuilder(1025);
                if (SendMessageTimeout(child, 0x000D, new UIntPtr((uint)value.Capacity), value, 0x23, 150, out _) != IntPtr.Zero && value.Length > 0)
                    text.Add(value.ToString().Trim());
                return text.Count < 64;
            }, IntPtr.Zero);
            try
            {
                var root = AutomationElement.FromHandle(window);
                var bars = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar));
                if (bars.Count > 16) return 5;
                for (int i = 0; i < bars.Count; i++)
                {
                    Add(bars[i]);
                    var children = bars[i].FindAll(TreeScope.Children, Condition.TrueCondition);
                    if (children.Count > 64) return 5;
                    for (int j = 0; j < children.Count; j++) Add(children[j]);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            if (!WindowsProcessProbe.IdentityMatches(identity)) return 3;
            Console.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', text.Distinct()))));
            return 0;

            void Add(AutomationElement element)
            {
                if (element.Current.ProcessId != pid) return;
                var name = element.Current.Name;
                if (!string.IsNullOrWhiteSpace(name) && name.Length <= 1024 && text.Count < 64) text.Add(name.Trim());
            }
        }
        catch { return 6; }
    }

    internal static async Task<string?> ReadAsync(ProcessIdentity identity, CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;
        if (executable == null || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return null;
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
            if (!worker.Start()) return null;
            var output = new StringBuilder();
            var buffer = new char[2048];
            int length;
            while ((length = await worker.StandardOutput.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + length > 90_000) return null;
                output.Append(buffer, 0, length);
            }
            await worker.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return worker.ExitCode == 0 ? Encoding.UTF8.GetString(Convert.FromBase64String(output.ToString())) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (IOException) { return null; }
        catch (FormatException) { return null; }
        finally
        {
            // This is our read-only helper, never the Cadence process.
            try { if (!worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsHungAppWindow(IntPtr window);
    private delegate bool EnumerateWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr window, EnumerateWindow callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
}
