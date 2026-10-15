using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPDScheduler.Core;

public sealed class QueueStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public string DataDirectory { get; }
    public string QueuePath => Path.Combine(DataDirectory, "queue.json");

    public QueueStore(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SPDScheduler"));
        Directory.CreateDirectory(DataDirectory);
    }

    public QueueDocument Load()
    {
        if (!File.Exists(QueuePath)) return new();
        try
        {
            using var stream = File.OpenRead(QueuePath);
            using var json = JsonDocument.Parse(stream);
            var document = json.RootElement.Deserialize<QueueDocument>(JsonOptions)
                ?? throw new InvalidDataException("목록 파일이 비어 있습니다.");
            if (document.SchemaVersion != 1 || document.Jobs is null || document.Settings is null)
                throw new InvalidDataException("지원하지 않는 목록 형식입니다.");
            if (document.Jobs.Any(j => j is null || j.Id == Guid.Empty || !Enum.IsDefined(j.Status) ||
                    !Path.IsPathFullyQualified(j.FilePath) || !Path.GetExtension(j.FilePath).Equals(".spd", StringComparison.OrdinalIgnoreCase)) ||
                document.Jobs.Select(j => j.Id).Distinct().Count() != document.Jobs.Count)
                throw new InvalidDataException("목록에 유효하지 않은 작업이 있습니다.");
            var runIds = new HashSet<Guid>();
            foreach (var job in document.Jobs)
            {
                var run = job.Run;
                if (run is null) continue;
                var expectedDirectory = Path.Combine(DataDirectory, "runs", run.Id.ToString("N"));
                if (run.Id == Guid.Empty || !runIds.Add(run.Id) ||
                    !Path.IsPathFullyQualified(run.DirectoryPath) ||
                    !string.Equals(Path.GetFullPath(run.DirectoryPath), expectedDirectory, StringComparison.OrdinalIgnoreCase) ||
                    run.Children is null || run.Children.Any(identity => !ValidIdentity(identity)) ||
                    run.Process is not null && !ValidIdentity(run.Process))
                    throw new InvalidDataException("목록에 유효하지 않은 실행 경로 또는 프로세스 정보가 있습니다.");
            }
            bool legacyPolicy = !json.RootElement.TryGetProperty("Settings", out var settingsJson) ||
                !settingsJson.TryGetProperty("ResourcePolicyVersion", out _);
            if (legacyPolicy)
            {
                // Missing legacy fields must retain their old meaning before validation/migration.
                if (settingsJson.ValueKind == JsonValueKind.Undefined || !settingsJson.TryGetProperty("IdleCpuPercent", out _)) document.Settings.IdleCpuPercent = 1;
                if (settingsJson.ValueKind == JsonValueKind.Undefined || !settingsJson.TryGetProperty("IdleMemoryMiB", out _)) document.Settings.IdleMemoryMiB = 16;
                if (settingsJson.ValueKind == JsonValueKind.Undefined || !settingsJson.TryGetProperty("IdleSeconds", out _)) document.Settings.IdleSeconds = 180;
            }
            SettingsValidator.Validate(document.Settings);
            if (legacyPolicy && document.Settings.IdleCpuPercent == 1 && document.Settings.IdleMemoryMiB == 16)
            {
                document.Settings.IdleCpuPercent = 50;
                document.Settings.IdleMemoryMiB = 128;
                if (document.Settings.IdleSeconds == 180) document.Settings.IdleSeconds = 60;
            }
            return document;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"목록을 읽을 수 없습니다. 원본과 queue.json.bak 파일을 보존했습니다: {QueuePath}", ex);
        }
    }

    private static bool ValidIdentity(ProcessIdentity? identity) =>
        identity is not null && identity.Pid > 0 && identity.StartedAt > DateTimeOffset.UnixEpoch &&
        identity.StartedAt < DateTimeOffset.MaxValue && Path.IsPathFullyQualified(identity.ImagePath);

    public void Save(QueueDocument document)
    {
        var temporary = QueuePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(true);
            }
            if (File.Exists(QueuePath)) File.Replace(temporary, QueuePath, QueuePath + ".bak", true);
            else File.Move(temporary, QueuePath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void AppendLog(string message, Guid? jobId = null)
    {
        var logDirectory = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var line = $"{DateTimeOffset.Now:O}\t{jobId?.ToString() ?? "app"}\t{message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(logDirectory, $"scheduler-{DateTime.Now:yyyyMMdd}.log"), line);
    }
}
