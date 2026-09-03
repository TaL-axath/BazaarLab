namespace BazaarLab.Combat;

public sealed class CombatantState
{
    public required string Id { get; init; }
    public string? Hero { get; set; }
    // null preserves the legacy state-wide behavior. Preview adapters set this
    // explicitly because live player attributes include auras while static
    // monster attributes do not.
    public bool? AttributesArePrecomputed { get; set; }
    public int MaxHealth { get; set; }
    public int Health { get; set; }
    public int Shield { get; set; }
    public int Poison { get; set; }
    public int Burn { get; set; }
    public int Regen { get; set; }
    public int DamageReductionPercent { get; set; }
    public int FlatDamageReduction { get; set; }
    public int? InitialTempoCooldownMilliseconds { get; set; }
    public int TempoCooldownRemainingMilliseconds { get; set; }
    public Dictionary<string, int> IntrinsicAttributes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Attributes { get; } = new(StringComparer.Ordinal);
    public List<CombatCardState> Cards { get; } = [];

    public int GetAttribute(string attribute) => attribute switch
    {
        "Health" => Health,
        // During an aura pass Attributes already contains earlier HealthMax
        // contributions while MaxHealth is synchronized at the end of the pass.
        "HealthMax" => Attributes.GetValueOrDefault("HealthMax", MaxHealth),
        "Shield" => Shield,
        "Burn" => Burn,
        "Poison" => Poison,
        "Regen" or "HealthRegen" => Regen,
        "PercentDamageReduction" => DamageReductionPercent,
        "FlatDamageReduction" => FlatDamageReduction,
        _ => Attributes.GetValueOrDefault(attribute),
    };

    public void SetIntrinsicAttribute(string attribute, int value)
    {
        IntrinsicAttributes[attribute] = value;
        Attributes[attribute] = value;
        if (attribute == "HealthMax")
        {
            SetEffectiveMaxHealth(value);
        }
        else if (attribute == "PercentDamageReduction")
        {
            DamageReductionPercent = value;
        }
        else if (attribute == "FlatDamageReduction")
        {
            FlatDamageReduction = value;
        }
        else if (attribute is "HealthRegen" or "Regen")
        {
            Regen = value;
        }
    }

    public void SetEffectiveMaxHealth(int value)
    {
        int newMaximum = Math.Max(1, value);
        Health = Math.Min(newMaximum, checked(Health + newMaximum - MaxHealth));
        MaxHealth = newMaximum;
    }
}

public sealed class CombatCardState
{
    public const int MinimumActiveCooldownMilliseconds = 500;

