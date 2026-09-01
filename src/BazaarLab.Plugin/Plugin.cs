using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BazaarGameClient.Domain.Models;
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarPlusPlus.Game.PvpBattles;
using BepInEx;
using HarmonyLib;
using TheBazaar;

namespace BazaarLab.Plugin;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("BazaarPlusPlus")]
public sealed partial class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.bazaarlab.plugin";
    public const string PluginName = "BazaarLab";
    public const string PluginVersion = "1.0.3";

    private static Plugin? _instance;
    private Harmony? _harmony;
    private string _outputDirectory = string.Empty;
    private readonly Dictionary<string, string> _captureIdsByMessageId =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private string? _latestCaptureId;
    private DateTime _nextLiveInventoryCaptureUtc;
    private string? _lastLiveInventoryPayload;

    private static bool IsCombatOrReplayActive()
    {
        if (Data.IsInCombat) return true;
        AppState? state = AppState.CurrentState;
        try
        {
            if (state?.IsCombatState() == true) return true;
        }
        catch (Exception)
        {
            // State can change while Unity is transitioning between screens.
        }
        string appState = state?.GetType().Name ?? string.Empty;
        string runState = Data.CurrentState?.StateName.ToString() ?? string.Empty;
        return appState.IndexOf("Combat", StringComparison.OrdinalIgnoreCase) >= 0 ||
            appState.IndexOf("Replay", StringComparison.OrdinalIgnoreCase) >= 0 ||
            runState.IndexOf("Combat", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void MigrateLegacyOutputDirectory(string destination)
    {
        string legacy = Path.Combine(Paths.ConfigPath, "LookingIN.LocalCapture");
        if (Directory.Exists(destination) || !Directory.Exists(legacy)) return;
        try
        {
            Directory.Move(legacy, destination);
            Logger.LogInfo("Migrated legacy plugin data to " + destination);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Could not migrate legacy plugin data: " + exception.Message);
        }
    }

    private static string GetRuntimeFile(string fileName) =>
        Path.Combine(Paths.PluginPath, "BazaarLab", "runtime", fileName);

    private static string GetCatalogFile() =>
        Path.Combine(Paths.PluginPath, "BazaarLab", "data", "official-cards.jsonl");

    private void Awake()
    {
        _instance = this;
        _outputDirectory = Path.Combine(Paths.ConfigPath, "BazaarLab");
        MigrateLegacyOutputDirectory(_outputDirectory);
        Directory.CreateDirectory(_outputDirectory);
        InitializePlacementControls();
        InitializeMonsterCombatControls();
        InitializeBaselineCurveControls();
        InitializeEncounterPreviewControls();
        InitializeLineupDuelControls();
        InitializeFloatingWindowControls();

        Type processorType = AccessTools.TypeByName("TheBazaar.NetMessageProcessor")
            ?? throw new TypeLoadException("TheBazaar.NetMessageProcessor not found");
        MethodBase target = AccessTools.Method(processorType, "Receive",
                new[] { typeof(INetMessage), typeof(bool) })
            ?? AccessTools.Method(processorType, "ReceiveOrQueue", new[] { typeof(INetMessage) })
            ?? throw new MissingMethodException(processorType.FullName, "Receive/ReceiveOrQueue");
        MethodInfo postfix = AccessTools.Method(typeof(Plugin), nameof(ObserveMessage));
        _harmony = new Harmony(PluginGuid);
        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        InitializeDecisionTrace(_harmony);
        WriteStatus("ready", null, null);
        Logger.LogInfo($"capture bridge ready: {_outputDirectory}");
    }

    private void OnDestroy()
    {
        DisposePlacementControls();
        DisposeMonsterCombatControls();
        DisposeBaselineCurveControls();
        DisposeEncounterPreviewControls();
        DisposeLineupDuelControls();
        DisposeFloatingWindowControls();
        DisposeDecisionTrace();
        _harmony?.UnpatchSelf();
        _harmony = null;
        _instance = null;
    }

    private void Update()
    {
        UpdateLineupDuelControls();
        UpdateDecisionTrace();
        UpdatePlacementControls();
        UpdateMonsterCombatControls();
        UpdateEncounterPreviewControls();
        UpdateBaselineCurveControls();
        DateTime now = DateTime.UtcNow;
        if (now < _nextLiveInventoryCaptureUtc)
        {
            return;
        }
        _nextLiveInventoryCaptureUtc = now.AddSeconds(1);
        try
        {
            CaptureLiveInventory(now);
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"live inventory capture skipped: {exception.GetType().Name}: " +
                exception.Message);
        }
    }

    private void OnGUI()
    {
        DrawPlacementControls();
        DrawMonsterCombatControls();
        DrawBaselineCurveControls();
        DrawEncounterPreviewControls();
        DrawLineupDuelControls();
    }

    private void CaptureLiveInventory(DateTime recordedAtUtc)
    {
        Player? player = Data.Run?.Player;
        if (player is null)
        {
            return;
        }
        Player? opponent = Data.Run?.Opponent;
        Dictionary<string, int> playerAttributes = NormalizePreCombatHealth(
            ConvertAttributes(player.Attributes));
        Dictionary<string, int> opponentAttributes = NormalizePreCombatHealth(
            ConvertAttributes(opponent?.Attributes));
        object playerHand = ConvertLiveSet(
            "PlayerHand", 0, "Hand", player.Hand.GetItemsAsEnumerable().OfType<Card>());
        object playerStash = ConvertLiveSet(
            "PlayerStash", 0, "Stash", player.Stash.GetItemsAsEnumerable().OfType<Card>());
        object playerSkills = ConvertLiveSet(
            "PlayerSkills", 0, "Skills", player.Skills.Cast<Card>());
        object opponentHand = ConvertLiveSet(
            "OpponentHand", 1, "Hand",
            opponent?.Hand.GetItemsAsEnumerable().OfType<Card>() ?? Enumerable.Empty<Card>());
        object opponentSkills = ConvertLiveSet(
            "OpponentSkills", 1, "Skills",
            opponent?.Skills.Cast<Card>() ?? Enumerable.Empty<Card>());
        var document = new
        {
            schema = "bazaarlab-combat-snapshot-v1",
            capture = new
            {
                plugin_version = PluginVersion,
                source = "live-inventory",
                bpp_version = typeof(PvpBattleSnapshots).Assembly.GetName().Version?.ToString(),
                game_runtime_version = typeof(Data).Assembly.GetName().Version?.ToString(),
            },
            battle = new
            {
                id = "live-inventory",
                combat_kind = Data.CurrentEncounterId.HasValue
                    ? "pve"
                    : Data.SimPvpOpponent is not null ? "pvp" : "unknown",
                encounter_id = Data.CurrentEncounterId?.ToString("D"),
                recorded_at_utc = recordedAtUtc.ToString("O"),
                day = Data.Run?.Day ?? 0,
                hour = Data.Run?.Hour ?? 0,
                result = (string?)null,
                player_hero = player.Hero.ToString(),
                opponent_hero = opponent?.Hero.ToString() ?? string.Empty,
            },
            input_quality = new
            {
                prediction_ready = opponent is not null &&
                    opponent.Hand.GetItemsAsEnumerable().Any(),
                errors = Array.Empty<string>(),
                warnings = opponent is null || !opponent.Hand.GetItemsAsEnumerable().Any()
                    ? new[] { "opponent board is unavailable; placement uses the neutral-target objective" }
                    : Array.Empty<string>(),
            },
            combatants = new object[]
            {
                new { id = "player", hero = player.Hero.ToString(),
                    attributes = playerAttributes },
                new { id = "opponent", hero = opponent?.Hero.ToString() ?? string.Empty,
                    attributes = opponentAttributes },
            },
            card_sets = new[]
            {
                playerHand, playerStash, playerSkills, opponentHand, opponentSkills,
            },
        };
        string comparisonPayload = JsonSerializer.Serialize(new
        {
            encounter_id = Data.CurrentEncounterId?.ToString("D"),
            day = Data.Run?.Day ?? 0,
            hour = Data.Run?.Hour ?? 0,
            document.combatants,
            document.card_sets,
        });
        if (string.Equals(comparisonPayload, _lastLiveInventoryPayload, StringComparison.Ordinal))
        {
            return;
        }
        _lastLiveInventoryPayload = comparisonPayload;
        string json = JsonSerializer.Serialize(document,
            new JsonSerializerOptions { WriteIndented = true });
        PublishAtomic("live-inventory.json", json);
    }

    private static void ObserveMessage(object[] __args)
    {
        if (_instance is null || __args.Length == 0)
        {
            return;
        }
        try
        {
            if (__args[0] is NetMessageGameSim opening)
            {
                _instance.TryCapture(opening);
            }
            else if (__args[0] is NetMessageCombatSim actual)
            {
                _instance.TryCaptureActual(actual);
            }
        }
        catch (Exception exception)
        {
            _instance.Logger.LogError($"opening snapshot capture failed: {exception}");
        }
    }

    private void TryCapture(NetMessageGameSim message)
    {
        Assembly assembly = typeof(PvpBattleSnapshots).Assembly;
        Type collectorType = assembly.GetType(
            "BazaarPlusPlus.Game.PvpBattles.PvpBattleSnapshotCollector", throwOnError: true)!;
        object collector = Activator.CreateInstance(collectorType, nonPublic: true)!;
        MethodInfo create = collectorType.GetMethod("CreateOpeningCandidate")!;
        object candidate = create.Invoke(collector, new object?[] { message, null })!;
        MethodInfo build = collectorType.GetMethod("BuildSnapshots")!;
        var snapshots = (PvpBattleSnapshots)build.Invoke(collector, new[] { candidate })!;
        if (snapshots.OpponentHand.Items.Count == 0)
        {
            return;
        }

        string playerHero = ReadString(candidate, "PlayerHero") ?? SafeHero(Data.Run?.Player);
        string opponentHero = ReadString(candidate, "OpponentHero") ?? SafeHero(Data.Run?.Opponent);
        Dictionary<string, int> playerAttributes =
            MergeAttributes(message.Data.Player.Attributes, Data.Run?.Player);
        Dictionary<string, int> opponentAttributes =
            MergeAttributes(message.Data.Opponent.Attributes, null);
        string captureId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + message.MessageId;
        try
        {
            CaptureOpeningLineups(snapshots, playerHero, opponentHero,
                playerAttributes, opponentAttributes, message.Data.Run.Day, message.Data.Run.Hour,
                message.MessageId, captureId);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("PvP lineup-code capture skipped without aborting opening snapshot: " +
                exception.Message);
        }
        var inputErrors = new List<string>();
        var inputWarnings = new List<string>();
        ValidateCombatantAttributes(
            "player", playerHero, playerAttributes, inputErrors, inputWarnings);
        ValidateCombatantAttributes(
            "opponent", opponentHero, opponentAttributes, inputErrors, inputWarnings);
        var document = new
        {
            schema = "bazaarlab-combat-snapshot-v1",
            capture = new
            {
                plugin_version = PluginVersion,
                bpp_version = typeof(PvpBattleSnapshots).Assembly.GetName().Version?.ToString(),
                game_runtime_version = typeof(Data).Assembly.GetName().Version?.ToString(),
                opening_message_id = message.MessageId,
            },
            battle = new
            {
                id = captureId,
                recorded_at_utc = DateTime.UtcNow.ToString("O"),
                day = message.Data.Run.Day,
                hour = message.Data.Run.Hour,
                result = (string?)null,
                player_hero = playerHero,
                opponent_hero = opponentHero,
            },
            input_quality = new
            {
                prediction_ready = inputErrors.Count == 0,
                errors = inputErrors,
                warnings = inputWarnings,
            },
            input_warnings = inputErrors.Concat(inputWarnings).ToArray(),
            combatants = new object[]
            {
                new { id = "player", hero = playerHero,
                    attributes = playerAttributes },
                new { id = "opponent", hero = opponentHero,
                    attributes = opponentAttributes },
            },
            card_sets = new object[]
            {
                ConvertSet("PlayerHand", 0, "Hand", snapshots.PlayerHand),
                ConvertSet("PlayerSkills", 0, "Skills", snapshots.PlayerSkills),
                ConvertSet("OpponentHand", 1, "Hand", snapshots.OpponentHand),
                ConvertSet("OpponentSkills", 1, "Skills", snapshots.OpponentSkills),
            },
        };
        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        string archivePath = Path.Combine(_outputDirectory, captureId + ".json");
        File.WriteAllText(archivePath, json + Environment.NewLine);
        _captureIdsByMessageId[message.MessageId] = captureId;
        _latestCaptureId = captureId;
        PublishLatest(json);
        WriteStatus("captured", captureId, archivePath);
        foreach (string warning in inputWarnings)
        {
            Logger.LogWarning($"capture input warning: {warning}");
        }
        foreach (string error in inputErrors)
        {
            Logger.LogError($"capture input error: {error}");
        }
        Logger.LogInfo($"captured PvP opening snapshot: {archivePath}");
    }

    private void TryCaptureActual(NetMessageCombatSim message)
    {
        string captureId = _captureIdsByMessageId.TryGetValue(message.MessageId, out string? paired)
            ? paired
            : _latestCaptureId ??
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + message.MessageId;
        _captureIdsByMessageId.Remove(message.MessageId);
        if (string.Equals(_latestCaptureId, captureId, StringComparison.Ordinal))
        {
            _latestCaptureId = null;
        }
        var playerAttributes = new List<object>();
        var opponentAttributes = new List<object>();
        var healthChanges = new List<object>();
        var cardAttributes = new List<object>();
        var effects = new List<object>();
        for (int frameIndex = 0; frameIndex < message.Data.Frames.Count; frameIndex++)
        {
            CombatSimFrame frame = message.Data.Frames[frameIndex];
            AppendPlayerUpdates(frameIndex, "player", frame.PlayerUpdates,
                playerAttributes, healthChanges);
            AppendPlayerUpdates(frameIndex, "opponent", frame.OpponentUpdates,
                opponentAttributes, healthChanges);
            foreach (KeyValuePair<BazaarGameShared.Domain.Core.InstanceId,
                CombatSimCardUpdate> pair in frame.CardUpdates)
            {
                foreach (CombatSimCardAttributeUpdate update in pair.Value.Attributes.Values)
                {
                    cardAttributes.Add(new
                    {
                        frame = frameIndex,
                        card_id = pair.Key.Value,
                        attribute = update.AttributeType.ToString(),
                        previous = update.PreviousValue,
                        current = update.CurrentValue,
                    });
                }
            }
            foreach (CombatSimEventEffectExecuted effect in
                frame.Events.OfType<CombatSimEventEffectExecuted>())
            {
                effects.Add(new
                {
                    frame = frameIndex,
                    source = effect.Source?.Value,
                    trigger_source = effect.TriggerSource?.Value,
                    effect_id = effect.EffectId,
                    action_type = effect.ActionType.ToString(),
                    execution_context_id = effect.ExecutionContextId,
                });
            }
        }
        var document = new
        {
            schema = "bazaarlab-combat-actual-v1",
            capture_plugin_version = PluginVersion,
            bpp_version = typeof(PvpBattleSnapshots).Assembly.GetName().Version?.ToString(),
            game_runtime_version = typeof(Data).Assembly.GetName().Version?.ToString(),
            capture_id = captureId,
            message_id = message.MessageId,
            recorded_at_utc = DateTime.UtcNow.ToString("O"),
            winner = message.Data.Winner.ToString(),
            loser = message.Data.Loser.ToString(),
            frame_count = message.Data.Frames.Count,
            player_attribute_changes = playerAttributes,
            opponent_attribute_changes = opponentAttributes,
            health_changes = healthChanges,
            card_attribute_changes = cardAttributes,
            effects,
        };
        string actualPath = Path.Combine(_outputDirectory, captureId + ".actual.json");
        File.WriteAllText(actualPath, JsonSerializer.Serialize(document,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        WriteStatus("actual-captured", captureId, actualPath);
        Logger.LogInfo($"captured official combat result: {actualPath}");
    }

    private static void AppendPlayerUpdates(
        int frame,
        string side,
        CombatSimPlayerUpdate? update,
        ICollection<object> attributes,
        ICollection<object> healthChanges)
    {
        if (update is null)
        {
            return;
        }
        foreach (CombatSimPlayerAttributeUpdate attribute in update.Attributes.Values)
        {
            attributes.Add(new
            {
                frame,
                side,
                attribute = attribute.AttributeType.ToString(),
                previous = attribute.PreviousValue,
                current = attribute.CurrentValue,
            });
        }
        foreach (CombatSimPlayerHealthAdjustment health in update.HealthAdjustments)
        {
            healthChanges.Add(new
            {
                frame,
                side,
                damage_type = health.DamageType.ToString(),
                attribute = health.AttributeChanged.ToString(),
                amount = health.Amount,
                is_crit = health.IsCrit,
                is_damage_reduced = health.IsDamageReduced,
            });
        }
    }

    private void WriteStatus(string state, string? captureId, string? capturePath)
    {
        var status = new
        {
            state,
            plugin_version = PluginVersion,
            updated_at_utc = DateTime.UtcNow.ToString("O"),
            capture_id = captureId,
            capture_path = capturePath,
            bpp_version = typeof(PvpBattleSnapshots).Assembly.GetName().Version?.ToString(),
            game_runtime_version = typeof(Data).Assembly.GetName().Version?.ToString(),
        };
        File.WriteAllText(
            Path.Combine(_outputDirectory, "status.json"),
            JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine);
    }

    private void PublishLatest(string json)
    {
        PublishAtomic("latest.json", json);
    }

    private void PublishAtomic(string fileName, string json)
    {
        string latestPath = Path.Combine(_outputDirectory, fileName);
        string temporaryPath = latestPath + ".tmp";
        File.WriteAllText(temporaryPath, json + Environment.NewLine);
        if (File.Exists(latestPath))
        {
            File.Replace(temporaryPath, latestPath, null);
        }
        else
        {
            File.Move(temporaryPath, latestPath);
        }
    }

    private static object ConvertLiveSet(
        string label,
        int owner,
        string section,
        IEnumerable<Card> cards) => new
    {
        label,
        owner,
        section,
        status = "Captured",
        source = "LiveInventory",
        items = cards.OrderBy(card => card.LeftSocketId.HasValue
                ? (int)card.LeftSocketId.Value : int.MaxValue)
            .ThenBy(card => card.InstanceId.Value, StringComparer.Ordinal)
            .Select(card => new
            {
                instance_id = card.InstanceId.Value,
                template_id = card.TemplateId,
                type = card.Type.ToString(),
                size = card.Size.ToString(),
                section,
                socket = card.LeftSocketId?.ToString(),
                name = card.Name,
                tier = card.Tier.ToString(),
                enchant = (card as ItemCard)?.Enchantment?.ToString() ?? string.Empty,
                tags = card.Tags.Select(tag => tag.ToString()).ToArray(),
                attributes = ConvertAttributes(card.Attributes),
            }).ToArray(),
    };

    private static Dictionary<string, int> ConvertAttributes<T>(
        IReadOnlyDictionary<T, int>? source) where T : notnull
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (source is null)
        {
            return result;
        }
        foreach (KeyValuePair<T, int> pair in source)
        {
            result[pair.Key.ToString() ?? string.Empty] = pair.Value;
        }
        return result;
    }

    private static Dictionary<string, int> NormalizePreCombatHealth(
        Dictionary<string, int> attributes)
    {
        if ((!attributes.TryGetValue("Health", out int health) || health <= 0) &&
            attributes.TryGetValue("HealthMax", out int healthMax) && healthMax > 0)
        {
            attributes["Health"] = healthMax;
        }
        return attributes;
    }

    private static object ConvertSet(
        string label, int owner, string section, PvpBattleCardSetCapture set) => new
    {
        label,
        owner,
        section,
        status = set.Status.ToString(),
        source = set.Source.ToString(),
        items = set.Items.Select(item => new
        {
            instance_id = item.InstanceId,
            template_id = item.TemplateId,
            type = item.Type.ToString(),
            size = item.Size.ToString(),
            section = item.Section?.ToString(),
            socket = item.Socket?.ToString(),
            name = item.Name,
            tier = item.Tier,
            enchant = item.Enchant,
            tags = item.Tags,
            attributes = item.Attributes,
        }).ToArray(),
    };

    private static Dictionary<string, int> MergeAttributes(
        IReadOnlyDictionary<EPlayerAttributeType, int>? messageAttributes, Player? livePlayer)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (livePlayer?.Attributes is not null)
        {
            foreach (KeyValuePair<EPlayerAttributeType, int> pair in livePlayer.Attributes)
            {
                result[pair.Key.ToString()] = pair.Value;
            }
        }
        if (messageAttributes is not null)
        {
            foreach (KeyValuePair<EPlayerAttributeType, int> pair in messageAttributes)
            {
                result[pair.Key.ToString()] = pair.Value;
            }
        }
        return result;
    }

    private static void ValidateCombatantAttributes(
        string id,
        string hero,
        IReadOnlyDictionary<string, int> attributes,
        ICollection<string> errors,
        ICollection<string> warnings)
    {
        if (!attributes.TryGetValue("Health", out int health) || health <= 0)
        {
            errors.Add($"{id}: Health is missing or invalid");
        }
        if (!attributes.TryGetValue("HealthMax", out int healthMax) || healthMax <= 0)
        {
            errors.Add($"{id}: HealthMax is missing or invalid");
        }
        else if (health > healthMax)
        {
            errors.Add($"{id}: Health exceeds HealthMax");
        }
        if (string.Equals(hero, "Hero8", StringComparison.Ordinal) &&
            !attributes.ContainsKey("TempoGainCooldownRemaining") &&
            !attributes.ContainsKey("TempoCooldownRemaining"))
        {
            warnings.Add($"{id}: opening Tempo cooldown remainder is unavailable; " +
                "Monte Carlo will marginalize its first-period phase");
        }
    }

    private static string? ReadString(object instance, string propertyName) =>
        instance.GetType().GetProperty(propertyName)?.GetValue(instance) as string;

    private static string SafeHero(Player? player) => player?.Hero.ToString() ?? string.Empty;
}
