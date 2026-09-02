using System.Text.Json;

namespace BazaarLab.Combat;

public sealed class CombatAuraRuntime
{
    private readonly CombatState _state;
    private readonly XorShiftCombatRandom _random;
    private bool _precomputedUnbaked;

    public CombatAuraRuntime(CombatState state, XorShiftCombatRandom random)
    {
        _state = state;
        _random = random;
    }

    public int Recompute()
    {
        Dictionary<CombatCardState, (int Percent, int Flat)> cooldownReductions =
            _state.Combatants
                .SelectMany(combatant => combatant.Cards)
                .ToDictionary(
                    card => card,
                    card => (
                        card.Attributes.GetValueOrDefault("PercentCooldownReduction"),
                        card.Attributes.GetValueOrDefault("FlatCooldownReduction")));
        if (_state.CardAttributesArePrecomputed && !_precomputedUnbaked)
        {
            UnbakeInitialAuras();
            _precomputedUnbaked = true;
        }
        ResetEffectiveState();
        ApplyTimedModifiers();
        int applied = 0;
        var cardAdditiveMultiplyGroups =
            new Dictionary<(CombatCardState Target, string Attribute), double>();
        var playerAdditiveMultiplyGroups =
            new Dictionary<(CombatantState Target, string Attribute), double>();
        foreach ((CombatCardState owner, MaterializedEffectDefinition effect) in ActiveAuras())
        {
            JsonElement action = effect.Definition.GetObjectOrNull("Action")!.Value;
            var context = new CombatActionContext(_state, owner, _random);
            if (!RulePrerequisiteEvaluator.AreSatisfied(effect, context))
            {
                continue;
            }
            switch (action.GetStringOrNull("$type"))
            {
                case "TAuraActionCardModifyAttribute":
                    if (action.GetStringOrNull("Operation") == "AdditiveMultiply")
                    {
                        CollectCardAdditiveMultiplyAura(
                            action, context, cardAdditiveMultiplyGroups);
                    }
                    else
                    {
                        ApplyCardAttributeAura(action, context);
                    }
                    applied++;
                    break;
                case "TAuraActionPlayerModifyAttribute":
                    if (action.GetStringOrNull("Operation") == "AdditiveMultiply")
                    {
                        CollectPlayerAdditiveMultiplyAura(
                            action, context, playerAdditiveMultiplyGroups);
                    }
                    else
                    {
                        ApplyPlayerAttributeAura(action, context);
                    }
                    applied++;
                    break;
                case "TAuraActionCardAddTagsList":
                    ApplyTagAura(action, context);
                    applied++;
                    break;
                case "TAuraActionCardAddTagsBySource":
                    ApplyTagAuraBySource(action, context);
                    applied++;
                    break;
            }
        }
        ApplyAdditiveMultiplyGroups(
            cardAdditiveMultiplyGroups, playerAdditiveMultiplyGroups);
        foreach ((CombatCardState card, (int oldPercent, int oldFlat)) in
            cooldownReductions)
        {
            card.AdjustCooldownForReductionTransition(
                oldPercent,
                oldFlat,
                card.Attributes.GetValueOrDefault("PercentCooldownReduction"),
                card.Attributes.GetValueOrDefault("FlatCooldownReduction"));
        }
        RefreshDerivedPlayerState();
        return applied;
    }

