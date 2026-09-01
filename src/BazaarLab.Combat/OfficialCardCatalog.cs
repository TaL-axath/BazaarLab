using System.Text.Json;

namespace BazaarLab.Combat;

public sealed class OfficialCardCatalog
{
    private readonly Dictionary<string, OfficialCardDefinition> _cards =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _cards.Count;
    public IEnumerable<OfficialCardDefinition> Cards => _cards.Values;

    public static OfficialCardCatalog LoadJsonLines(string path)
    {
        var catalog = new OfficialCardCatalog();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? id = root.GetStringOrNull("Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            catalog._cards.Add(id, new OfficialCardDefinition(root.Clone()));
        }
        return catalog;
    }

    public bool TryGet(string templateId, out OfficialCardDefinition? definition) =>
        _cards.TryGetValue(templateId, out definition);

    public OfficialCardDefinition Get(string templateId) =>
        _cards.TryGetValue(templateId, out OfficialCardDefinition? definition)
            ? definition
            : throw new KeyNotFoundException($"Official card {templateId} was not found.");
}

public sealed class OfficialCardDefinition
{
    private static readonly string[] TierOrder = ["Bronze", "Silver", "Gold", "Diamond"];
    private readonly JsonElement _root;

    internal OfficialCardDefinition(JsonElement root) => _root = root;

    public string Id => _root.GetStringOrNull("Id") ?? string.Empty;
    public string Type => _root.GetStringOrNull("$type") ?? string.Empty;
    public string Size => _root.GetStringOrNull("Size") ?? "Small";
    public string InternalName => _root.GetStringOrNull("InternalName") ?? string.Empty;
    public string Name => _root
        .GetObjectOrNull("Localization")?
        .GetObjectOrNull("Title")?
        .GetStringOrNull("Text") ?? InternalName;
    public IReadOnlySet<string> Heroes => ReadStrings(_root.GetArrayOrNull("Heroes"));
    public IReadOnlySet<string> Tags => ReadStrings(_root.GetArrayOrNull("Tags"));
    public IReadOnlySet<string> HiddenTags => ReadStrings(_root.GetArrayOrNull("HiddenTags"));

    public bool HasTier(string tier)
    {
        string lookupTier = CanonicalTier(tier);
        return _root.GetObjectOrNull("Tiers")?.GetPropertyOrNull(lookupTier) is not null;
    }

    public bool HasEnchantment(string enchantment) =>
        _root.GetObjectOrNull("Enchantments")?.GetPropertyOrNull(enchantment) is not null;

    public MaterializedCardDefinition Materialize(
        string tier,
        string? enchantment = null,
        IReadOnlyDictionary<string, int>? runtimeAttributes = null)
    {
        if (!string.IsNullOrWhiteSpace(enchantment) && !HasEnchantment(enchantment))
        {
            throw new InvalidDataException($"Card {Id} has no {enchantment} enchantment.");
        }
        string lookupTier = string.Equals(tier, "Legendary", StringComparison.OrdinalIgnoreCase)
            ? "Diamond"
            : CanonicalTier(tier);
        JsonElement? tiers = _root.GetObjectOrNull("Tiers");
        JsonElement? selectedTier = tiers?.GetPropertyOrNull(lookupTier);
        if (selectedTier is null)
        {
            throw new InvalidDataException($"Card {Id} has no {lookupTier} tier.");
        }

        var attributes = ResolveAttributes(tiers!.Value, lookupTier);
        var effects = new List<MaterializedEffectDefinition>();
        AddSelectedEffects(effects, selectedTier.Value, "AbilityIds", "Abilities", "Ability", "base");
        AddSelectedEffects(effects, selectedTier.Value, "AuraIds", "Auras", "Aura", "base");

        var tags = ReadStrings(_root.GetArrayOrNull("Tags"));
        var hiddenTags = ReadStrings(_root.GetArrayOrNull("HiddenTags"));
        if (!string.IsNullOrWhiteSpace(enchantment) &&
            _root.GetObjectOrNull("Enchantments") is JsonElement enchantments &&
            enchantments.GetPropertyOrNull(enchantment) is JsonElement enchantmentDefinition)
        {
            AddAttributes(attributes, enchantmentDefinition.GetObjectOrNull("Attributes"));
            AddAllEffects(effects, enchantmentDefinition, "Abilities", "Ability", $"enchantment:{enchantment}");
            AddAllEffects(effects, enchantmentDefinition, "Auras", "Aura", $"enchantment:{enchantment}");
            tags.UnionWith(ReadStrings(enchantmentDefinition.GetArrayOrNull("Tags")));
            hiddenTags.UnionWith(ReadStrings(enchantmentDefinition.GetArrayOrNull("HiddenTags")));
        }

        AddCompletedQuestRewards(
            effects, attributes, tags, hiddenTags, lookupTier, runtimeAttributes);

        return new MaterializedCardDefinition(
            Id,
            Name,
            Type,
            _root.GetStringOrNull("Size") ?? "Small",
            lookupTier,
            enchantment,
            attributes,
            tags,
            hiddenTags,
            effects);
    }

