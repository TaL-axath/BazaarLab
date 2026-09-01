using System.Text.Json;

namespace BazaarLab.Combat;

public sealed record SnapshotRuleCoverageReport(
    int SnapshotCount,
    int ImportedSnapshotCount,
    int ImportErrorCount,
    int CardCount,
    int TypedInstanceCount,
    int UnsupportedInstanceCount,
    IReadOnlyDictionary<string, int> Unsupported,
    IReadOnlyDictionary<string, int> Types,
    IReadOnlyDictionary<string, int> Variants,
    IReadOnlyDictionary<string, string> ImportErrors);

public static class SnapshotRuleCoverage
{
    public static SnapshotRuleCoverageReport Analyze(
        string snapshotDirectory,
        OfficialCardCatalog catalog,
        string supportedTypesPath)
    {
        using JsonDocument supportedDocument = JsonDocument.Parse(
            File.ReadAllText(supportedTypesPath));
        var supported = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty group in supportedDocument.RootElement.EnumerateObject())
        {
            foreach (JsonElement name in group.Value.EnumerateArray())
            {
                if (name.GetString() is string value)
                {
                    supported.Add(value);
                }
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var variants = new Dictionary<string, int>(StringComparer.Ordinal);
        var importErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        int snapshots = 0;
        int importedSnapshots = 0;
        int cards = 0;
        foreach (string path in Directory.EnumerateFiles(snapshotDirectory, "*.json")
            .OrderBy(value => value, StringComparer.Ordinal))
        {
            snapshots++;
            BppSnapshotImportResult imported;
            try
            {
                imported = BppCombatSnapshotAdapter.Import(path, catalog);
                importedSnapshots++;
            }
            catch (Exception exception) when (exception is InvalidDataException or
                KeyNotFoundException or JsonException)
            {
                importErrors[Path.GetFileNameWithoutExtension(path)] = exception.Message;
                continue;
            }
            foreach (CombatCardState card in imported.State.Combatants.SelectMany(value => value.Cards))
            {
                cards++;
                foreach (MaterializedEffectDefinition effect in card.Definition.Effects)
                {
                    CountEffectSignalVariants(effect.Definition, variants);
                    CountTypes(effect.Definition, counts, variants);
                }
            }
        }
        Dictionary<string, int> unsupported = counts
            .Where(value => !supported.Contains(value.Key))
            .OrderByDescending(value => value.Value)
            .ThenBy(value => value.Key, StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        return new SnapshotRuleCoverageReport(
            snapshots, importedSnapshots, importErrors.Count,
            cards, counts.Values.Sum(), unsupported.Values.Sum(), unsupported,
            counts.OrderBy(value => value.Key, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
            variants.OrderBy(value => value.Key, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
            importErrors);
    }

    private static void CountEffectSignalVariants(
        JsonElement definition,
        Dictionary<string, int> variants)
    {
        string actionTarget = definition.GetObjectOrNull("Action")?
            .GetObjectOrNull("Target")?.GetStringOrNull("$type") ?? "null";
        IEnumerable<JsonElement> triggers = definition.GetObjectOrNull("Trigger") is JsonElement trigger
            ? [trigger]
            : definition.GetArrayOrNull("Triggers") is JsonElement triggerArray
                ? triggerArray.EnumerateArray().ToArray()
                : [];
        foreach (JsonElement candidate in triggers)
        {
            string? triggerType = candidate.GetStringOrNull("$type");
            if (triggerType?.StartsWith("TTriggerOnCardPerformed", StringComparison.Ordinal) != true)
            {
                continue;
            }
            string key = $"{triggerType}.ActionTarget={actionTarget}";
            variants[key] = variants.GetValueOrDefault(key) + 1;
        }
    }

    private static void CountTypes(
        JsonElement value,
        Dictionary<string, int> counts,
        Dictionary<string, int> variants)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            string? type = value.GetStringOrNull("$type");
            if (type is not null && IsCombatRuleType(type))
            {
                counts[type] = counts.GetValueOrDefault(type) + 1;
                CountVariant(value, type, "TargetSection", variants);
                CountVariant(value, type, "TargetMode", variants);
                CountVariant(value, type, "Origin", variants);
                CountVariant(value, type, "Comparison", variants);
                CountVariant(value, type, "ComparisonOperator", variants);
                CountVariant(value, type, "ModifyMode", variants);
                CountVariant(value, type, "Operation", variants);
                CountVariant(value, type, "AttributeType", variants);
                CountVariant(value, type, "Attribute", variants);
                CountVariant(value, type, "AttributeChanged", variants);
                CountVariant(value, type, "PlayerAttributeType", variants);
                CountVariant(value, type, "Operator", variants);
                CountVariant(value, type, "ChangeType", variants);
                CountVariant(value, type, "DurationType", variants);
                CountBooleanVariant(value, type, "IsNot", variants);
                CountBooleanVariant(value, type, "ExcludeSelf", variants);
                if (value.GetObjectOrNull("Modifier") is JsonElement modifier)
                {
                    CountVariant(modifier, type + ".Modifier", "ModifyMode", variants);
                    CountBooleanVariant(
                        modifier, type + ".Modifier", "ShouldRound", variants);
                }
                CountTriggerTargetVariant(value, type, "Subject", variants);
                CountTriggerTargetVariant(value, type, "Source", variants);
                CountTriggerTargetVariant(value, type, "Target", variants);
                CountAuraOperationValueVariant(value, type, variants);
            }
            foreach (JsonProperty property in value.EnumerateObject())
            {
                CountTypes(property.Value, counts, variants);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in value.EnumerateArray())
            {
                CountTypes(child, counts, variants);
            }
        }
    }

    private static void CountVariant(
        JsonElement value,
        string type,
        string propertyName,
        Dictionary<string, int> variants)
    {
        if (value.GetStringOrNull(propertyName) is not string variant)
        {
            return;
        }
        string key = $"{type}.{propertyName}={variant}";
        variants[key] = variants.GetValueOrDefault(key) + 1;
    }

    private static void CountBooleanVariant(
        JsonElement value,
        string type,
        string propertyName,
        Dictionary<string, int> variants)
    {
        if (value.GetPropertyOrNull(propertyName) is not JsonElement property ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }
        string key = $"{type}.{propertyName}={property.GetBoolean()}";
        variants[key] = variants.GetValueOrDefault(key) + 1;
    }

    private static void CountTriggerTargetVariant(
        JsonElement value,
        string type,
        string propertyName,
        Dictionary<string, int> variants)
    {
        if (!type.StartsWith("TTrigger", StringComparison.Ordinal) ||
            value.GetPropertyOrNull(propertyName) is not JsonElement property)
        {
            return;
        }
        string variant = property.ValueKind == JsonValueKind.Null
            ? "null"
            : property.GetStringOrNull("$type") ?? property.ValueKind.ToString();
        string key = $"{type}.{propertyName}={variant}";
        variants[key] = variants.GetValueOrDefault(key) + 1;
    }

    private static void CountAuraOperationValueVariant(
        JsonElement value,
        string type,
        Dictionary<string, int> variants)
    {
        if (!type.StartsWith("TAuraAction", StringComparison.Ordinal) ||
            value.GetStringOrNull("Operation") is not string operation ||
            value.GetObjectOrNull("Value") is not JsonElement amount ||
            amount.GetStringOrNull("$type") != "TFixedValue" ||
            amount.GetPropertyOrNull("Value") is not JsonElement fixedValue ||
            fixedValue.ValueKind != JsonValueKind.Number)
        {
            return;
        }
        string key = $"{type}.Operation={operation}.FixedValue={fixedValue.GetRawText()}";
        variants[key] = variants.GetValueOrDefault(key) + 1;
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
