using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Cards.Interfaces;
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Cards.PlayerEffects;
using BazaarGameShared.Domain.Cards.Skill;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Players;
using BazaarPlusPlus.Game.PvpBattles;
using BepInEx;
using TheBazaar;
using UnityEngine;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private sealed class EncounterPreviewEntry
    {
        public string InstanceId { get; set; } = string.Empty;
        public Guid TemplateId { get; set; }
        public EncounterController Controller { get; set; } = null!;
        public TMonster Monster { get; set; } = null!;
        public MonsterPredictionDto? Result { get; set; }
        public string Status { get; set; } = "Queued";
    }

    private readonly Dictionary<string, EncounterPreviewEntry> _encounterPreviews =
        new Dictionary<string, EncounterPreviewEntry>(StringComparer.Ordinal);
    private readonly Queue<string> _encounterPreviewQueue = new Queue<string>();
    private Process? _encounterPreviewProcess;
    private StringBuilder? _encounterPreviewLog;
    private string? _encounterPreviewRunningId;
    private string? _encounterPreviewRunningFingerprint;
    private string? _encounterPreviewInputPath;
    private string? _encounterPreviewResultPath;
    private string? _encounterPreviewCandidateFingerprint;
    private string? _encounterPreviewAppliedFingerprint;
    private float _encounterPreviewChangedAt;
    private float _nextEncounterPreviewProbeAt;
    private bool _encounterPreviewCombatSuppressed;
    private readonly Dictionary<Guid, MonsterEncounterIdentity> _knownMonsterEncounters =
        new Dictionary<Guid, MonsterEncounterIdentity>();
    private MonsterCalibrationStore _monsterCalibrations = new MonsterCalibrationStore();

    private sealed class MonsterEncounterIdentity
    {
        public Guid EncounterId { get; set; }
        public Guid MonsterId { get; set; }
        public TMonster Monster { get; set; } = null!;
    }

    private sealed class MonsterCardPayload
    {
        public string instance_id { get; set; } = string.Empty;
        public string template_id { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public string size { get; set; } = string.Empty;
        public string section { get; set; } = string.Empty;
        public string? socket { get; set; }
        public string name { get; set; } = string.Empty;
        public string tier { get; set; } = string.Empty;
        public string enchant { get; set; } = string.Empty;
        public string[] tags { get; set; } = Array.Empty<string>();
        public Dictionary<string, int> attributes { get; set; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private sealed class MonsterCalibrationStore
    {
        public string Schema { get; set; } = "bazaarlab-monster-calibrations-v1";
        public string DataFingerprint { get; set; } = string.Empty;
        public Dictionary<string, MonsterCalibrationRecord> Monsters { get; set; } =
            new Dictionary<string, MonsterCalibrationRecord>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class MonsterCalibrationRecord
    {
        public string EncounterId { get; set; } = string.Empty;
        public string MonsterId { get; set; } = string.Empty;
        public uint Day { get; set; }
        public uint Hour { get; set; }
        public string ObservedAtUtc { get; set; } = string.Empty;
        public Dictionary<string, int> OpponentAttributes { get; set; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public List<MonsterCalibrationCard> Cards { get; set; } =
            new List<MonsterCalibrationCard>();
    }

    private sealed class MonsterCalibrationCard
    {
        public string TemplateId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Section { get; set; } = string.Empty;
        public string? Socket { get; set; }
        public string Tier { get; set; } = string.Empty;
        public string Enchant { get; set; } = string.Empty;
        public string[] Tags { get; set; } = Array.Empty<string>();
        public Dictionary<string, int> Attributes { get; set; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private bool IsEncounterPreviewCalculating => _encounterPreviewProcess is not null ||
        _encounterPreviewQueue.Count > 0;

    private void InitializeEncounterPreviewControls()
    {
        _encounterPreviewChangedAt = Time.realtimeSinceStartup;
        LoadMonsterCalibrations();
    }

    private void DisposeEncounterPreviewControls()
    {
        if (_encounterPreviewProcess is not null)
        {
            try
            {
                if (!_encounterPreviewProcess.HasExited) _encounterPreviewProcess.Kill();
            }
            catch (Exception)
            {
                // Process may have exited between checks.
            }
            _encounterPreviewProcess.Dispose();
            _encounterPreviewProcess = null;
        }
        _encounterPreviewLog = null;
        _encounterPreviewRunningId = null;
        _encounterPreviewRunningFingerprint = null;
        FinishEncounterPreviewArtifacts(false, "plugin shutdown", null);
        _encounterPreviewQueue.Clear();
        _encounterPreviews.Clear();
    }

    private void UpdateEncounterPreviewControls()
    {
        if (IsCombatOrReplayActive())
        {
            if (!_encounterPreviewCombatSuppressed)
            {
                _encounterPreviewCombatSuppressed = true;
                CancelEncounterPreviewsForCombat();
            }
            return;
        }
        _encounterPreviewCombatSuppressed = false;
        PollEncounterPreview();
        if (Time.realtimeSinceStartup >= _nextEncounterPreviewProbeAt)
        {
            _nextEncounterPreviewProbeAt = Time.realtimeSinceStartup + 0.35f;
            ProbeEncounterChoices();
        }
        if (_encounterPreviewProcess is null && _encounterPreviewQueue.Count > 0 &&
            !IsMonsterCalculating && !IsBaselineCalculating && !IsSearching && !IsMoving &&
            !IsLocalDuelCalculating &&
            !Data.IsInCombat && !CardController.IsAnyCardDragging &&
            !AppState.IsWaitingForServerResponse)
        {
            StartNextEncounterPreview();
        }
    }

    private void CancelEncounterPreviewsForCombat()
    {
        Process? process = _encounterPreviewProcess;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception exception)
            {
                Logger.LogWarning("cannot stop encounter preview on combat entry: " +
                    exception.Message);
            }
            process.Dispose();
        }
        _encounterPreviewProcess = null;
        _encounterPreviewLog = null;
        _encounterPreviewRunningId = null;
        _encounterPreviewRunningFingerprint = null;
        FinishEncounterPreviewArtifacts(false, "preview cancelled", null);
        _encounterPreviewQueue.Clear();
        _encounterPreviews.Clear();
        _encounterPreviewAppliedFingerprint = null;
        _encounterPreviewCandidateFingerprint = null;
    }

    private void ProbeEncounterChoices()
    {
        EncounterController[] controllers = FindObjectsByType<EncounterController>(
                FindObjectsSortMode.None)
            .Where(controller => controller is not null && controller.gameObject.activeInHierarchy &&
                controller.CardData is CombatEncounterCard)
            .OrderBy(controller => controller.CardData.InstanceId.Value, StringComparer.Ordinal)
            .ToArray();
        var resolved = new List<EncounterPreviewEntry>();
        foreach (EncounterController controller in controllers)
        {
            var card = (CombatEncounterCard)controller.CardData;
            // PvP encounter cards also expose a placeholder through GetMonsterTemplate().
            // Only the game's concrete combat-monster choices use the com_ instance family.
            // Keep this as a whitelist so an unknown encounter type can never leak into the
            // automatic monster prediction UI.
            if (!IsStaticMonsterEncounter(card))
            {
                continue;
            }
            TMonster? monster;
            try
            {
                monster = card.GetMonsterTemplate();
            }
            catch (Exception)
            {
                continue;
            }
            if (monster?.Player?.Hand?.Items is null)
            {
                continue;
            }
            resolved.Add(new EncounterPreviewEntry
            {
                InstanceId = card.InstanceId.Value,
                TemplateId = card.TemplateId,
                Controller = controller,
                Monster = monster,
            });
            _knownMonsterEncounters[card.TemplateId] = new MonsterEncounterIdentity
            {
                EncounterId = card.TemplateId,
                MonsterId = monster.Id,
                Monster = monster,
            };
        }
        if (resolved.Count == 0 &&
            (_encounterPreviews.Count > 0 || IsEncounterPreviewCalculating))
        {
            // Remove a stale monster overlay immediately when the choice surface changes
            // to PvP, and do not let an already-started PvE calculation waste CPU.
            CancelEncounterPreviewsForCombat();
            return;
        }
        string? playerFingerprint = ComputePlayerBoardFingerprint();
        string fingerprint = (playerFingerprint ?? string.Empty) + "#" + string.Join("|",
            resolved.Select(entry => entry.InstanceId + ":" + entry.TemplateId.ToString("D") +
                ":" + entry.Monster.Id.ToString("D")));
        if (string.Equals(fingerprint, _encounterPreviewCandidateFingerprint,
                StringComparison.Ordinal))
        {
            foreach (EncounterPreviewEntry entry in resolved)
            {
                if (_encounterPreviews.TryGetValue(entry.InstanceId, out EncounterPreviewEntry? old))
                    old.Controller = entry.Controller;
            }
            if (!string.Equals(fingerprint, _encounterPreviewAppliedFingerprint,
                    StringComparison.Ordinal) && resolved.Count > 0 &&
                Time.realtimeSinceStartup - _encounterPreviewChangedAt >= 0.75f)
            {
                ApplyEncounterChoiceSet(resolved, fingerprint);
            }
            return;
        }
        _encounterPreviewCandidateFingerprint = fingerprint;
        _encounterPreviewChangedAt = Time.realtimeSinceStartup;
        if (resolved.Count == 0)
        {
            _encounterPreviewQueue.Clear();
            _encounterPreviews.Clear();
            _encounterPreviewAppliedFingerprint = null;
            return;
        }
        if (Time.realtimeSinceStartup - _encounterPreviewChangedAt < 0.75f)
        {
            // The next probe will confirm that the choice set has settled.
            return;
        }
        ApplyEncounterChoiceSet(resolved, fingerprint);
    }

    private static bool IsStaticMonsterEncounter(CombatEncounterCard card) =>
        card.InstanceId.Value.StartsWith("com_", StringComparison.OrdinalIgnoreCase);

    private void ApplyEncounterChoiceSet(IReadOnlyList<EncounterPreviewEntry> resolved,
        string fingerprint)
    {
        if (string.Equals(fingerprint, _encounterPreviewAppliedFingerprint,
                StringComparison.Ordinal))
            return;
        _encounterPreviewAppliedFingerprint = fingerprint;
        _encounterPreviewQueue.Clear();
        _encounterPreviews.Clear();
        foreach (EncounterPreviewEntry entry in resolved)
        {
            _encounterPreviews[entry.InstanceId] = entry;
            _encounterPreviewQueue.Enqueue(entry.InstanceId);
        }
    }

    private void StartNextEncounterPreview()
    {
        if (!CanUseCatalog(out string catalogReason))
        {
            foreach (EncounterPreviewEntry pending in _encounterPreviews.Values)
            {
                if (pending.Result is null) pending.Status = catalogReason;
            }
            return;
        }
        while (_encounterPreviewQueue.Count > 0)
        {
            string id = _encounterPreviewQueue.Dequeue();
            if (!_encounterPreviews.TryGetValue(id, out EncounterPreviewEntry? entry)) continue;
            try
            {
                string gameRoot = Paths.GameRootPath;
                string core = GetRuntimeFile("BazaarLab.Combat.dll");
                string catalog = GetCatalogFile();
                if (!File.Exists(core) || !File.Exists(catalog))
                {
                    entry.Status = "Runtime missing";
                    continue;
                }
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                _encounterPreviewInputPath = TemporaryArtifactFile("encounter-preview",
                    "encounter-preview-input-" + stamp + "-" + SafeFileId(id) + ".json");
                _encounterPreviewResultPath = TemporaryArtifactFile("encounter-preview",
                    "encounter-preview-result-" + stamp + "-" + SafeFileId(id) + ".json");
                File.WriteAllText(_encounterPreviewInputPath,
                    BuildEncounterPreviewJson(entry) + Environment.NewLine);
                string dotnetExecutable = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
                if (!File.Exists(dotnetExecutable)) dotnetExecutable = "dotnet";
                var output = new StringBuilder();
                var start = new ProcessStartInfo
                {
                    FileName = dotnetExecutable,
                    Arguments = Quote(core) + " predict-bpp " + Quote(catalog) + " " +
                        Quote(_encounterPreviewInputPath) + " 20260831 50 2400 " +
                        Quote(_encounterPreviewResultPath),
                    WorkingDirectory = Path.GetDirectoryName(core) ?? gameRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var process = new Process { StartInfo = start, EnableRaisingEvents = false };
                process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data is not null) lock (output) output.AppendLine(args.Data);
                };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data is not null) lock (output) output.AppendLine(args.Data);
                };
                if (!process.Start())
                {
                    process.Dispose();
                    entry.Status = "Start failed";
                    FinishEncounterPreviewArtifacts(true, entry.Status, null);
                    continue;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                entry.Status = "Calculating...";
                _encounterPreviewProcess = process;
                _encounterPreviewLog = output;
                _encounterPreviewRunningId = id;
                _encounterPreviewRunningFingerprint = _encounterPreviewAppliedFingerprint;
                return;
            }
            catch (Exception exception)
            {
                entry.Status = "Error: " + exception.Message;
                FinishEncounterPreviewArtifacts(true, entry.Status, exception.ToString());
            }
        }
    }

    private string BuildEncounterPreviewJson(EncounterPreviewEntry entry)
    {
        var player = Data.Run.Player;
        TPlayer monster = entry.Monster.Player;
        var materializationWarnings = new List<string>();
        MonsterCalibrationRecord? calibration = GetApplicableMonsterCalibration(entry);
        Dictionary<string, int> opponentAttributes = ConvertAttributes(monster.Attributes);
        if (calibration is not null)
        {
            opponentAttributes = new Dictionary<string, int>(
                calibration.OpponentAttributes, StringComparer.Ordinal);
        }
        if (!opponentAttributes.ContainsKey("Health") &&
            opponentAttributes.TryGetValue("HealthMax", out int opponentHealthMax))
        {
            opponentAttributes["Health"] = opponentHealthMax;
        }
        object playerHand = ConvertLiveSet("PlayerHand", 0, "Hand",
            player.Hand.GetItemsAsEnumerable().OfType<Card>());
        object playerSkills = ConvertLiveSet("PlayerSkills", 0, "Skills",
            player.Skills.Cast<Card>());
        object opponentHand = ConvertMonsterItems(entry, monster.Hand.Items,
            materializationWarnings, calibration);
        object opponentSkills = ConvertMonsterSkills(entry, monster.Skills, monster.Effects,
            materializationWarnings, calibration);
        var inputWarnings = new List<string>
        {
            "opponent is reconstructed with the game's tier materialization rules; result is an estimate"
        };
        inputWarnings.AddRange(materializationWarnings);
        if (calibration is not null)
        {
            inputWarnings.Add("opponent opening attributes were calibrated from a previous observed combat");
        }
        var document = new
        {
            schema = "bazaarlab-combat-snapshot-v1",
            capture = new
            {
                plugin_version = PluginVersion,
                source = calibration is null
                    ? "game-ui-tier-materialized-monster-preview"
                    : "observed-monster-calibration",
                monster_data_fingerprint = CurrentMonsterDataFingerprint(),
            },
            battle = new
            {
                id = "encounter-preview-" + entry.InstanceId,
                combat_kind = "pve-preview",
                encounter_id = entry.TemplateId.ToString("D"),
                monster_template_id = entry.Monster.Id.ToString("D"),
                recorded_at_utc = DateTime.UtcNow.ToString("O"),
                day = Data.Run.Day,
                hour = Data.Run.Hour,
                result = (string?)null,
                player_hero = player.Hero.ToString(),
                opponent_hero = "Common",
            },
            input_quality = new
            {
                prediction_ready = monster.Hand.Items.Count > 0 &&
                    opponentAttributes.TryGetValue("Health", out int health) && health > 0,
                errors = Array.Empty<string>(),
                warnings = inputWarnings.ToArray(),
            },
            input_warnings = inputWarnings.ToArray(),
            combatants = new object[]
            {
                new { id = "player", hero = player.Hero.ToString(),
                    attributes_precomputed = true,
                    attributes = ConvertAttributes(player.Attributes) },
                new { id = "opponent", hero = "Common",
                    attributes_precomputed = calibration is not null,
                    attributes = opponentAttributes },
            },
            card_sets = new[] { playerHand, playerSkills, opponentHand, opponentSkills },
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object ConvertMonsterItems(EncounterPreviewEntry entry,
        IEnumerable<TCardInstanceItem> items, ICollection<string> warnings,
        MonsterCalibrationRecord? calibration) => new
    {
        label = "OpponentHand",
        owner = 1,
        section = "Hand",
        status = "Captured",
        source = calibration is null ? "GameUiTierMaterialized" : "ObservedMonsterOpening",
        items = items.Select((item, index) => ConvertMonsterCard(entry, item,
            "Item", "Hand", item.SocketId?.ToString(), item.EnchantmentType?.ToString(), index,
            warnings, calibration))
            .ToArray(),
    };

    private static object ConvertMonsterSkills(EncounterPreviewEntry entry,
        IEnumerable<TCardInstanceSkill> skills,
        IEnumerable<TCardInstancePlayerEffect> effects, ICollection<string> warnings,
        MonsterCalibrationRecord? calibration) => new
    {
        label = "OpponentSkills",
        owner = 1,
        section = "Skills",
        status = "Captured",
        source = calibration is null ? "GameUiTierMaterialized" : "ObservedMonsterOpening",
        items = skills.Select((skill, index) => ConvertMonsterCard(entry, skill,
                "Skill", "Skills", null, null, index, warnings, calibration))
            .Concat(effects.Select((effect, index) => ConvertMonsterCard(entry, effect,
                "PlayerEffect", "Skills", null, null, 1000 + index, warnings, calibration))).ToArray(),
    };

    private static MonsterCardPayload ConvertMonsterCard(EncounterPreviewEntry entry,
        TCardInstance card, string type, string section, string? socket, string? enchant, int index,
        ICollection<string> warnings, MonsterCalibrationRecord? calibration)
    {
        Guid templateId = card.TemplateId;
        ITCard? template = Data.GetStatic().GetCardById(templateId);
        string size = ReadProperty(template, "Size")?.ToString() ?? "Small";
        string name = ReadProperty(template, "InternalName")?.ToString() ?? string.Empty;
        string tier = ResolveMonsterTier(card.Tier, template);
        Dictionary<string, int> attributes = MaterializeMonsterAttributes(
            card, template, tier, enchant, name, warnings);
        MonsterCalibrationCard? observed = calibration?.Cards.FirstOrDefault(candidate =>
            string.Equals(candidate.TemplateId, templateId.ToString("D"),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Section, section, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Socket ?? string.Empty, socket ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
        if (observed is not null)
        {
            attributes = new Dictionary<string, int>(observed.Attributes, StringComparer.Ordinal);
            tier = observed.Tier;
            enchant = observed.Enchant;
        }
        return new MonsterCardPayload
        {
            instance_id = "monster:" + entry.Monster.Id.ToString("D") + ":" + type + ":" + index,
            template_id = templateId.ToString("D"),
            type = type,
            size = size,
            section = section,
            socket = socket,
            name = name,
            tier = tier,
            enchant = enchant ?? string.Empty,
            tags = observed?.Tags ??
                ConvertTags(ReadProperty(card, "Tags") ?? ReadProperty(template, "Tags")),
            attributes = attributes,
        };
    }

    private static string ResolveMonsterTier(object? instanceTier, ITCard? template)
    {
        string requested = instanceTier?.ToString() ?? "Bronze";
        string minimum = ReadProperty(template, "StartingTier")?.ToString() ?? requested;
        return TierRank(requested) < TierRank(minimum) ? minimum : requested;
    }

    private static int TierRank(string tier) => tier switch
    {
        "Bronze" => 0,
        "Silver" => 1,
        "Gold" => 2,
        "Diamond" or "Legendary" => 3,
        _ => -1,
    };

    private static Dictionary<string, int> MaterializeMonsterAttributes(
        TCardInstance instance, ITCard? template, string resolvedTier, string? enchant,
        string name, ICollection<string> warnings)
    {
        Dictionary<string, int> instanceAttributes = ConvertAttributeObject(instance.Attributes);
        if (template is null || !Enum.TryParse(resolvedTier, out ETier tier))
        {
            warnings.Add("monster card could not be tier-materialized: " + name);
            return instanceAttributes;
        }
        try
        {
            Card preview = DTOUtils.CreateCard(template.Id.ToString(), template.Type);
            preview.Attributes = new Dictionary<ECardAttributeType, int>();
            foreach (KeyValuePair<string, int> pair in instanceAttributes)
            {
                if (Enum.TryParse(pair.Key, out ECardAttributeType attribute))
                    preview.Attributes[attribute] = pair.Value;
            }
            if (template is IHasTierData tiered)
            {
                foreach (KeyValuePair<ETier, TCardTier> tierData in tiered.Tiers)
                {
                    if (tierData.Key > template.StartingTier) break;
                    foreach (KeyValuePair<ECardAttributeType, int> pair in tierData.Value.Attributes)
                        preview.Attributes[pair.Key] = pair.Value;
                }
            }
            preview.Tier = tier;
            Dictionary<ECardAttributeType, int> materialized =
                TheBazaar.CardExtensions.BuildAttributeDictionaryForTier(preview, template, tier);
            if (preview is ItemCard previewItem && template is TCardItem itemTemplate &&
                Enum.TryParse(enchant, out EEnchantmentType enchantment))
            {
                TheBazaar.CardExtensions.ApplyPreviewEnchantment(
                    itemTemplate, previewItem, materialized, enchantment);
            }
            var result = materialized.ToDictionary(
                pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> pair in result)
            {
                if (instanceAttributes.TryGetValue(pair.Key, out int oldValue) &&
                    oldValue != pair.Value)
                {
                    warnings.Add("tier materialization corrected " + name + "." + pair.Key +
                        ": " + oldValue + " -> " + pair.Value);
                }
            }
            return result;
        }
        catch (Exception exception)
        {
            warnings.Add("monster card tier materialization failed for " + name + ": " +
                exception.GetType().Name);
            return instanceAttributes;
        }
    }

    private static Dictionary<string, int> ConvertAttributeObject(object? attributes)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (attributes is System.Collections.IDictionary dictionary)
        {
            foreach (System.Collections.DictionaryEntry pair in dictionary)
            {
                result[pair.Key?.ToString() ?? string.Empty] = Convert.ToInt32(pair.Value);
            }
        }
        else if (attributes is System.Collections.IEnumerable enumerable)
        {
            foreach (object? pair in enumerable)
            {
                object? key = ReadProperty(pair, "Key");
                object? value = ReadProperty(pair, "Value");
                if (key is not null && value is not null)
                    result[key.ToString() ?? string.Empty] = Convert.ToInt32(value);
            }
        }
        return result;
    }

    private string MonsterCalibrationPath() =>
        StateFile("monster-calibrations.json");

    private string CurrentMonsterDataFingerprint()
    {
        string runtime = typeof(Data).Assembly.GetName().Version?.ToString() ?? "unknown";
        return runtime + ":" + GetCatalogFingerprint();
    }

    private void LoadMonsterCalibrations()
    {
        string fingerprint = CurrentMonsterDataFingerprint();
        try
        {
            string path = MonsterCalibrationPath();
            if (File.Exists(path))
            {
                MonsterCalibrationStore? loaded = JsonSerializer.Deserialize<MonsterCalibrationStore>(
                    File.ReadAllText(path), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                if (loaded is not null &&
                    string.Equals(loaded.DataFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    _monsterCalibrations = loaded;
                    return;
                }
                Logger.LogInfo("monster calibrations ignored because game/card data changed");
            }
        }
        catch (Exception exception)
        {
            Logger.LogWarning("monster calibrations could not be loaded: " + exception.Message);
        }
        _monsterCalibrations = new MonsterCalibrationStore { DataFingerprint = fingerprint };
    }

    private void SaveMonsterCalibrations()
    {
        _monsterCalibrations.DataFingerprint = CurrentMonsterDataFingerprint();
        PublishAtomic("monster-calibrations.json", JsonSerializer.Serialize(
            _monsterCalibrations, new JsonSerializerOptions { WriteIndented = true }));
    }

    private MonsterCalibrationRecord? GetApplicableMonsterCalibration(
        EncounterPreviewEntry entry)
    {
        if (!_monsterCalibrations.Monsters.TryGetValue(entry.Monster.Id.ToString("D"),
                out MonsterCalibrationRecord? calibration))
            return null;
        return MonsterLineupMatches(entry.Monster, calibration.Cards) ? calibration : null;
    }

    private static bool MonsterLineupMatches(TMonster monster,
        IEnumerable<MonsterCalibrationCard> observed)
    {
        string[] expected = monster.Player.Hand.Items.Cast<TCardInstance>()
            .Concat(monster.Player.Skills.Cast<TCardInstance>())
            .Concat(monster.Player.Effects.Cast<TCardInstance>())
            .Select(card => card.TemplateId.ToString("D") + "|" +
                ResolveMonsterTier(card.Tier, Data.GetStatic().GetCardById(card.TemplateId)))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] actual = observed.Select(card => card.TemplateId + "|" + card.Tier)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase);
    }

    private void TryRecordMonsterOpeningCalibration(PvpBattleSnapshots snapshots,
        IReadOnlyDictionary<string, int> opponentAttributes, uint day, uint hour)
    {
        var cards = new List<MonsterCalibrationCard>();
        AddObservedCards(cards, snapshots.OpponentHand, "Hand");
        AddObservedCards(cards, snapshots.OpponentSkills, "Skills");
        if (cards.Count == 0) return;
        MonsterEncounterIdentity? identity = null;
        Guid? encounterId = Data.CurrentEncounterId;
        if (encounterId.HasValue)
            _knownMonsterEncounters.TryGetValue(encounterId.Value, out identity);
        if (identity is null || !MonsterLineupMatches(identity.Monster, cards))
        {
            identity = _knownMonsterEncounters.Values.FirstOrDefault(candidate =>
                MonsterLineupMatches(candidate.Monster, cards));
        }
        if (identity is null)
        {
            Logger.LogWarning("monster opening did not match the selected static lineup; " +
                "calibration was not saved");
            return;
        }
        var record = new MonsterCalibrationRecord
        {
            EncounterId = identity.EncounterId.ToString("D"),
            MonsterId = identity.MonsterId.ToString("D"),
            Day = day,
            Hour = hour,
            ObservedAtUtc = DateTime.UtcNow.ToString("O"),
            OpponentAttributes = new Dictionary<string, int>(
                opponentAttributes, StringComparer.Ordinal),
            Cards = cards,
        };
        _monsterCalibrations.Monsters[record.MonsterId] = record;
        SaveMonsterCalibrations();
        Logger.LogInfo("saved observed monster opening calibration: " + record.MonsterId);
    }

    private static void AddObservedCards(ICollection<MonsterCalibrationCard> target,
        PvpBattleCardSetCapture set, string fallbackSection)
    {
        foreach (PvpBattleCardSnapshot card in set.Items)
        {
            target.Add(new MonsterCalibrationCard
            {
                TemplateId = card.TemplateId,
                Type = card.Type.ToString(),
                Section = card.Section?.ToString() ?? fallbackSection,
                Socket = card.Socket?.ToString(),
                Tier = card.Tier ?? "Bronze",
                Enchant = card.Enchant ?? string.Empty,
                Tags = card.Tags.ToArray(),
                Attributes = new Dictionary<string, int>(card.Attributes, StringComparer.Ordinal),
            });
        }
    }

    private static string[] ConvertTags(object? tags) => tags is System.Collections.IEnumerable values
        ? values.Cast<object?>().Where(value => value is not null)
            .Select(value => value!.ToString() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray()
        : Array.Empty<string>();

    private static object? ReadProperty(object? instance, string propertyName) =>
        instance?.GetType().GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private void PollEncounterPreview()
    {
        Process? process = _encounterPreviewProcess;
        if (process is null || !process.HasExited) return;
        string? id = _encounterPreviewRunningId;
        string? runningFingerprint = _encounterPreviewRunningFingerprint;
        try
        {
            process.WaitForExit();
            int exitCode = process.ExitCode;
            string log = string.Empty;
            if (_encounterPreviewLog is not null)
            {
                lock (_encounterPreviewLog) log = _encounterPreviewLog.ToString();
            }
            process.Dispose();
            _encounterPreviewProcess = null;
            _encounterPreviewLog = null;
            _encounterPreviewRunningId = null;
            _encounterPreviewRunningFingerprint = null;
            if (!string.Equals(runningFingerprint, _encounterPreviewAppliedFingerprint,
                    StringComparison.Ordinal))
            {
                FinishEncounterPreviewArtifacts(false, "stale preview", null);
                return;
            }
            if (id is null || !_encounterPreviews.TryGetValue(id, out EncounterPreviewEntry? entry))
            {
                FinishEncounterPreviewArtifacts(false, "preview no longer visible", null);
                return;
            }
            if (exitCode != 0 || string.IsNullOrEmpty(_encounterPreviewResultPath) ||
                !File.Exists(_encounterPreviewResultPath))
            {
                Logger.LogWarning("encounter-preview process failed (exit " + exitCode + "):\n" + log);
                entry.Status = "Failed: " + LastLine(log);
                FinishEncounterPreviewArtifacts(true, entry.Status, log);
                return;
            }
            string resultJson = File.ReadAllText(_encounterPreviewResultPath);
            entry.Result = JsonSerializer.Deserialize<MonsterPredictionDto>(
                resultJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            if (entry.Result is null)
            {
                entry.Status = "Empty result";
                FinishEncounterPreviewArtifacts(true, entry.Status, log);
            }
            else if (!entry.Result.PredictionReady)
            {
                entry.Status = "UNRELIABLE: " +
                    (entry.Result.ValidationErrors?.FirstOrDefault() ?? "incomplete snapshot");
                FinishEncounterPreviewArtifacts(true, entry.Status, log);
            }
            else
            {
                entry.Status = "Ready";
                string inputJson = _encounterPreviewInputPath is not null &&
                    File.Exists(_encounterPreviewInputPath)
                    ? File.ReadAllText(_encounterPreviewInputPath) : string.Empty;
                _encounterPredictionsByEncounter[entry.TemplateId.ToString("D")] =
                    new MonsterPredictionAudit
                    {
                        EncounterId = entry.TemplateId.ToString("D"),
                        Predicted = NormalizePredictedOutcome(entry.Result),
                        PlayerWins = entry.Result.PlayerWins,
                        OpponentWins = entry.Result.OpponentWins,
                        Draws = entry.Result.Draws,
                        InputJson = inputJson,
                        ResultJson = resultJson,
                    };
                FinishEncounterPreviewArtifacts(false, "success", null);
            }
        }
        catch (Exception exception)
        {
            process.Dispose();
            _encounterPreviewProcess = null;
            _encounterPreviewLog = null;
            _encounterPreviewRunningId = null;
            _encounterPreviewRunningFingerprint = null;
            if (!string.Equals(runningFingerprint, _encounterPreviewAppliedFingerprint,
                    StringComparison.Ordinal))
            {
                FinishEncounterPreviewArtifacts(true, "stale preview failed: " +
                    exception.Message, exception.ToString());
                return;
            }
            if (id is not null && _encounterPreviews.TryGetValue(id, out EncounterPreviewEntry? entry))
                entry.Status = "Error: " + exception.Message;
            FinishEncounterPreviewArtifacts(true, "preview result error: " + exception.Message,
                exception.ToString());
        }
    }

    private void FinishEncounterPreviewArtifacts(bool preserve, string reason, string? log)
    {
        if (preserve)
            PreserveArtifacts("encounter-preview", reason, log,
                _encounterPreviewInputPath, _encounterPreviewResultPath);
        else
            DeleteArtifacts(_encounterPreviewInputPath, _encounterPreviewResultPath);
        _encounterPreviewInputPath = null;
        _encounterPreviewResultPath = null;
    }

    private void DrawEncounterPreviewControls()
    {
        foreach (EncounterPreviewEntry entry in _encounterPreviews.Values)
        {
            if (entry.Controller is null || !entry.Controller.gameObject.activeInHierarchy) continue;
            Vector3 screen;
            try
            {
                Camera? camera = Camera.main;
                if (camera is null) continue;
                screen = camera.WorldToScreenPoint(entry.Controller.transform.position);
                if (screen.z <= 0f) continue;
            }
            catch (Exception)
            {
                continue;
            }
            float x = screen.x - 92f;
            float y = Screen.height - screen.y - 76f;
            var rect = new Rect(x, y, 184f, 54f);
            GUI.Box(rect, GUIContent.none);
            string text;
            if (entry.Result is null)
            {
                text = entry.Status;
            }
            else
            {
                if (!entry.Result.PredictionReady)
                {
                    text = "UNRELIABLE\n" +
                        (entry.Result.ValidationErrors?.FirstOrDefault() ?? "incomplete snapshot");
                }
                else
                {
                    text = "胜率 " + entry.Result.PlayerWinRate.ToString("P0") + "\n" +
                        entry.Result.PlayerWins + "胜 " + entry.Result.OpponentWins + "负 " +
                        entry.Result.Draws + "平 · " + entry.Result.Samples + "场";
                }
            }
            GUI.Label(new Rect(rect.x + 7f, rect.y + 8f, rect.width - 14f, 38f), text);
        }
    }

    private static string SafeFileId(string value) => new string(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
}
