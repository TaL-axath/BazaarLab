using System.Text.Json;

namespace BazaarLab.Combat;

public sealed record CombatActionContext(
    CombatState State,
    CombatCardState SourceCard,
    XorShiftCombatRandom Random,
    CombatCardState? TriggerSource = null,
    CombatCardState? TriggerTarget = null,
    bool IsCritical = false,
    int? AttributeDelta = null,
    Action<string, CombatCardState>? CardActionApplied = null);

public sealed record ActionExecutionResult(bool Supported, string ActionType, int TargetCount);

public static class RulePrerequisiteEvaluator
{
    public static bool AreSatisfied(
        MaterializedEffectDefinition effect,
        CombatActionContext context)
    {
        JsonElement? prerequisites = effect.Definition.GetArrayOrNull("Prerequisites");
        return prerequisites is null || prerequisites.Value.EnumerateArray()
            .All(value => IsSatisfied(value, context));
    }

    internal static bool IsSatisfied(JsonElement prerequisite, CombatActionContext context)
    {
        string type = prerequisite.GetStringOrNull("$type") ?? string.Empty;
        if (type == "TPrerequisiteCardCount")
        {
            int count = TargetResolver.ResolveCardTarget(
                prerequisite.GetObjectOrNull("Subject"), context, null).Count;
            return Compare(count, prerequisite.GetPropertyOrNull("Amount")?.GetInt32() ?? 0,
                prerequisite.GetStringOrNull("Comparison"));
        }
        if (type == "TPrerequisitePlayer")
        {
            return TargetResolver.ResolvePlayers(
                prerequisite.GetObjectOrNull("Subject"), context).Count > 0;
        }
        if (type == "TPrerequisiteCardAttributeComparator")
        {
            JsonElement? other = prerequisite.GetObjectOrNull("SubjectOther");
            string? otherAttribute = prerequisite.GetStringOrNull("AttributeOther");
            if (other is null && otherAttribute is null)
            {
                return prerequisite.GetStringOrNull("Comparison") is
                    "Equal" or "GreaterThanOrEqual" or "LessThanOrEqual";
            }
            List<CombatCardState> left = TargetResolver.ResolveCardTarget(
                prerequisite.GetObjectOrNull("Subject"), context, null);
            List<CombatCardState> right = other is null
                ? left
                : TargetResolver.ResolveCardTarget(other, context, null);
            string attribute = prerequisite.GetStringOrNull("Attribute") ?? string.Empty;
            string rightName = otherAttribute ?? attribute;
            return left.All(a => right.All(b => Compare(
                a.Attributes.GetValueOrDefault(attribute),
                b.Attributes.GetValueOrDefault(rightName),
                prerequisite.GetStringOrNull("Comparison"))));
        }
        return type != "TPrerequisiteRun";
    }

    internal static bool Compare(int left, int right, string? comparison) => comparison switch
    {
        "Equal" => left == right,
        "NotEqual" => left != right,
        "GreaterThan" => left > right,
        "LessThan" => left < right,
        "GreaterThanOrEqual" => left >= right,
        "LessThanOrEqual" => left <= right,
        _ => false,
    };
}

public static class CombatActionDispatcher
{
    private static readonly Dictionary<string, string> DefaultAttributeByAction = new(StringComparer.Ordinal)
    {
        ["TActionPlayerDamage"] = "DamageAmount",
        ["TActionPlayerHeal"] = "HealAmount",
        ["TActionPlayerReviveHeal"] = "HealAmount",
        ["TActionPlayerShieldApply"] = "ShieldApplyAmount",
        ["TActionPlayerBurnApply"] = "BurnApplyAmount",
        ["TActionPlayerBurnRemove"] = "BurnRemoveAmount",
        ["TActionPlayerPoisonApply"] = "PoisonApplyAmount",
        ["TActionPlayerPoisonRemove"] = "PoisonRemoveAmount",
        ["TActionPlayerRegenApply"] = "RegenApplyAmount",
        ["TActionPlayerRegenRemove"] = "RegenRemoveAmount",
        ["TActionPlayerRageApply"] = "RageApplyAmount",
        ["TActionPlayerTempoApply"] = "TempoApplyAmount",
        ["TActionCardHaste"] = "HasteAmount",
        ["TActionCardSlow"] = "SlowAmount",
        ["TActionCardFreeze"] = "FreezeAmount",
        ["TActionCardCharge"] = "ChargeAmount",
        ["TActionCardReload"] = "ReloadAmount",
    };

    public static ActionExecutionResult Execute(
        MaterializedEffectDefinition effect,
        CombatActionContext context)
    {
        JsonElement action = effect.Definition.GetObjectOrNull("Action")
            ?? throw new InvalidDataException($"Effect {effect.Id} has no Action.");
        string actionType = action.GetStringOrNull("$type") ?? string.Empty;

        if (!TryPayCost(action.GetObjectOrNull("Cost"), context))
        {
            return new ActionExecutionResult(true, actionType, 0);
        }

        if (actionType == "TActionCardBeginSandstorm")
        {
            CombatEngine.StartSandstorm(context.State, forced: true);
            return new ActionExecutionResult(true, actionType, 0);
        }

        if (actionType == "TActionAnd")
        {
            if (action.GetArrayOrNull("Actions") is not JsonElement actions)
            {
                return new ActionExecutionResult(true, actionType, 0);
            }
            bool supported = true;
            int targetCount = 0;
            foreach (JsonElement childAction in actions.EnumerateArray())
            {
                int firstChildEvent = context.State.Events.Count;
                using JsonDocument childDocument = JsonDocument.Parse(
                    "{\"Action\":" + childAction.GetRawText() + "}");
                var childEffect = new MaterializedEffectDefinition(
                    effect.Id, effect.Kind, effect.Source, childDocument.RootElement);
                ActionExecutionResult childResult = Execute(childEffect, context);
                for (int eventIndex = firstChildEvent;
                    eventIndex < context.State.Events.Count; eventIndex++)
                {
                    CombatEvent childEvent = context.State.Events[eventIndex];
                    if (childEvent.ActionType is null)
                    {
                        context.State.Events[eventIndex] = childEvent with
                        {
                            ActionType = childResult.ActionType,
                        };
                    }
                }
                supported &= childResult.Supported;
                targetCount = checked(targetCount + childResult.TargetCount);
            }
            return new ActionExecutionResult(supported, actionType, targetCount);
        }

        if (actionType == "TActionPlayerModifyAttribute")
        {
            List<CombatantState> targets = TargetResolver.ResolvePlayers(
                action.GetObjectOrNull("Target"), context);
            string attribute = action.GetStringOrNull("AttributeType") ?? string.Empty;
            string operation = action.GetStringOrNull("Operation") ?? "Add";
            double amount = EvaluateAttributeOperationAmount(action, operation, context);
            foreach (CombatantState target in targets)
            {
                ApplyPlayerAttribute(target, attribute, operation, amount, action, context.State,
                    context.SourceCard.InstanceId);
            }
            return new ActionExecutionResult(true, actionType, targets.Count);
        }

        if (actionType.StartsWith("TActionPlayer", StringComparison.Ordinal))
        {
            List<CombatantState> targets = TargetResolver.ResolvePlayers(
                action.GetObjectOrNull("Target"), context);
            int amount = ResolveActionAmount(action, actionType, context);
            if (context.IsCritical && actionType is
                "TActionPlayerDamage" or "TActionPlayerHeal" or
                "TActionPlayerShieldApply" or "TActionPlayerBurnApply" or
                "TActionPlayerPoisonApply" or "TActionPlayerRegenApply")
            {
                amount = checked(amount * 2);
            }
            foreach (CombatantState target in targets)
            {
                ExecutePlayerAction(actionType, amount, target, context);
            }
            return new ActionExecutionResult(
                IsSupportedPlayerAction(actionType), actionType, targets.Count);
        }

        if (actionType == "TActionCardModifyAttribute")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            string attribute = action.GetStringOrNull("AttributeType") ?? string.Empty;
            string operation = action.GetStringOrNull("Operation") ?? "Add";
            double amount = EvaluateAttributeOperationAmount(action, operation, context);
            foreach (CombatCardState target in targets)
            {
                ApplyCardAttribute(
                    target, attribute, operation, amount, action, context.State,
                    context.SourceCard.InstanceId);
                context.CardActionApplied?.Invoke(actionType, target);
            }
            return new ActionExecutionResult(true, actionType, targets.Count);
        }

