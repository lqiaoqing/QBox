using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using QBox.Core.Search;
using QBox.Windows.Indexing;

sealed class NativePriorityModel
{
    private readonly Dictionary<string, byte> _folders;
    private readonly (NameRule Rule, Regex Matcher)[] _patterns;
    private readonly Dictionary<ulong, VolumeMetadata> _volumes;
    private readonly Dictionary<char, string[]> _suffixes;
    public object Summary { get; }

    public NativePriorityModel(TableCopy defaults, VolumeIndex[] indexes, string suffixSnapshot)
    {
        _folders = defaults.Paths.ToDictionary(rule => Environment.ExpandEnvironmentVariables(rule.Path).TrimEnd('\\'),
            rule => Decode(rule.Priority), StringComparer.OrdinalIgnoreCase);
        if (_folders.Keys.Any(path => path.Contains('%'))) throw new InvalidDataException("环境变量未展开");
        _patterns = defaults.Patterns.Select(rule => (rule, new Regex(rule.Pattern,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))).ToArray();
        var raw = File.ReadAllBytes(suffixSnapshot);
        using var document = JsonDocument.Parse(raw);
        var suffixes = document.RootElement.GetProperty("registeredSuffixes").EnumerateArray().Select(value => value.GetString()!).ToArray();
        if (suffixes.Any(value => !value.StartsWith('.') || value.Any(c => c > 127)))
            throw new InvalidDataException("本实验只接受已核对的 ASCII 注册后缀快照");
        _suffixes = suffixes.GroupBy(value => value[^1]).ToDictionary(group => group.Key, group => group.ToArray());
        _volumes = indexes.ToDictionary(index => index.Geometry.Serial, index => new VolumeMetadata(index, this));
        Summary = new
        {
            SuffixSnapshotSha256 = Convert.ToHexStringLower(SHA256.HashData(raw)), SuffixCount = suffixes.Length,
            DirectoryRecords = _volumes.Values.Sum(volume => volume.DirectoryCount),
            AttributeNames = _volumes.Values.Sum(volume => volume.AttributeNameCount),
            BoundaryChecks = 9,
            Assumptions = new[] {
                "Recovered default folder/name rules; SYSTEM/HIDDEN demotion enabled; root inherited priority is normal",
                "Exact directory overrides and per-parent inheritance; directory path uses the same first long name as the index",
                "No directory context; raw tail is zero; current QBox application/direct-EXE policies and all launcher buckets retained",
                "Current complete candidate set retained; does not reproduce upstream native bounded selection, timestamp semantics or all source policies"
            }
        };
        CheckBoundaries();
    }

    public object Study(SearchResponse response, int[] original, Func<SearchCandidate, bool> isTarget)
    {
        var hits = response.Items;
        var decisions = hits.Select(hit => Describe(hit)).ToArray();
        var slots = original.Where(i => !Protected(hits[i]) && hits[i].Candidate.Kind != SearchItemKind.Directory).ToArray();
        // 只替换原混合桶已有位置，保留强目录、混合前十、弱目录、混合尾部的原布局。
        var baseline = slots.OrderByDescending(i => PublishedKey(hits[i])).ThenBy(i => i).ToArray();
        if (!baseline.SequenceEqual(slots)) throw new InvalidDataException("混合桶基线复算不一致");
        var ordered = slots.OrderByDescending(i => decisions[i].Key).ThenBy(i => i).ToArray();
        var after = (int[])original.Clone();
        for (var i = 0; i < slots.Length; i++) after[slots[i]] = ordered[i];
        if (!after.Order().SequenceEqual(original)) throw new InvalidDataException("原生键模拟改变了召回集合");
        var ranks = new int[after.Length];
        for (var i = 0; i < after.Length; i++) ranks[after[i]] = i + 1;
        return new
        {
            RecallSetUnchanged = true, ExistingBucketsPreserved = true, BaselineMixedOrderEqual = true,
            FirstNineUnchanged = after.Take(9).SequenceEqual(original.Take(9)),
            MixedEntries = slots.Length, MovedMixedEntries = slots.Count(i => ranks[i] != i + 1),
            AppliedEntries = decisions.Count(decision => decision.Applied),
            UnmodeledWeakApplications = original.Count(i => !Protected(hits[i]) && !decisions[i].Applied),
            NormalEntries = decisions.Count(decision => decision.Applied && decision.Priority == 2),
            UncommonEntries = decisions.Count(decision => decision.Applied && decision.Priority == 1),
            RegisteredSuffixEntries = decisions.Count(decision => decision.Applied && decision.RegisteredSuffix),
            TargetRanks = original.Where(i => isTarget(hits[i].Candidate)).Select(i => new
                { hits[i].Candidate.Name, hits[i].Candidate.Target, Before = i + 1, After = ranks[i], Decision = decisions[i] }).ToArray(),
            FirstNine = Rows(after.Take(9)), MixedBefore = Rows(slots.Take(9)), MixedAfter = Rows(ordered.Take(9)),
            DirectExecutableSourceProposal = StudyDirectSource()
        };

