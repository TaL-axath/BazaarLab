namespace BazaarLab.Combat;

public sealed record LocalReplayProjectionResult(
    string BattleId,
    int FrameCount,
    string? WinnerId,
    IReadOnlyList<LocalReplayFrame> Frames,
    IReadOnlyList<string> VfxKeys,
    IReadOnlyDictionary<string, int> UnsupportedActions);

public sealed record LocalReplayFrame(
    int Frame,
    IReadOnlyList<LocalReplayAttributeTransition> PlayerAttributes,
    IReadOnlyList<LocalReplayAttributeTransition> OpponentAttributes,
    IReadOnlyList<LocalReplayHealthTransition> PlayerHealth,
    IReadOnlyList<LocalReplayHealthTransition> OpponentHealth,
    IReadOnlyList<LocalReplayCardTransition> CardAttributes,
    IReadOnlyList<LocalReplayEffect> Effects,
    string? Died);

public sealed record LocalReplayAttributeTransition(string Attribute, int Previous, int Current);

public sealed record LocalReplayHealthTransition(
    string Pool, int Previous, int Current, int Amount, string Kind, string? SourceId,
    bool Critical);

public sealed record LocalReplayCardTransition(
    string CardId, string Attribute, int Previous, int Current, string? SourceId);

public sealed record LocalReplayEffect(
    string Kind, string? SourceId, string? TargetId, int Amount, bool Critical,
    string? EffectId, string? ActionType, string? ExecutionContextId,
    string? TriggerSourceId, string? VfxOverrideKey);

public static class LocalReplayProjection
{
    public static LocalReplayProjectionResult Build(
        string battleId, CombatSimulationResult simulation)
    {
        var frames = new List<LocalReplayFrame>(simulation.Ticks);
        var vfxKeys = new HashSet<string>(StringComparer.Ordinal);
        ILookup<int, CombatEvent> byTick = simulation.FullEventTrace.ToLookup(value => value.Tick);
        ILookup<int, CombatCardAttributeTransition> cardAttributesByTick =
            simulation.CardAttributeTrace.ToLookup(value => value.Tick);
        for (int tick = 1; tick <= simulation.Ticks; tick++)
        {
            var playerAttributes = new List<LocalReplayAttributeTransition>();
            var opponentAttributes = new List<LocalReplayAttributeTransition>();
            var playerHealth = new List<LocalReplayHealthTransition>();
            var opponentHealth = new List<LocalReplayHealthTransition>();
            var cardAttributes = new List<LocalReplayCardTransition>();
            var effects = new List<LocalReplayEffect>();
            string? died = null;
            bool critical = byTick[tick].Any(value => value.Kind == "CardCrit");
            var emittedExecutions = new HashSet<string>(StringComparer.Ordinal);
            var tracedCardAttributes = new HashSet<string>(StringComparer.Ordinal);
            foreach (CombatCardAttributeTransition transition in cardAttributesByTick[tick])
            {
                tracedCardAttributes.Add(transition.CardId + "\0" + transition.Attribute);
                cardAttributes.Add(new LocalReplayCardTransition(
                    transition.CardId, transition.Attribute, transition.Previous,
                    transition.Current, null));
            }
            foreach (CombatEvent value in byTick[tick])
            {
                // CardTransformedSpawn is internal state bookkeeping for the extra
                // cards produced by one transform action.  The native replay action
                // must be emitted once against the destroyed card; emitting another
                // action for every spawned card makes the client perform the random
                // transform repeatedly.
                if (!string.IsNullOrWhiteSpace(value.ExecutionContextId) &&
                    value.Kind != "CardTransformedSpawn")
                {
                    string executionKey = value.ExecutionContextId + "\0" + value.TargetId;
                    if (emittedExecutions.Add(executionKey))
                    {
                        string kind = TryEffectKind(value.Kind, out string? directKind)
                            ? directKind! : NormalizeActionType(value.ActionType);
                        effects.Add(new LocalReplayEffect(kind, value.SourceId,
                            value.TargetId, value.Amount, critical, value.EffectId,
                            NormalizeActionType(value.ActionType), value.ExecutionContextId,
                            value.TriggerSourceId, value.VfxOverrideKey));
                        if (!string.IsNullOrWhiteSpace(value.VfxOverrideKey))
                            vfxKeys.Add(value.VfxOverrideKey);
                    }
                }
                if (value.Kind.StartsWith("PlayerAttribute:", StringComparison.Ordinal))
                {
                    string attribute = value.Kind["PlayerAttribute:".Length..];
                    if (attribute == "Health")
                    {
                        bool isDamage = value.Amount < value.SecondaryAmount;
                        string? source = FindHealthSource(
                            byTick[tick], value.TargetId, isDamage);
                        var transition = new LocalReplayHealthTransition(
                            "Health", value.SecondaryAmount, value.Amount,
                            Math.Abs(value.Amount - value.SecondaryAmount),
                            FindHealthKind(byTick[tick], value.TargetId, isDamage, "Health"),
                            source, IsCritical(byTick[tick], source));
                        Select(value.TargetId, playerHealth, opponentHealth).Add(transition);
                    }
                    else if (attribute == "Shield")
                    {
                        bool isDamage = value.Amount < value.SecondaryAmount;
                        string? source = FindHealthSource(
                            byTick[tick], value.TargetId, isDamage);
                        var transition = new LocalReplayHealthTransition(
                            "Shield", value.SecondaryAmount, value.Amount,
                            Math.Abs(value.Amount - value.SecondaryAmount),
                            FindHealthKind(byTick[tick], value.TargetId, isDamage, "Shield"),
                            source, IsCritical(byTick[tick], source));
                        Select(value.TargetId, playerHealth, opponentHealth).Add(transition);
                    }
                    else
                    {
                        var transition = new LocalReplayAttributeTransition(
                            attribute, value.SecondaryAmount, value.Amount);
                        Select(value.TargetId, playerAttributes, opponentAttributes).Add(transition);
                    }
                    continue;
                }
                if (value.Kind.StartsWith("CardModifyAttribute:", StringComparison.Ordinal) ||
                    value.Kind.StartsWith("CardAttribute:", StringComparison.Ordinal))
                {
                    int separator = value.Kind.IndexOf(':');
                    string attribute = value.Kind[(separator + 1)..];
                    if (tracedCardAttributes.Contains(
                        (value.TargetId ?? string.Empty) + "\0" + attribute))
                        continue;
                    cardAttributes.Add(new LocalReplayCardTransition(
                        value.TargetId ?? string.Empty, attribute,
                        value.SecondaryAmount, value.Amount, value.SourceId));
                    continue;
                }
                if (value.Kind == "CombatantDied")
                {
                    died = value.TargetId;
                    continue;
                }
                if (TryEffectKind(value.Kind, out string? effectKind))
                {
                    if (value.ExecutionContextId is null)
                        effects.Add(new LocalReplayEffect(effectKind!, value.SourceId,
                            value.TargetId, value.Amount, critical, null, null, null,
                            null, null));
                }
            }
            if (tick == simulation.Ticks && died is null && simulation.WinnerId is not null)
                died = string.Equals(simulation.WinnerId, "player", StringComparison.Ordinal)
                    ? "opponent" : "player";
            frames.Add(new LocalReplayFrame(tick - 1, playerAttributes, opponentAttributes,
                playerHealth, opponentHealth, cardAttributes, effects, died));
        }
        return new LocalReplayProjectionResult(battleId, frames.Count, simulation.WinnerId,
            frames, vfxKeys.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            simulation.UnsupportedActions);
    }

