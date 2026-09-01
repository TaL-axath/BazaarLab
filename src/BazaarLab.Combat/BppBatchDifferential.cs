using System.Text.Json;

namespace BazaarLab.Combat;

public sealed record BppDifferentialCase(
    string BattleId, string? Actual, string? Predicted, bool Match,
    int Ticks, IReadOnlyDictionary<string, int> UnsupportedActions,
    string? EventSha256, string? Error)
{
    public int UnsupportedActionCount => UnsupportedActions.Values.Sum();
}

public sealed record BppDifferentialReport(
    int Total, int Decided, int Matches, double Accuracy,
    IReadOnlyDictionary<string, int> UnsupportedActions,
    IReadOnlyList<BppDifferentialCase> Cases);

public static class BppBatchDifferential
{
    public static BppDifferentialReport Run(
        string directory, OfficialCardCatalog catalog, int seed, int maximumTicks)
    {
        var cases = new List<BppDifferentialCase>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json")
            .OrderBy(value => value, StringComparer.Ordinal))
        {
            string battleId = Path.GetFileNameWithoutExtension(path);
            string? actual = null;
            try
            {
                BppSnapshotImportResult imported = BppCombatSnapshotAdapter.Import(path, catalog);
                battleId = imported.BattleId ?? battleId;
                actual = imported.ActualResult;
                CombatSimulationResult simulation = CombatSimulation.Run(
                    imported.State, seed, maximumTicks);
                string? predicted = simulation.WinnerId switch
                {
                    "player" => "win",
                    "opponent" => "loss",
                    _ => null,
                };
                cases.Add(new BppDifferentialCase(
                    battleId, actual, predicted,
                    predicted is not null && predicted == actual,
                    simulation.Ticks, simulation.UnsupportedActions,
                    simulation.EventSha256, null));
            }
            catch (Exception exception)
            {
                cases.Add(new BppDifferentialCase(
                    battleId, actual, null, false,
                    0, new Dictionary<string, int>(), null,
                    exception.GetType().Name + ": " + exception.Message));
            }
        }
        int decided = cases.Count(value => value.Predicted is not null);
        int matches = cases.Count(value => value.Match);
        Dictionary<string, int> unsupported = cases
            .SelectMany(value => value.UnsupportedActions)
            .GroupBy(value => value.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(value => value.Value),
                StringComparer.Ordinal);
        return new BppDifferentialReport(
            cases.Count, decided, matches,
            decided == 0 ? 0 : (double)matches / decided,
            unsupported,
            cases);
    }

    public static void Write(string path, BppDifferentialReport report) =>
        File.WriteAllText(path, JsonSerializer.Serialize(
            report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
}
