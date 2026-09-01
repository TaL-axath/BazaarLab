using System.Text.Json;
using BazaarLab.Combat;

if (args.Length != 7)
{
    Console.Error.WriteLine(
        "usage: BazaarLab.BaselineMetrics <catalog.jsonl> <snapshot.json> " +
        "<output.json> <base-seed> <samples> <ticks> <sample-interval-ticks>");
    Environment.ExitCode = 2;
    return;
}

OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[0]);
string snapshotPath = args[1];
string outputPath = args[2];
uint baseSeed = uint.Parse(args[3]);
int samples = int.Parse(args[4]);
int maximumTicks = int.Parse(args[5]);
int intervalTicks = int.Parse(args[6]);
if (samples <= 0 || maximumTicks <= 0 || intervalTicks <= 0)
{
    throw new ArgumentOutOfRangeException(nameof(args), "samples, ticks and interval must be positive");
}

int pointCount = maximumTicks / intervalTicks + 1;
double[] damage = new double[pointCount];
double[] shield = new double[pointCount];
double[] healing = new double[pointCount];
var unsupported = new Dictionary<string, int>(StringComparer.Ordinal);

for (int sample = 0; sample < samples; sample++)
{
    BppSnapshotImportResult imported = BppCombatSnapshotAdapter.Import(snapshotPath, catalog);
    CombatState state = imported.State;
    CombatantState player = state.Combatants[0];
    CombatantState target = state.Combatants[1];

    const int infiniteHealth = 1_000_000_000;
    player.SetIntrinsicAttribute("HealthMax", infiniteHealth);
    player.Health = infiniteHealth / 2;
    player.IntrinsicAttributes["Health"] = player.Health;
    player.Attributes["Health"] = player.Health;
    ResetStatus(player);
    target.SetIntrinsicAttribute("HealthMax", infiniteHealth);
    target.Health = infiniteHealth;
    target.IntrinsicAttributes["Health"] = target.Health;
    target.Attributes["Health"] = target.Health;
    ResetStatus(target);
    target.Cards.Clear();
    state.Sandstorm.Enabled = false;

    CombatSimulationResult result = CombatSimulation.RunIndexed(
        state, baseSeed, sample, maximumTicks);
    foreach ((string action, int count) in result.UnsupportedActions)
    {
        unsupported[action] = unsupported.GetValueOrDefault(action) + count;
    }

    List<CombatEvent> events = result.KeyEventTrace
        .OrderBy(value => value.Tick).ToList();
    long cumulativeDamage = 0;
    long cumulativeShield = 0;
    long cumulativeHealing = 0;
    int eventIndex = 0;
    for (int point = 0; point < pointCount; point++)
    {
        int tick = point * intervalTicks;
        while (eventIndex < events.Count && events[eventIndex].Tick <= tick)
        {
            CombatEvent combatEvent = events[eventIndex++];
            if (string.Equals(combatEvent.TargetId, target.Id, StringComparison.Ordinal) &&
                IsDamage(combatEvent.Kind))
            {
                cumulativeDamage += Math.Max(0, combatEvent.Amount) +
                    Math.Max(0, combatEvent.SecondaryAmount);
            }
            if (string.Equals(combatEvent.TargetId, player.Id, StringComparison.Ordinal))
            {
                if (combatEvent.Kind == "Shield")
                    cumulativeShield += Math.Max(0, combatEvent.Amount);
                else if (IsHealing(combatEvent.Kind))
                    cumulativeHealing += Math.Max(0, combatEvent.Amount);
            }
        }
        damage[point] += cumulativeDamage;
        shield[point] += cumulativeShield;
        healing[point] += cumulativeHealing;
    }
}

var points = Enumerable.Range(0, pointCount).Select(index => new BaselinePoint(
    index * intervalTicks * CombatEngine.TickMilliseconds / 1000.0,
    damage[index] / samples,
    shield[index] / samples,
    healing[index] / samples)).ToArray();
var report = new BaselineReport(
    samples,
    maximumTicks * CombatEngine.TickMilliseconds / 1000.0,
    intervalTicks * CombatEngine.TickMilliseconds / 1000.0,
    points,
    points[^1].Damage,
    points[^1].Shield,
    points[^1].Healing,
    unsupported);
string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
File.WriteAllText(outputPath, JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine(JsonSerializer.Serialize(report));

static bool IsDamage(string kind) => kind is
    "Damage" or "CardDamage" or "Burn" or "BurnShield" or "Poison";

static bool IsHealing(string kind) => kind is
    "Heal" or "LifeSteal" or "Regen" or "ReviveHeal";

static void ResetStatus(CombatantState combatant)
{
    combatant.Shield = 0;
    combatant.Poison = 0;
    combatant.Burn = 0;
    combatant.Regen = 0;
    combatant.SetIntrinsicAttribute("Shield", 0);
    combatant.SetIntrinsicAttribute("Poison", 0);
    combatant.SetIntrinsicAttribute("Burn", 0);
    combatant.SetIntrinsicAttribute("HealthRegen", 0);
    combatant.SetIntrinsicAttribute("PercentDamageReduction", 0);
    combatant.SetIntrinsicAttribute("FlatDamageReduction", 0);
}

public sealed record BaselinePoint(double TimeSeconds, double Damage, double Shield, double Healing);
public sealed record BaselineReport(
    int Samples,
    double DurationSeconds,
    double IntervalSeconds,
    IReadOnlyList<BaselinePoint> Points,
    double TotalDamage,
    double TotalShield,
    double TotalHealing,
    IReadOnlyDictionary<string, int> UnsupportedActions);