    private void UnbakeInitialAuras()
    {
        List<(CombatCardState Card, MaterializedEffectDefinition Effect)> auras =
            ActiveAuras().ToList();
        var cardAdditiveMultiplyGroups =
            new Dictionary<(CombatCardState Target, string Attribute), double>();
        var playerAdditiveMultiplyGroups =
            new Dictionary<(CombatantState Target, string Attribute), double>();
        foreach ((CombatCardState owner, MaterializedEffectDefinition effect) in auras)
        {
            JsonElement action = effect.Definition.GetObjectOrNull("Action")!.Value;
            if (action.GetStringOrNull("Operation") != "AdditiveMultiply")
            {
                continue;
            }
            var context = new CombatActionContext(_state, owner, _random);
            if (!RulePrerequisiteEvaluator.AreSatisfied(effect, context))
            {
                continue;
            }
            if (action.GetStringOrNull("$type") == "TAuraActionCardModifyAttribute")
            {
                CollectCardAdditiveMultiplyAura(
                    action, context, cardAdditiveMultiplyGroups);
            }
            else if (action.GetStringOrNull("$type") == "TAuraActionPlayerModifyAttribute")
            {
                CollectPlayerAdditiveMultiplyAura(
                    action, context, playerAdditiveMultiplyGroups);
            }
        }
        InvertAdditiveMultiplyGroups(
            cardAdditiveMultiplyGroups, playerAdditiveMultiplyGroups);
        foreach ((CombatCardState owner, MaterializedEffectDefinition effect) in auras.AsEnumerable().Reverse())
        {
            JsonElement action = effect.Definition.GetObjectOrNull("Action")!.Value;
            if (action.GetStringOrNull("Operation") == "AdditiveMultiply")
            {
                continue;
            }
            var context = new CombatActionContext(_state, owner, _random);
            if (!RulePrerequisiteEvaluator.AreSatisfied(effect, context))
            {
                continue;
            }
            string attribute = action.GetStringOrNull("AttributeType") ?? string.Empty;
            string operation = action.GetStringOrNull("Operation") ?? "Add";
            double amount = EvaluateAuraOperationAmount(action, context, operation);
            if (action.GetStringOrNull("$type") == "TAuraActionCardModifyAttribute")
            {
                foreach (CombatCardState target in TargetResolver.ResolveCardTarget(
                    action.GetObjectOrNull("Target"), context, null))
                {
                    if (!target.AttributesArePrecomputed) continue;
                    int current = target.IntrinsicAttributes.GetValueOrDefault(attribute);
                    target.SetIntrinsicAttribute(attribute, InvertOperation(current, amount, operation));
                }
            }
            else if (action.GetStringOrNull("$type") == "TAuraActionPlayerModifyAttribute")
            {
                foreach (CombatantState target in TargetResolver.ResolvePlayers(
                    action.GetObjectOrNull("Target"), context))
                {
                    if (!AreAttributesPrecomputed(target)) continue;
                    int current = target.IntrinsicAttributes.GetValueOrDefault(attribute);
                    target.SetIntrinsicAttribute(attribute, InvertOperation(current, amount, operation));
                }
            }
        }
    }

    private static int InvertOperation(int current, double amount, string operation) => operation switch
    {
        "Add" => checked(current - checked((int)amount)),
        "Subtract" => checked(current + checked((int)amount)),
        "Multiply" => amount == 0 ? current : RoundAwayFromZero(current / amount),
        _ => current,
    };

    private static double EvaluateAuraOperationAmount(
        JsonElement action,
        CombatActionContext context,
        string operation) => operation == "Multiply"
            ? RuleValueEvaluator.Evaluate(action.GetObjectOrNull("Value"), context)
            : RuleValueEvaluator.EvaluateToInt(action.GetObjectOrNull("Value"), context);

    private static void CollectCardAdditiveMultiplyAura(
        JsonElement action,
        CombatActionContext context,
        Dictionary<(CombatCardState Target, string Attribute), double> groups)
    {
        double amount = RuleValueEvaluator.Evaluate(
            action.GetObjectOrNull("Value"), context);
        string attribute = action.GetStringOrNull("AttributeType") ?? string.Empty;
        foreach (CombatCardState target in TargetResolver.ResolveCardTarget(
            action.GetObjectOrNull("Target"), context, null))
        {
            var key = (target, attribute);
            groups[key] = groups.GetValueOrDefault(key) + amount;
        }
    }

    private static void CollectPlayerAdditiveMultiplyAura(
        JsonElement action,
        CombatActionContext context,
        Dictionary<(CombatantState Target, string Attribute), double> groups)
    {
        double amount = RuleValueEvaluator.Evaluate(
            action.GetObjectOrNull("Value"), context);
        string attribute = action.GetStringOrNull("AttributeType") ?? string.Empty;
        foreach (CombatantState target in TargetResolver.ResolvePlayers(
            action.GetObjectOrNull("Target"), context))
        {
            var key = (target, attribute);
            groups[key] = groups.GetValueOrDefault(key) + amount;
        }
    }

