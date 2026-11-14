using System.Text.Json;

namespace QBox.Desktop;

public sealed record DesktopSettings(int Version = 1, string? IndexDirectory = null, string Hotkey = "DoubleCtrl", bool AutoUpdate = true);

public sealed record DesktopOptions(string StateDirectory, string? IndexDirectory, bool StartHidden, string? WorkerJob, bool WatchWorker = false)
{
    public static DesktopOptions Parse(string[] args)
    {
        var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QBox");
        string? indexes = null, workerJob = null;
        var hidden = false; var watch = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--start-hidden") { hidden = true; continue; }
            if (args[i] == "--watch-worker") { watch = true; continue; }
            var key = args[i];
            if (key is not ("--state-directory" or "--index-directory" or "--sync-worker") || ++i == args.Length)
                throw new ArgumentException($"无效的启动参数：{key}");
            if (key == "--state-directory") state = args[i];
            else if (key == "--index-directory") indexes = args[i];
            else workerJob = args[i];
        }
        return new(Path.GetFullPath(state), indexes == null ? null : Path.GetFullPath(indexes), hidden,
            workerJob == null ? null : Path.GetFullPath(workerJob), watch);
    }
}

internal sealed class DesktopStore(string stateDirectory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath { get; } = stateDirectory;
    private string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string? LoadError { get; private set; }

    public DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllBytes(SettingsPath), Json);
            if (settings == null || settings.Version != 1) throw new InvalidDataException("设置文件版本无效");
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LoadError = "设置无法读取，原文件已保留。";
            Log(error);
            return new();
        }
    }

    public void Save(DesktopSettings settings) => WriteJson(SettingsPath, settings);
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Log(Exception error)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(Path.Combine(DirectoryPath, "errors.log"), $"{DateTimeOffset.Now:O} {error}{Environment.NewLine}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    public string ResolveIndexDirectory(DesktopOptions options, DesktopSettings settings)
    {
        if (options.IndexDirectory != null) return options.IndexDirectory;
        if (!string.IsNullOrWhiteSpace(settings.IndexDirectory)) return Path.GetFullPath(settings.IndexDirectory);
        // 工作区发布版复用已经验证的索引，移动程序后则使用用户数据目录。
        var nearby = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "work", "indexes"));
        return System.IO.Directory.Exists(nearby) ? nearby : Path.Combine(DirectoryPath, "indexes");
    }
}
