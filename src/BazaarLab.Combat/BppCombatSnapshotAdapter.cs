using System.Text.Json;

namespace BazaarLab.Combat;

public sealed record BppSnapshotImportResult(
    CombatState State,
    string? BattleId,
    string? ActualResult,
    IReadOnlyList<string> SkippedCards);

public static class BppCombatSnapshotAdapter
{
    public static BppSnapshotImportResult Import(
        string path,
        OfficialCardCatalog catalog,
        int defaultHealth = 100)
    {
        return ImportJson(File.ReadAllText(path), catalog, defaultHealth);
    }

    public static BppSnapshotImportResult ImportJson(
        string json,
        OfficialCardCatalog catalog,
        int defaultHealth = 100)
        => ImportJsonCore(json, catalog, defaultHealth, includeStash: false);

    public static BppSnapshotImportResult ImportJsonForPlacement(
        string json,
        OfficialCardCatalog catalog,
        int defaultHealth = 100)
        => ImportJsonCore(json, catalog, defaultHealth, includeStash: true);

    private static BppSnapshotImportResult ImportJsonCore(
        string json,
        OfficialCardCatalog catalog,
        int defaultHealth,
        bool includeStash)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        var state = new CombatState
        {
            CardCatalog = catalog,
            CardAttributesArePrecomputed = true,
        };
        JsonElement? combatants = GetArray(root, "combatants", "Combatants");
        if (combatants is not null)
        {
            foreach (JsonElement definition in combatants.Value.EnumerateArray())
            {
                var combatant = new CombatantState
                {
                    Id = GetString(definition, "id", "Id") ?? $"combatant-{state.Combatants.Count}",
                    MaxHealth = defaultHealth,
                    Health = defaultHealth,
                    AttributesArePrecomputed = GetBool(
                        definition, "attributes_precomputed", "AttributesPrecomputed"),
                };
                combatant.SetIntrinsicAttribute("RageMax", 100);
                combatant.SetIntrinsicAttribute("EnragedDurationMax", 5000);
                ApplyCombatantAttributes(
                    combatant, GetObject(definition, "attributes", "Attributes"));
                state.Combatants.Add(combatant);
            }
        }
        while (state.Combatants.Count < 2)
        {
            state.Combatants.Add(new CombatantState
            {
                Id = state.Combatants.Count == 0 ? "player" : "opponent",
                MaxHealth = defaultHealth,
                Health = defaultHealth,
            });
        }

        if (GetObject(root, "battle", "Battle") is JsonElement heroBattle)
        {
            ApplyHeroDefaults(state.Combatants[0],
                GetString(heroBattle, "player_hero", "PlayerHero"));
            ApplyHeroDefaults(state.Combatants[1],
                GetString(heroBattle, "opponent_hero", "OpponentHero"));
        }