        if (actionType == "TActionCardAddTagsRandom")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            List<string> available = action.GetArrayOrNull("Tags") is JsonElement tagValues
                ? tagValues.EnumerateArray().Select(value => value.GetString())
                    .Where(value => value is not null).Cast<string>().Distinct().ToList()
                : [];
            int count = Math.Clamp(
                RuleValueEvaluator.EvaluateToInt(action.GetObjectOrNull("Value"), context),
                0, available.Count);
            var selected = new List<string>();
            while (selected.Count < count && available.Count > 0)
            {
                int index = context.Random.Next(available.Count);
                selected.Add(available[index]);
                available.RemoveAt(index);
            }
            foreach (CombatCardState target in targets)
            {
                target.IntrinsicTags.UnionWith(selected);
                target.Tags.UnionWith(selected);
            }
            return new ActionExecutionResult(true, actionType, targets.Count);
        }

        if (actionType == "TActionCardAddTagsList")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            string[] tags = action.GetArrayOrNull("Tags") is JsonElement tagValues
                ? tagValues.EnumerateArray().Select(value => value.GetString())
                    .Where(value => value is not null).Cast<string>().Distinct().ToArray()
                : [];
            foreach (CombatCardState target in targets)
            {
                target.IntrinsicTags.UnionWith(tags);
                target.Tags.UnionWith(tags);
            }
            return new ActionExecutionResult(true, actionType, targets.Count);
        }

        if (actionType == "TActionCardAddTagsBySource")
        {
            List<CombatCardState> sources = TargetResolver.ResolveCardTarget(
                action.GetObjectOrNull("Source"), context, null);
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            string[] tags = sources.SelectMany(source => source.Tags)
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (CombatCardState target in targets)
            {
                target.IntrinsicTags.UnionWith(tags);
                target.Tags.UnionWith(tags);
            }
            return new ActionExecutionResult(true, actionType, targets.Count);
        }

        if (actionType == "TActionCardReload")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            int amount = ResolveActionAmount(action, actionType, context);
            int changed = 0;
            CombatCardState? firstChanged = null;
            foreach (CombatCardState target in targets)
            {
                int maximum = Math.Max(0, target.Attributes.GetValueOrDefault("AmmoMax"));
                int before = target.IntrinsicAttributes.GetValueOrDefault("Ammo");
                int after = Math.Min(maximum, checked(before + Math.Max(0, amount)));
                if (after == before)
                {
                    continue;
                }
                target.SetIntrinsicAttribute("Ammo", after);
                context.State.Events.Add(new CombatEvent(
                    context.State.Tick, "CardAttribute:Ammo", target.InstanceId, after, before,
                    context.SourceCard.InstanceId));
                context.CardActionApplied?.Invoke(actionType, target);
                firstChanged ??= target;
                changed++;
            }
            if (firstChanged is not null)
            {
                // The official effect trace records one Reload action per
                // effect execution, while performed-reload and Ammo attribute
                // listeners are dispatched once for each card that changed.
                context.State.Events.Add(new CombatEvent(
                    context.State.Tick, "CardReload", firstChanged.InstanceId,
                    amount, SourceId: context.SourceCard.InstanceId));
            }
            return new ActionExecutionResult(true, actionType, changed);
        }

        if (actionType is "TActionCardDisable" or "TActionCardRepair")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            int changed = 0;
            foreach (CombatCardState target in targets)
            {
                bool disable = actionType == "TActionCardDisable";
                if (target.IsDisabled == disable || target.IsDestroyed)
                {
                    continue;
                }
                if (disable)
                {
                    // The live combat protocol represents "destroy for the fight" as
                    // CardDisable, but still runs TTriggerOnBeforeCardDestroyed before
                    // the card becomes inactive.  Defer the state mutation to the rule
                    // runtime so replacement transforms and destroy immunity can run in
                    // the same lifecycle as the official client.
                    context.State.Events.Add(new CombatEvent(
                        context.State.Tick, "CardDisableRequested", target.InstanceId,
                        SourceId: context.SourceCard.InstanceId));
                    changed++;
                    continue;
                }
                target.IsDisabled = disable;
                changed++;
                context.State.Events.Add(new CombatEvent(
                    context.State.Tick, disable ? "CardDisabled" : "CardRepaired",
                    target.InstanceId, SourceId: context.SourceCard.InstanceId));
            }
            return new ActionExecutionResult(true, actionType, changed);
        }

        if (actionType == "TActionCardDestroy")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            foreach (CombatCardState target in targets)
            {
                context.State.Events.Add(new CombatEvent(
                    context.State.Tick, "CardDestroyRequested", target.InstanceId,
                    SourceId: context.SourceCard.InstanceId));
            }
            return new ActionExecutionResult(true, actionType, targets.Count);
        }

        if (actionType is "TActionCardTransform" or "TActionCardTransformDestroyed")
        {
            List<CombatCardState> targets = ResolveTransformationTargets(
                action, actionType, context);
            if (targets.Count == 0)
            {
                // A transform with no legal target is a normal no-op (Virus is the
                // common combat example), not an unsupported action.
                return new ActionExecutionResult(true, actionType, 0);
            }
            bool supported = true;
            int changed = 0;
            foreach (CombatCardState target in targets)
            {
                int originalPosition = target.BoardPosition;
                int availableSpan = Math.Max(1, target.Span);
                int replacementLimit = Math.Max(1, RuleValueEvaluator.EvaluateToInt(
                    action.GetObjectOrNull("SpawnContext")?.GetObjectOrNull("Limit"), context));
                int occupiedSpan = 0;
                for (int index = 0; index < replacementLimit; index++)
                {
                    TransformationChoiceStatus status = TryChooseTransformation(
                        action, target, context, out MaterializedCardDefinition? replacement);
                    if (status == TransformationChoiceStatus.Unsupported)
                    {
                        supported = false;
                        break;
                    }
                    if (status == TransformationChoiceStatus.NoCandidate || replacement is null)
                    {
                        break;
                    }
                    int replacementSpan = SizeToSpan(replacement.Size);
                    if (occupiedSpan + replacementSpan > availableSpan)
                    {
                        break;
                    }
                    if (index == 0)
                    {
                        ApplyTransformation(target, replacement, replacementSpan);
                        target.BoardPosition = originalPosition;
                        target.IsDestroyed = false;
                        target.IsDisabled = false;
                        context.State.Events.Add(new CombatEvent(
                            context.State.Tick, "CardTransformed", target.InstanceId,
                            SourceId: context.SourceCard.InstanceId));
                    }
                    else
                    {
                        CombatCardState clone = CombatCardState.Create(
                            $"{target.InstanceId}:transform:{context.State.Tick}:{index}",
                            replacement, target.Owner, originalPosition + occupiedSpan,
                            target.Section, replacementSpan);
                        clone.CooldownRemainingMilliseconds = clone.GetEffectiveCooldownMilliseconds();
                        context.State.Events.Add(new CombatEvent(
                            context.State.Tick, "CardTransformedSpawn", clone.InstanceId,
                            SourceId: context.SourceCard.InstanceId));
                    }
                    occupiedSpan += replacementSpan;
                    changed++;
                }
            }
            return new ActionExecutionResult(supported, actionType, changed);
        }

        if (actionType == "TActionCardUpgrade")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            int changed = 0;
            foreach (CombatCardState target in targets)
            {
                if (ApplyUpgrade(target, action.GetStringOrNull("UpgradeToTier"), context.State))
                {
                    changed++;
                    context.State.Events.Add(new CombatEvent(
                        context.State.Tick, "CardUpgraded", target.InstanceId,
                        SourceId: context.SourceCard.InstanceId));
                }
            }
            return new ActionExecutionResult(true, actionType, changed);
        }

        if (actionType == "TActionCardForceUse")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            foreach (CombatCardState target in targets)
            {
                context.State.Events.Add(new CombatEvent(
                    context.State.Tick, "ForceUse", target.InstanceId,
                    SourceId: context.SourceCard.InstanceId));
            }
            return new ActionExecutionResult(true, actionType, targets.Count);
        }

        if (actionType is "TActionCardEnchant" or "TActionCardEnchantRandom")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            int changed = 0;
            foreach (CombatCardState target in targets)
            {
                string? enchantment = actionType == "TActionCardEnchant"
                    ? action.GetStringOrNull("Enchantment")
                    : ChooseRandomEnchantment(action, context.Random);
                if (enchantment is not null && ApplyEnchantment(target, enchantment, context.State))
                {
                    changed++;
                    context.State.Events.Add(new CombatEvent(
                        context.State.Tick, "CardEnchanted:" + enchantment, target.InstanceId,
                        SourceId: context.SourceCard.InstanceId));
                }
            }
            return new ActionExecutionResult(true, actionType, changed);
        }

        if (actionType == "TActionCardEnchantRemove")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            int changed = 0;
            foreach (CombatCardState target in targets)
            {
                if (RemoveEnchantment(target, context.State))
                {
                    changed++;
                    context.State.Events.Add(new CombatEvent(
                        context.State.Tick, "CardEnchantmentRemoved", target.InstanceId));
                }
            }
            return new ActionExecutionResult(true, actionType, changed);
        }

        if (actionType is "TActionCardHaste" or "TActionCardSlow" or
            "TActionCardFreeze" or "TActionCardCharge" or
            "TActionCardFlyingStart" or "TActionCardFlyingStop" or
            "TActionCardFlyingToggle")
        {
            List<CombatCardState> targets = TargetResolver.ResolveCards(action, context);
            string attribute = actionType.StartsWith("TActionCardFlying", StringComparison.Ordinal)
                ? "Flying"
                : actionType["TActionCard".Length..];
            int amount = actionType.StartsWith("TActionCardFlying", StringComparison.Ordinal)
                ? 1
                : ResolveActionAmount(action, actionType, context);
            int affected = 0;
            foreach (CombatCardState target in targets)
            {
                int targetAmount = amount;
                if (actionType is "TActionCardHaste" or "TActionCardSlow" or
                    "TActionCardFreeze" or "TActionCardCharge")
                {
                    string reductionAttribute = "Percent" + attribute + "Reduction";
                    int reduction = Math.Clamp(
                        target.Attributes.GetValueOrDefault(reductionAttribute), 0, 100);
                    targetAmount = checked((int)((long)targetAmount * (100 - reduction) / 100));
                }
                int current = target.IntrinsicAttributes.GetValueOrDefault(attribute);
                if ((actionType == "TActionCardFlyingStart" && current != 0) ||
                    (actionType == "TActionCardFlyingStop" && current == 0))
                {
                    // Flying start/stop are lifecycle transitions, not writes.
                    // Repeating either operation in its existing state emits no
                    // action/performed signal in the worker.
                    continue;
                }
                int next = actionType switch
                {
                    "TActionCardFlyingStop" => 0,
                    "TActionCardFlyingToggle" => current == 0 ? 1 : 0,
                    _ => actionType == "TActionCardFlyingStart" ? 1 : checked(current + targetAmount),
                };
                target.SetIntrinsicAttribute(attribute, next);
                context.State.Events.Add(new CombatEvent(
                    context.State.Tick, "Card" + attribute, target.InstanceId, next, current,
                    context.SourceCard.InstanceId));
                context.CardActionApplied?.Invoke(actionType, target);
                affected++;
            }
            return new ActionExecutionResult(true, actionType, affected);
        }

        return new ActionExecutionResult(false, actionType, 0);
    }

    private static string? ChooseRandomEnchantment(JsonElement action, XorShiftCombatRandom random)
    {
        if (action.GetArrayOrNull("Enchantments") is not JsonElement choices)
        {
            return null;
        }
        var weighted = choices.EnumerateArray()
            .Select(value => (Name: value.GetStringOrNull("Enchantment"),
                Weight: Math.Max(0, (int)Math.Round(
                    value.GetPropertyOrNull("Weight")?.GetDouble() ?? 0))))
            .Where(value => value.Name is not null && value.Weight > 0)
            .ToList();
        int total = weighted.Sum(value => value.Weight);
        if (total <= 0)
        {
            return null;
        }
        int roll = random.Next(total);
        foreach (var choice in weighted)
        {
            if (roll < choice.Weight)
            {
                return choice.Name;
            }
            roll -= choice.Weight;
        }
        return weighted[^1].Name;
    }

    private static bool ApplyEnchantment(
        CombatCardState target, string enchantment, CombatState state)
    {
        if (state.CardCatalog is null ||
            !state.CardCatalog.TryGet(target.Definition.TemplateId, out OfficialCardDefinition? official) ||
            official is null || !official.HasEnchantment(enchantment))
        {
            return false;
        }
        MaterializedCardDefinition before = official.Materialize(
            target.Definition.Tier, target.Definition.Enchantment,
            target.IntrinsicAttributes);
        MaterializedCardDefinition after = official.Materialize(
            target.Definition.Tier, enchantment, target.IntrinsicAttributes);
        foreach (string attribute in before.Attributes.Keys.Union(after.Attributes.Keys))
        {
            int delta = after.Attributes.GetValueOrDefault(attribute) -
                before.Attributes.GetValueOrDefault(attribute);
            if (delta != 0)
            {
                target.SetIntrinsicAttribute(attribute,
                    checked(target.IntrinsicAttributes.GetValueOrDefault(attribute) + delta));
            }
        }
        target.IntrinsicTags.ExceptWith(before.Tags.Except(
            official.Materialize(target.Definition.Tier, null, target.IntrinsicAttributes).Tags));
        target.IntrinsicHiddenTags.ExceptWith(before.HiddenTags.Except(
            official.Materialize(target.Definition.Tier, null,
                target.IntrinsicAttributes).HiddenTags));
        target.IntrinsicTags.UnionWith(after.Tags);
        target.IntrinsicHiddenTags.UnionWith(after.HiddenTags);
        target.Definition = after;
        return true;
    }

    private static bool RemoveEnchantment(CombatCardState target, CombatState state)
    {
        if (target.Definition.Enchantment is null || state.CardCatalog is null ||
            !state.CardCatalog.TryGet(target.Definition.TemplateId, out OfficialCardDefinition? official) ||
            official is null)
        {
            return false;
        }
        MaterializedCardDefinition before = official.Materialize(
            target.Definition.Tier, target.Definition.Enchantment,
            target.IntrinsicAttributes);
        MaterializedCardDefinition after = official.Materialize(
            target.Definition.Tier, null, target.IntrinsicAttributes);
        foreach (string attribute in before.Attributes.Keys.Union(after.Attributes.Keys))
        {
            int delta = after.Attributes.GetValueOrDefault(attribute) -
                before.Attributes.GetValueOrDefault(attribute);
            if (delta != 0)
            {
                target.SetIntrinsicAttribute(attribute,
                    checked(target.IntrinsicAttributes.GetValueOrDefault(attribute) + delta));
            }
        }
        target.IntrinsicTags.ExceptWith(before.Tags.Except(after.Tags));
        target.IntrinsicHiddenTags.ExceptWith(before.HiddenTags.Except(after.HiddenTags));
        target.Definition = after;
        return true;
    }

    private enum TransformationChoiceStatus
    {
        Selected,
        NoCandidate,
        Unsupported,
    }

    private sealed record TransformationCandidate(
        OfficialCardDefinition Definition,
        CombatCardState? SourceCard);

    private static List<CombatCardState> ResolveTransformationTargets(
        JsonElement action,
        string actionType,
        CombatActionContext context)
    {
        if (actionType != "TActionCardTransformDestroyed")
        {
            return TargetResolver.ResolveCards(action, context);
        }
        string targetType = action.GetObjectOrNull("Target")?.GetStringOrNull("$type")
            ?? string.Empty;
        return targetType switch
        {
            "TTargetCardSelf" => [context.SourceCard],
            "TTargetCardTriggerSource" when context.TriggerSource is not null =>
                [context.TriggerSource],
            "TTargetCardTriggerTarget" when context.TriggerTarget is not null =>
                [context.TriggerTarget],
            _ => TargetResolver.ResolveCards(action, context),
        };
    }

    private static TransformationChoiceStatus TryChooseTransformation(
        JsonElement action,
        CombatCardState target,
        CombatActionContext context,
        out MaterializedCardDefinition? replacement)
    {
        replacement = null;
        OfficialCardCatalog? catalog = context.State.CardCatalog;
        JsonElement? spawnContext = action.GetObjectOrNull("SpawnContext");
        if (catalog is null || spawnContext is null ||
            spawnContext.Value.GetArrayOrNull("Groups") is not JsonElement groupsElement)
        {
            return TransformationChoiceStatus.Unsupported;
        }
        var groups = new List<(JsonElement Definition, List<TransformationCandidate> Cards)>();
        foreach (JsonElement group in groupsElement.EnumerateArray())
        {
            if (group.GetArrayOrNull("Prerequisites") is JsonElement prerequisites &&
                !prerequisites.EnumerateArray().All(value =>
                    RulePrerequisiteEvaluator.IsSatisfied(value, context)))
            {
                continue;
            }
            List<TransformationCandidate> candidates = catalog.Cards
                .Select(card => new TransformationCandidate(card, null)).ToList();
            if (group.GetArrayOrNull("Filters") is JsonElement filters)
            {
                foreach (JsonElement filter in filters.EnumerateArray())
                {
                    string filterType = filter.GetStringOrNull("$type") ?? string.Empty;
                    if (filterType == "TSpawnFilterIdList")
                    {
                        HashSet<string> ids = filter.GetArrayOrNull("Ids") is JsonElement values
                            ? values.EnumerateArray().Select(value => value.GetString())
                                .Where(value => value is not null).Cast<string>()
                                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                            : [];
                        candidates = candidates.Where(card => ids.Contains(card.Definition.Id)).ToList();
                    }
                    else if (filterType == "TSpawnFilterTarget")
                    {
                        List<CombatCardState> sourceCards = TargetResolver.ResolveCardTarget(
                            filter.GetObjectOrNull("Target"), context, null);
                        var sourceCandidates = new List<TransformationCandidate>();
                        foreach (CombatCardState sourceCard in sourceCards)
                        {
                            if (catalog.TryGet(sourceCard.Definition.TemplateId,
                                out OfficialCardDefinition? definition) && definition is not null &&
                                candidates.Any(value => string.Equals(value.Definition.Id,
                                    definition.Id, StringComparison.OrdinalIgnoreCase)))
                            {
                                sourceCandidates.Add(new TransformationCandidate(definition, sourceCard));
                            }
                        }
                        candidates = sourceCandidates;
                    }
                    else if (filterType == "TSpawnFilterQuery" &&
                        filter.GetObjectOrNull("Constraints") is JsonElement constraints)
                    {
                        if (!IsSupportedSpawnConstraint(constraints))
                        {
                            return TransformationChoiceStatus.Unsupported;
                        }
                        candidates = candidates.Where(card =>
                            string.Equals(card.Definition.SpawningEligibility, "Always",
                                StringComparison.OrdinalIgnoreCase) &&
                            MatchesSpawnConstraint(
                                card.Definition, constraints, target, context)).ToList();
                    }
                    else
                    {
                        return TransformationChoiceStatus.Unsupported;
                    }
                }
            }
            JsonElement[] groupBehaviors = (spawnContext.Value.GetArrayOrNull("Behaviors") is JsonElement outerBehaviors
                    ? outerBehaviors.EnumerateArray() : [])
                .Concat(group.GetArrayOrNull("Behaviors") is JsonElement innerBehaviors
                    ? innerBehaviors.EnumerateArray() : [])
                .ToArray();
            bool excludesPlayerHero = groupBehaviors.Any(value =>
                value.GetStringOrNull("$type") == "TSpawnBehaviorExcludePlayerHero");
            bool ignoresHero = groupBehaviors.Any(value =>
                value.GetStringOrNull("$type") == "TSpawnBehaviorIgnoreHero" &&
                value.GetPropertyOrNull("IgnoreHero")?.GetBoolean() == true);
            if (!ignoresHero && !excludesPlayerHero &&
                context.SourceCard.Owner.Hero is string restrictedHero)
            {
                candidates = candidates.Where(value =>
                    value.SourceCard is not null ||
                    value.Definition.Heroes.Contains("Common") ||
                    value.Definition.Heroes.Contains(restrictedHero)).ToList();
            }
            if (groupBehaviors.Any(value =>
                value.GetStringOrNull("$type") == "TSpawnBehaviorInheritSize" &&
                value.GetPropertyOrNull("Inherits")?.GetBoolean() == true))
            {
                candidates = candidates.Where(value => string.Equals(
                    value.Definition.Size, target.Definition.Size,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (excludesPlayerHero &&
                context.SourceCard.Owner.Hero is string playerHero)
            {
                candidates = candidates.Where(value =>
                    !value.Definition.Heroes.Contains(playerHero)).ToList();
            }
            JsonElement? fixedTierBehavior = groupBehaviors.FirstOrDefault(value =>
                value.GetStringOrNull("$type") == "TSpawnBehaviorTier");
            string? fixedTier = fixedTierBehavior is JsonElement fixedTierElement &&
                fixedTierElement.GetArrayOrNull("Tiers") is JsonElement fixedTiers
                ? fixedTiers.EnumerateArray().FirstOrDefault().GetString()
                : null;
            bool doesNotInheritTier = groupBehaviors.Any(value =>
                value.GetStringOrNull("$type") == "TSpawnBehaviorInheritTier" &&
                value.GetPropertyOrNull("Inherits")?.GetBoolean() == false);
            if (fixedTier is not null)
            {
                candidates = candidates.Where(value => value.Definition.HasTier(fixedTier)).ToList();
            }
            else if (!doesNotInheritTier)
            {
                candidates = candidates.Where(value => value.SourceCard is not null ||
                    value.Definition.HasTier(target.Definition.Tier)).ToList();
            }
            if (candidates.Count > 0)
            {
                groups.Add((group, candidates));
            }
        }
        if (groups.Count == 0)
        {
            return TransformationChoiceStatus.NoCandidate;
        }
        int groupIndex = 0;
        if (spawnContext.Value.GetStringOrNull("SelectionMethod") == "Random" && groups.Count > 1)
        {
            int totalWeight = groups.Sum(value => Math.Max(0,
                value.Definition.GetPropertyOrNull("RandomWeight")?.GetInt32() ?? 1));
            if (totalWeight > 0)
            {
                int roll = context.Random.Next(totalWeight);
                for (int index = 0; index < groups.Count; index++)
                {
                    roll -= Math.Max(0, groups[index].Definition
                        .GetPropertyOrNull("RandomWeight")?.GetInt32() ?? 1);
                    if (roll < 0)
                    {
                        groupIndex = index;
                        break;
                    }
                }
            }
        }
        (JsonElement selectedGroup, List<TransformationCandidate> selectedCards) = groups[groupIndex];
        TransformationCandidate candidate = selectedCards.Count == 1
            ? selectedCards[0]
            : selectedCards[context.Random.Next(selectedCards.Count)];
        OfficialCardDefinition selected = candidate.Definition;
        JsonElement[] behaviors = (spawnContext.Value.GetArrayOrNull("Behaviors") is JsonElement outer
                ? outer.EnumerateArray() : [])
            .Concat(selectedGroup.GetArrayOrNull("Behaviors") is JsonElement inner
                ? inner.EnumerateArray() : [])
            .ToArray();
        bool explicitlyDoesNotInheritTier = behaviors.Any(value =>
            value.GetStringOrNull("$type") == "TSpawnBehaviorInheritTier" &&
            value.GetPropertyOrNull("Inherits")?.GetBoolean() == false);
        string tier = candidate.SourceCard?.Definition.Tier ??
            (explicitlyDoesNotInheritTier ? selected.StartingTier : target.Definition.Tier);
        JsonElement? tierBehavior = behaviors.FirstOrDefault(value =>
            value.GetStringOrNull("$type") == "TSpawnBehaviorTier");
        if (tierBehavior is JsonElement explicitTier &&
            explicitTier.GetArrayOrNull("Tiers") is JsonElement tiers &&
            tiers.EnumerateArray().FirstOrDefault().GetString() is string selectedTier)
        {
            tier = selectedTier;
        }
        if (!selected.HasTier(tier))
        {
            tier = selected.StartingTier;
            if (!selected.HasTier(tier))
            {
                return TransformationChoiceStatus.NoCandidate;
            }
        }
        JsonElement? enchantmentBehavior = behaviors.FirstOrDefault(value =>
            value.GetStringOrNull("$type") == "TSpawnBehaviorInheritEnchantment");
        bool inheritEnchantment = enchantmentBehavior is JsonElement explicitEnchantment
            ? explicitEnchantment.GetPropertyOrNull("Inherits")?.GetBoolean() == true
            : candidate.SourceCard is not null;
        string? inheritedEnchantment = candidate.SourceCard?.Definition.Enchantment ??
            target.Definition.Enchantment;
        string? enchantment = inheritEnchantment && inheritedEnchantment is string current &&
            selected.HasEnchantment(current) ? current : null;
        replacement = selected.Materialize(tier, enchantment,
            candidate.SourceCard?.IntrinsicAttributes ?? target.IntrinsicAttributes);
        if (candidate.SourceCard is CombatCardState copiedCard)
        {
            replacement = replacement with
            {
                Attributes = new Dictionary<string, int>(copiedCard.IntrinsicAttributes,
                    StringComparer.Ordinal),
                Tags = new HashSet<string>(copiedCard.IntrinsicTags, StringComparer.Ordinal),
                HiddenTags = new HashSet<string>(copiedCard.IntrinsicHiddenTags,
                    StringComparer.Ordinal),
            };
        }
        return TransformationChoiceStatus.Selected;
    }

    private static bool IsSupportedSpawnConstraint(JsonElement constraint)
    {
        string type = constraint.GetStringOrNull("$type") ?? string.Empty;
        if (type is "ConstraintAnd" or "ConstraintOr")
        {
            return constraint.GetArrayOrNull("Constraints") is JsonElement children &&
                children.EnumerateArray().All(IsSupportedSpawnConstraint);
        }
        return type is "ConstraintCardType" or "ConstraintTag" or
            "ConstraintHiddenTag" or "ConstraintSize" or "ConstraintTier" or
            "ConstraintHero" or "ConstraintEnchantmentEligible";
    }

    private static bool MatchesSpawnConstraint(
        OfficialCardDefinition card,
        JsonElement constraint,
        CombatCardState target,
        CombatActionContext context)
    {
        string type = constraint.GetStringOrNull("$type") ?? string.Empty;
        bool result = type switch
        {
            "ConstraintAnd" => constraint.GetArrayOrNull("Constraints") is JsonElement andItems &&
                andItems.EnumerateArray().All(value =>
                    MatchesSpawnConstraint(card, value, target, context)),
            "ConstraintOr" => constraint.GetArrayOrNull("Constraints") is JsonElement orItems &&
                orItems.EnumerateArray().Any(value =>
                    MatchesSpawnConstraint(card, value, target, context)),
            "ConstraintCardType" => MatchesStrings(
                card.Type.Replace("TCard", string.Empty, StringComparison.Ordinal),
                constraint.GetArrayOrNull("Types")),
            "ConstraintTag" => constraint.GetArrayOrNull("Tags") is JsonElement tags &&
                tags.EnumerateArray().Any(value => value.GetString() is string tag && card.Tags.Contains(tag)),
            "ConstraintHiddenTag" => constraint.GetArrayOrNull("Tags") is JsonElement hiddenTags &&
                hiddenTags.EnumerateArray().Any(value => value.GetString() is string tag && card.HiddenTags.Contains(tag)),
            "ConstraintSize" => MatchesStrings(card.Size, constraint.GetArrayOrNull("Sizes")),
            "ConstraintTier" => constraint.GetArrayOrNull("Tiers") is JsonElement tiers &&
                tiers.EnumerateArray().Any(value => value.GetString() is string tier && card.HasTier(tier)),
            "ConstraintHero" => constraint.GetArrayOrNull("Heroes") is JsonElement heroes &&
                heroes.EnumerateArray().Any(value =>
                    value.GetString() is string hero && card.Heroes.Contains(hero)),
            "ConstraintEnchantmentEligible" => constraint.GetArrayOrNull("Enchantments") is JsonElement enchants &&
                enchants.EnumerateArray().Any(value =>
                    value.GetString() is string enchantment && card.HasEnchantment(enchantment)),
            _ => false,
        };
        return constraint.GetPropertyOrNull("IsNot")?.GetBoolean() == true ? !result : result;
    }

    private static bool MatchesStrings(string value, JsonElement? values) =>
        values is JsonElement array && array.EnumerateArray().Any(item =>
            string.Equals(item.GetString(), value, StringComparison.OrdinalIgnoreCase));

    private static int SizeToSpan(string size) => size switch
    {
        "Large" => 3,
        "Medium" => 2,
        _ => 1,
    };

    private static void ApplyTransformation(
        CombatCardState target,
        MaterializedCardDefinition replacement,
        int replacementSpan)
    {
        var statuses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string attribute in new[] { "Haste", "Slow", "Freeze", "Flying" })
        {
            statuses[attribute] = target.IntrinsicAttributes.GetValueOrDefault(attribute);
        }
        target.Definition = replacement;
        target.IntrinsicAttributes.Clear();
        target.Attributes.Clear();
        foreach ((string attribute, int value) in replacement.Attributes)
        {
            target.IntrinsicAttributes[attribute] = value;
            target.Attributes[attribute] = value;
        }
        foreach ((string attribute, int value) in statuses.Where(value => value.Value != 0))
        {
            target.SetIntrinsicAttribute(attribute, value);
        }
        target.IntrinsicTags.Clear();
        target.IntrinsicTags.UnionWith(replacement.Tags);
        target.Tags.Clear();
        target.Tags.UnionWith(replacement.Tags);
        target.IntrinsicHiddenTags.Clear();
        target.IntrinsicHiddenTags.UnionWith(replacement.HiddenTags);
        target.HiddenTags.Clear();
        target.HiddenTags.UnionWith(replacement.HiddenTags);
        target.Span = replacementSpan;
        target.AttributesArePrecomputed = false;
        target.CooldownRemainingMilliseconds = target.GetEffectiveCooldownMilliseconds();
    }

    private static bool ApplyUpgrade(
        CombatCardState target,
        string? requestedTier,
        CombatState state)
    {
        if (state.CardCatalog is null ||
            !state.CardCatalog.TryGet(target.Definition.TemplateId, out OfficialCardDefinition? official) ||
            official is null)
        {
            return false;
        }
        string[] tiers = ["Bronze", "Silver", "Gold", "Diamond"];
        int currentIndex = Array.FindIndex(tiers, value => string.Equals(
            value, target.Definition.Tier, StringComparison.OrdinalIgnoreCase));
        string nextTier = requestedTier ??
            (currentIndex >= 0 && currentIndex < tiers.Length - 1 ? tiers[currentIndex + 1] : string.Empty);
        if (string.IsNullOrEmpty(nextTier) || !official.HasTier(nextTier))
        {
            return false;
        }
        MaterializedCardDefinition before = official.Materialize(
            target.Definition.Tier, target.Definition.Enchantment,
            target.IntrinsicAttributes);
        string? enchantment = target.Definition.Enchantment is string currentEnchantment &&
            official.HasEnchantment(currentEnchantment) ? currentEnchantment : null;
        MaterializedCardDefinition after = official.Materialize(
            nextTier, enchantment, target.IntrinsicAttributes);
        foreach (string attribute in before.Attributes.Keys.Union(after.Attributes.Keys))
        {
            int delta = after.Attributes.GetValueOrDefault(attribute) -
                before.Attributes.GetValueOrDefault(attribute);
            if (delta != 0)
            {
                target.SetIntrinsicAttribute(attribute,
                    checked(target.IntrinsicAttributes.GetValueOrDefault(attribute) + delta));
            }
        }
        target.Definition = after;
        return true;
    }

    private static bool TryPayCost(JsonElement? cost, CombatActionContext context)
    {
        if (cost is null)
        {
            return true;
        }
        if (cost.Value.GetStringOrNull("$type") != "TActionCostPlayerAttribute")
        {
            return false;
        }
        string attribute = cost.Value.GetStringOrNull("PlayerAttributeType") ?? string.Empty;
        int amount = Math.Max(0, RuleValueEvaluator.EvaluateToInt(
            cost.Value.GetObjectOrNull("Value"), context));
        string reductionName = "Percent" + attribute + "CostReduction";
        int reduction = Math.Clamp(
            context.SourceCard.Attributes.GetValueOrDefault(reductionName), 0, 100);
        amount = checked((int)((long)amount * (100 - reduction) / 100));
        CombatantState player = context.SourceCard.Owner;
        int before = player.GetAttribute(attribute);
        if (before < amount)
        {
            return false;
        }
        int after = before - amount;
        player.SetIntrinsicAttribute(attribute, checked(
            player.IntrinsicAttributes.GetValueOrDefault(attribute) - amount));
        context.State.Events.Add(new CombatEvent(
            context.State.Tick, "PlayerAttribute:" + attribute, player.Id, after, before));
        return true;
    }

    private static int ResolveActionAmount(
        JsonElement action,
        string actionType,
        CombatActionContext context)
    {
        JsonElement? value = action.GetObjectOrNull("ReferenceValue")
            ?? action.GetObjectOrNull("Value");
        if (value is not null)
        {
            return RuleValueEvaluator.EvaluateToInt(value, context);
        }
        return DefaultAttributeByAction.TryGetValue(actionType, out string? attribute)
            ? context.SourceCard.Attributes.GetValueOrDefault(attribute)
            : 0;
    }

    private static bool IsSupportedPlayerAction(string actionType) => actionType is
        "TActionPlayerDamage" or "TActionPlayerHeal" or "TActionPlayerShieldApply" or
        "TActionPlayerBurnApply" or "TActionPlayerBurnRemove" or
        "TActionPlayerPoisonApply" or "TActionPlayerPoisonRemove" or
        "TActionPlayerRegenApply" or "TActionPlayerRegenRemove" or
        "TActionPlayerRageApply" or "TActionPlayerTempoApply" or
        "TActionPlayerReviveHeal";

    private static void ExecutePlayerAction(
        string actionType,
        int amount,
        CombatantState target,
        CombatActionContext context)
    {
        CombatState state = context.State;
        switch (actionType)
        {
            case "TActionPlayerDamage":
            {
                DamageResult result = CombatEngine.DealDamage(
                    state, target, amount, eventKind: "CardDamage",
                    sourceId: context.SourceCard.InstanceId);
                int lifesteal = Math.Max(0,
                    context.SourceCard.Attributes.GetValueOrDefault("Lifesteal"));
                if (lifesteal > 0 && target != context.SourceCard.Owner)
                {
                    int dealt = checked(result.HealthDamage + result.ShieldAbsorbed);
                    int requestedHeal = checked((int)((long)dealt * lifesteal / 100));
                    CombatantState owner = context.SourceCard.Owner;
                    int before = owner.Health;
                    int healed = Math.Min(Math.Max(0, requestedHeal), owner.MaxHealth - owner.Health);
                    owner.Health += healed;
                    state.Events.Add(new CombatEvent(
                        state.Tick, "LifeSteal", owner.Id, healed,
                        SourceId: context.SourceCard.InstanceId));
                    if (healed != 0)
                    {
                        state.Events.Add(new CombatEvent(
                            state.Tick, "PlayerAttribute:Health", owner.Id, owner.Health, before));
                    }
                }
                break;
            }
            case "TActionPlayerHeal":
            {
                int before = target.Health;
                int healed = Math.Min(Math.Max(0, amount), target.MaxHealth - target.Health);
                target.Health += healed;
                state.Events.Add(new CombatEvent(
                    state.Tick, "Heal", target.Id, healed,
                    SourceId: context.SourceCard.InstanceId));
                if (amount > healed)
                {
                    state.Events.Add(new CombatEvent(
                        state.Tick, "OverHeal", target.Id, amount - healed,
                        SourceId: context.SourceCard.InstanceId));
                }
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:Health", target.Id, target.Health, before));
                break;
            }
            case "TActionPlayerReviveHeal":
            {
                int before = target.Health;
                target.Health = Math.Min(target.MaxHealth, Math.Max(1, amount));
                state.Events.Add(new CombatEvent(
                    state.Tick, "ReviveHeal", target.Id, target.Health,
                    SourceId: context.SourceCard.InstanceId));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:Health", target.Id, target.Health, before));
                break;
            }
            case "TActionPlayerShieldApply":
            {
                int before = target.Shield;
                target.Shield = checked(target.Shield + Math.Max(0, amount));
                state.Events.Add(new CombatEvent(
                    state.Tick, "Shield", target.Id, amount,
                    SourceId: context.SourceCard.InstanceId));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:Shield", target.Id, target.Shield, before));
                break;
            }
            case "TActionPlayerBurnApply":
            {
                int before = target.Burn;
                target.Burn = checked(target.Burn + Math.Max(0, amount));
                state.Events.Add(new CombatEvent(
                    state.Tick, "BurnApply", target.Id, amount,
                    SourceId: context.SourceCard.InstanceId));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:Burn", target.Id, target.Burn, before));
                break;
            }
            case "TActionPlayerBurnRemove":
            {
                int before = target.Burn;
                int removed = Math.Min(before, Math.Max(0, amount));
                target.Burn -= removed;
                state.Events.Add(new CombatEvent(
                    state.Tick, "BurnRemove", target.Id, removed,
                    SourceId: context.SourceCard.InstanceId));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:Burn", target.Id, target.Burn, before));
                break;
            }
            case "TActionPlayerPoisonApply":
            {
                int before = target.Poison;
                target.Poison = checked(target.Poison + Math.Max(0, amount));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PoisonApply", target.Id, amount,
                    SourceId: context.SourceCard.InstanceId));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:Poison", target.Id, target.Poison, before));
                break;
            }
            case "TActionPlayerPoisonRemove":
            {
                int before = target.Poison;
                int removed = Math.Min(before, Math.Max(0, amount));
                target.Poison -= removed;
                state.Events.Add(new CombatEvent(
                    state.Tick, "PoisonRemove", target.Id, removed,
                    SourceId: context.SourceCard.InstanceId));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:Poison", target.Id, target.Poison, before));
                break;
            }
            case "TActionPlayerRegenApply":
            {
                int before = target.Regen;
                int delta = Math.Max(0, amount);
                target.SetIntrinsicAttribute("HealthRegen", checked(
                    target.IntrinsicAttributes.GetValueOrDefault("HealthRegen") + delta));
                target.Regen = checked(before + delta);
                state.Events.Add(new CombatEvent(
                    state.Tick, "RegenApply", target.Id, amount,
                    SourceId: context.SourceCard.InstanceId));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:Regen", target.Id, target.Regen, before));
                break;
            }
            case "TActionPlayerRegenRemove":
            {
                int before = target.Regen;
                int intrinsicBefore = target.IntrinsicAttributes.GetValueOrDefault("HealthRegen");
                int delta = Math.Min(intrinsicBefore, Math.Max(0, amount));
                target.SetIntrinsicAttribute("HealthRegen", intrinsicBefore - delta);
                // SetIntrinsicAttribute synchronizes Regen to the base value. Preserve
                // the still-active aura contribution until the caller recomputes auras.
                target.Regen = before - delta;
                state.Events.Add(new CombatEvent(
                    state.Tick, "RegenRemove", target.Id, delta,
                    SourceId: context.SourceCard.InstanceId));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:Regen", target.Id, target.Regen, before));
                break;
            }
            case "TActionPlayerRageApply":
            case "TActionPlayerTempoApply":
            {
                string attribute = actionType == "TActionPlayerRageApply" ? "Rage" : "Tempo";
                int before = target.Attributes.GetValueOrDefault(attribute);
                int after = checked(before + Math.Max(0, amount));
                if (attribute == "Rage" && target.Attributes.GetValueOrDefault("Enraged") <= 0)
                {
                    after = Math.Min(after, Math.Max(1,
                        target.Attributes.GetValueOrDefault("RageMax", 100)));
                }
                target.SetIntrinsicAttribute(attribute, checked(
                    target.IntrinsicAttributes.GetValueOrDefault(attribute) + after - before));
                state.Events.Add(new CombatEvent(
                    state.Tick, attribute + "Apply", target.Id, after - before,
                    SourceId: context.SourceCard.InstanceId));
                state.Events.Add(new CombatEvent(
                    state.Tick, "PlayerAttribute:" + attribute, target.Id, after, before));
                break;
            }
        }
    }

    private static void ApplyCardAttribute(
        CombatCardState target,
        string attribute,
        string operation,
        double amount,
        JsonElement action,
        CombatState state,
        string sourceId)
    {
        int before = target.Attributes.GetValueOrDefault(attribute);
        int intrinsicBefore = target.IntrinsicAttributes.GetValueOrDefault(attribute);
        if (action.GetObjectOrNull("Duration") is JsonElement duration &&
            duration.GetStringOrNull("$type") == "TCombatDuration")
        {
            int durationMilliseconds = Math.Max(
                0, duration.GetPropertyOrNull("DurationInMs")?.GetInt32() ?? 0);
            int ticks = (durationMilliseconds + CombatEngine.TickMilliseconds - 1) /
                CombatEngine.TickMilliseconds;
            state.TimedCardModifiers.Add(new TimedCardModifier(
                target, attribute, operation, amount, checked(state.Tick + ticks)));
            int temporaryAfter = ApplyOperation(before, amount, operation);
            target.Attributes[attribute] = temporaryAfter;
            state.Events.Add(new CombatEvent(
                state.Tick, "CardModifyAttribute:" + attribute,
                target.InstanceId, temporaryAfter, before, sourceId));
            return;
        }
        int after = ApplyOperation(intrinsicBefore, amount, operation);
        target.SetIntrinsicAttribute(attribute, after);
        AdjustCooldownForReductionChange(
            target, attribute, before, intrinsicBefore, after);
        state.Events.Add(new CombatEvent(
            state.Tick, "CardModifyAttribute:" + attribute,
            target.InstanceId, after, intrinsicBefore, sourceId));
    }

    private static void AdjustCooldownForReductionChange(
        CombatCardState target,
        string attribute,
        int effectiveBefore,
        int intrinsicBefore,
        int intrinsicAfter)
    {
        if (attribute is not ("PercentCooldownReduction" or "FlatCooldownReduction"))
        {
            return;
        }
        int effectiveAfter = checked(effectiveBefore + intrinsicAfter - intrinsicBefore);
        int oldPercent = attribute == "PercentCooldownReduction"
            ? effectiveBefore
            : target.Attributes.GetValueOrDefault("PercentCooldownReduction");
        int oldFlat = attribute == "FlatCooldownReduction"
            ? effectiveBefore
            : target.Attributes.GetValueOrDefault("FlatCooldownReduction");
        int newPercent = attribute == "PercentCooldownReduction"
            ? effectiveAfter
            : oldPercent;
        int newFlat = attribute == "FlatCooldownReduction"
            ? effectiveAfter
            : oldFlat;
        target.AdjustCooldownForReductionTransition(
            oldPercent, oldFlat, newPercent, newFlat);
        // SetIntrinsicAttribute temporarily replaces the effective value. Keep
        // any currently applied aura contribution visible until aura recompute.
        target.Attributes[attribute] = effectiveAfter;
    }

    private static double EvaluateAttributeOperationAmount(
        JsonElement action,
        string operation,
        CombatActionContext context) => operation is "Multiply" or "AdditiveMultiply"
            ? RuleValueEvaluator.Evaluate(action.GetObjectOrNull("Value"), context)
            : RuleValueEvaluator.EvaluateToInt(action.GetObjectOrNull("Value"), context);

    private static int ApplyOperation(int before, double amount, string operation) => operation switch
    {
        "Add" => checked(before + checked((int)amount)),
        "Subtract" => checked(before - checked((int)amount)),
        "Multiply" => RoundAwayFromZero(before * amount),
        "AdditiveMultiply" => RoundAwayFromZero(before * (1d + amount)),
        _ => before,
    };

    private static int RoundAwayFromZero(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static void ApplyPlayerAttribute(
        CombatantState target,
        string attribute,
        string operation,
        double amount,
        JsonElement action,
        CombatState state,
        string sourceId)
    {
        int before = target.GetAttribute(attribute);
        int effectiveAfter = ApplyOperation(before, amount, operation);
        if (action.GetObjectOrNull("Duration") is JsonElement duration &&
            duration.GetStringOrNull("$type") == "TCombatDuration")
        {
            int milliseconds = Math.Max(0, duration.GetPropertyOrNull("DurationInMs")?.GetInt32() ?? 0);
            int ticks = (milliseconds + CombatEngine.TickMilliseconds - 1) / CombatEngine.TickMilliseconds;
            state.TimedPlayerModifiers.Add(new TimedPlayerModifier(
                target, attribute, operation, amount, checked(state.Tick + ticks)));
            target.Attributes[attribute] = effectiveAfter;
        }
        else
        {
            int intrinsic = target.IntrinsicAttributes.GetValueOrDefault(attribute);
            target.SetIntrinsicAttribute(attribute, ApplyOperation(intrinsic, amount, operation));
        }
        if (attribute == "PercentDamageReduction")
        {
            target.DamageReductionPercent = effectiveAfter;
        }
        state.Events.Add(new CombatEvent(
            state.Tick, "PlayerModifyAttribute:" + attribute, target.Id, effectiveAfter, before,
            sourceId));
    }
}

