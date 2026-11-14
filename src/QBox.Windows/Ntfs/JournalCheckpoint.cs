namespace QBox.Windows.Ntfs;

public readonly record struct JournalCheckpoint(ulong JournalId, long NextUsn);
public readonly record struct JournalBounds(ulong JournalId, long FirstUsn, long LowestValidUsn, long NextUsn);
public enum JournalResume { ReadChanges, CaughtUp, RebuildRequired }

public static class JournalContinuity
{
    public static JournalResume Check(JournalCheckpoint saved, JournalBounds current)
    {
        // MFT 初建前保存的 USN 也必须通过此检查；日志已覆盖时不能把未追齐的索引标为就绪。
        if (saved.JournalId != current.JournalId || saved.NextUsn < Math.Max(current.FirstUsn, current.LowestValidUsn) ||
            saved.NextUsn > current.NextUsn)
            return JournalResume.RebuildRequired;
        return saved.NextUsn == current.NextUsn ? JournalResume.CaughtUp : JournalResume.ReadChanges;
    }
}