    private void AddCompletedQuestRewards(
        List<MaterializedEffectDefinition> effects,
        Dictionary<string, int> attributes,
        HashSet<string> tags,
        HashSet<string> hiddenTags,
        string tier,
        IReadOnlyDictionary<string, int>? runtimeAttributes)
    {
        if (runtimeAttributes is null ||
            _root.GetArrayOrNull("Quests") is not JsonElement quests)
        {
            return;
        }
        foreach (JsonElement quest in quests.EnumerateArray())
        {
            if (quest.GetArrayOrNull("Entries") is not JsonElement entries)
            {
                continue;
            }
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                string? questAttribute = entry.GetStringOrNull("AttributeType");
                int target = entry.GetPropertyOrNull("Target")?.GetInt32() ?? 1;
                if (string.IsNullOrEmpty(questAttribute) ||
                    !runtimeAttributes.TryGetValue(questAttribute, out int progress) ||
                    progress < target ||
                    entry.GetObjectOrNull("Reward") is not JsonElement reward)
                {
                    continue;
                }

                if (reward.GetObjectOrNull("Tiers") is JsonElement rewardTiers)
                {
                    int selectedIndex = Array.FindIndex(TierOrder,
                        value => string.Equals(value, tier, StringComparison.OrdinalIgnoreCase));
                    for (int index = 0; index <= selectedIndex; index++)
                    {
                        if (rewardTiers.GetPropertyOrNull(TierOrder[index]) is JsonElement rewardTier)
                        {
                            AddAttributes(attributes, rewardTier.GetObjectOrNull("Attributes"));
                            AddAllEffects(effects, rewardTier, "Abilities", "Ability",
                                $"quest:{questAttribute}:tier");
                            AddAllEffects(effects, rewardTier, "Auras", "Aura",
                                $"quest:{questAttribute}:tier");
                        }
                    }
                }
                AddAttributes(attributes, reward.GetObjectOrNull("Attributes"));
                AddAllEffects(effects, reward, "Abilities", "Ability",
                    $"quest:{questAttribute}");
                AddAllEffects(effects, reward, "Auras", "Aura",
                    $"quest:{questAttribute}");
                tags.UnionWith(ReadStrings(reward.GetArrayOrNull("Tags")));
                hiddenTags.UnionWith(ReadStrings(reward.GetArrayOrNull("HiddenTags")));
            }
        }
    }

    private Dictionary<string, int> ResolveAttributes(JsonElement tiers, string selectedTier)
    {
        int selectedIndex = Array.FindIndex(
            TierOrder,
            value => string.Equals(value, selectedTier, StringComparison.OrdinalIgnoreCase));
        if (selectedIndex < 0)
        {
            throw new InvalidDataException($"Unsupported tier {selectedTier}.");
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index <= selectedIndex; index++)
        {
            JsonElement? tier = tiers.GetPropertyOrNull(TierOrder[index]);
            if (tier is not null)
            {
                AddAttributes(result, tier.Value.GetObjectOrNull("Attributes"));
            }
        }
        return result;
    }

    private static void AddAttributes(
        Dictionary<string, int> target,
        JsonElement? attributes)
    {
        if (attributes is null)
        {
            return;
        }
        foreach (JsonProperty property in attributes.Value.EnumerateObject())
        {
            target[property.Name] = property.Value.GetInt32();
        }
    }

    private void AddSelectedEffects(
        List<MaterializedEffectDefinition> target,
        JsonElement tier,
        string idProperty,
        string dictionaryProperty,
        string kind,
        string source)
    {
        JsonElement? ids = tier.GetArrayOrNull(idProperty);
        JsonElement? definitions = _root.GetObjectOrNull(dictionaryProperty);
        if (ids is null || definitions is null)
        {
            return;
        }
        foreach (JsonElement idElement in ids.Value.EnumerateArray())
        {
            string? id = idElement.GetString();
            if (id is not null && definitions.Value.GetPropertyOrNull(id) is JsonElement definition)
            {
                target.Add(new MaterializedEffectDefinition(id, kind, source, definition.Clone()));
            }
        }
    }

    private static void AddAllEffects(
        List<MaterializedEffectDefinition> target,
        JsonElement owner,
        string dictionaryProperty,
        string kind,
        string source)
    {
        JsonElement? definitions = owner.GetObjectOrNull(dictionaryProperty);
        if (definitions is null)
        {
            return;
        }
        foreach (JsonProperty property in definitions.Value.EnumerateObject())
        {
            target.Add(new MaterializedEffectDefinition(
                property.Name, kind, source, property.Value.Clone()));
        }
    }

    private static HashSet<string> ReadStrings(JsonElement? array)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (array is null)
        {
            return result;
        }
        foreach (JsonElement value in array.Value.EnumerateArray())
        {
            if (value.GetString() is string text)
            {
                result.Add(text);
            }
        }
        return result;
    }

    private static string CanonicalTier(string tier) => TierOrder.FirstOrDefault(
        value => string.Equals(value, tier, StringComparison.OrdinalIgnoreCase))
        ?? tier;
}

