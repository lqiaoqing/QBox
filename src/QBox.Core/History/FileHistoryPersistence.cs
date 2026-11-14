using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QBox.Core.History;

public sealed class FileHistoryPersistence : IHistoryPersistence
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;
    private readonly string _mutexName;
    private string? _expectedFingerprint;
    private bool _hasRead;

    public FileHistoryPersistence(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        var identity = OperatingSystem.IsWindows() ? _path.ToUpperInvariant() : _path;
        _mutexName = (OperatingSystem.IsWindows() ? @"Local\" : "") + "QBox.History." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public HistorySnapshot Read()
    {
        using var mutex = EnterMutex();
        var bytes = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
        HistorySnapshot snapshot;
        try
        {
            snapshot = bytes == null ? HistorySnapshot.Empty :
                JsonSerializer.Deserialize<HistorySnapshot>(bytes, Json) ?? throw new InvalidDataException("历史文件为空");
            SelectionHistory.Validate(snapshot);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"历史文件格式损坏，原文件已保留：{_path}", error);
        }
        _expectedFingerprint = Fingerprint(bytes);
        _hasRead = true;
        return snapshot;
    }

    public void Write(HistorySnapshot snapshot)
    {
        SelectionHistory.Validate(snapshot);
        using var mutex = EnterMutex();
        if (!_hasRead) throw new InvalidOperationException("写入前必须读取历史版本");
        var current = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
        if (Fingerprint(current) != _expectedFingerprint)
            throw new HistoryConflictException("历史已被另一进程更新，需要重新合并");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // 临时文件位于同一目录，完整落盘后再替换；失败时原历史仍可读取。
            File.Move(temporary, _path, overwrite: true);
            _expectedFingerprint = Fingerprint(bytes);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private MutexLease EnterMutex()
    {
        var mutex = new Mutex(false, _mutexName);
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(3))) throw new IOException("历史文件正忙，请稍后重试");
            }
            catch (AbandonedMutexException)
            {
                // 上一进程异常退出时已取得所有权；后续仍验证磁盘内容和版本。
            }
            return new(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static string? Fingerprint(byte[]? bytes) => bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