public static class RuleValueEvaluator
{
    public static int EvaluateToInt(JsonElement? definition, CombatActionContext context)
    {
        float value = Evaluate(definition, context);
        bool truncate = definition?.GetObjectOrNull("Modifier") is JsonElement modifier &&
            modifier.GetPropertyOrNull("ShouldRound")?.GetBoolean() == false;
        return truncate
            ? checked((int)value)
            : checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    public static float Evaluate(JsonElement? definition, CombatActionContext context)
    {
        if (definition is null)
        {
            return 0;
        }
        JsonElement value = definition.Value;
        string type = value.GetStringOrNull("$type") ?? string.Empty;
        float raw = type switch
        {
            "TFixedValue" => value.GetPropertyOrNull("Value")?.GetSingle() ?? 0,
            "TRangeValue" => EvaluateRange(value, context),
            "TReferenceValueCardAttribute" or "TReferenceValueCardAttributeUnscaled" =>
                EvaluateCardAttributeReference(value, context),
            "TReferenceValuePlayerAttribute" or "TReferenceValuePlayerAttributeUnscaled" =>
                EvaluatePlayerAttributeReference(value, context),
            "TReferenceValueAttributeChange" => Math.Abs(
                context.AttributeDelta ?? (int)(value.GetPropertyOrNull("DefaultValue")?.GetSingle() ?? 0)),
            "TReferenceValueCardCount" => TargetResolver.ResolveCardTarget(
                value.GetObjectOrNull("Target"), context, null).Count,
            "TReferenceValueCardTagCount" => EvaluateCardTagCount(value, context),
            "TReferenceValueCardAttributeAggregate" => EvaluateCardAttributeAggregate(
                value, context),
            _ => value.GetPropertyOrNull("DefaultValue")?.GetSingle() ?? 0,
        };
        return ApplyModifier(raw, value.GetObjectOrNull("Modifier"), context);
    }

    private static float EvaluateRange(
        JsonElement definition,
        CombatActionContext context)
    {
        int minimum = checked((int)(definition.GetPropertyOrNull("MinValue")?.GetSingle() ?? 0));
        int maximum = checked((int)(definition.GetPropertyOrNull("MaxValue")?.GetSingle() ?? 0));
        if (maximum < minimum)
        {
            (minimum, maximum) = (maximum, minimum);
        }
        long width = (long)maximum - minimum + 1;
        return width <= 0 || width > int.MaxValue
            ? minimum
            : minimum + context.Random.Next((int)width);
    }

    private static float EvaluateCardTagCount(
        JsonElement definition,
        CombatActionContext context)
    {
        List<CombatCardState> targets = TargetResolver.ResolveCardTarget(
            definition.GetObjectOrNull("Target"), context, null);
        return definition.GetPropertyOrNull("Distinct")?.GetBoolean() != false
            ? targets.SelectMany(card => card.Tags).Distinct(StringComparer.Ordinal).Count()
            : targets.Sum(card => card.Tags.Count);
    }

    private static float EvaluateCardAttributeAggregate(
        JsonElement definition,
        CombatActionContext context)
    {
        string attribute = definition.GetStringOrNull("AttributeType") ?? string.Empty;
        List<CombatCardState> targets = TargetResolver.ResolveCardTarget(
            definition.GetObjectOrNull("Target"), context, null);
        return targets.Count == 0
            ? definition.GetPropertyOrNull("DefaultValue")?.GetSingle() ?? 0
            : targets.Sum(card => card.Attributes.GetValueOrDefault(attribute));
    }

    private static float EvaluateCardAttributeReference(
        JsonElement definition,
        CombatActionContext context)
    {
        string attribute = definition.GetStringOrNull("AttributeType") ?? string.Empty;
        List<CombatCardState> targets = TargetResolver.ResolveCardTarget(
            definition.GetObjectOrNull("Target"), context, null);
        return targets.Count > 0
            ? targets[0].Attributes.GetValueOrDefault(attribute)
            : definition.GetPropertyOrNull("DefaultValue")?.GetSingle() ?? 0;
    }

    private static float EvaluatePlayerAttributeReference(
        JsonElement definition,
        CombatActionContext context)
    {
        string attribute = definition.GetStringOrNull("AttributeType") ?? string.Empty;
        List<CombatantState> targets = TargetResolver.ResolvePlayers(
            definition.GetObjectOrNull("Target"), context);
        if (targets.Count == 0)
        {
            return definition.GetPropertyOrNull("DefaultValue")?.GetSingle() ?? 0;
        }
        return targets[0].GetAttribute(attribute);
    }

    private static float ApplyModifier(
        float original,
        JsonElement? modifier,
        CombatActionContext context)
    {
        if (modifier is null)
        {
            return original;
        }
        float operand = Evaluate(modifier.Value.GetObjectOrNull("Value"), context);
        string mode = modifier.Value.GetStringOrNull("ModifyMode") ?? string.Empty;
        float result = mode switch
        {
            "Add" => original + operand,
            "Subtract" => original - operand,
            "Multiply" => original * operand,
            "Divide" => operand == 0 ? original : original / operand,
            _ => original,
        };
        bool shouldRound = modifier.Value.GetPropertyOrNull("ShouldRound")?.GetBoolean() ?? true;
        if (!shouldRound || mode is not ("Multiply" or "Divide"))
        {
            return result;
        }
        return result is > 0 and < 1
            ? 1
            : (float)Math.Round(result, MidpointRounding.AwayFromZero);
    }
}

public static class TargetResolver
{
    public static List<CombatantState> ResolvePlayers(
        JsonElement? target,
        CombatActionContext context)
    {
        if (target is null)
        {
            return [];
        }
        string type = target.Value.GetStringOrNull("$type") ?? string.Empty;
        string mode = target.Value.GetStringOrNull("TargetMode") ?? string.Empty;
        List<CombatantState> result;
        if (type is "TTargetPlayerRelative" or "TTargetPlayer")
        {
            result = mode switch
            {
                "Self" => [context.SourceCard.Owner],
                "Opponent" => context.State.Combatants
                    .Where(value => value != context.SourceCard.Owner).Take(1).ToList(),
                "Both" => context.State.Combatants.ToList(),
                _ => [],
            };
        }
        else if (type == "TTargetPlayerAbsolute")
        {
            result = mode switch
            {
                "Player" => context.State.Combatants.Take(1).ToList(),
                "Opponent" => context.State.Combatants.Skip(1).Take(1).ToList(),
                "Both" => context.State.Combatants.ToList(),
                _ => [],
            };
        }
        else
        {
            return [];
        }
        JsonElement? condition = target.Value.GetObjectOrNull("Conditions");
        return result.Where(player => MatchesPlayerCondition(player, condition, context)).ToList();
    }

