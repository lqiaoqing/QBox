using System.Text.Json;

namespace QBox.Desktop;

internal static class DesktopDefaults
{
#if QBOX_GLOBAL_NAME_SHARING
    public const string StateFolder = "QBox.GlobalTrial";
    public const string DisplayName = "QBox 全局共享试用";
    public const string Hotkey = "Ctrl+Alt+Space";
    public const bool AutoUpdate = false;
    public const string SearchEngine = "csharp";
#else
    public const string StateFolder = "QBox";
    public const string DisplayName = "QBox";
    public const string Hotkey = "DoubleCtrl";
    public const bool AutoUpdate = true;
    public const string SearchEngine = "rust";
#endif
}

public sealed record DesktopSettings(int Version = 1, string? IndexDirectory = null, string Hotkey = DesktopDefaults.Hotkey, bool AutoUpdate = DesktopDefaults.AutoUpdate,
    bool ResultShortcuts = false);

public sealed record DesktopOptions(string StateDirectory, string? IndexDirectory, bool StartHidden, string? WorkerJob,
    bool WatchWorker = false, string SearchEngine = DesktopDefaults.SearchEngine)
{
    public static DesktopOptions Parse(string[] args)
    {
        var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DesktopDefaults.StateFolder);
        string? indexes = null, workerJob = null;
        var hidden = false; var watch = false; var engine = DesktopDefaults.SearchEngine;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--start-hidden") { hidden = true; continue; }
            if (args[i] == "--watch-worker") { watch = true; continue; }
            var key = args[i];
            if (key is not ("--state-directory" or "--index-directory" or "--sync-worker" or "--search-engine") || ++i == args.Length)
                throw new ArgumentException($"无效的启动参数：{key}");
            if (key == "--state-directory") state = args[i];
            else if (key == "--index-directory") indexes = args[i];
            else if (key == "--search-engine") engine = args[i];
            else workerJob = args[i];
        }
        if (engine is not ("csharp" or "rust"))
            throw new ArgumentException("--search-engine 只能是 csharp 或 rust");
        return new(Path.GetFullPath(state), indexes == null ? null : Path.GetFullPath(indexes), hidden,
            workerJob == null ? null : Path.GetFullPath(workerJob), watch, engine);
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
#if QBOX_GLOBAL_NAME_SHARING
        // 试用版默认只读写自己的缓存，不能自动选中标准版的工作区索引。
        return Path.Combine(DirectoryPath, "indexes");
#else
        // 工作区发布版复用已经验证的索引，移动程序后则使用用户数据目录。
        var nearby = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "work", "indexes"));
        return System.IO.Directory.Exists(nearby) ? nearby : Path.Combine(DirectoryPath, "indexes");
#endif
    }
}
