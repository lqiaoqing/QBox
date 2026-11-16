using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using QBox.Core.Matching;
using QBox.Core.Search;
using QBox.Windows.Search;

namespace QBox.Checks;

// 固定 Rust serialize_response 的线格式：可选字符串按「长度→内容」交错写出。
// 解码端若把 arguments 与 aumid 的长度连读再读内容，非空启动参数会让后续字段全部错位。
internal static class RustEngineChecks
{
    public static void Run(CheckSuite checks)
    {
        var cases = new (string? Arguments, string? Aumid)[]
        {
            (null, null),
            ("", ""),
            ("--profile one", null),
            (null, "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"),
            ("--profile one", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"),
            ("-x", ""),
        };
        foreach (var (arguments, aumid) in cases)
        {
            checks.Run($"FFI 可选字段交错解码 arguments={Literal(arguments)} aumid={Literal(aumid)}", () =>
            {
                var response = Decode(("zzprobe", arguments, aumid), ("second", "-y", "App!X"));
                CheckSuite.Equal("zzprobe", response.Query);
                CheckSuite.Equal(7, response.TotalMatches);
                CheckSuite.Equal(2, response.Items.Count);
                var first = response.Items[0];
                CheckSuite.Equal(arguments, first.Candidate.Arguments);
                CheckSuite.Equal(aumid, first.Candidate.AppUserModelId);
                CheckSuite.Equal(SearchItemKind.Shortcut, first.Candidate.Kind);
                CheckSuite.Equal(RankGroup.ApplicationEntry, first.Group);
                CheckSuite.Equal(42, first.Match.AlignmentScore);
                CheckSuite.Equal(100UL, first.Candidate.NormalPriority);
                CheckSuite.True(first.Candidate.IsApplicationEntry);
                CheckSuite.True(first.Match.IsMatch);
                CheckSuite.Equal(new TextRange(2, 3), first.Match.Highlights[0]);
                // 第二项暴露首项错位后累积的游标漂移。
                var second = response.Items[1];
                CheckSuite.Equal("second", second.Candidate.Name);
                CheckSuite.Equal("id:second", second.Candidate.Id);
                CheckSuite.Equal(@"C:\app\second.exe", second.Candidate.Target);
                CheckSuite.Equal("-y", second.Candidate.Arguments);
                CheckSuite.Equal("App!X", second.Candidate.AppUserModelId);
                CheckSuite.Equal(new TextRange(2, 3), second.Match.Highlights[0]);
            });
        }
        checks.Run("FFI 解码空结果集", () =>
        {
            var response = Decode([]);
            CheckSuite.Equal("zzprobe", response.Query);
            CheckSuite.Equal(0, response.Items.Count);
        });
        checks.Run("FFI 解码拒绝截断缓冲区", () =>
            CheckSuite.Throws<InvalidDataException>(() => DecodeRaw(Wire([("zzprobe", "--profile one", null)])[..^6])));
    }

    private static SearchResponse DecodeRaw(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return RustSearchEngine.ParseResponse(new QboxEngineNative.NativeBuffer
            {
                Pointer = pointer,
                Length = (nuint)bytes.Length,
            });
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static string Literal(string? value) => value is null ? "null" : "\"" + value + "\"";

    private static SearchResponse Decode(params (string Name, string? Arguments, string? Aumid)[] items) =>
        DecodeRaw(Wire(items));

    private static byte[] Wire((string Name, string? Arguments, string? Aumid)[] items)
    {
        using var stream = new MemoryStream();
        stream.Write("QBXRES01"u8);
        U32(stream, 1);
        I32(stream, 7);
        U32(stream, (uint)items.Length);
        Text(stream, "zzprobe");
        foreach (var (name, arguments, aumid) in items)
        {
            stream.WriteByte(3);
            stream.WriteByte(3);
            stream.WriteByte(5);
            stream.WriteByte(0);
            I32(stream, 42);
            U64(stream, 100);
            U32(stream, 1);
            Text(stream, "id:" + name);
            Text(stream, name);
            Text(stream, @"C:\app\" + name + ".exe");
            Optional(stream, arguments);
            Optional(stream, aumid);
            I32(stream, 2);
            I32(stream, 3);
        }
        return stream.ToArray();
    }

    private static void Optional(Stream stream, string? value)
    {
        if (value is null) { U32(stream, uint.MaxValue); return; }
        Text(stream, value);
    }

    private static void Text(Stream stream, string value)
    {
        var units = Encoding.Unicode.GetBytes(value);
        U32(stream, (uint)(units.Length / 2));
        stream.Write(units);
    }

    private static void U32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void I32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void U64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}