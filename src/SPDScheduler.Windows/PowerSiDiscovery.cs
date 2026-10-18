using System.IO;

namespace SPDScheduler.Windows;

public static class PowerSiDiscovery
{
    public static string ResolveExecutablePath(string? configuredPath) => ResolveExecutablePath(configuredPath, FindInstalled);

    internal static string ResolveExecutablePath(string? configuredPath, Func<IReadOnlyList<string>> discover)
    {
        string? path = string.IsNullOrWhiteSpace(configuredPath) ? discover().FirstOrDefault() : configuredPath.Trim();
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("PowerSI 실행 파일을 찾지 못했습니다. 설정에서 powersi.exe 경로를 지정하고 저장하세요.");
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            !WindowsProcessProbe.IsPowerSi(path) || !File.Exists(path))
            throw new ArgumentException("PowerSI 실행 파일 경로가 올바르지 않거나 파일에 접근할 수 없습니다. 설정에서 powersi.exe 경로를 확인하고 저장하세요.");
        return Path.GetFullPath(path);
    }

    public static IReadOnlyList<string> FindInstalled()
    {
        var root = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Cadence");
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateDirectories(root, "Sigrity*")
                .Select(folder => Path.Combine(folder, "tools", "bin", "powersi.exe"))
                .Where(File.Exists).OrderDescending(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
