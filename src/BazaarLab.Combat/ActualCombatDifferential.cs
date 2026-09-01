using System.Text.Json;

namespace BazaarLab.Combat;

public sealed record ActualLocalCountDelta(
    string Kind, int Actual, int Local, int Delta);

public sealed record ActualLocalAmountDelta(
    string Kind, long Actual, long Local, long Delta);

public sealed record ActualCombatDifferentialReport(
    string? CaptureId,
    string? ActualWinner,
    string? LocalWinner,
    bool WinnerMatch,
    int ActualFrames,
    int LocalTicks,
    int ComparedThroughFrame,
    IReadOnlyDictionary<string, int> ActualActionCounts,
    IReadOnlyDictionary<string, int> LocalActionCounts,
    IReadOnlyList<ActualLocalCountDelta> ActionDeltas,
    IReadOnlyDictionary<string, int> ActualSourceActionCounts,
    IReadOnlyDictionary<string, int> LocalSourceActionCounts,
    IReadOnlyList<ActualLocalCountDelta> SourceActionDeltas,
    IReadOnlyDictionary<string, int> ActualCardAttributeCounts,
    IReadOnlyDictionary<string, int> LocalModifiedAttributeCounts,
    IReadOnlyList<ActualLocalCountDelta> ModifiedAttributeDeltas,
    IReadOnlyDictionary<string, int> ActualCardAttributeTargetCounts,
    IReadOnlyDictionary<string, int> LocalCardAttributeTargetCounts,
    IReadOnlyList<ActualLocalCountDelta> CardAttributeTargetDeltas,
    IReadOnlyDictionary<string, long> ActualHealthAdjustmentAmounts,
    IReadOnlyDictionary<string, long> LocalHealthAdjustmentAmounts,
    IReadOnlyList<ActualLocalAmountDelta> HealthAdjustmentDeltas);

public static class ActualCombatDifferential
{
    public static ActualCombatDifferentialReport Compare(
        string actualPath, string localSimulationPath)
    {
        using JsonDocument actualDocument = JsonDocument.Parse(File.ReadAllText(actualPath));
        using JsonDocument localDocument = JsonDocument.Parse(File.ReadAllText(localSimulationPath));
        return Compare(actualDocument.RootElement, localDocument.RootElement);
    }

    public static ActualCombatDifferentialReport CompareJson(
        string actualJson, string localSimulationJson)
    {
        using JsonDocument actualDocument = JsonDocument.Parse(actualJson);
        using JsonDocument localDocument = JsonDocument.Parse(localSimulationJson);
        return Compare(actualDocument.RootElement, localDocument.RootElement);
    }