    private static bool MatchesPlayerCondition(
        CombatantState player, JsonElement? condition, CombatActionContext context)
    {
        if (condition is null)
        {
            return true;
        }
        string type = condition.Value.GetStringOrNull("$type") ?? string.Empty;
        bool result = type switch
        {
            "TPlayerConditionalAttribute" => RulePrerequisiteEvaluator.Compare(
                player.GetAttribute(condition.Value.GetStringOrNull("Attribute") ?? string.Empty),
                RuleValueEvaluator.EvaluateToInt(condition.Value.GetObjectOrNull("ComparisonValue"), context),
                condition.Value.GetStringOrNull("ComparisonOperator")),
            _ => true,
        };
        return condition.Value.GetPropertyOrNull("IsNot")?.GetBoolean() == true ? !result : result;
    }

    public static List<CombatCardState> ResolveCards(
        JsonElement action,
        CombatActionContext context)
    {
        int? count = null;
        if (action.GetObjectOrNull("TargetCount") is JsonElement targetCount)
        {
            count = Math.Max(0, RuleValueEvaluator.EvaluateToInt(targetCount, context));
        }
        else
        {
            string? countAttribute = action.GetStringOrNull("$type") switch
            {
                "TActionCardCharge" => "ChargeTargets",
                "TActionCardHaste" => "HasteTargets",
                "TActionCardSlow" => "SlowTargets",
                "TActionCardFreeze" => "FreezeTargets",
                "TActionCardReload" => "ReloadTargets",
                "TActionCardFlyingStart" or "TActionCardFlyingStop" or
                    "TActionCardFlyingToggle" => "FlyingTargets",
                _ => null,
            };
            if (countAttribute is not null &&
                context.SourceCard.Attributes.TryGetValue(countAttribute, out int configured))
            {
                count = Math.Max(0, configured);
            }
        }
        return ResolveCardTarget(
            action.GetObjectOrNull("Target"), context, count,
            action.GetStringOrNull("$type"));
    }

