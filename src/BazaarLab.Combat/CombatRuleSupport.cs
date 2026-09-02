using System.Text.Json;

namespace BazaarLab.Combat;

public static class CombatRuleSupport
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "TActionCardBeginSandstorm", "TActionCardCharge", "TActionCardFlyingStart",
        "TActionCardFlyingStop", "TActionCardFlyingToggle", "TActionCardForceUse",
        "TActionCardFreeze", "TActionCardHaste", "TActionCardModifyAttribute",
        "TActionCardReload", "TActionCardSlow", "TActionPlayerBurnApply",
        "TActionPlayerBurnRemove", "TActionPlayerDamage", "TActionPlayerHeal",
        "TActionPlayerPoisonApply", "TActionPlayerPoisonRemove", "TActionPlayerRegenApply",
        "TActionPlayerRageApply", "TActionPlayerRegenRemove", "TActionPlayerShieldApply",
        "TActionPlayerTempoApply", "TActionPlayerModifyAttribute", "TActionPlayerReviveHeal",
        "TActionCardEnchant", "TActionCardEnchantRandom", "TActionCardAddTagsRandom",
        "TActionCardDisable", "TActionCardRepair", "TActionCardDestroy",
        "TActionCardAddTagsList", "TActionAnd", "TActionCardAddTagsBySource",
        "TActionCardEnchantRemove", "TActionCardTransform", "TActionCardUpgrade",
        "TActionCardTransformDestroyed",

        "TAuraActionCardAddTagsList", "TAuraActionCardModifyAttribute",
        "TAuraActionPlayerModifyAttribute", "TAuraActionCardAddTagsBySource",

        "TTriggerOnCardAttributeChanged", "TTriggerOnCardCritted", "TTriggerOnCardFired",
        "TTriggerOnCardPerformedBurn", "TTriggerOnCardPerformedFreeze",
        "TTriggerOnCardPerformedHaste", "TTriggerOnCardPerformedHeal",
        "TTriggerOnCardPerformedPoison", "TTriggerOnCardPerformedShield",
        "TTriggerOnCardPerformedSlow", "TTriggerOnCardStartedFlying",
        "TTriggerOnCardStoppedFlying", "TTriggerOnFightStarted", "TTriggerOnItemUsed",
        "TTriggerOnPlayerAttributeChanged", "TTriggerOnPlayerEnraged",
        "TTriggerOnPlayerEnrageEnded", "TTriggerOnPlayerRaged",
        "TTriggerOnPlayerRagedWhileEnraged", "TTriggerOr",
        "TTriggerOnCardPerformedRegen", "TTriggerOnCardPerformedOverHeal",
        "TTriggerOnSandstorm", "TTriggerOnBeforeItemUsed",
        "TTriggerOnCardPerformedDamage", "TTriggerOnCardPerformedReload",
        "TTriggerOnCardStartsFlying", "TTriggerOnCardDisabled", "TTriggerOnCardRepaired",
        "TTriggerOnBeforeCardDestroyed", "TTriggerOnCardPerformedDestruction",
        "TTriggerOnPlayerDied", "TTriggerOnCardTransformed", "TTriggerOnCardUpgraded",

        "TTargetCardPositional", "TTargetCardRandom", "TTargetCardSection",
        "TTargetCardSelf", "TTargetCardTriggerSource", "TTargetCardTriggerTarget",
        "TTargetCardXMost", "TTargetCardOccupying", "TTargetPlayerAbsolute",
        "TTargetPlayerRelative", "TTargetPlayer",

        "TCardConditionalAnd", "TCardConditionalAttribute", "TCardConditionalCanCrit",
        "TCardConditionalHasEnchantment", "TCardConditionalHiddenTag", "TCardConditionalId",
        "TCardConditionalOr", "TCardConditionalSize", "TCardConditionalTag",
        "TCardConditionalTier", "TCardConditionalType", "TCardConditionalTriggerSource",
        "TCardConditionalEnchantmentEligible", "TCardConditionalAttributeHighest",
        "TCardConditionalAttributeLowest", "TCardConditionalPlayerHero",
        "TPlayerConditionalAttribute", "TCardConditionalSizeLargest",
        "TCardConditionalSizeSmallest",

        "TFixedValue", "TRangeValue", "TReferenceValueAttributeChange",
        "TReferenceValueCardAttribute", "TReferenceValueCardAttributeUnscaled",
        "TReferenceValueCardCount", "TReferenceValuePlayerAttribute",
        "TReferenceValuePlayerAttributeUnscaled", "TReferenceValueCardTagCount",
        "TReferenceValueCardAttributeAggregate",

        "TPrerequisiteCardAttributeComparator", "TPrerequisiteCardCount",
        "TPrerequisitePlayer", "TActionCostPlayerAttribute", "TCombatDuration",
        "TDeterminantDuration",
    };

    private static readonly HashSet<string> KnownNonCombatTriggers = new(StringComparer.Ordinal)
    {
        "TTriggerOnCardSold", "TTriggerOnCardSelected", "TTriggerOnEncounterCardsDealt",
        "TTriggerOnDayStarted", "TTriggerOnCardPurchased", "TTriggerOnEncounterSelected",
        "TTriggerOnFightEnded", "TTriggerOnHourStarted", "TTriggerOnCardQuestCompleted",
        "TTriggerOnEncounterEntered", "TTriggerOnEncounterExited",
    };

    public static IReadOnlyList<string> FindUnsupported(
        MaterializedCardDefinition card,
        string? section)
    {
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        foreach (MaterializedEffectDefinition effect in card.Effects)
        {
            if (!CanRunInCombat(effect, card.Type, section)) continue;
            CountUnsupported(effect.Definition, unsupported);
        }
        return unsupported.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static bool CanRunInCombat(
        MaterializedEffectDefinition effect,
        string cardType,
        string? section)
    {
        string? worksIn = effect.Definition.GetStringOrNull("WorksIn");
        if (worksIn == "OutOfCombatOnly") return false;
        if (cardType == "TCardItem")
        {
            string? activeIn = effect.Definition.GetStringOrNull("ActiveIn");
            if (activeIn == "HandOnly" && section != "Hand" ||
                activeIn == "StashOnly" && section != "Stash" ||
                activeIn == "HandAndStash" && section is not ("Hand" or "Stash"))
                return false;
        }
        if (effect.Kind == "Aura") return true;
        if (effect.Definition.GetObjectOrNull("Trigger") is not JsonElement trigger)
            return worksIn == "CombatOnly";
        var triggerTypes = new HashSet<string>(StringComparer.Ordinal);
        CollectTriggerTypes(trigger, triggerTypes);
        if (triggerTypes.Any(type => Supported.Contains(type) && type != "TTriggerOr"))
            return true;
        if (triggerTypes.Count > 0 && triggerTypes.All(type =>
                type == "TTriggerOr" || KnownNonCombatTriggers.Contains(type)))
            return false;
        // A new trigger is treated conservatively as combat-capable until the
        // simulator explicitly classifies it.
        return triggerTypes.Count > 0 || worksIn == "CombatOnly";
    }

    private static void CollectTriggerTypes(JsonElement value, ISet<string> types)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            string? type = value.GetStringOrNull("$type");
            if (type is not null && type.StartsWith("TTrigger", StringComparison.Ordinal))
                types.Add(type);
            foreach (JsonProperty property in value.EnumerateObject())
                CollectTriggerTypes(property.Value, types);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in value.EnumerateArray())
                CollectTriggerTypes(child, types);
        }
    }

    private static void CountUnsupported(JsonElement value, ISet<string> unsupported)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            string? type = value.GetStringOrNull("$type");
            if (type is not null && IsCombatRuleType(type) && !Supported.Contains(type))
                unsupported.Add(type);
            foreach (JsonProperty property in value.EnumerateObject())
                CountUnsupported(property.Value, unsupported);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in value.EnumerateArray())
                CountUnsupported(child, unsupported);
        }
    }

    private static bool IsCombatRuleType(string type) =>
        type.StartsWith("TAction", StringComparison.Ordinal) ||
        type.StartsWith("TAuraAction", StringComparison.Ordinal) ||
        type.StartsWith("TTrigger", StringComparison.Ordinal) ||
        type.StartsWith("TTarget", StringComparison.Ordinal) ||
        type.StartsWith("TCardConditional", StringComparison.Ordinal) ||
        type.StartsWith("TPlayerConditional", StringComparison.Ordinal) ||
        type.StartsWith("TReferenceValue", StringComparison.Ordinal) ||
        type.StartsWith("TFixedValue", StringComparison.Ordinal) ||
        type == "TRangeValue" ||
        type.StartsWith("TPrerequisite", StringComparison.Ordinal) ||
        type is "TCombatDuration" or "TDeterminantDuration";
}