        var skipped = new List<string>();
        JsonElement? cardSets = GetArray(root, "card_sets", "CardSets");
        if (cardSets is not null)
        {
            foreach (JsonElement set in cardSets.Value.EnumerateArray())
            {
                int ownerIndex = GetInt(set, "owner", "Owner") ?? 0;
                if (ownerIndex < 0 || ownerIndex >= state.Combatants.Count)
                {
                    continue;
                }
                string section = GetString(set, "section", "Section") ?? "Hand";
                if (section is not "Hand" and not "Skills" &&
                    !(includeStash && section == "Stash"))
                {
                    continue;
                }
                JsonElement? items = GetArray(set, "items", "Items");
                if (items is null)
                {
                    continue;
                }
                foreach (JsonElement item in items.Value.EnumerateArray())
                {
                    string templateId = GetString(item, "template_id", "TemplateId") ?? string.Empty;
                    if (!catalog.TryGet(templateId, out OfficialCardDefinition? definition) ||
                        definition is null)
                    {
                        skipped.Add(templateId);
                        continue;
                    }
                    string tier = GetString(item, "tier", "Tier") ?? "Diamond";
                    string? enchantment = GetString(item, "enchant", "Enchant");
                    if (string.IsNullOrWhiteSpace(enchantment) ||
                        string.Equals(enchantment, "None", StringComparison.OrdinalIgnoreCase))
                    {
                        enchantment = null;
                    }
                    JsonElement? attributes = GetObject(item, "attributes", "Attributes");
                    Dictionary<string, int>? runtimeAttributes = attributes is null
                        ? null
                        : attributes.Value.EnumerateObject().ToDictionary(
                            attribute => attribute.Name,
                            attribute => attribute.Value.GetInt32(),
                            StringComparer.Ordinal);
                    MaterializedCardDefinition materialized = definition.Materialize(
                        tier, enchantment, runtimeAttributes);
                    int position = ParseSocket(GetString(item, "socket", "Socket"));
                    int span = ParseSize(GetString(item, "size", "Size"));
                    CombatCardState card = CombatCardState.Create(
                        GetString(item, "instance_id", "InstanceId") ?? Guid.NewGuid().ToString("N"),
                        materialized, state.Combatants[ownerIndex], position, section, span);
                    card.AttributesArePrecomputed = GetBool(
                        item, "attributes_precomputed", "AttributesPrecomputed") ??
                        state.Combatants[ownerIndex].AttributesArePrecomputed ?? true;
                    if (attributes is not null)
                    {
                        foreach (JsonProperty attribute in attributes.Value.EnumerateObject())
                        {
                            card.SetIntrinsicAttribute(attribute.Name, attribute.Value.GetInt32());
                        }
                    }
                    JsonElement? tags = GetArray(item, "tags", "Tags");
                    if (tags is not null)
                    {
                        foreach (JsonElement tag in tags.Value.EnumerateArray())
                        {
                            if (tag.GetString() is string value)
                            {
                                card.IntrinsicTags.Add(value);
                                card.Tags.Add(value);
                            }
                        }
                    }
                }
            }
        }
        AddImplicitPlayerEffects(state, catalog);
        return new BppSnapshotImportResult(
            state,
            GetObject(root, "battle", "Battle") is JsonElement battle
                ? GetString(battle, "id", "Id") : null,
            GetObject(root, "battle", "Battle") is JsonElement resultBattle
                ? GetString(resultBattle, "result", "Result") : null,
            skipped);
    }

    private static void AddImplicitPlayerEffects(
        CombatState state,
        OfficialCardCatalog catalog)
    {
        string[] commonCombatEffectIds =
        [
            "4472da8a-26a3-4e10-bd9a-e93c2e22f19c", // Base Rage Effect
            "f74011cc-0f8b-462e-bc96-3a314afaa2af", // Gold Gained tracker
        ];
        for (int ownerIndex = 0; ownerIndex < state.Combatants.Count; ownerIndex++)
        {
            CombatantState combatant = state.Combatants[ownerIndex];
            for (int effectIndex = 0; effectIndex < commonCombatEffectIds.Length; effectIndex++)
            {
                string templateId = commonCombatEffectIds[effectIndex];
                if (!catalog.TryGet(templateId, out OfficialCardDefinition? definition) ||
                    definition is null)
                {
                    continue;
                }
                MaterializedCardDefinition materialized = definition.Materialize("Diamond");
                CombatCardState card = CombatCardState.Create(
                    $"implicit-player-effect-{effectIndex}-{ownerIndex}",
                    materialized,
                    combatant,
                    int.MaxValue - commonCombatEffectIds.Length + effectIndex,
                    "Skills");
                // Common PlayerEffect cards are absent from BPP opening
                // card_sets. Their attributes come from the catalog rather
                // than from the precomputed runtime item payload.
                card.AttributesArePrecomputed = false;
            }
        }
    }

    private static int ParseSocket(string? socket) =>
        socket is not null && int.TryParse(socket.Split('_').Last(), out int value) ? value : 0;

    private static int ParseSize(string? size) => size switch
    {
        "Medium" => 2,
        "Large" => 3,
        _ => 1,
    };

    private static void ApplyHeroDefaults(CombatantState combatant, string? hero)
    {
        if (string.Equals(hero, "TheDragons", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(hero, "The Dragons", StringComparison.OrdinalIgnoreCase))
        {
            hero = "Hero8";
        }
        combatant.Hero = hero;
        if (combatant.IntrinsicAttributes.ContainsKey("TempoGainCooldownMax"))
        {
            return;
        }
        int cooldown = hero == "Hero8" ? 1000 : 999_999_999;
        combatant.SetIntrinsicAttribute("TempoGainCooldownMax", cooldown);
        combatant.SetIntrinsicAttribute(
            "FlatTempoGainCooldownReduction", hero == "Hero8" ? 0 : -999_999_999);
        combatant.SetIntrinsicAttribute("PercentTempoGainCooldownReduction", 0);
    }

    private static void ApplyCombatantAttributes(
        CombatantState combatant, JsonElement? attributes)
    {
        if (attributes is null)
        {
            return;
        }
        foreach (JsonProperty attribute in attributes.Value.EnumerateObject())
        {
            int value = attribute.Value.GetInt32();
            combatant.SetIntrinsicAttribute(attribute.Name, value);
            switch (attribute.Name)
            {
                case "Health": combatant.Health = value; break;
                case "HealthMax": combatant.MaxHealth = value; break;
                case "Shield": combatant.Shield = value; break;
                case "Burn": combatant.Burn = value; break;
                case "Poison": combatant.Poison = value; break;
                case "Regen":
                case "HealthRegen": combatant.Regen = value; break;
                case "TempoCooldownRemaining":
                case "TempoGainCooldownRemaining":
                    combatant.InitialTempoCooldownMilliseconds = value;
                    break;
            }
        }
        if (!attributes.Value.TryGetProperty("HealthMax", out _) &&
            attributes.Value.TryGetProperty("Health", out JsonElement health))
        {
            combatant.MaxHealth = health.GetInt32();
            combatant.SetIntrinsicAttribute("HealthMax", health.GetInt32());
        }
        else if ((!attributes.Value.TryGetProperty("Health", out JsonElement currentHealth) ||
                currentHealth.GetInt32() <= 0) &&
            attributes.Value.TryGetProperty("HealthMax", out JsonElement healthMax) &&
            healthMax.GetInt32() > 0)
        {
            combatant.Health = healthMax.GetInt32();
            combatant.SetIntrinsicAttribute("Health", healthMax.GetInt32());
        }
    }

    private static string? GetString(JsonElement value, params string[] names)
    {
        JsonElement? property = Get(value, names);
        return property is { ValueKind: JsonValueKind.String } ? property.Value.GetString() : null;
    }

    private static int? GetInt(JsonElement value, params string[] names)
    {
        JsonElement? property = Get(value, names);
        return property is { ValueKind: JsonValueKind.Number } ? property.Value.GetInt32() : null;
    }

    private static bool? GetBool(JsonElement value, params string[] names)
    {
        JsonElement? property = Get(value, names);
        return property is { ValueKind: JsonValueKind.True } ? true :
            property is { ValueKind: JsonValueKind.False } ? false : null;
    }

    private static JsonElement? GetArray(JsonElement value, params string[] names) =>
        Get(value, names) is { ValueKind: JsonValueKind.Array } property ? property : null;

    private static JsonElement? GetObject(JsonElement value, params string[] names) =>
        Get(value, names) is { ValueKind: JsonValueKind.Object } property ? property : null;

    private static JsonElement? Get(JsonElement value, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property))
            {
                return property;
            }
        }
        return null;
    }
}
