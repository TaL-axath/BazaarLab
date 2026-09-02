using System.Text.Json;

namespace BazaarLab.Combat;

public sealed record BppSnapshotValidationReport(
    bool PredictionReady,
    string? Schema,
    int CombatantCount,
    int CardSetCount,
    int CardCount,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public static class BppSnapshotValidator
{
    public const string LiveSchema = "bazaarlab-combat-snapshot-v1";
    public const string LegacyLiveSchema = "lookingin-localcombat-bpp-snapshot-v1";

    public static BppSnapshotValidationReport ValidateLive(
        string path, OfficialCardCatalog catalog) =>
        ValidateLiveJson(File.ReadAllText(path), catalog);

    public static BppSnapshotValidationReport ValidateLiveJson(
        string json, OfficialCardCatalog catalog) =>
        ValidateJsonCore(json, catalog, requireOpponentHand: true);

    public static BppSnapshotValidationReport ValidatePlacementJson(
        string json, OfficialCardCatalog catalog) =>
        ValidateJsonCore(json, catalog, requireOpponentHand: false);

    private static BppSnapshotValidationReport ValidateJsonCore(
        string json, OfficialCardCatalog catalog, bool requireOpponentHand)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        var errors = new List<string>();
        var warnings = new List<string>();
        string? schema = ReadString(root, "schema");
        if (!string.Equals(schema, LiveSchema, StringComparison.Ordinal) &&
            !string.Equals(schema, LegacyLiveSchema, StringComparison.Ordinal))
        {
            errors.Add($"schema must be {LiveSchema}");
        }

        JsonElement combatants = ReadArray(root, "combatants", errors);
        int combatantCount = combatants.ValueKind == JsonValueKind.Array
            ? combatants.GetArrayLength() : 0;
        if (combatantCount != 2)
        {
            errors.Add($"combatants must contain exactly 2 entries; found {combatantCount}");
        }
        if (combatants.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement combatant in combatants.EnumerateArray())
            {
                string id = ReadString(combatant, "id") ?? $"combatant[{index}]";
                JsonElement attributes = ReadObject(combatant, "attributes", errors, id);
                if (!requireOpponentHand && index == 1)
                {
                    int? optionalHealthMax = ReadInt(attributes, "HealthMax");
                    int? optionalHealth = ReadInt(attributes, "Health");
                    if (optionalHealthMax is null || optionalHealth is null)
                    {
                        warnings.Add($"{id}: neutral target health will use simulator defaults");
                    }
                    index++;
                    continue;
                }
                int? healthMax = ReadPositiveInt(attributes, "HealthMax", errors, id);
                int? health = ReadInt(attributes, "Health");
                if (health is null || health <= 0)
                {
                    if (health == -1 && healthMax is > 0)
                    {
                        warnings.Add($"{id}: pre-combat Health sentinel will use HealthMax");
                        health = healthMax;
                    }
                    else
                    {
                        errors.Add($"{id}: Health is missing or invalid");
                        health = null;
                    }
                }
                if (health is not null && healthMax is not null && health > healthMax)
                {
                    errors.Add($"{id}: Health exceeds HealthMax");
                }
                index++;
            }
        }

        JsonElement cardSets = ReadArray(root, "card_sets", errors);
        int cardSetCount = cardSets.ValueKind == JsonValueKind.Array
            ? cardSets.GetArrayLength() : 0;
        int cardCount = 0;
        var instanceIds = new HashSet<string>(StringComparer.Ordinal);
        var observedSets = new HashSet<string>(StringComparer.Ordinal);
        if (cardSets.ValueKind == JsonValueKind.Array)
        {
            int setIndex = 0;
            foreach (JsonElement set in cardSets.EnumerateArray())
            {
                int? owner = ReadInt(set, "owner");
                if (owner is not 0 and not 1)
                {
                    errors.Add($"card_sets[{setIndex}]: owner must be 0 or 1");
                }
                string? section = ReadString(set, "section");
                if (section is not "Hand" and not "Skills" and not "Stash")
                {
                    errors.Add($"card_sets[{setIndex}]: section must be Hand, Skills or Stash");
                }
                else if (owner is 0 or 1 && !observedSets.Add($"{owner}:{section}"))
                {
                    errors.Add($"duplicate card set for owner {owner} section {section}");
                }
                JsonElement items = ReadArray(set, "items", errors, $"card_sets[{setIndex}]");
                if (requireOpponentHand && owner == 1 && section == "Hand" &&
                    items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 0)
                {
                    errors.Add("opponent Hand must contain at least one captured card");
                }
                if (items.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        cardCount++;
                        string? instanceId = ReadString(item, "instance_id");
                        if (string.IsNullOrWhiteSpace(instanceId))
                        {
                            errors.Add($"card_sets[{setIndex}]: card is missing instance_id");
                        }
                        else if (!instanceIds.Add(instanceId))
                        {
                            errors.Add($"duplicate card instance_id: {instanceId}");
                        }
                        string? templateId = ReadString(item, "template_id");
                        if (string.IsNullOrWhiteSpace(templateId))
                        {
                            errors.Add($"{instanceId ?? $"card[{cardCount - 1}]"}: missing template_id");
                        }
                        else if (!catalog.TryGet(templateId, out OfficialCardDefinition? definition) ||
                            definition is null)
                        {
                            errors.Add($"{instanceId ?? $"card[{cardCount - 1}]"}: unknown template_id {templateId}");
                        }
                        else
                        {
                            ValidateCardMaterialization(
                                item, definition,
                                instanceId ?? $"card[{cardCount - 1}]",
                                section, errors);
                        }
                    }
                }
                setIndex++;
            }
        }
        foreach (string expectedSet in new[] { "0:Hand", "0:Skills", "1:Hand", "1:Skills" })
        {
            if (!observedSets.Contains(expectedSet))
            {
                errors.Add($"missing card set {expectedSet}");
            }
        }

        if (root.TryGetProperty("input_warnings", out JsonElement inputWarnings) &&
            inputWarnings.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement warning in inputWarnings.EnumerateArray())
            {
                if (warning.ValueKind == JsonValueKind.String &&
                    warning.GetString() is string value && !warnings.Contains(value))
                {
                    warnings.Add(value);
                }
            }
        }
        AddTempoWarnings(root, warnings);

        return new BppSnapshotValidationReport(
            errors.Count == 0, schema, combatantCount, cardSetCount, cardCount,
            errors, warnings);
    }

    private static void ValidateCardMaterialization(
        JsonElement item,
        OfficialCardDefinition definition,
        string cardLabel,
        string? setSection,
        ICollection<string> errors)
    {
        string? tier = ReadString(item, "tier");
        if (string.IsNullOrWhiteSpace(tier))
        {
            errors.Add($"{cardLabel}: tier is missing");
        }
        string? enchantment = ReadString(item, "enchant");
        if (string.IsNullOrWhiteSpace(enchantment) ||
            string.Equals(enchantment, "None", StringComparison.OrdinalIgnoreCase))
        {
            enchantment = null;
        }
        bool validEnchantment = enchantment is null || definition.HasEnchantment(enchantment);
        if (!validEnchantment)
        {
            errors.Add($"{cardLabel}: unknown enchantment {enchantment}");
        }

        string? size = ReadString(item, "size");
        if (size is not "Small" and not "Medium" and not "Large")
        {
            errors.Add($"{cardLabel}: size must be Small, Medium or Large");
        }
        if (setSection == "Hand")
        {
            string? socket = ReadString(item, "socket");
            bool validSocket = socket is not null &&
                socket.StartsWith("Socket_", StringComparison.Ordinal) &&
                int.TryParse(socket["Socket_".Length..], out int position) &&
                position is >= 0 and <= 9;
            if (!validSocket)
            {
                errors.Add($"{cardLabel}: hand card socket must be Socket_0 through Socket_9");
            }
        }

        var runtimeAttributes = new Dictionary<string, int>(StringComparer.Ordinal);
        bool validAttributes = true;
        if (!item.TryGetProperty("attributes", out JsonElement attributes) ||
            attributes.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{cardLabel}: attributes must be an object");
            validAttributes = false;
        }
        else
        {
            foreach (JsonProperty attribute in attributes.EnumerateObject())
            {
                if (attribute.Value.ValueKind != JsonValueKind.Number ||
                    !attribute.Value.TryGetInt32(out int value))
                {
                    errors.Add($"{cardLabel}: attribute {attribute.Name} must be a 32-bit integer");
                    validAttributes = false;
                }
                else if (!runtimeAttributes.TryAdd(attribute.Name, value))
                {
                    errors.Add($"{cardLabel}: duplicate attribute {attribute.Name}");
                    validAttributes = false;
                }
            }
        }

        if (!item.TryGetProperty("tags", out JsonElement tags) ||
            tags.ValueKind != JsonValueKind.Array ||
            tags.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String))
        {
            errors.Add($"{cardLabel}: tags must be an array of strings");
        }

        if (!string.IsNullOrWhiteSpace(tier) && validEnchantment && validAttributes)
        {
            try
            {
                MaterializedCardDefinition materialized = definition.Materialize(
                    tier, enchantment, runtimeAttributes);
                IReadOnlyList<string> unsupported = CombatRuleSupport.FindUnsupported(
                    materialized, setSection);
                if (unsupported.Count > 0)
                {
                    errors.Add($"{cardLabel}: unsupported combat rules: " +
                        string.Join(", ", unsupported));
                }
            }
            catch (InvalidDataException exception)
            {
                errors.Add($"{cardLabel}: {exception.Message}");
            }
        }
    }

    private static void AddTempoWarnings(JsonElement root, ICollection<string> warnings)
    {
        if (!root.TryGetProperty("combatants", out JsonElement combatants) ||
            combatants.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement combatant in combatants.EnumerateArray())
        {
            string? hero = ReadString(combatant, "hero");
            if (!string.Equals(hero, "Hero8", StringComparison.Ordinal) ||
                !combatant.TryGetProperty("attributes", out JsonElement attributes) ||
                attributes.ValueKind != JsonValueKind.Object ||
                attributes.TryGetProperty("TempoGainCooldownRemaining", out _) ||
                attributes.TryGetProperty("TempoCooldownRemaining", out _))
            {
                continue;
            }
            string id = ReadString(combatant, "id") ?? "Hero8";
            string message = $"{id}: opening Tempo cooldown remainder is unavailable; " +
                "Monte Carlo will marginalize its first-period phase";
            if (!warnings.Contains(message))
            {
                warnings.Add(message);
            }
        }
    }

    private static JsonElement ReadArray(
        JsonElement parent, string name, ICollection<string> errors, string? prefix = null)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return value;
        }
        errors.Add($"{(prefix is null ? name : prefix + "." + name)} must be an array");
        return default;
    }

    private static JsonElement ReadObject(
        JsonElement parent, string name, ICollection<string> errors, string prefix)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        errors.Add($"{prefix}.{name} must be an object");
        return default;
    }

    private static int? ReadPositiveInt(
        JsonElement parent, string name, ICollection<string> errors, string prefix)
    {
        int? value = ReadInt(parent, name);
        if (value is null || value <= 0)
        {
            errors.Add($"{prefix}: {name} is missing or invalid");
            return null;
        }
        return value;
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result : null;
}