    public static List<CombatCardState> ResolveCardTarget(
        JsonElement? target,
        CombatActionContext context,
        int? count,
        string? actionType = null)
    {
        if (target is null)
        {
            return [];
        }
        string type = target.Value.GetStringOrNull("$type") ?? string.Empty;
        List<CombatCardState> candidates = type switch
        {
            "TTargetCardSelf" => [context.SourceCard],
            "TTargetCardTriggerSource" => context.TriggerSource is null ? [] : [context.TriggerSource],
            "TTargetCardTriggerTarget" => context.TriggerTarget is null ? [] : [context.TriggerTarget],
            "TTargetCardSection" or "TTargetCardRandom" => ResolveSection(target.Value, context),
            "TTargetCardPositional" => ResolvePositional(target.Value, context),
            "TTargetCardXMost" => ResolveXMost(target.Value, context),
            "TTargetCardOccupying" => ResolveOccupying(context),
            _ => [],
        };
        candidates = candidates.Where(card => !card.IsDestroyed).ToList();
        if (actionType == "TActionCardRepair")
        {
            candidates = candidates.Where(card => card.IsDisabled).ToList();
        }
        else if (type is not ("TTargetCardSelf" or "TTargetCardTriggerSource" or
            "TTargetCardTriggerTarget"))
        {
            // Bazaar represents ordinary in-combat destruction as Disabled.  A
            // disabled card is no longer a normal board target even though its
            // entity remains available for Repair and lifecycle references.
            candidates = candidates.Where(card => !card.IsDisabled).ToList();
        }
        else if (actionType == "TActionCardDisable")
        {
            candidates = candidates.Where(card => !card.IsDisabled).ToList();
        }
        if (target.Value.GetPropertyOrNull("ExcludeSelf")?.GetBoolean() == true)
        {
            candidates.Remove(context.SourceCard);
        }
        JsonElement? condition = target.Value.GetObjectOrNull("Conditions");
        List<CombatCardState> allPotentialTargets = candidates.ToList();
        candidates = candidates.Where(card => MatchesCondition(
            card, condition, context, allPotentialTargets)).ToList();

        if (type == "TTargetCardRandom" && count is null)
        {
            count = 1;
        }

        if (count is null || count >= candidates.Count)
        {
            return candidates;
        }
        if (count <= 0)
        {
            return [];
        }
        bool randomSelection = type == "TTargetCardRandom";
        if (actionType is "TActionCardHaste" or "TActionCardSlow" or
            "TActionCardFreeze" or "TActionCardReload")
        {
            return SelectByPriorityGroups(
                candidates,
                [
                    card => card.Attributes.GetValueOrDefault("CooldownMax") > 0,
                    card => card.Attributes.GetValueOrDefault("CooldownMax") <= 0,
                ],
                count.Value, randomSelection, context.Random, fillAcrossGroups: true);
        }
        if (actionType == "TActionCardCharge")
        {
            return SelectByPriorityGroups(
                candidates,
                [
                    card => card.Attributes.GetValueOrDefault("CooldownMax") > 0 &&
                        (card.Attributes.GetValueOrDefault("AmmoMax") <= 0 ||
                         card.Attributes.GetValueOrDefault("Ammo") > 0),
                    card => card.Attributes.GetValueOrDefault("CooldownMax") > 0,
                ],
                count.Value, randomSelection, context.Random, fillAcrossGroups: true);
        }
        if (actionType == "TActionCardForceUse")
        {
            List<CombatCardState> notFrozen = candidates.Where(card =>
                card.Attributes.GetValueOrDefault("Freeze") <= 0).ToList();
            if (notFrozen.Count > 0)
            {
                return SelectCards(
                    notFrozen, count.Value, randomSelection, context.Random);
            }
        }
        if (type != "TTargetCardRandom")
        {
            return candidates.Take(count.Value).ToList();
        }
        return SelectCards(candidates, count.Value, true, context.Random);
    }

