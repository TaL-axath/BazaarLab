using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BazaarLab.Combat;

public sealed record CombatantSimulationResult(
    string Id, int Health, int Shield, int Poison, int Burn, int Regen,
    IReadOnlyDictionary<string, int> KeyAttributes);

public sealed record CombatEventAggregate(int Count, long Amount, long SecondaryAmount);

public sealed record CombatCardAttributeTransition(
    int Tick, string CardId, string Attribute, int Previous, int Current);

public sealed record CombatSimulationResult(
    uint MasterSeed,
    int RunIndex,
    uint EffectiveSeed,
    int Ticks,
    string? WinnerId,
    IReadOnlyList<CombatantSimulationResult> Combatants,
    int EventCount,
    IReadOnlyDictionary<string, int> UnsupportedActions,
    IReadOnlyDictionary<string, CombatEventAggregate> EventSummary,
    IReadOnlyList<CombatEvent> KeyEventTrace,
    IReadOnlyList<CombatEvent> FullEventTrace,
    IReadOnlyList<CombatCardAttributeTransition> CardAttributeTrace,
    string EventSha256);

public sealed record CombatSimulationOutcome(
    string? WinnerId,
    IReadOnlyDictionary<string, int> UnsupportedActions);

public static class CombatSimulation
{
    public static CombatSimulationOutcome RunOutcomeIndexed(
        CombatState state, uint masterSeed, int runIndex, int maximumTicks)
    {
        uint effectiveSeed = SeedMixer.Mix(masterSeed, runIndex);
        var random = new XorShiftCombatRandom(effectiveSeed);
        var rules = new CombatRuleRuntime(state, random);
        var scheduler = new CombatScheduler(state, rules, random);
        scheduler.StartFight();
        int advanced = 0;
        while (advanced < maximumTicks && state.Combatants.Count(value => value.Health > 0) > 1)
        {
            scheduler.AdvanceOneTick();
            advanced++;
        }
        string? winner = state.Combatants.Count(value => value.Health > 0) == 1
            ? state.Combatants.Single(value => value.Health > 0).Id
            : null;
        Dictionary<string, int> unsupported = state.Events
            .Where(value => value.Kind.StartsWith("UnsupportedAction:", StringComparison.Ordinal))
            .GroupBy(value => value.Kind["UnsupportedAction:".Length..], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new CombatSimulationOutcome(winner, unsupported);
    }

    public static CombatSimulationResult Run(
        CombatState state, int seed, int maximumTicks, bool captureReplayTrace = false)
        => RunIndexed(state, unchecked((uint)seed), 0, maximumTicks, captureReplayTrace);

    // Keep binary compatibility with placement/baseline tools compiled before
    // replay trace capture became an optional fifth parameter.
    public static CombatSimulationResult RunIndexed(
        CombatState state, uint masterSeed, int runIndex, int maximumTicks)
        => RunIndexed(state, masterSeed, runIndex, maximumTicks, false);

    public static CombatSimulationResult RunIndexed(
        CombatState state, uint masterSeed, int runIndex, int maximumTicks,
        bool captureReplayTrace = false)
    {
        uint effectiveSeed = SeedMixer.Mix(masterSeed, runIndex);
        var random = new XorShiftCombatRandom(effectiveSeed);
        var rules = new CombatRuleRuntime(state, random);
        var scheduler = new CombatScheduler(state, rules, random);
        scheduler.StartFight();
        var cardAttributeTrace = new List<CombatCardAttributeTransition>();
        Dictionary<(string CardId, string Attribute), int>? previousCardAttributes =
            captureReplayTrace ? CaptureCardAttributes(state) : null;
        int advanced = 0;
        while (advanced < maximumTicks && state.Combatants.Count(value => value.Health > 0) > 1)
        {
            scheduler.AdvanceOneTick();
            advanced++;
            if (previousCardAttributes is null)
                continue;
            foreach (CombatCardState card in state.Combatants.SelectMany(value => value.Cards))
            {
                foreach ((string attribute, int current) in EnumerateCardAttributes(card))
                {
                    var key = (card.InstanceId, attribute);
                    if (!previousCardAttributes.TryGetValue(key, out int previous))
                        previous = current;
                    if (current != previous)
                    {
                        cardAttributeTrace.Add(new CombatCardAttributeTransition(
                            state.Tick, card.InstanceId, attribute, previous, current));
                    }
                    previousCardAttributes[key] = current;
                }
            }
        }
        string? winner = state.Combatants.Count(value => value.Health > 0) == 1
            ? state.Combatants.Single(value => value.Health > 0).Id
            : null;
        Dictionary<string, int> unsupported = state.Events
            .Where(value => value.Kind.StartsWith("UnsupportedAction:", StringComparison.Ordinal))
            .GroupBy(value => value.Kind["UnsupportedAction:".Length..], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        string eventJson = JsonSerializer.Serialize(state.Events);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventJson)));
        Dictionary<string, CombatEventAggregate> summary = state.Events
            .GroupBy(value => value.Kind, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new CombatEventAggregate(
                    group.Count(), group.Sum(value => (long)value.Amount),
                    group.Sum(value => (long)value.SecondaryAmount)),
                StringComparer.Ordinal);
        return new CombatSimulationResult(
            masterSeed,
            runIndex,
            effectiveSeed,
            advanced,
            winner,
            state.Combatants.Select(value => new CombatantSimulationResult(
                value.Id, value.Health, value.Shield, value.Poison, value.Burn, value.Regen,
                new[] { "Tempo", "Rage", "Enraged", "EnragedDuration" }
                    .ToDictionary(attribute => attribute,
                        attribute => value.Attributes.GetValueOrDefault(attribute),
                        StringComparer.Ordinal))).ToList(),
            state.Events.Count,
            unsupported,
            summary,
            state.Events.Where(IsKeyTraceEvent).ToList(),
            state.Events.ToList(),
            cardAttributeTrace,
            hash);
    }

    private static Dictionary<(string CardId, string Attribute), int> CaptureCardAttributes(
        CombatState state)
    {
        var result = new Dictionary<(string CardId, string Attribute), int>();
        foreach (CombatCardState card in state.Combatants.SelectMany(value => value.Cards))
            foreach ((string attribute, int value) in EnumerateCardAttributes(card))
                result[(card.InstanceId, attribute)] = value;
        return result;
    }

    private static IEnumerable<KeyValuePair<string, int>> EnumerateCardAttributes(
        CombatCardState card)
    {
        yield return new KeyValuePair<string, int>(
            "Cooldown", card.CooldownRemainingMilliseconds);
        foreach (KeyValuePair<string, int> pair in card.Attributes)
            if (!string.Equals(pair.Key, "Cooldown", StringComparison.Ordinal))
                yield return pair;
    }

    internal static bool IsKeyTraceEvent(CombatEvent value) =>
        ActualCombatDifferential.MapLocalEventToAction(value.Kind) is not null ||
        value.Kind is "CardUsed" or "CardDamage" or "CardCrit" or "ForceUse" or
            "Heal" or "OverHeal" or "Shield" or "BurnApply" or "PoisonApply" or
            "RegenApply" or "LifeSteal" or "Burn" or "BurnShield" or "Poison" or
            "Regen" or "CardCharge" or "CardHaste" or
            "CardSlow" or "CardFreeze" or "CardTransformed" ||
        value.Kind is "PlayerAttribute:Tempo" or "PlayerAttribute:Rage" or
            "PlayerAttribute:Enraged" ||
        value.Kind.StartsWith("CardModifyAttribute:", StringComparison.Ordinal) ||
        value.Kind.StartsWith("CardAttribute:", StringComparison.Ordinal);
}