    private static List<T> Select<T>(string? targetId, List<T> player, List<T> opponent) =>
        string.Equals(targetId, "opponent", StringComparison.OrdinalIgnoreCase)
            ? opponent : player;

    private static string? FindHealthSource(
        IEnumerable<CombatEvent> frame, string? target, bool damage)
    {
        string[] kinds = damage
            ? new[] { "CardDamage", "Burn", "Poison", "SandstormDamage" }
            : new[] { "Heal", "OverHeal", "Regen", "LifeSteal" };
        return frame.LastOrDefault(value =>
            string.Equals(value.TargetId, target, StringComparison.Ordinal) &&
            kinds.Contains(value.Kind, StringComparer.Ordinal))?.SourceId;
    }

    private static string FindHealthKind(
        IEnumerable<CombatEvent> frame, string? target, bool damage, string pool)
    {
        if (!damage && pool == "Shield") return "Shield";
        string[] kinds = damage
            ? new[] { "CardDamage", "Burn", "BurnShield", "Poison", "SandstormDamage" }
            : new[] { "Heal", "OverHeal", "Regen", "LifeSteal" };
        string? kind = frame.LastOrDefault(value =>
            string.Equals(value.TargetId, target, StringComparison.Ordinal) &&
            kinds.Contains(value.Kind, StringComparer.Ordinal))?.Kind;
        return kind switch
        {
            "Burn" or "BurnShield" => "Burn",
            "Poison" => "Poison",
            "Regen" => "Regen",
            "Shield" => "Shield",
            _ => damage ? "Damage" : "Heal",
        };
    }

    private static bool IsCritical(IEnumerable<CombatEvent> frame, string? source) =>
        source is not null && frame.Any(value => value.Kind == "CardCrit" &&
            string.Equals(value.SourceId, source, StringComparison.Ordinal));

    private static bool TryEffectKind(string kind, out string? projected)
    {
        projected = kind switch
        {
            "CardUsed" => "Use",
            "CardDamage" => "Damage",
            "Heal" or "OverHeal" or "LifeSteal" => "Heal",
            "Shield" => "Shield",
            "BurnApply" => "Burn",
            "PoisonApply" => "Poison",
            "RegenApply" => "Regen",
            "CardCharge" => "Charge",
            "CardHaste" => "Haste",
            "CardSlow" => "Slow",
            "CardFreeze" => "Freeze",
            "ForceUse" => "ForceUse",
            "Burn" or "BurnShield" or "Poison" or "Regen" => kind,
            _ => null,
        };
        return projected is not null;
    }

    private static string NormalizeActionType(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType)) return "None";
        string result = actionType!;
        if (result.StartsWith("TAction", StringComparison.Ordinal)) result = result[7..];
        else if (result.StartsWith("TAuraAction", StringComparison.Ordinal)) result = result[11..];
        return result switch
        {
            "CardFlyingStart" => "FlyingStart",
            "CardFlyingStop" => "FlyingStop",
            _ => result,
        };
    }
}
