using System.IO;
using SPDScheduler.Core;

namespace SPDScheduler.Windows;

public static class PowerSiDiscovery
{
    public static string ResolveExecutablePath(string? configuredPath, SimulationTool tool = SimulationTool.PowerSI) =>
        ResolveForTool(configuredPath, tool, () => FindInstalled(tool));

    internal static string ResolveExecutablePath(string? configuredPath, Func<IReadOnlyList<string>> discover) =>
        ResolveForTool(configuredPath, SimulationTool.PowerSI, discover);

    internal static string ResolveForTool(string? configuredPath, SimulationTool tool, Func<IReadOnlyList<string>> discover)
    {
        if (!Enum.IsDefined(tool)) throw new ArgumentException("지원하지 않는 실행 프로그램입니다.", nameof(tool));
        string? path = string.IsNullOrWhiteSpace(configuredPath) ? discover().FirstOrDefault() : configuredPath.Trim();
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{tool} 실행 파일을 찾지 못했습니다. 설정에서 {tool}.exe 경로를 지정하고 저장하세요.");
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            !(tool == SimulationTool.PowerDC ? WindowsProcessProbe.IsPowerDc(path) : WindowsProcessProbe.IsPowerSi(path)) || !File.Exists(path))
            throw new ArgumentException($"{tool} 실행 파일 경로가 올바르지 않거나 파일에 접근할 수 없습니다. 설정에서 {tool}.exe 경로를 확인하고 저장하세요.");
        return Path.GetFullPath(path);
    }

    public static IReadOnlyList<string> FindInstalled(SimulationTool tool = SimulationTool.PowerSI)
    {
        if (!Enum.IsDefined(tool)) throw new ArgumentException("지원하지 않는 실행 프로그램입니다.", nameof(tool));
        var root = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Cadence");
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateDirectories(root, "Sigrity*")
                .Select(folder => Path.Combine(folder, "tools", "bin", tool + ".exe"))
                .Where(File.Exists).OrderDescending(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
