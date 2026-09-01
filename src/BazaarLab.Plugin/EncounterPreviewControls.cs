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
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Cards.PlayerEffects;
using BazaarGameShared.Domain.Cards.Skill;
using BazaarGameShared.Domain.Players;
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
    private string? _encounterPreviewResultPath;
    private string? _encounterPreviewCandidateFingerprint;
    private string? _encounterPreviewAppliedFingerprint;
    private float _encounterPreviewChangedAt;
    private float _nextEncounterPreviewProbeAt;
    private bool _encounterPreviewCombatSuppressed;

    private bool IsEncounterPreviewCalculating => _encounterPreviewProcess is not null ||
        _encounterPreviewQueue.Count > 0;

    private void InitializeEncounterPreviewControls()
    {
        _encounterPreviewChangedAt = Time.realtimeSinceStartup;
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
        _encounterPreviewResultPath = null;
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
                string input = Path.Combine(_outputDirectory,
                    "encounter-preview-input-" + stamp + "-" + SafeFileId(id) + ".json");
                _encounterPreviewResultPath = Path.Combine(_outputDirectory,
                    "encounter-preview-result-" + stamp + "-" + SafeFileId(id) + ".json");
                File.WriteAllText(input, BuildEncounterPreviewJson(entry) + Environment.NewLine);
                string dotnetExecutable = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
                if (!File.Exists(dotnetExecutable)) dotnetExecutable = "dotnet";
                var output = new StringBuilder();
                var start = new ProcessStartInfo
                {
                    FileName = dotnetExecutable,
                    Arguments = Quote(core) + " predict-bpp-adaptive " + Quote(catalog) + " " +
                        Quote(input) + " 20260831 31 101 20 2400 " +
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
            }
        }
    }

    private string BuildEncounterPreviewJson(EncounterPreviewEntry entry)
    {
        var player = Data.Run.Player;
        TPlayer monster = entry.Monster.Player;
        Dictionary<string, int> opponentAttributes = ConvertAttributes(monster.Attributes);
        if (!opponentAttributes.ContainsKey("Health") &&
            opponentAttributes.TryGetValue("HealthMax", out int opponentHealthMax))
        {
            opponentAttributes["Health"] = opponentHealthMax;
        }
        object playerHand = ConvertLiveSet("PlayerHand", 0, "Hand",
            player.Hand.GetItemsAsEnumerable().OfType<Card>());
        object playerSkills = ConvertLiveSet("PlayerSkills", 0, "Skills",
            player.Skills.Cast<Card>());
        object opponentHand = ConvertMonsterItems(entry, monster.Hand.Items);
        object opponentSkills = ConvertMonsterSkills(entry, monster.Skills, monster.Effects);
        var document = new
        {
            schema = "bazaarlab-combat-snapshot-v1",
            capture = new { plugin_version = PluginVersion, source = "static-monster-preview" },
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
                warnings = new[] { "opponent is reconstructed from a static encounter template" },
            },
            input_warnings = new[]
            {
                "opponent is reconstructed from a static encounter template; result is an estimate"
            },
            combatants = new object[]
            {
                new { id = "player", hero = player.Hero.ToString(),
                    attributes = ConvertAttributes(player.Attributes) },
                new { id = "opponent", hero = "Common",
                    attributes = opponentAttributes },
            },
            card_sets = new[] { playerHand, playerSkills, opponentHand, opponentSkills },
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object ConvertMonsterItems(EncounterPreviewEntry entry,
        IEnumerable<TCardInstanceItem> items) => new
    {
        label = "OpponentHand",
        owner = 1,
        section = "Hand",
        status = "Captured",
        source = "StaticMonsterTemplate",
        items = items.Select((item, index) => ConvertMonsterCard(entry, item,
            "Item", "Hand", item.SocketId?.ToString(), item.EnchantmentType?.ToString(), index))
            .ToArray(),
    };

    private static object ConvertMonsterSkills(EncounterPreviewEntry entry,
        IEnumerable<TCardInstanceSkill> skills,
        IEnumerable<TCardInstancePlayerEffect> effects) => new
    {
        label = "OpponentSkills",
        owner = 1,
        section = "Skills",
        status = "Captured",
        source = "StaticMonsterTemplate",
        items = skills.Select((skill, index) => ConvertMonsterCard(entry, skill,
                "Skill", "Skills", null, null, index))
            .Concat(effects.Select((effect, index) => ConvertMonsterCard(entry, effect,
                "PlayerEffect", "Skills", null, null, 1000 + index))).ToArray(),
    };

    private static object ConvertMonsterCard(EncounterPreviewEntry entry,
        object card, string type, string section, string? socket, string? enchant, int index)
    {
        Type cardType = card.GetType();
        Guid templateId = (Guid)(cardType.GetProperty("TemplateId")?.GetValue(card) ?? Guid.Empty);
        object? tier = cardType.GetProperty("Tier")?.GetValue(card);
        object? attributes = cardType.GetProperty("Attributes")?.GetValue(card);
        ITCard? template = Data.GetStatic().GetCardById(templateId);
        string size = ReadProperty(template, "Size")?.ToString() ?? "Small";
        string name = ReadProperty(template, "InternalName")?.ToString() ?? string.Empty;
        return new
        {
            instance_id = "monster:" + entry.Monster.Id.ToString("D") + ":" + type + ":" + index,
            template_id = templateId.ToString("D"),
            type,
            size,
            section,
            socket,
            name,
            tier = ResolveMonsterTier(tier, template),
            enchant = enchant ?? string.Empty,
            tags = ConvertTags(ReadProperty(card, "Tags") ?? ReadProperty(template, "Tags")),
            attributes = ConvertAttributeObject(attributes),
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
                return;
            if (id is null || !_encounterPreviews.TryGetValue(id, out EncounterPreviewEntry? entry))
                return;
            if (exitCode != 0 || string.IsNullOrEmpty(_encounterPreviewResultPath) ||
                !File.Exists(_encounterPreviewResultPath))
            {
                Logger.LogWarning("encounter-preview process failed (exit " + exitCode + "):\n" + log);
                entry.Status = "Failed: " + LastLine(log);
                return;
            }
            entry.Result = JsonSerializer.Deserialize<MonsterPredictionDto>(
                File.ReadAllText(_encounterPreviewResultPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            entry.Status = entry.Result is null ? "Empty result" : "Ready";
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
                return;
            if (id is not null && _encounterPreviews.TryGetValue(id, out EncounterPreviewEntry? entry))
                entry.Status = "Error: " + exception.Message;
        }
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
                    string decision = entry.Result.ConfidentPrediction ??
                        (entry.Result.Predicted ?? "uncertain");
                    text = "EST " + decision.ToUpperInvariant() + "  " +
                        entry.Result.ConservativePlayerProbabilityLower95.ToString("P0") + "–" +
                        entry.Result.ConservativePlayerProbabilityUpper95.ToString("P0") +
                        "  n=" + entry.Result.Samples;
                }
            }
            GUI.Label(new Rect(rect.x + 7f, rect.y + 8f, rect.width - 14f, 38f), text);
        }
    }

    private static string SafeFileId(string value) => new string(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
}
