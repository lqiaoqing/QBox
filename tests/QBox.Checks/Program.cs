using System.Text.Json;
using QBox.Core.Matching;
using QBox.Checks;

var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
var samples = Directory.EnumerateFiles(fixtureDirectory, "ascii-*.json").Order().SelectMany(path =>
    JsonSerializer.Deserialize<Oracle>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Cases).ToArray();
var includeHoldout = args.Contains("--include-holdout", StringComparer.Ordinal);
var matcher = new FuzzyMatcher();
var failures = 0;
var executed = 0;
foreach (var sample in samples)
{
    if (!includeHoldout && sample.Partition == "holdout") continue;
    executed++;
    try
    {
        if (!sample.Completed) throw new InvalidDataException("Oracle 样本未完成");
        var actual = matcher.Match(sample.Query, sample.Candidate);
        if (actual.AlignmentScore != sample.Score || actual.IsMatch != (sample.Score > 0))
            throw new InvalidOperationException($"expected={sample.Score}, actual={actual.AlignmentScore}");
        var scoreOnly = matcher.Prepare(sample.Query).Score(sample.Candidate);
        if (scoreOnly != sample.Score) throw new InvalidOperationException($"score-only expected={sample.Score}, actual={scoreOnly}");
    }
    catch (Exception error)
    {
        failures++;
        if (failures <= 25)
            Console.Error.WriteLine($"FAIL {JsonSerializer.Serialize(sample.Query)} / {JsonSerializer.Serialize(sample.Candidate)}: {error.Message}");
    }
}
Console.WriteLine($"匹配检查：{executed - failures}/{executed} 通过；留出未执行：{samples.Length - executed}");
var checks = new CheckSuite();
await CoreChecks.Run(checks);
NtfsParserChecks.Run(checks);
NtfsIndexChecks.Run(checks);
ControlGestureChecks.Run(checks);
LauncherOrderingChecks.Run(checks);
ShortcutChecks.Run(checks);
await ApplicationChecks.Run(checks);
PinyinChecks.Run(checks);
LiveWorkerReliabilityChecks.Run(checks);
RustEngineChecks.Run(checks);
Console.WriteLine($"行为检查：{checks.Total - checks.Failed}/{checks.Total} 通过");
return failures + checks.Failed == 0 ? 0 : 1;

sealed record Oracle(OracleCase[] Cases);
sealed record OracleCase(string Query, string Candidate, bool Completed, int Score, string? Partition = null);