    private static List<CombatCardState> SelectByPriorityGroups(
        List<CombatCardState> candidates,
        IReadOnlyList<Func<CombatCardState, bool>> priorities,
        int count,
        bool random,
        XorShiftCombatRandom randomSource,
        bool fillAcrossGroups)
    {
        var result = new List<CombatCardState>();
        var remaining = new HashSet<CombatCardState>(candidates);
        foreach (Func<CombatCardState, bool> priority in priorities)
        {
            List<CombatCardState> group = candidates
                .Where(card => remaining.Contains(card) && priority(card)).ToList();
            foreach (CombatCardState selected in SelectCards(
                group, count - result.Count, random, randomSource))
            {
                if (remaining.Remove(selected))
                {
                    result.Add(selected);
                }
            }
            if (result.Count >= count || !fillAcrossGroups && result.Count > 0)
            {
                break;
            }
        }
        return result.Count > 0
            ? result
            : SelectCards(candidates, count, random, randomSource);
    }

    private static List<CombatCardState> SelectCards(
        List<CombatCardState> candidates,
        int count,
        bool random,
        XorShiftCombatRandom randomSource)
    {
        if (count >= candidates.Count)
        {
            return candidates;
        }
        if (!random)
        {
            return candidates.Take(count).ToList();
        }
        var pool = candidates.ToList();
        var result = new List<CombatCardState>();
        while (result.Count < count && pool.Count > 0)
        {
            int index = randomSource.Next(pool.Count);
            result.Add(pool[index]);
            pool.RemoveAt(index);
        }
        return result;
    }

