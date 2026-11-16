using QBox.Core.History;

namespace QBox.Windows.Search;

// 与 Rust history::parse_history_blob 对应的单查询快照；时间为从 0001-01-01 UTC 起的 ticks。
public static class EngineHistoryBlob
{
    private static ReadOnlySpan<byte> Magic => "QBXHST01"u8;

    // 对照宿主一次导出四个查询的快照，Rust 基准按查询名选择。
    public static byte[] SerializeSet(SelectionHistory history, IEnumerable<string> queries)
    {
        var entries = queries.Distinct(StringComparer.Ordinal)
            .Select(query => (Query: query, Blob: Serialize(history.ForQuery(query)))).ToArray();
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write("QBXHSB01"u8);
        writer.Write(1u);
        writer.Write((uint)entries.Length);
        foreach (var (query, blob) in entries)
        {
            writer.Write((uint)query.Length);
            foreach (var letter in query) writer.Write((ushort)letter);
            writer.Write((uint)blob.Length);
            writer.Write(blob);
        }
        return buffer.ToArray();
    }

    public static byte[] Serialize(HistoryView history)
    {
        var entries = history.EnumerateSignals().ToArray();
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(Magic);
        writer.Write(1u);
        writer.Write((uint)entries.Length);
        foreach (var (targetId, signals) in entries)
        {
            writer.Write((uint)targetId.Length);
            foreach (var letter in targetId) writer.Write((ushort)letter);
            byte flags = (byte)((signals.ExactQuery != null ? 1 : 0) |
                (signals.PrefixQuery != null ? 2 : 0) | (signals.LastUsed != null ? 4 : 0));
            writer.Write(flags);
            if (signals.ExactQuery is { } exact) writer.Write(exact.UtcTicks);
            if (signals.PrefixQuery is { } prefix) writer.Write(prefix.UtcTicks);
            if (signals.LastUsed is { } last) writer.Write(last.UtcTicks);
        }
        return buffer.ToArray();
    }
}