    private static void ApplyAdditiveMultiplyGroups(
        Dictionary<(CombatCardState Target, string Attribute), double> cardGroups,
        Dictionary<(CombatantState Target, string Attribute), double> playerGroups)
    {
        foreach (((CombatCardState target, string attribute), double amount) in cardGroups)
        {
            int before = target.Attributes.GetValueOrDefault(attribute);
            target.Attributes[attribute] = ScaleAdditiveMultiply(before, amount);
        }
        foreach (((CombatantState target, string attribute), double amount) in playerGroups)
        {
            int before = target.Attributes.GetValueOrDefault(attribute);
            target.Attributes[attribute] = ScaleAdditiveMultiply(before, amount);
        }
    }

    private void InvertAdditiveMultiplyGroups(
        Dictionary<(CombatCardState Target, string Attribute), double> cardGroups,
        Dictionary<(CombatantState Target, string Attribute), double> playerGroups)
    {
        foreach (((CombatCardState target, string attribute), double amount) in cardGroups)
        {
            if (!target.AttributesArePrecomputed) continue;
            int current = target.IntrinsicAttributes.GetValueOrDefault(attribute);
            target.SetIntrinsicAttribute(attribute, UnscaleAdditiveMultiply(current, amount));
        }
        foreach (((CombatantState target, string attribute), double amount) in playerGroups)
        {
            if (!AreAttributesPrecomputed(target)) continue;
            int current = target.IntrinsicAttributes.GetValueOrDefault(attribute);
            target.SetIntrinsicAttribute(attribute, UnscaleAdditiveMultiply(current, amount));
        }
    }

    private bool AreAttributesPrecomputed(CombatantState target) =>
        target.AttributesArePrecomputed ?? _state.CardAttributesArePrecomputed;

    private static int ScaleAdditiveMultiply(int value, double amount) =>
        RoundAwayFromZero(value * (1d + amount));

    private static int UnscaleAdditiveMultiply(int value, double amount)
    {
        double factor = 1d + amount;
        return Math.Abs(factor) < 1e-12
            ? value
            : RoundAwayFromZero(value / factor);
    }