    private static List<CombatCardState> ResolveSection(
        JsonElement target,
        CombatActionContext context)
    {
        string section = target.GetStringOrNull("TargetSection") ?? string.Empty;
        CombatantState self = context.SourceCard.Owner;
        CombatantState? opponent = context.State.Combatants.FirstOrDefault(value => value != self);
        static bool IsSocketEffect(CombatCardState card) =>
            card.Definition.Type.Contains("SocketEffect", StringComparison.Ordinal);
        static bool IsHandCard(CombatCardState card) =>
            card.Section == "Hand" && !IsSocketEffect(card);
        static bool IsBoardCard(CombatCardState card) =>
            !IsSocketEffect(card) && card.Section is "Hand" or "Skills";
        IEnumerable<CombatCardState> cards = section switch
        {
            "SelfHand" => self.Cards.Where(IsHandCard),
            "SelfStash" => self.Cards.Where(card => card.Section == "Stash"),
            "SelfHandAndStash" => self.Cards.Where(card =>
                IsHandCard(card) || card.Section == "Stash"),
            "SelfSkills" => self.Cards.Where(card => card.Section == "Skills"),
            "SelfBoard" => self.Cards.Where(IsBoardCard),
            "SelfSocketEffects" => self.Cards.Where(IsSocketEffect),
            "SelfNeighbors" => ResolveNeighbors(context.SourceCard),
            "TriggerSourceNeighbors" => context.TriggerSource is null
                ? [] : ResolveNeighbors(context.TriggerSource),
            "OpponentHand" => opponent?.Cards.Where(IsHandCard) ?? [],
            "OpponentStash" => opponent?.Cards.Where(card => card.Section == "Stash") ?? [],
            "OpponentHandAndStash" => opponent?.Cards.Where(card =>
                IsHandCard(card) || card.Section == "Stash") ?? [],
            "OpponentSkills" => opponent?.Cards.Where(card => card.Section == "Skills") ?? [],
            "OpponentBoard" => opponent?.Cards.Where(IsBoardCard) ?? [],
            "OpponentSocketEffects" => opponent?.Cards.Where(IsSocketEffect) ?? [],
            "AllHands" => context.State.Combatants.SelectMany(value => value.Cards)
                .Where(IsHandCard),
            "AllStashes" => context.State.Combatants.SelectMany(value => value.Cards)
                .Where(card => card.Section == "Stash"),
            "AllHandsAndStashes" => context.State.Combatants.SelectMany(value => value.Cards)
                .Where(card => IsHandCard(card) || card.Section == "Stash"),
            "AllHandsAndSkills" => context.State.Combatants.SelectMany(value => value.Cards)
                .Where(card => IsHandCard(card) || card.Section == "Skills"),
            "AllBoards" => context.State.Combatants.SelectMany(value => value.Cards)
                .Where(IsBoardCard),
            "AllSocketEffects" => context.State.Combatants.SelectMany(value => value.Cards)
                .Where(IsSocketEffect),
            "AbsolutePlayerHand" => context.State.Combatants.Take(1)
                .SelectMany(value => value.Cards).Where(IsHandCard),
            "AbsolutePlayerStash" => context.State.Combatants.Take(1)
                .SelectMany(value => value.Cards).Where(card => card.Section == "Stash"),
            "AbsoluteOpponentHand" => context.State.Combatants.Skip(1).Take(1)
                .SelectMany(value => value.Cards).Where(IsHandCard),
            "AbsoluteOpponentStash" => context.State.Combatants.Skip(1).Take(1)
                .SelectMany(value => value.Cards).Where(card => card.Section == "Stash"),
            "AbsolutePlayerSkills" => context.State.Combatants.Take(1)
                .SelectMany(value => value.Cards).Where(card => card.Section == "Skills"),
            "AbsoluteOpponentSkills" => context.State.Combatants.Skip(1).Take(1)
                .SelectMany(value => value.Cards).Where(card => card.Section == "Skills"),
            "AbsolutePlayerSocketEffects" => context.State.Combatants.Take(1)
                .SelectMany(value => value.Cards).Where(IsSocketEffect),
            "AbsoluteOpponentSocketEffects" => context.State.Combatants.Skip(1).Take(1)
                .SelectMany(value => value.Cards).Where(IsSocketEffect),
            "AbsolutePlayerHandAndStash" => context.State.Combatants.Take(1)
                .SelectMany(value => value.Cards)
                .Where(card => IsHandCard(card) || card.Section == "Stash"),
            "AbsoluteOpponentHandAndStash" => context.State.Combatants.Skip(1).Take(1)
                .SelectMany(value => value.Cards)
                .Where(card => IsHandCard(card) || card.Section == "Stash"),
            _ => [],
        };
        return cards.OrderBy(card => card.BoardPosition).ToList();
    }

    private static IEnumerable<CombatCardState> ResolveNeighbors(CombatCardState origin)
    {
        bool originIsSocketEffect = origin.Definition.Type.Contains(
            "SocketEffect", StringComparison.Ordinal);
        return origin.Owner.Cards.Where(card =>
            card != origin &&
            card.Section == origin.Section &&
            card.Definition.Type.Contains("SocketEffect", StringComparison.Ordinal) ==
                originIsSocketEffect &&
            (card.BoardPosition + card.Span == origin.BoardPosition ||
             origin.BoardPosition + origin.Span == card.BoardPosition));
    }

