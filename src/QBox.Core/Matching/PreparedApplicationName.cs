using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace QBox.Core.Matching;

public readonly record struct ApplicationNameMatch(int Score, MatchResult? PhoneticMatch)
{
    public bool IsPhonetic => PhoneticMatch != null;
}

public sealed class PreparedApplicationName
{
    private readonly record struct Token(int Start, int Length, string[] Readings, bool IsHan);
    private static readonly Lazy<FrozenDictionary<int, string[]>> Readings = new(LoadReadings);
    private readonly Token[] _tokens;
    public string Text { get; }

    public PreparedApplicationName(string text)
    {
        Text = text;
        if (!text.EnumerateRunes().Any(rune => rune.Value > 127)) { _tokens = []; return; }
        var tokens = new List<Token>();
        var offset = 0;
        var hasHan = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var han = Readings.Value.TryGetValue(rune.Value, out var readings);
            tokens.Add(new(offset, rune.Utf16SequenceLength, readings ?? [Rune.ToLowerInvariant(rune).ToString()], han));
            hasHan |= han;
            offset += rune.Utf16SequenceLength;
        }
        _tokens = hasHan ? tokens.ToArray() : [];
    }

    public ApplicationNameMatch Match(PreparedQuery query, CancellationToken cancellationToken = default)
    {
        var literal = query.Score(Text, cancellationToken);
        if (literal > 0) return new(literal, null);
        if (_tokens.Length == 0 || query.Text.Length is 0 or > 128 ||
            !query.Text.All(char.IsAsciiLetterOrDigit)) return default;
        var input = query.Text.ToLowerInvariant();
        var rejected = new HashSet<(int Token, int Query, bool Han)>();
        bool Consume(int at, int next, bool han, out int end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            end = at;
            if (next == input.Length) return han;
            if (at == _tokens.Length || rejected.Contains((at, next, han))) return false;
            var token = _tokens[at];
            foreach (var reading in token.Readings)
                if (input.AsSpan(next).StartsWith(reading, StringComparison.Ordinal) &&
                    Consume(at + 1, next + reading.Length, han || token.IsHan, out end)) return true;
            rejected.Add((at, next, han));
            return false;
        }
        for (var start = 0; start < _tokens.Length; start++)
        {
            // 消费完整音节并映射回原始 UTF-16 范围，避免用首字母或音节片段制造额外误匹配。
            if (!Consume(start, 0, false, out var end)) continue;
            var first = _tokens[start]; var last = _tokens[end - 1];
            // 补充候选使用最低应用分值，有效字面应用匹配仍保持在前；普通排序器不变。
            return new(1, new(true, 1, [new(first.Start, last.Start + last.Length - first.Start)]));
        }
        return default;
    }

    private static FrozenDictionary<int, string[]> LoadReadings()
    {
        using var stream = typeof(PreparedApplicationName).Assembly.GetManifestResourceStream("QBox.Core.PinyinData")
            ?? throw new InvalidDataException("缺少拼音读音表");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new Dictionary<int, string[]>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split('\t');
            if (fields.Length != 2 || !int.TryParse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rune) || !Rune.IsValid(rune))
                throw new InvalidDataException("拼音读音表格式无效");
            result.Add(rune, fields[1].Split(','));
        }
        return result.ToFrozenDictionary();
    }
}