    private static int RoundAwayFromZero(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private void RefreshDerivedPlayerState()
    {
        foreach (CombatantState combatant in _state.Combatants)
        {
            combatant.SetEffectiveMaxHealth(combatant.Attributes.GetValueOrDefault(
                "HealthMax", combatant.MaxHealth));
            combatant.DamageReductionPercent = combatant.Attributes
                .GetValueOrDefault("PercentDamageReduction");
            combatant.FlatDamageReduction = combatant.Attributes
                .GetValueOrDefault("FlatDamageReduction");
            combatant.Regen = combatant.Attributes.GetValueOrDefault(
                "HealthRegen", combatant.Attributes.GetValueOrDefault("Regen"));
        }
    }

    private void ApplyTimedModifiers()
    {
        foreach (TimedCardModifier modifier in _state.TimedCardModifiers)
        {
            int before = modifier.Target.Attributes.GetValueOrDefault(modifier.Attribute);
            modifier.Target.Attributes[modifier.Attribute] = ApplyOperation(
                before, modifier.Amount, modifier.Operation);
        }
        foreach (TimedPlayerModifier modifier in _state.TimedPlayerModifiers)
        {
            int before = modifier.Target.Attributes.GetValueOrDefault(modifier.Attribute);
            modifier.Target.Attributes[modifier.Attribute] = ApplyOperation(
                before, modifier.Amount, modifier.Operation);
        }
    }

    private void ResetEffectiveState()
    {
        foreach (CombatantState combatant in _state.Combatants)
        {
            combatant.Attributes.Clear();
            foreach ((string attribute, int value) in combatant.IntrinsicAttributes)
            {
                combatant.Attributes[attribute] = value;
            }
            foreach (CombatCardState card in combatant.Cards)
            {
                card.Attributes.Clear();
                foreach ((string attribute, int value) in card.IntrinsicAttributes)
                {
                    card.Attributes[attribute] = value;
                }
                card.Tags.Clear();
                card.Tags.UnionWith(card.IntrinsicTags);
                card.HiddenTags.Clear();
                card.HiddenTags.UnionWith(card.IntrinsicHiddenTags);
            }
        }
    }

    private IEnumerable<(CombatCardState Card, MaterializedEffectDefinition Effect)> ActiveAuras() =>
        _state.Combatants
            .SelectMany(combatant => combatant.Cards)
            .Where(card => !card.IsDisabled && !card.IsDestroyed)
            .SelectMany(card => card.Definition.Effects
                .Where(effect => effect.Kind == "Aura" &&
                    CombatEffectActivation.IsActive(effect, card))
                .Select(effect => (Card: card, Effect: effect)))
            // Resolve base card-derived values first (for example Pawn Shop
            // SellPrice), then max-health auras, then card values that read the
            // effective max health. This removes board-order dependence.
            .OrderBy(value => AuraDependencyPhase(value.Effect))
            .ThenBy(value => _state.Combatants.IndexOf(value.Card.Owner))
            .ThenBy(value => value.Card.BoardPosition)
            .ThenBy(value => value.Card.InstanceId, StringComparer.Ordinal)
            .ThenBy(value => value.Effect.Id, StringComparer.Ordinal);

    private static int AuraDependencyPhase(MaterializedEffectDefinition effect)
    {
        JsonElement? action = effect.Definition.GetObjectOrNull("Action");
        if (action is null) return 0;
        if (action.Value.GetStringOrNull("$type") == "TAuraActionPlayerModifyAttribute" &&
            action.Value.GetStringOrNull("AttributeType") == "HealthMax")
        {
            return 1;
        }
        return ContainsPlayerHealthMaxReference(action.Value) ? 2 : 0;
    }

    private static bool ContainsPlayerHealthMaxReference(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.GetStringOrNull("$type") is string type &&
                type.StartsWith("TReferenceValuePlayerAttribute", StringComparison.Ordinal) &&
                value.GetStringOrNull("AttributeType") == "HealthMax")
            {
                return true;
            }
            return value.EnumerateObject().Any(property =>
                ContainsPlayerHealthMaxReference(property.Value));
        }
        return value.ValueKind == JsonValueKind.Array &&
            value.EnumerateArray().Any(ContainsPlayerHealthMaxReference);
    }

    private static void ApplyCardAttributeAura(JsonElement action, CombatActionContext context)
    {
        string attribute = action.GetStringOrNull("AttributeType") ?? string.Empty;
        string operation = action.GetStringOrNull("Operation") ?? "Add";
        double amount = EvaluateAuraOperationAmount(action, context, operation);
        foreach (CombatCardState target in TargetResolver.ResolveCardTarget(
            action.GetObjectOrNull("Target"), context, null))
        {
            int before = target.Attributes.GetValueOrDefault(attribute);
            target.Attributes[attribute] = ApplyOperation(before, amount, operation);
        }
    }

    private static void ApplyPlayerAttributeAura(JsonElement action, CombatActionContext context)
    {
        string attribute = action.GetStringOrNull("AttributeType") ?? string.Empty;
        string operation = action.GetStringOrNull("Operation") ?? "Add";
        double amount = EvaluateAuraOperationAmount(action, context, operation);
        foreach (CombatantState target in TargetResolver.ResolvePlayers(
            action.GetObjectOrNull("Target"), context))
        {
            int before = target.Attributes.GetValueOrDefault(attribute);
            target.Attributes[attribute] = ApplyOperation(before, amount, operation);
        }
    }

    private static void ApplyTagAura(JsonElement action, CombatActionContext context)
    {
        string[] tags = action.GetArrayOrNull("Tags") is JsonElement values
            ? values.EnumerateArray().Select(value => value.GetString())
                .Where(value => value is not null).Cast<string>().ToArray()
            : [];
        foreach (CombatCardState target in TargetResolver.ResolveCardTarget(
            action.GetObjectOrNull("Target"), context, null))
        {
            target.Tags.UnionWith(tags);
        }
    }

    private static void ApplyTagAuraBySource(JsonElement action, CombatActionContext context)
    {
        string[] tags = TargetResolver.ResolveCardTarget(
                action.GetObjectOrNull("Source"), context, null)
            .SelectMany(card => card.Tags)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (CombatCardState target in TargetResolver.ResolveCardTarget(
            action.GetObjectOrNull("Target"), context, null))
        {
            target.Tags.UnionWith(tags);
        }
    }

    private static int ApplyOperation(int before, double amount, string operation) => operation switch
    {
        "Add" => checked(before + checked((int)amount)),
        "Subtract" => checked(before - checked((int)amount)),
        "Multiply" => RoundAwayFromZero(before * amount),
        _ => before,
    };
}