    private static ActualCombatDifferentialReport Compare(JsonElement actual, JsonElement local)
    {
        int actualFrames = GetInt32(actual, "frame_count", "FrameCount");
        int localTicks = GetInt32(local, "Ticks", "ticks");
        // Official replay frames are zero-based while local scheduler ticks are
        // one-based (official frame 70 aligns with local tick 71).
        int comparedThroughFrame = Math.Max(0,
            Math.Min(actualFrames - 1, localTicks - 1));

        JsonElement[] comparableActualEffects = ThroughFrame(
            GetArray(actual, "effects", "Effects"), comparedThroughFrame).ToArray();
        Dictionary<string, int> actualActions = CountStrings(
            comparableActualEffects,
            "action_type", "ActionType");
        Dictionary<string, int> localActions = ReadLocalActionCounts(local);
        List<ActualLocalCountDelta> actionDeltas = BuildDeltas(actualActions, localActions);
        Dictionary<string, int> actualSourceActions = CountSourceActions(
            comparableActualEffects, "source", "Source", "action_type", "ActionType");
        Dictionary<string, int> localSourceActions = ReadLocalSourceActionCounts(local);
        List<ActualLocalCountDelta> sourceActionDeltas = BuildDeltas(
            actualSourceActions, localSourceActions);

        Dictionary<string, int> actualAttributeTargets =
            ReadComparableActualAttributeTargetCounts(
            ThroughFrame(GetArray(actual, "card_attribute_changes", "CardAttributeChanges"),
                comparedThroughFrame));
        Dictionary<string, int> localAttributeTargets =
            ReadLocalAttributeTargetCounts(local);
        Dictionary<string, int> actualAttributes = CollapseAttributeTargetCounts(
            actualAttributeTargets);
        Dictionary<string, int> localAttributes = CollapseAttributeTargetCounts(
            localAttributeTargets);
        // Local events deliberately omit per-tick Cooldown/Haste/Slow/Freeze
        // countdown noise. Compare only attributes changed by a rule action.
        List<ActualLocalCountDelta> attributeDeltas = localAttributes.Keys
            .Order(StringComparer.Ordinal)
            .Select(kind => new ActualLocalCountDelta(
                kind,
                actualAttributes.GetValueOrDefault(kind),
                localAttributes[kind],
                localAttributes[kind] - actualAttributes.GetValueOrDefault(kind)))
            .ToList();
        List<ActualLocalCountDelta> attributeTargetDeltas = BuildDeltas(
            actualAttributeTargets, localAttributeTargets);

        Dictionary<string, long> healthAmounts = new(StringComparer.Ordinal);
        foreach (JsonElement change in ThroughFrame(
            GetArray(actual, "health_changes", "HealthChanges"), comparedThroughFrame))
        {
            string side = GetString(change, "side", "Side") ?? "unknown";
            string damage = GetString(change, "damage_type", "DamageType") ?? "unknown";
            string attribute = GetString(change, "attribute", "Attribute") ?? "unknown";
            long amount = GetInt64(change, "amount", "Amount");
            string key = $"{side}:{damage}:{attribute}";
            healthAmounts[key] = healthAmounts.GetValueOrDefault(key) + amount;
        }
        Dictionary<string, long> localHealthAmounts =
            ReadLocalHealthAdjustmentAmounts(local, comparedThroughFrame);
        List<ActualLocalAmountDelta> healthAmountDeltas = BuildAmountDeltas(
            healthAmounts, localHealthAmounts);

        string? actualWinner = NormalizeWinner(GetString(actual, "winner", "Winner"));
        string? localWinner = NormalizeWinner(GetString(local, "WinnerId", "winner_id"));
        return new ActualCombatDifferentialReport(
            GetString(actual, "capture_id", "CaptureId"),
            actualWinner,
            localWinner,
            string.Equals(actualWinner, localWinner, StringComparison.Ordinal),
            actualFrames,
            localTicks,
            comparedThroughFrame,
            actualActions,
            localActions,
            actionDeltas,
            actualSourceActions,
            localSourceActions,
            sourceActionDeltas,
            actualAttributes,
            localAttributes,
            attributeDeltas,
            actualAttributeTargets,
            localAttributeTargets,
            attributeTargetDeltas,
            healthAmounts,
            localHealthAmounts,
            healthAmountDeltas);
    }

    public static void Write(string path, ActualCombatDifferentialReport report)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(
            report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static Dictionary<string, int> ReadLocalActionCounts(JsonElement local)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        JsonElement summary = GetObject(local, "EventSummary", "event_summary");
        if (summary.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        foreach (JsonProperty property in summary.EnumerateObject())
        {
            string? action = MapLocalEventToAction(property.Name);
            if (action is null)
            {
                continue;
            }
            int count = GetInt32(property.Value, "Count", "count");
            result[action] = result.GetValueOrDefault(action) + count;
        }
        return result;
    }

    private static Dictionary<string, long> ReadLocalHealthAdjustmentAmounts(
        JsonElement local, int comparedThroughFrame)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        void Add(string side, string damage, string attribute, long amount)
        {
            if (amount == 0)
            {
                return;
            }
            string key = $"{side}:{damage}:{attribute}";
            result[key] = result.GetValueOrDefault(key) + amount;
        }

        foreach (JsonElement combatEvent in GetArray(local, "KeyEventTrace", "key_event_trace"))
        {
            int tick = GetInt32(combatEvent, "Tick", "tick");
            if (tick > comparedThroughFrame + 1)
            {
                continue;
            }
            string? kind = GetString(combatEvent, "Kind", "kind");
            string side = GetString(combatEvent, "TargetId", "target_id") ?? "unknown";
            long amount = GetInt64(combatEvent, "Amount", "amount");
            long secondary = GetInt64(combatEvent, "SecondaryAmount", "secondary_amount");
            switch (kind)
            {
                case "CardDamage":
                    Add(side, "Damage", "Health", -amount);
                    Add(side, "Damage", "Shield", -secondary);
                    break;
                case "Shield":
                    Add(side, "Shield", "Shield", amount);
                    break;
                case "Heal":
                case "LifeSteal":
                    Add(side, "Heal", "Health", amount);
                    break;
                case "Burn":
                    Add(side, "Burn", "Health", -amount);
                    break;
                case "BurnShield":
                    Add(side, "Burn", "Shield", -secondary);
                    break;
                case "Poison":
                    Add(side, "Poison", "Health", -amount);
                    break;
                case "Regen":
                    Add(side, "Regen", "Health", amount);
                    break;
            }
        }
        return result;
    }