        object StudyDirectSource()
        {
            if (hits.Any(hit => hit.History.ExactQuery != null || hit.History.PrefixQuery != null || hit.History.LastUsed != null))
                throw new InvalidDataException("来源分桶实验限定为空历史，不能隐式改变历史桶");
            var direct = original.Where(i => hits[i].Candidate.IsDirectExecutable).ToArray();
            var keys = decisions.Select(decision => decision.Key).ToArray();
            foreach (var i in direct) keys[i] = Describe(hits[i], true).Key;
            // 独立方案 D 只取消直接 EXE 的专用提前桶；显示匹配、应用入口和目录桶全部沿用当前输入。
            var mixed = slots.Concat(direct).OrderByDescending(i => keys[i]).ThenBy(i => i).ToArray();
            var proposal = original.Where(i => hits[i].Candidate.IsApplicationEntry && !hits[i].Candidate.IsDirectExecutable)
                .Concat(original.Where(i => hits[i].Candidate.Kind == SearchItemKind.Directory && hits[i].Match.AlignmentScore > 1))
                .Concat(mixed.Take(10))
                .Concat(original.Where(i => hits[i].Candidate.Kind == SearchItemKind.Directory && hits[i].Match.AlignmentScore <= 1))
                .Concat(mixed.Skip(10)).ToArray();
            if (!proposal.Order().SequenceEqual(original)) throw new InvalidDataException("来源分桶模拟改变了召回集合");
            var proposedRanks = new int[proposal.Length];
            for (var i = 0; i < proposal.Length; i++) proposedRanks[proposal[i]] = i + 1;
            return new
            {
                Scope = "D: direct EXEs moved to ordinary mixed files; current display matching retained; no context or tail; not proven full Listary source policy",
                RecallSetUnchanged = true, DirectExecutableEntries = direct.Length,
                FirstNineUnchanged = proposal.Take(9).SequenceEqual(original.Take(9)),
                TargetRanks = original.Where(i => isTarget(hits[i].Candidate)).Select(i => new
                    { hits[i].Candidate.Name, hits[i].Candidate.Target, Before = i + 1, After = proposedRanks[i] }).ToArray(),
                FirstNine = proposal.Take(9).Select(i => new
                    { OriginalRank = i + 1, AfterRank = proposedRanks[i], hits[i].Candidate.Name, hits[i].Candidate.Target,
                      hits[i].Candidate.Kind, Score = hits[i].Match.AlignmentScore }).ToArray()
            };
        }

        object[] Rows(IEnumerable<int> selected) => selected.Select(i => (object)new
        {
            OriginalRank = i + 1, AfterRank = ranks[i], hits[i].Candidate.Name, hits[i].Candidate.Target,
            Score = hits[i].Match.AlignmentScore, Decision = decisions[i]
        }).ToArray();
    }

    private NativeDecision Describe(SearchHit hit, bool includeDirectExecutable = false)
    {
        var candidate = hit.Candidate;
        if (Protected(hit) && !(includeDirectExecutable && candidate.IsDirectExecutable))
            return new(false, 0, false, PublishedKey(hit));
        var fields = candidate.Id.Split(':');
        if (fields.Length != 5 || fields[0] != "ntfs")
        {
            // 打包应用的弱匹配也会进入混合桶；本次只补磁盘来源键，保留该来源的既有键。
            if (candidate.Kind != SearchItemKind.Application) throw new InvalidDataException("未知普通来源身份");
            return new(false, 0, false, PublishedKey(hit));
        }
        var serial = ulong.Parse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var file = ulong.Parse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var parent = ulong.Parse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var directory = candidate.Kind == SearchItemKind.Directory;
        var volume = _volumes[serial];
        var name = Path.GetFileName(candidate.Target);
        var priority = Evaluate(candidate.Target, name, directory, volume.ParentPriority(parent), volume.Attributes(file, parent, name));
        if (priority == 0) throw new InvalidDataException("默认表意外产生不索引项；本实验禁止静默过滤");
        var suffix = !directory && RegisteredSuffix(name);
        var lane = priority < 2 ? 0UL : priority == 4 ? 3UL : directory || suffix ? 2UL : 1UL;
        var score = Math.Min(unchecked((uint)hit.Match.AlignmentScore + 0x800000U), 0xffffffU);
        return new(true, priority, suffix, (lane << 60) | ((ulong)score << 32));
    }