    public required string InstanceId { get; init; }
    public required MaterializedCardDefinition Definition { get; set; }
    public required CombatantState Owner { get; init; }
    public string Section { get; init; } = "Hand";
    public int BoardPosition { get; set; }
    public int Span { get; set; } = 1;
    public int CooldownRemainingMilliseconds { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsDestroyed { get; set; }
    public bool AttributesArePrecomputed { get; set; }
    public Dictionary<string, int> IntrinsicAttributes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Attributes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> IntrinsicTags { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);
    public HashSet<string> IntrinsicHiddenTags { get; } = new(StringComparer.Ordinal);
    public HashSet<string> HiddenTags { get; } = new(StringComparer.Ordinal);

    public void SetIntrinsicAttribute(string attribute, int value)
    {
        IntrinsicAttributes[attribute] = value;
        Attributes[attribute] = value;
    }

    public int GetEffectiveCooldownMilliseconds()
    {
        int maximum = Math.Max(0, Attributes.GetValueOrDefault("CooldownMax"));
        if (AttributesArePrecomputed)
        {
            return ClampActiveCooldown(maximum);
        }
        int flat = Attributes.GetValueOrDefault("FlatCooldownReduction");
        int afterFlat = Math.Max(0, checked(maximum - flat));
        int reduction = Math.Clamp(Attributes.GetValueOrDefault("PercentCooldownReduction"), -100, 100);
        return ClampActiveCooldown(checked(
            (int)((long)afterFlat * (100 - reduction) / 100)));
    }

    public void AdjustCooldownForReductionTransition(
        int oldPercentReduction,
        int oldFlatReduction,
        int newPercentReduction,
        int newFlatReduction)
    {
        if (oldPercentReduction == newPercentReduction &&
            oldFlatReduction == newFlatReduction)
        {
            return;
        }

        int storedMaximum = Math.Max(0, Attributes.GetValueOrDefault("CooldownMax"));
        int oldEffectiveMaximum;
        int newEffectiveMaximum;
        if (AttributesArePrecomputed)
        {
            oldEffectiveMaximum = ClampActiveCooldown(storedMaximum);
            int oldFactor = 100 - Math.Clamp(oldPercentReduction, -100, 100);
            int newFactor = 100 - Math.Clamp(newPercentReduction, -100, 100);
            int flatDelta = checked(newFlatReduction - oldFlatReduction);
            int afterFlatTransition = Math.Max(0, checked(
                storedMaximum - checked((int)((long)flatDelta * oldFactor / 100))));
            newEffectiveMaximum = oldFactor == 0
                ? afterFlatTransition
                : Math.Max(0, checked((int)(
                    (long)afterFlatTransition * newFactor / oldFactor)));
            newEffectiveMaximum = ClampActiveCooldown(newEffectiveMaximum);
            IntrinsicAttributes["CooldownMax"] = newEffectiveMaximum;
            Attributes["CooldownMax"] = newEffectiveMaximum;
        }
        else
        {
            oldEffectiveMaximum = ClampActiveCooldown(CalculateEffectiveCooldown(
                storedMaximum, oldFlatReduction, oldPercentReduction));
            newEffectiveMaximum = ClampActiveCooldown(CalculateEffectiveCooldown(
                storedMaximum, newFlatReduction, newPercentReduction));
        }

        if (CooldownRemainingMilliseconds > 0)
        {
            CooldownRemainingMilliseconds = Math.Max(0, checked(
                CooldownRemainingMilliseconds +
                newEffectiveMaximum - oldEffectiveMaximum));
        }
    }

    private static int CalculateEffectiveCooldown(
        int maximum,
        int flatReduction,
        int percentReduction)
    {
        int afterFlat = Math.Max(0, checked(maximum - flatReduction));
        int factor = 100 - Math.Clamp(percentReduction, -100, 100);
        return checked((int)((long)afterFlat * factor / 100));
    }

    private int ClampActiveCooldown(int cooldown)
    {
        bool isActiveCooldownCard =
            Definition.Attributes.GetValueOrDefault("CooldownMax") > 0;
        return isActiveCooldownCard
            ? Math.Max(MinimumActiveCooldownMilliseconds, cooldown)
            : Math.Max(0, cooldown);
    }

    public static CombatCardState Create(
        string instanceId,
        MaterializedCardDefinition definition,
        CombatantState owner,
        int boardPosition = 0,
        string section = "Hand",
        int span = 1)
    {
        var card = new CombatCardState
        {
            InstanceId = instanceId,
            Definition = definition,
            Owner = owner,
            BoardPosition = boardPosition,
            Section = section,
            Span = span,
        };
        foreach ((string key, int value) in definition.Attributes)
        {
            card.IntrinsicAttributes[key] = value;
            card.Attributes[key] = value;
        }
        card.IntrinsicTags.UnionWith(definition.Tags);
        card.Tags.UnionWith(definition.Tags);
        card.IntrinsicHiddenTags.UnionWith(definition.HiddenTags);
        card.HiddenTags.UnionWith(definition.HiddenTags);
        owner.Cards.Add(card);
        return card;
    }
}

public sealed class SandstormState
{
    public bool Enabled { get; set; } = true;
    public bool Started { get; internal set; }
    public int IntervalMilliseconds { get; internal set; } = 250;
    public int ElapsedMilliseconds { get; internal set; }
    public int Damage { get; internal set; } = 1;
}

public sealed record CombatEvent(
    int Tick,
    string Kind,
    string? TargetId = null,
    int Amount = 0,
    int SecondaryAmount = 0,
    string? SourceId = null,
    string? EffectId = null,
    string? ActionType = null,
    string? ExecutionContextId = null,
    string? TriggerSourceId = null,
    string? VfxOverrideKey = null,
    bool Critical = false);

public sealed record TimedCardModifier(
    CombatCardState Target,
    string Attribute,
    string Operation,
    double Amount,
    int ExpiresAtTick);

public sealed record TimedPlayerModifier(
    CombatantState Target,
    string Attribute,
    string Operation,
    double Amount,
    int ExpiresAtTick);

public sealed record ScheduledForceUse(
    CombatCardState Card, int DueTick, bool AllowDisabled = false);

public sealed record ScheduledChargeReadyUse(CombatCardState Card, int DueTick);

public sealed record ScheduledRuleEffect(
    CombatCardState Card,
    MaterializedEffectDefinition Effect,
    int DueTick,
    CombatCardState? TriggerSource,
    CombatCardState? TriggerTarget,
    bool Critical,
    int? AttributeDelta,
    bool EmitCardCrittedAfterCompletion = false,
    long? ReadyScopeId = null,
    bool CompletesReadyScope = false);

public sealed record ScheduledReadySignal(
    CombatCardState Card,
    string TriggerType,
    int DueTick,
    long? ReadyScopeId = null);

public sealed class CombatState
{
    public int Tick { get; internal set; }
    public List<CombatantState> Combatants { get; } = [];
    public SandstormState Sandstorm { get; } = new();
    public List<CombatEvent> Events { get; } = [];
    public List<TimedCardModifier> TimedCardModifiers { get; } = [];
    public List<TimedPlayerModifier> TimedPlayerModifiers { get; } = [];
    public List<ScheduledForceUse> ScheduledForceUses { get; } = [];
    public List<ScheduledChargeReadyUse> ScheduledChargeReadyUses { get; } = [];
    public List<ScheduledRuleEffect> ScheduledRuleEffects { get; } = [];
    public List<ScheduledReadySignal> ScheduledReadySignals { get; } = [];
    public bool CardAttributesArePrecomputed { get; set; }
    public OfficialCardCatalog? CardCatalog { get; set; }
}

public readonly record struct DamageResult(int ShieldAbsorbed, int HealthDamage);
