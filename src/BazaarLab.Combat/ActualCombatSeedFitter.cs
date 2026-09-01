using System.Text.Json;

namespace BazaarLab.Combat;

public sealed record ActualCombatSeedCandidate(
    int RunIndex,
    uint EffectiveSeed,
    string? LocalWinner,
    bool WinnerMatch,
    int LocalTicks,
    int FrameDistance,
    int ActionDistance,
    int SourceActionDistance,
    int AttributeTargetDistance,
    double Score);

public sealed record ActualCombatSeedFitReport(
    uint MasterSeed,
    int Samples,
    int MinimumSamples,
    int MaximumSamples,
    int BatchSamples,
    int ActualFrames,
    string? ActualWinner,
    bool StoppedEarly,
    string StopReason,
    IReadOnlyList<ActualCombatSeedCandidate> BestCandidates);

public static class ActualCombatSeedFitter
{
    public static ActualCombatSeedFitReport Fit(
        string snapshotPath,
        string actualPath,
        OfficialCardCatalog catalog,
        uint masterSeed,
        int samples,
        int maximumTicks,
        int retainedCandidates = 10)
    {
        return FitAdaptive(
            snapshotPath, actualPath, catalog, masterSeed,
            samples, samples, samples, maximumTicks, retainedCandidates);
    }

    public static ActualCombatSeedFitReport FitAdaptive(
        string snapshotPath,
        string actualPath,
        OfficialCardCatalog catalog,
        uint masterSeed,
        int minimumSamples,
        int maximumSamples,
        int batchSamples,
        int maximumTicks,
        int retainedCandidates = 10)
    {
        if (minimumSamples <= 0 || maximumSamples < minimumSamples || batchSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSamples),
                "seed-fit samples must satisfy 0 < minimum <= maximum and batch > 0");
        }
        string actualJson = File.ReadAllText(actualPath);
        using JsonDocument actualDocument = JsonDocument.Parse(actualJson);
        JsonElement actual = actualDocument.RootElement;
        int actualFrames = ReadInt(actual, "FrameCount", "frame_count");
        string? actualWinner = NormalizeWinner(ReadString(actual, "Winner", "winner"));
        int simulationLimit = Math.Max(1, Math.Min(maximumTicks, Math.Max(1, actualFrames)));
        var candidates = new List<ActualCombatSeedCandidate>(maximumSamples);
        bool stoppedEarly = false;
        string stopReason = "maximum-samples";

        for (int runIndex = 0; runIndex < maximumSamples; runIndex++)
        {
            BppSnapshotImportResult imported = BppCombatSnapshotAdapter.Import(snapshotPath, catalog);
            CombatSimulationResult simulation = CombatSimulation.RunIndexed(
                imported.State, masterSeed, runIndex, simulationLimit);
            string simulationJson = JsonSerializer.Serialize(simulation);
            ActualCombatDifferentialReport differential =
                ActualCombatDifferential.CompareJson(actualJson, simulationJson);
            int actionDistance = differential.ActionDeltas.Sum(value => Math.Abs(value.Delta));
            int sourceDistance = differential.SourceActionDeltas.Sum(value => Math.Abs(value.Delta));
            string[] volatileAttributes = ["Haste", "Slow", "Freeze", "Flying", "Ammo"];
            bool IsVolatileAttributeKey(string key) => volatileAttributes.Any(attribute =>
                key.EndsWith("|" + attribute, StringComparison.Ordinal));
            int attributeTargetDistance = differential.CardAttributeTargetDeltas
                .Where(value => IsVolatileAttributeKey(value.Kind))
                .Sum(value => Math.Abs(value.Delta));
            int actualActionCount = Math.Max(1, differential.ActualActionCounts.Values.Sum());
            int actualSourceCount = Math.Max(1,
                differential.ActualSourceActionCounts.Values.Sum());
            int actualAttributeTargetCount = Math.Max(1,
                differential.ActualCardAttributeTargetCounts
                    .Where(value => IsVolatileAttributeKey(value.Key))
                    .Sum(value => value.Value));
            int frameDistance = Math.Abs(actualFrames - simulation.Ticks);
            // Target-level status placement refines otherwise close trajectories,
            // but remains lower-weight than action/source cadence because captures
            // omit natural countdown events and some stable attribute transitions.
            double score = (differential.WinnerMatch ? 0d : 4d) +
                (double)sourceDistance / actualSourceCount +
                (double)actionDistance / actualActionCount +
                0.1d * attributeTargetDistance / actualAttributeTargetCount +
                (double)frameDistance / Math.Max(1, actualFrames);
            candidates.Add(new ActualCombatSeedCandidate(
                runIndex,
                simulation.EffectiveSeed,
                simulation.WinnerId,
                differential.WinnerMatch,
                simulation.Ticks,
                frameDistance,
                actionDistance,
                sourceDistance,
                attributeTargetDistance,
                score));

            int completedSamples = runIndex + 1;
            bool checkpoint = completedSamples >= minimumSamples &&
                ((completedSamples - minimumSamples) % batchSamples == 0 ||
                    completedSamples == maximumSamples);
            if (checkpoint && candidates.Any(IsExactTraceMatch))
            {
                stoppedEarly = completedSamples < maximumSamples;
                stopReason = "exact-trace-match";
                break;
            }
        }

        return new ActualCombatSeedFitReport(
            masterSeed,
            candidates.Count,
            minimumSamples,
            maximumSamples,
            batchSamples,
            actualFrames,
            actualWinner,
            stoppedEarly,
            stopReason,
            candidates.OrderBy(value => value.Score)
                .ThenBy(value => value.RunIndex)
                .Take(Math.Max(1, retainedCandidates))
                .ToArray());
    }

    public static bool IsExactTraceMatch(ActualCombatSeedCandidate candidate) =>
        candidate.WinnerMatch &&
        candidate.FrameDistance == 0 &&
        candidate.ActionDistance == 0 &&
        candidate.SourceActionDistance == 0 &&
        candidate.AttributeTargetDistance == 0;

    public static void Write(string path, ActualCombatSeedFitReport report)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(
            report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static int ReadInt(JsonElement value, params string[] names)
    {
        foreach (string name in names)
        {
            if (value.TryGetProperty(name, out JsonElement property) &&
                property.TryGetInt32(out int result))
            {
                return result;
            }
        }
        return 0;
    }

    private static string? ReadString(JsonElement value, params string[] names)
    {
        foreach (string name in names)
        {
            if (value.TryGetProperty(name, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }
        return null;
    }

    private static string? NormalizeWinner(string? winner) => winner?.ToLowerInvariant() switch
    {
        "player" or "win" => "player",
        "opponent" or "loss" => "opponent",
        "draw" => "draw",
        _ => winner?.ToLowerInvariant(),
    };
}