    private byte Evaluate(string path, string name, bool directory, byte inherited, uint attributes)
    {
        if (directory && _folders.TryGetValue(path.TrimEnd('\\'), out var explicitPriority)) return explicitPriority;
        var result = (attributes & 6) != 0 ? Merge(inherited, 1) : inherited;
        foreach (var pair in _patterns)
            if ((directory ? pair.Rule.ApplyToFolders : pair.Rule.ApplyToFiles) && pair.Matcher.IsMatch(name))
                return Merge(result, Decode(pair.Rule.Priority));
        return result;
    }
    private bool RegisteredSuffix(string name)
    {
        if (name.Length == 0) return false;
        // 原生标记函数只将 ASCII A-Z 转小写，再按末字节分组比较整个后缀，不只取最后一个点。
        var lower = string.Create(name.Length, name, (span, value) =>
        {
            for (var i = 0; i < value.Length; i++) span[i] = value[i] is >= 'A' and <= 'Z' ? (char)(value[i] + 32) : value[i];
        });
        return _suffixes.TryGetValue(lower[^1], out var suffixes) && suffixes.Any(suffix => lower.EndsWith(suffix, StringComparison.Ordinal));
    }
    private static byte Merge(byte a, byte b) => a >= 2 && b >= 2 ? Math.Max(a, b) : Math.Min(a, b);
    private static byte Decode(int value) => value switch { 0 => 4, 1 => 2, 2 => 1, 3 => 0, _ => throw new InvalidDataException("设置类别无效") };
    private static bool Protected(SearchHit hit) => hit.Candidate.IsApplicationEntry || hit.History.ExactQuery != null || hit.History.PrefixQuery != null;
    private static ulong PublishedKey(SearchHit hit) => hit.Candidate.NormalPriority != 0 ? hit.Candidate.NormalPriority :
        hit.Candidate.Kind is SearchItemKind.Shortcut or SearchItemKind.Application ? (ulong)Math.Max(0, hit.Match.AlignmentScore) :
        (0x10000000UL | (ulong)((long)Math.Clamp(hit.Match.AlignmentScore, -0x800000, 0x7fffff) + 0x800000)) << 32;

    private void CheckBoundaries()
    {
        var appData = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\AppData");
        var start = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\Windows\Start Menu");
        if (Evaluate(start, "Start Menu", true, 1, 6) != 2 || Evaluate(appData, "AppData", true, 2, 0) != 1 ||
            Evaluate(@"D:\normal\.codex", ".codex", true, 2, 0) != 1 ||
            Evaluate(@"D:\normal\README", "README", true, 2, 0) != 2 ||
            Evaluate(@"D:\normal\README", "README", false, 2, 0) != 1 ||
            Evaluate(@"D:\normal\any.txt", "any.txt", false, 1, 0) != 1 ||
            Evaluate(@"D:\normal\any.txt", "any.txt", false, 2, 2) != 1 ||
            !RegisteredSuffix("report.PDF") || RegisteredSuffix("report.PDF.backup-unregistered-qbox"))
            throw new InvalidDataException("原生类别模型边界检查失败");
    }

    private readonly record struct NativeDecision(bool Applied, byte Priority, bool RegisteredSuffix, ulong Key);

    private sealed class VolumeMetadata
    {
        private readonly Dictionary<ulong, IndexedFile> _directories = [];
        private readonly Dictionary<ulong, IndexedName[]> _attributes = [];
        private readonly Dictionary<ulong, (string Path, byte Priority)> _states = [];
        private readonly HashSet<ulong> _resolving = [];
        private readonly NativePriorityModel _rules;
        public int DirectoryCount => _directories.Count;
        public int AttributeNameCount => _attributes.Values.Sum(names => names.Length);

        public VolumeMetadata(VolumeIndex index, NativePriorityModel rules)
        {
            _rules = rules;
            foreach (var file in index.ExportFiles())
            {
                if (file.IsDirectory) _directories.Add(file.File.Low, file);
                var uncommon = file.Names.Where(name => (name.Attributes & 6) != 0).ToArray();
                if (uncommon.Length != 0) _attributes.Add(file.File.Low, uncommon);
                // 卷根不是普通查询候选，本次明确以正常类别作为继承起点。
                if ((file.File.Low & 0x0000ffffffffffffUL) == 5) _states.Add(file.File.Low, (index.Geometry.Root.TrimEnd('\\'), 2));
            }
        }
        public byte ParentPriority(ulong parent) => State(parent).Priority;
        public uint Attributes(ulong file, ulong parent, string name) => _attributes.TryGetValue(file, out var names) ?
            names.FirstOrDefault(entry => entry.Parent.Low == parent && entry.Name.Equals(name, StringComparison.Ordinal))?.Attributes ?? 0 : 0;
        private (string Path, byte Priority) State(ulong file)
        {
            if (_states.TryGetValue(file, out var state)) return state;
            if (!_resolving.Add(file)) throw new InvalidDataException("目录继承存在循环");
            if (!_directories.TryGetValue(file, out var directory) || directory.Names.Count == 0)
                throw new InvalidDataException("目录元数据缺失");
            var name = directory.Names[0];
            var parent = State(name.Parent.Low);
            var path = parent.Path + "\\" + name.Name;
            state = (path, _rules.Evaluate(path, name.Name, true, parent.Priority, name.Attributes));
            _states.Add(file, state);
            _resolving.Remove(file);
            return state;
        }
    }
}