public sealed record MaterializedCardDefinition(
    string TemplateId,
    string Name,
    string Type,
    string Size,
    string Tier,
    string? Enchantment,
    IReadOnlyDictionary<string, int> Attributes,
    IReadOnlySet<string> Tags,
    IReadOnlySet<string> HiddenTags,
    IReadOnlyList<MaterializedEffectDefinition> Effects,
    int ActivationPriority = 0);

public sealed record MaterializedEffectDefinition(
    string Id,
    string Kind,
    string Source,
    JsonElement Definition)
{
    public string DefinitionType => Definition.GetObjectOrNull("Action")?.GetStringOrNull("$type")
        ?? string.Empty;
    public string TriggerType => Definition.GetObjectOrNull("Trigger")?.GetStringOrNull("$type")
        ?? (Kind == "Aura" ? "Aura" : string.Empty);
    public string? VfxOverrideKey => Definition.GetObjectOrNull("VFXConfig")?
        .GetStringOrNull("VFXOverrideKey");
}

internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertyOrNull(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement child)
            ? child
            : null;

    public static JsonElement? GetObjectOrNull(this JsonElement value, string name)
    {
        JsonElement? child = value.GetPropertyOrNull(name);
        return child is { ValueKind: JsonValueKind.Object } ? child : null;
    }

    public static JsonElement? GetArrayOrNull(this JsonElement value, string name)
    {
        JsonElement? child = value.GetPropertyOrNull(name);
        return child is { ValueKind: JsonValueKind.Array } ? child : null;
    }

    public static string? GetStringOrNull(this JsonElement value, string name)
    {
        JsonElement? child = value.GetPropertyOrNull(name);
        return child is { ValueKind: JsonValueKind.String } ? child.Value.GetString() : null;
    }
}