    private static List<CombatCardState> ResolvePositional(
        JsonElement target,
        CombatActionContext context)
    {
        CombatCardState origin = target.GetStringOrNull("Origin") == "TriggerSource"
            ? context.TriggerSource ?? context.SourceCard
            : context.SourceCard;
        string mode = target.GetStringOrNull("TargetMode") ?? string.Empty;
        bool originIsSocketEffect = origin.Definition.Type.Contains(
            "SocketEffect", StringComparison.Ordinal);
        List<CombatCardState> board = origin.Owner.Cards
            .Where(card => card.Section == origin.Section &&
                card.Definition.Type.Contains("SocketEffect", StringComparison.Ordinal) ==
                    originIsSocketEffect)
            .OrderBy(card => card.BoardPosition).ToList();
        IEnumerable<CombatCardState> result = mode switch
        {
            "Neighbor" => board.Where(card =>
                card.BoardPosition + card.Span == origin.BoardPosition ||
                origin.BoardPosition + origin.Span == card.BoardPosition),
            "LeftCard" => board.Where(card => card.BoardPosition + card.Span == origin.BoardPosition),
            "RightCard" => board.Where(card => origin.BoardPosition + origin.Span == card.BoardPosition),
            "AllLeftCards" => board.Where(card => card.BoardPosition < origin.BoardPosition),
            "AllRightCards" => board.Where(card => card.BoardPosition > origin.BoardPosition),
            _ => [],
        };
        var cards = result.ToList();
        if (target.GetPropertyOrNull("IncludeOrigin")?.GetBoolean() == true && !cards.Contains(origin))
        {
            cards.Add(origin);
        }
        return cards;
    }

    private static List<CombatCardState> ResolveOccupying(CombatActionContext context)
    {
        CombatCardState socket = context.SourceCard;
        return socket.Owner.Cards
            .Where(card => card != socket && card.Section == "Hand" &&
                card.Definition.Type == "TCardItem" &&
                card.BoardPosition <= socket.BoardPosition &&
                socket.BoardPosition < card.BoardPosition + card.Span)
            .OrderBy(card => card.BoardPosition)
            .ThenBy(card => card.InstanceId, StringComparer.Ordinal)
            .ToList();
    }

    private static List<CombatCardState> ResolveXMost(
        JsonElement target,
        CombatActionContext context)
    {
        List<CombatCardState> candidates = ResolveSection(target, context);
        if (target.GetPropertyOrNull("ExcludeSelf")?.GetBoolean() == true)
        {
            candidates.Remove(context.SourceCard);
        }
        JsonElement? condition = target.GetObjectOrNull("Conditions");
        List<CombatCardState> allPotentialTargets = candidates.ToList();
        candidates = candidates.Where(card => MatchesCondition(
            card, condition, context, allPotentialTargets)).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }
        return target.GetStringOrNull("TargetMode") == "LeftMostCard"
            ? [candidates.MinBy(card => card.BoardPosition)!]
            : [candidates.MaxBy(card => card.BoardPosition)!];
    }

    private static bool MatchesCondition(
        CombatCardState card,
        JsonElement? condition,
        CombatActionContext context,
        IReadOnlyList<CombatCardState>? allPotentialTargets = null)
    {
        if (condition is null)
        {
            return true;
        }
        string type = condition.Value.GetStringOrNull("$type") ?? string.Empty;
        bool result = type switch
        {
            "TCardConditionalTag" => MatchesStrings(
                card.Tags, condition.Value.GetArrayOrNull("Tags"),
                condition.Value.GetStringOrNull("Operator")),
            "TCardConditionalHiddenTag" => MatchesStrings(
                card.HiddenTags, condition.Value.GetArrayOrNull("Tags"),
                condition.Value.GetStringOrNull("Operator")),
            "TCardConditionalId" => string.Equals(
                card.Definition.TemplateId,
                condition.Value.GetStringOrNull("Id"),
                StringComparison.OrdinalIgnoreCase),
            "TCardConditionalTier" => MatchesArrayString(
                card.Definition.Tier, condition.Value.GetArrayOrNull("Tiers")),
            "TCardConditionalSize" => MatchesArrayString(
                card.Definition.Size, condition.Value.GetArrayOrNull("Sizes")),
            "TCardConditionalType" => MatchesArrayString(
                card.Definition.Type.Replace("TCard", string.Empty, StringComparison.Ordinal),
                condition.Value.GetArrayOrNull("Types")),
            "TCardConditionalHasEnchantment" => string.Equals(
                card.Definition.Enchantment,
                condition.Value.GetStringOrNull("Enchantment"),
                StringComparison.OrdinalIgnoreCase),
            "TCardConditionalAttribute" => MatchesAttribute(card, condition.Value, context),
            "TCardConditionalCanCrit" => CanCardCrit(card),
            "TCardConditionalTriggerSource" => card == context.TriggerSource,
            "TCardConditionalPlayerHero" => MatchesPlayerHero(card, condition.Value, context),
            "TCardConditionalEnchantmentEligible" =>
                condition.Value.GetStringOrNull("Enchantment") is string eligible &&
                context.State.CardCatalog is not null &&
                context.State.CardCatalog.TryGet(
                    card.Definition.TemplateId, out OfficialCardDefinition? definition) &&
                definition is not null && definition.HasEnchantment(eligible),
            "TCardConditionalAttributeHighest" => IsAttributeExtreme(
                card, condition.Value, allPotentialTargets, highest: true),
            "TCardConditionalAttributeLowest" => IsAttributeExtreme(
                card, condition.Value, allPotentialTargets, highest: false),
            "TCardConditionalSizeLargest" => IsSizeExtreme(
                card, allPotentialTargets, largest: true),
            "TCardConditionalSizeSmallest" => IsSizeExtreme(
                card, allPotentialTargets, largest: false),
            "TCardConditionalAnd" => condition.Value.GetArrayOrNull("Conditions") is JsonElement andItems &&
                andItems.EnumerateArray().All(item => MatchesCondition(
                    card, item, context, allPotentialTargets)),
            "TCardConditionalOr" => condition.Value.GetArrayOrNull("Conditions") is JsonElement orItems &&
                orItems.EnumerateArray().Any(item => MatchesCondition(
                    card, item, context, allPotentialTargets)),
            _ => true,
        };
        return condition.Value.GetPropertyOrNull("IsNot")?.GetBoolean() == true ? !result : result;
    }

    private static bool IsAttributeExtreme(
        CombatCardState card,
        JsonElement condition,
        IReadOnlyList<CombatCardState>? allPotentialTargets,
        bool highest)
    {
        if (allPotentialTargets is null)
        {
            return false;
        }
        string attribute = condition.GetStringOrNull("AttributeType") ?? string.Empty;
        CombatCardState? selected = null;
        int selectedValue = 0;
        foreach (CombatCardState candidate in allPotentialTargets)
        {
            if (!candidate.Attributes.TryGetValue(attribute, out int value))
            {
                continue;
            }
            if (highest ? value > selectedValue : selected is null || value < selectedValue)
            {
                selected = candidate;
                selectedValue = value;
            }
        }
        return card == selected;
    }

    private static bool IsSizeExtreme(
        CombatCardState card,
        IReadOnlyList<CombatCardState>? allPotentialTargets,
        bool largest)
    {
        if (allPotentialTargets is null || allPotentialTargets.Count == 0)
        {
            return false;
        }
        int extreme = largest
            ? allPotentialTargets.Max(candidate => candidate.Span)
            : allPotentialTargets.Min(candidate => candidate.Span);
        return card.Span == extreme;
    }

    private static bool MatchesPlayerHero(
        CombatCardState card,
        JsonElement condition,
        CombatActionContext context)
    {
        string? hero = context.SourceCard.Owner.Hero;
        if (hero is null || context.State.CardCatalog is null ||
            !context.State.CardCatalog.TryGet(
                card.Definition.TemplateId, out OfficialCardDefinition? definition) ||
            definition is null)
        {
            return false;
        }
        bool contains = definition.Heroes.Contains(hero);
        return condition.GetPropertyOrNull("IsSameAsPlayerHero")?.GetBoolean() == true
            ? contains
            : !contains;
    }

    private static bool MatchesAttribute(
        CombatCardState card,
        JsonElement condition,
        CombatActionContext context)
    {
        int current = card.Attributes.GetValueOrDefault(condition.GetStringOrNull("Attribute") ?? string.Empty);
        int expected = RuleValueEvaluator.EvaluateToInt(
            condition.GetObjectOrNull("ComparisonValue"), context);
        return condition.GetStringOrNull("ComparisonOperator") switch
        {
            "Equal" => current == expected,
            "NotEqual" => current != expected,
            "GreaterThan" => current > expected,
            "LessThan" => current < expected,
            "GreaterThanOrEqual" => current >= expected,
            "LessThanOrEqual" => current <= expected,
            _ => false,
        };
    }

    private static bool MatchesStrings(
        HashSet<string> values,
        JsonElement? array,
        string? comparison)
    {
        if (array is not JsonElement items)
        {
            return false;
        }
        string[] expected = items.EnumerateArray()
            .Select(item => item.GetString())
            .OfType<string>()
            .ToArray();
        return comparison switch
        {
            "All" => expected.All(values.Contains),
            "None" => expected.All(value => !values.Contains(value)),
            _ => expected.Any(values.Contains),
        };
    }

    private static bool MatchesArrayString(string value, JsonElement? array) =>
        array is JsonElement items && items.EnumerateArray()
            .Any(item => string.Equals(item.GetString(), value, StringComparison.OrdinalIgnoreCase));

    public static bool CanCardCrit(CombatCardState card)
    {
        if (card.HiddenTags.Contains("CanCrit"))
        {
            return true;
        }
        if (card.Definition.Type != "TCardItem")
        {
            return false;
        }
        return card.Definition.Effects.Any(effect =>
            effect.Kind == "Ability" &&
            effect.Definition.GetObjectOrNull("Trigger")?.GetStringOrNull("$type") ==
                "TTriggerOnCardFired" &&
            effect.Definition.GetObjectOrNull("Action")?.GetStringOrNull("$type") is
                "TActionPlayerDamage" or "TActionPlayerShieldApply" or
                "TActionPlayerHeal" or "TActionPlayerReviveHeal" or
                "TActionPlayerBurnApply" or "TActionPlayerPoisonApply" or
                "TActionPlayerRegenApply");
    }
}
