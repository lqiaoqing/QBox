using System.Buffers.Binary;

namespace QBox.Windows.Ntfs;

public static class NtfsControlBuffers
{
    public static byte[] ReadJournal(JournalCheckpoint checkpoint)
    {
        var input = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(input, checkpoint.NextUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        // V0 的两个 DWORD 位于 8、12，三个 DWORDLONG 位于 16、24、32。
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), checkpoint.JournalId);
        return input;
    }
}
