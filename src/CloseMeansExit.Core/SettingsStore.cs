using System.Text.Json;

namespace CloseMeansExit.Core;

public sealed class AppSettings
{
    public List<AppRule> Rules { get; set; } = [];
    public bool StartWithWindows { get; set; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }
    public string? LastLoadError { get; private set; }

    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public static AppSettings Load(string path, out string? warning)
    {
        SettingsStore store = new(path);
        AppSettings settings = store.Load();
        warning = store.LastLoadError;
        return settings;
    }

    public static void Save(string path, AppSettings settings) => new SettingsStore(path).Save(settings);

    public AppSettings Load()
    {
        LastLoadError = null;

        if (!File.Exists(FilePath))
        {
            return new();
        }

        try
        {
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new JsonException("配置内容为空。");

            if (settings.Rules is null || settings.Rules.Any(rule => rule is null))
            {
                throw new JsonException("黑名单格式无效。");
            }

            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
            settings.Rules = settings.Rules.Select(rule => rule with
            {
                ExecutablePath = RulePaths.Normalize(rule.ExecutablePath)
            }).Where(rule => paths.Add(rule.ExecutablePath)).ToList();

            return settings;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            string backup = FilePath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");

            try
            {
                File.Move(FilePath, backup);
            }
            catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException)
            {
                throw new IOException("配置已损坏且无法保留原文件；为避免覆盖，已停止读取。", backupException);
            }

            LastLoadError = $"配置无法读取，原文件已保留至 {backup}。本次以空黑名单启动：{exception.Message}";
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Rules);
        AppSettings normalized = new()
        {
            StartWithWindows = settings.StartWithWindows,
            Rules = settings.Rules.Select(rule => rule with
            {
                ExecutablePath = RulePaths.Normalize(rule.ExecutablePath)
            }).ToList()
        };

        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