    private static Dictionary<string, int> ReadLocalAttributeTargetCounts(JsonElement local)
    {
        var transitions = new Dictionary<string,
            (string Target, string Attribute, int Previous, int Current)>(
            StringComparer.Ordinal);
        foreach (JsonElement combatEvent in GetArray(local, "KeyEventTrace", "key_event_trace"))
        {
            string? kind = GetString(combatEvent, "Kind", "kind");
            string? attribute = MapLocalEventToAttribute(kind);
            string? target = GetString(combatEvent, "TargetId", "target_id");
            if (attribute is null || target is null)
            {
                continue;
            }
            int current = GetInt32(combatEvent, "Amount", "amount");
            int previous = GetInt32(combatEvent, "SecondaryAmount", "secondary_amount");
            int tick = GetInt32(combatEvent, "Tick", "tick");
            string key = $"{tick}|{target}|{attribute}";
            if (transitions.TryGetValue(key, out var existing))
            {
                transitions[key] = (target, attribute, existing.Previous, current);
            }
            else
            {
                transitions[key] = (target, attribute, previous, current);
            }
        }
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var transition in transitions.Values.Where(value =>
            value.Previous != value.Current))
        {
            string key = $"{transition.Target}|{transition.Attribute}";
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }

    private static Dictionary<string, int> ReadComparableActualAttributeTargetCounts(
        IEnumerable<JsonElement> changes)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement change in changes)
        {
            string? attribute = GetString(change, "attribute", "Attribute");
            if (attribute is null || attribute == "Cooldown")
            {
                continue;
            }
            int previous = GetInt32(change, "previous", "Previous");
            int current = GetInt32(change, "current", "Current");
            bool naturalStatusCountdown = attribute is "Haste" or "Slow" or "Freeze" &&
                current == Math.Max(0, previous - CombatEngine.TickMilliseconds);
            if (current == previous || naturalStatusCountdown)
            {
                continue;
            }
            string target = GetString(change, "card_id", "CardId") ?? "unknown";
            string key = $"{target}|{attribute}";
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }

    private static Dictionary<string, int> CollapseAttributeTargetCounts(
        IReadOnlyDictionary<string, int> targetCounts)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in targetCounts)
        {
            int separator = pair.Key.LastIndexOf('|');
            string attribute = separator >= 0 ? pair.Key[(separator + 1)..] : pair.Key;
            result[attribute] = result.GetValueOrDefault(attribute) + pair.Value;
        }
        return result;
    }

    private static string? MapLocalEventToAttribute(string? kind)
    {
        if (kind is null)
        {
            return null;
        }
        const string modifyPrefix = "CardModifyAttribute:";
        const string attributePrefix = "CardAttribute:";
        if (kind.StartsWith(modifyPrefix, StringComparison.Ordinal))
        {
            return kind[modifyPrefix.Length..];
        }
        if (kind.StartsWith(attributePrefix, StringComparison.Ordinal))
        {
            return kind[attributePrefix.Length..];
        }
        return kind switch
        {
            "CardHaste" => "Haste",
            "CardSlow" => "Slow",
            "CardFreeze" => "Freeze",
            "CardFlying" => "Flying",
            _ => null,
        };
    }

    private static Dictionary<string, int> ReadLocalSourceActionCounts(JsonElement local)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement combatEvent in GetArray(local, "KeyEventTrace", "key_event_trace"))
        {
            string? kind = GetString(combatEvent, "Kind", "kind");
            string? action = kind is null ? null : MapLocalEventToAction(kind);
            if (action is null)
            {
                continue;
            }
            string source = GetString(combatEvent, "SourceId", "source_id") ?? "unknown";
            string key = $"{source}|{action}";
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }

    internal static string? MapLocalEventToAction(string kind)
    {
        if (kind.StartsWith("CardModifyAttribute:", StringComparison.Ordinal))
        {
            return "CardModifyAttribute";
        }
        if (kind.StartsWith("PlayerModifyAttribute:", StringComparison.Ordinal))
        {
            return "PlayerModifyAttribute";
        }
        if (kind.StartsWith("CardEnchanted:", StringComparison.Ordinal))
        {
            return "CardEnchant";
        }
        return kind switch
        {
            "CardDamage" => "PlayerDamage",
            "Shield" => "PlayerShieldApply",
            "Heal" => "PlayerHeal",
            "BurnApply" => "PlayerBurnApply",
            "PoisonApply" => "PlayerPoisonApply",
            "RegenApply" => "PlayerRegenApply",
            "RageApply" => "PlayerRageApply",
            "TempoApply" => "PlayerTempoApply",
            "CardHaste" => "CardHaste",
            "CardSlow" => "CardSlow",
            "CardFreeze" => "CardFreeze",
            "CardFlying" => "FlyingStart",
            "CardCharge" => "CardCharge",
            "CardReload" => "CardReload",
            "ForceUse" => "CardForceUse",
            "CardDestroy" => "CardDestroy",
            "CardDisabled" => "CardDisable",
            "CardRepaired" => "CardRepair",
            "CardUpgraded" => "CardUpgrade",
            _ => null,
        };
    }

    private static IEnumerable<JsonElement> ThroughFrame(
        IEnumerable<JsonElement> values, int maximumFrame) => values.Where(value =>
    {
        JsonElement frame = GetObject(value, "frame", "Frame");
        return frame.ValueKind != JsonValueKind.Number ||
            !frame.TryGetInt32(out int number) || number <= maximumFrame;
    });

    private static List<ActualLocalCountDelta> BuildDeltas(
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyDictionary<string, int> local) => actual.Keys.Concat(local.Keys)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Select(kind => new ActualLocalCountDelta(
            kind,
            actual.GetValueOrDefault(kind),
            local.GetValueOrDefault(kind),
            local.GetValueOrDefault(kind) - actual.GetValueOrDefault(kind)))
        .ToList();

    private static List<ActualLocalAmountDelta> BuildAmountDeltas(
        IReadOnlyDictionary<string, long> actual,
        IReadOnlyDictionary<string, long> local) => actual.Keys.Concat(local.Keys)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Select(kind => new ActualLocalAmountDelta(
            kind,
            actual.GetValueOrDefault(kind),
            local.GetValueOrDefault(kind),
            local.GetValueOrDefault(kind) - actual.GetValueOrDefault(kind)))
        .ToList();

    private static Dictionary<string, int> CountStrings(
        IEnumerable<JsonElement> values, params string[] names)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement value in values)
        {
            string? key = GetString(value, names);
            if (!string.IsNullOrEmpty(key))
            {
                result[key] = result.GetValueOrDefault(key) + 1;
            }
        }
        return result;
    }

    private static Dictionary<string, int> CountSourceActions(
        IEnumerable<JsonElement> values,
        string sourceName,
        string sourceAlternateName,
        string actionName,
        string actionAlternateName)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement value in values)
        {
            string? action = GetString(value, actionName, actionAlternateName);
            if (string.IsNullOrEmpty(action))
            {
                continue;
            }
            string source = GetString(value, sourceName, sourceAlternateName) ?? "unknown";
            string key = $"{source}|{action}";
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }

    private static IEnumerable<JsonElement> GetArray(JsonElement value, params string[] names)
    {
        foreach (string name in names)
        {
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(name, out JsonElement property) &&
                property.ValueKind == JsonValueKind.Array)
            {
                return property.EnumerateArray().ToArray();
            }
        }
        return [];
    }

    private static JsonElement GetObject(JsonElement value, params string[] names)
    {
        foreach (string name in names)
        {
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(name, out JsonElement property))
            {
                return property;
            }
        }
        return default;
    }

    private static string? GetString(JsonElement value, params string[] names)
    {
        JsonElement property = GetObject(value, names);
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int GetInt32(JsonElement value, params string[] names) =>
        checked((int)GetInt64(value, names));

    private static long GetInt64(JsonElement value, params string[] names)
    {
        JsonElement property = GetObject(value, names);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long result)
            ? result
            : 0;
    }

    private static string? NormalizeWinner(string? winner) => winner?.ToLowerInvariant() switch
    {
        "player" or "win" => "player",
        "opponent" or "loss" => "opponent",
        "draw" => "draw",
        _ => winner?.ToLowerInvariant(),
    };
}
