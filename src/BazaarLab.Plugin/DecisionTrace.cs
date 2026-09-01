using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BazaarGameClient.Domain.Models;
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using HarmonyLib;
using TheBazaar;
using UnityEngine;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private sealed class DecisionCardSnapshot
    {
        public int index { get; set; }
        public string source { get; set; } = string.Empty;
        public string instance_id { get; set; } = string.Empty;
        public string template_id { get; set; } = string.Empty;
        public string card_type { get; set; } = string.Empty;
        public string runtime_type { get; set; } = string.Empty;
        public string template_type { get; set; } = string.Empty;
        public string internal_name { get; set; } = string.Empty;
        public string tier { get; set; } = string.Empty;
        public string size { get; set; } = string.Empty;
        public string? section { get; set; }
        public string? socket { get; set; }
        public string? enchantment { get; set; }
        public string[] tags { get; set; } = Array.Empty<string>();
        public Dictionary<string, int> attributes { get; set; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public bool entity_resolved { get; set; }
        public bool visible { get; set; }
        public string[] controller_types { get; set; } = Array.Empty<string>();
        public float[][] screen_positions { get; set; } = Array.Empty<float[]>();
    }

    private sealed class DecisionSurfaceSnapshot
    {
        public bool active { get; set; }
        public string app_state { get; set; } = string.Empty;
        public string run_state { get; set; } = string.Empty;
        public string surface_kind { get; set; } = string.Empty;
        public string? current_encounter_entry_id { get; set; }
        public string? current_encounter_template_id { get; set; }
        public uint? reroll_cost { get; set; }
        public uint? rerolls_remaining { get; set; }
        public string[] allowed_operations { get; set; } = Array.Empty<string>();
        public Dictionary<string, object?> selection_rules { get; set; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);
        public List<DecisionCardSnapshot> options { get; set; } = new();
        public object? player { get; set; }
        public string canonical { get; set; } = string.Empty;
    }

    private readonly object _decisionTraceLock = new object();
    private string _decisionTraceSessionId = string.Empty;
    private string _decisionTracePath = string.Empty;
    private long _decisionTraceSequence;
    private float _nextDecisionProbeAt;
    private float _decisionCandidateChangedAt;
    private string? _decisionCandidateFingerprint;
    private DecisionSurfaceSnapshot? _decisionCandidate;
    private string? _decisionCommittedFingerprint;
    private DecisionSurfaceSnapshot? _decisionCommitted;
    private string? _decisionCurrentNodeId;
    private string? _decisionCurrentParentNodeId;
    private int _decisionCurrentDepth;
    private string? _decisionPendingActionId;
    private string? _decisionPendingActionKind;

    private void InitializeDecisionTrace(Harmony harmony)
    {
        _decisionTraceSessionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
            Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(_outputDirectory, "decision-traces");
        Directory.CreateDirectory(directory);
        _decisionTracePath = Path.Combine(directory,
            "decision-trace-" + _decisionTraceSessionId + ".jsonl");
        AppendDecisionRecord(new
        {
            schema = "bazaarlab-decision-trace-v1",
            record_type = "session_start",
            session_id = _decisionTraceSessionId,
            sequence = NextDecisionSequence(),
            recorded_at_utc = DateTime.UtcNow.ToString("O"),
            plugin_version = PluginVersion,
            bpp_version = typeof(BazaarPlusPlus.Game.PvpBattles.PvpBattleSnapshots)
                .Assembly.GetName().Version?.ToString(),
            game_runtime_version = typeof(Data).Assembly.GetName().Version?.ToString(),
            observation_only = true,
        });

        AppState.ItemPurchased += ObserveDecisionItemPurchased;
        AppState.ItemSold += ObserveDecisionItemSold;
        AppState.SkillSelected += ObserveDecisionSkillSelected;
        AppState.EncounterEntered += ObserveDecisionEncounterEntered;
        AppState.EncounterStepSelected += ObserveDecisionEncounterStepSelected;
        AppState.StateExited += ObserveDecisionStateExited;

        PatchDecisionCommand(harmony, nameof(AppState.SelectEncounterCommand), returnsBool: false);
        PatchDecisionCommand(harmony, nameof(AppState.SelectSkillCommand), returnsBool: false);
        PatchDecisionCommand(harmony, nameof(AppState.CommitToPedestalCommand), returnsBool: false);
        PatchDecisionCommand(harmony, nameof(AppState.ExitStateCommand), returnsBool: false);
        PatchDecisionCommand(harmony, nameof(AppState.RerollCommand), returnsBool: true);
        Logger.LogInfo("decision trace observer ready: " + _decisionTracePath);
    }

    private void DisposeDecisionTrace()
    {
        AppState.ItemPurchased -= ObserveDecisionItemPurchased;
        AppState.ItemSold -= ObserveDecisionItemSold;
        AppState.SkillSelected -= ObserveDecisionSkillSelected;
        AppState.EncounterEntered -= ObserveDecisionEncounterEntered;
        AppState.EncounterStepSelected -= ObserveDecisionEncounterStepSelected;
        AppState.StateExited -= ObserveDecisionStateExited;
        if (!string.IsNullOrEmpty(_decisionTracePath))
        {
            AppendDecisionRecord(new
            {
                schema = "bazaarlab-decision-trace-v1",
                record_type = "session_end",
                session_id = _decisionTraceSessionId,
                sequence = NextDecisionSequence(),
                recorded_at_utc = DateTime.UtcNow.ToString("O"),
                last_node_id = _decisionCurrentNodeId,
            });
        }
    }

    private static void PatchDecisionCommand(Harmony harmony, string methodName,
        bool returnsBool)
    {
        try
        {
            MethodInfo? target = typeof(AppState).GetMethods(BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, methodName,
                    StringComparison.Ordinal))
                .OrderBy(method => method.GetParameters().Length)
                .FirstOrDefault();
            if (target is null)
            {
                _instance?.Logger.LogWarning("decision action observer method missing: " +
                    methodName);
                return;
            }
            MethodInfo postfix = AccessTools.Method(typeof(Plugin), returnsBool
                ? nameof(ObserveBooleanDecisionCommand)
                : nameof(ObserveVoidDecisionCommand));
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        }
        catch (Exception exception)
        {
            _instance?.Logger.LogWarning("decision action observer degraded for " + methodName +
                ": " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void ObserveVoidDecisionCommand(MethodBase __originalMethod, object[] __args)
    {
        _instance?.RecordDecisionAction("command", NormalizeDecisionCommand(
            __originalMethod.Name), accepted: null, __args);
    }

    private static void ObserveBooleanDecisionCommand(MethodBase __originalMethod,
        object[] __args, bool __result)
    {
        _instance?.RecordDecisionAction("command", NormalizeDecisionCommand(
            __originalMethod.Name), __result, __args);
    }

    private static string NormalizeDecisionCommand(string methodName) => methodName switch
    {
        nameof(AppState.SelectEncounterCommand) => "select_encounter_or_step",
        nameof(AppState.SelectSkillCommand) => "select_skill",
        nameof(AppState.CommitToPedestalCommand) => "commit_pedestal_target",
        nameof(AppState.ExitStateCommand) => "exit_surface",
        nameof(AppState.RerollCommand) => "reroll",
        _ => methodName,
    };

    private static void ObserveDecisionItemPurchased(Card card) =>
        _instance?.RecordDecisionAction("confirmation", "buy_item", true,
            new object[] { card });

    private static void ObserveDecisionItemSold(Card card) =>
        _instance?.RecordDecisionAction("confirmation", "sell_item", true,
            new object[] { card });

    private static void ObserveDecisionSkillSelected() =>
        _instance?.RecordDecisionAction("confirmation", "select_skill", true,
            Array.Empty<object>());

    private static void ObserveDecisionEncounterEntered() =>
        _instance?.RecordDecisionAction("confirmation", "enter_encounter", true,
            Array.Empty<object>());

    private static void ObserveDecisionEncounterStepSelected() =>
        _instance?.RecordDecisionAction("confirmation", "select_event_step", true,
            Array.Empty<object>());

    private static void ObserveDecisionStateExited() =>
        _instance?.RecordDecisionAction("confirmation", "exit_surface", true,
            Array.Empty<object>());

    private void UpdateDecisionTrace()
    {
        if (Time.realtimeSinceStartup < _nextDecisionProbeAt) return;
        _nextDecisionProbeAt = Time.realtimeSinceStartup + 0.10f;
        DecisionSurfaceSnapshot snapshot;
        try
        {
            snapshot = BuildDecisionSurfaceSnapshot();
        }
        catch (Exception exception)
        {
            Logger.LogWarning("decision trace probe skipped: " + exception.GetType().Name +
                ": " + exception.Message);
            return;
        }
        string fingerprint = StableDecisionHash(snapshot.canonical);
        if (!string.Equals(fingerprint, _decisionCandidateFingerprint,
                StringComparison.Ordinal))
        {
            _decisionCandidateFingerprint = fingerprint;
            _decisionCandidate = snapshot;
            _decisionCandidateChangedAt = Time.realtimeSinceStartup;
            return;
        }
        if (string.Equals(fingerprint, _decisionCommittedFingerprint,
                StringComparison.Ordinal) || _decisionCandidate is null ||
            Time.realtimeSinceStartup - _decisionCandidateChangedAt < 0.18f ||
            AppState.IsWaitingForServerResponse)
            return;
        CommitDecisionSurface(_decisionCandidate, fingerprint);
    }

    private DecisionSurfaceSnapshot BuildDecisionSurfaceSnapshot()
    {
        RunState? runState = Data.CurrentState;
        AppState? appState = AppState.CurrentState;
        string appStateName = appState?.GetType().Name ?? "None";
        string runStateName = runState?.StateName.ToString() ?? "None";
        var options = new List<DecisionCardSnapshot>();
        Dictionary<string, List<CardController>> controllers = FindObjectsByType<CardController>(
                FindObjectsSortMode.None)
            .Where(controller => controller is not null && controller.CardData is not null &&
                controller.gameObject.activeInHierarchy)
            .GroupBy(controller => controller.CardData.InstanceId.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        if (runState?.SelectionSet is not null)
        {
            int index = 0;
            foreach (string entryId in runState.SelectionSet)
            {
                Card? card = null;
                try
                {
                    Data.Entities.TryGetValue(InstanceId.TryParse(entryId), out card);
                }
                catch (Exception)
                {
                    // Preserve unresolved option IDs for later reconciliation.
                }
                options.Add(BuildDecisionCard(card, entryId, index++, "selection_set",
                    controllers));
            }
        }

        if (appState is PedestalState pedestal && Data.Run?.Player is not null)
        {
            IEnumerable<Card> inventory = Data.Run.Player.Hand.GetItemsAsEnumerable()
                .OfType<Card>().Concat(Data.Run.Player.Stash.GetItemsAsEnumerable().OfType<Card>());
            foreach (Card card in inventory)
            {
                bool eligible;
                try { eligible = pedestal.CanBeUpgraded(card); }
                catch (Exception) { eligible = false; }
                if (eligible && options.All(option => !string.Equals(option.instance_id,
                        card.InstanceId.Value, StringComparison.Ordinal)))
                {
                    options.Add(BuildDecisionCard(card, card.InstanceId.Value, options.Count,
                        "pedestal_eligible", controllers));
                }
            }
        }

        string[] allowed = Enum.GetValues(typeof(StateOps)).Cast<StateOps>()
            .Where(operation => operation != StateOps.None && appState is not null &&
                appState.CanHandleOperation(operation))
            .Select(operation => operation.ToString()).ToArray();
        bool isDecisionRunState = runStateName is "Choice" or "Encounter" or "LevelUp" or
            "Loot" or "Pedestal";
        bool active = !Data.IsInCombat && Data.Run?.Player is not null &&
            (options.Count > 0 || isDecisionRunState &&
                (allowed.Contains(nameof(StateOps.Reroll), StringComparer.Ordinal) ||
                 allowed.Contains(nameof(StateOps.ExitState), StringComparer.Ordinal) ||
                 !string.IsNullOrEmpty(runState?.CurrentEncounterId)));
        string kind = ClassifyDecisionSurface(runStateName, options, runState?.RerollCost,
            Data.CurrentEncounterId);
        object? playerSnapshot = BuildDecisionPlayerSnapshot();
        string currentTemplate = Data.CurrentEncounterId?.ToString("D") ?? string.Empty;
        string canonical = JsonSerializer.Serialize(new
        {
            active,
            appStateName,
            runStateName,
            kind,
            current_entry = runState?.CurrentEncounterId,
            current_template = currentTemplate,
            reroll_cost = runState?.RerollCost,
            rerolls = runState?.RerollsRemaining,
            allowed,
            options = options.Select(option => new
            {
                option.index, option.source, option.instance_id, option.template_id,
                option.card_type, option.tier, option.size, option.section, option.socket,
                option.enchantment, option.attributes,
            }).ToArray(),
            playerSnapshot,
        });
        return new DecisionSurfaceSnapshot
        {
            active = active,
            app_state = appStateName,
            run_state = runStateName,
            surface_kind = kind,
            current_encounter_entry_id = runState?.CurrentEncounterId,
            current_encounter_template_id = string.IsNullOrEmpty(currentTemplate)
                ? null : currentTemplate,
            reroll_cost = runState?.RerollCost,
            rerolls_remaining = runState?.RerollsRemaining,
            allowed_operations = allowed,
            selection_rules = SnapshotSimpleObject(runState?.SelectionContextRules),
            options = options,
            player = playerSnapshot,
            canonical = canonical,
        };
    }

    private DecisionCardSnapshot BuildDecisionCard(Card? card, string entryId, int index,
        string source, IReadOnlyDictionary<string, List<CardController>> controllers)
    {
        var snapshot = new DecisionCardSnapshot
        {
            index = index,
            source = source,
            instance_id = card?.InstanceId.Value ?? entryId,
            template_id = card?.TemplateId.ToString("D") ?? string.Empty,
            card_type = card?.Type.ToString() ?? "Unresolved",
            runtime_type = card?.GetType().Name ?? string.Empty,
            template_type = card?.Template?.GetType().Name ?? string.Empty,
            internal_name = ReadProperty(card?.Template, "InternalName")?.ToString() ?? string.Empty,
            tier = card?.Tier.ToString() ?? string.Empty,
            size = card?.Size.ToString() ?? string.Empty,
            section = card?.Section?.ToString(),
            socket = card?.LeftSocketId?.ToString(),
            enchantment = (card as ItemCard)?.Enchantment?.ToString(),
            tags = card?.Tags.Select(tag => tag.ToString()).OrderBy(value => value,
                StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
            attributes = card is null ? new Dictionary<string, int>(StringComparer.Ordinal) :
                ConvertAttributes(card.Attributes),
            entity_resolved = card is not null,
        };
        if (controllers.TryGetValue(snapshot.instance_id, out List<CardController>? matches))
        {
            snapshot.visible = matches.Count > 0;
            snapshot.controller_types = matches.Select(controller => controller.GetType().Name)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            Camera? camera = Camera.main;
            if (camera is not null)
            {
                snapshot.screen_positions = matches.Select(controller =>
                {
                    Vector3 point = camera.WorldToScreenPoint(controller.transform.position);
                    return new[] { point.x, Screen.height - point.y, point.z };
                }).ToArray();
            }
        }
        return snapshot;
    }

    private object? BuildDecisionPlayerSnapshot()
    {
        var run = Data.Run;
        var player = run?.Player;
        if (player is null) return null;
        return new
        {
            day = run!.Day,
            hour = run.Hour,
            hero = player.Hero.ToString(),
            attributes = ConvertAttributes(player.Attributes),
            board = player.Hand.GetItemsAsEnumerable().OfType<Card>()
                .OrderBy(card => card.LeftSocketId.HasValue ? (int)card.LeftSocketId.Value : 99)
                .Select(card => BuildDecisionInventoryCard(card)).ToArray(),
            stash = player.Stash.GetItemsAsEnumerable().OfType<Card>()
                .OrderBy(card => card.LeftSocketId.HasValue ? (int)card.LeftSocketId.Value : 99)
                .Select(card => BuildDecisionInventoryCard(card)).ToArray(),
            skills = player.Skills.Cast<Card>().OrderBy(card => card.InstanceId.Value,
                    StringComparer.Ordinal)
                .Select(card => BuildDecisionInventoryCard(card)).ToArray(),
        };
    }

    private static object BuildDecisionInventoryCard(Card card) => new
    {
        instance_id = card.InstanceId.Value,
        template_id = card.TemplateId.ToString("D"),
        card_type = card.Type.ToString(),
        internal_name = ReadProperty(card.Template, "InternalName")?.ToString() ?? string.Empty,
        tier = card.Tier.ToString(),
        size = card.Size.ToString(),
        section = card.Section?.ToString(),
        socket = card.LeftSocketId?.ToString(),
        enchantment = (card as ItemCard)?.Enchantment?.ToString(),
        attributes = ConvertAttributes(card.Attributes),
    };

    private static string ClassifyDecisionSurface(string runState,
        IReadOnlyCollection<DecisionCardSnapshot> options, uint? rerollCost,
        Guid? currentEncounterTemplateId)
    {
        string[] types = options.Select(option => option.card_type).Distinct(StringComparer.Ordinal)
            .ToArray();
        if (runState == "Pedestal") return "pedestal_target";
        if (runState == "LevelUp") return "level_up_reward";
        if (runState == "Loot") return "loot_reward";
        if (runState == "Choice" && types.All(type => type.Contains("Encounter",
                StringComparison.OrdinalIgnoreCase)))
            return "encounter_choice";
        if (types.Any(type => type.Contains("EncounterStep",
                StringComparison.OrdinalIgnoreCase)))
            return "event_step_choice";
        if (rerollCost.HasValue && types.Any(type => type is "Item" or "Skill" or "Reward"))
            return "merchant_or_reroll";
        if (types.Length > 0 && types.All(type => type == "Skill")) return "skill_choice";
        if (types.Length > 0 && types.All(type => type == "Item")) return "item_choice";
        if (types.Any(type => type == "Reward")) return "reward_choice";
        if (currentEncounterTemplateId.HasValue && runState == "Encounter")
            return "encounter_subscreen";
        return options.Count > 0 ? "mixed_choice" : "decision_surface";
    }

    private void CommitDecisionSurface(DecisionSurfaceSnapshot snapshot, string fingerprint)
    {
        DecisionSurfaceSnapshot? previous = _decisionCommitted;
        string? previousNodeId = _decisionCurrentNodeId;
        string relation = InferDecisionRelation(previous, snapshot, _decisionPendingActionKind);
        string? nodeId = snapshot.active
            ? _decisionTraceSessionId + ":node:" + NextDecisionSequence().ToString("D6")
            : null;
        string? parentNodeId = null;
        int depth = 0;
        if (snapshot.active && previous?.active == true)
        {
            if (relation == "open_child")
            {
                parentNodeId = previousNodeId;
                depth = _decisionCurrentDepth + 1;
            }
            else
            {
                parentNodeId = _decisionCurrentParentNodeId;
                depth = _decisionCurrentDepth;
            }
        }
        long surfaceSequence = NextDecisionSequence();
        if (snapshot.active)
        {
            AppendDecisionRecord(new
            {
                schema = "bazaarlab-decision-trace-v1",
                record_type = "surface",
                session_id = _decisionTraceSessionId,
                sequence = surfaceSequence,
                recorded_at_utc = DateTime.UtcNow.ToString("O"),
                frame = Time.frameCount,
                node_id = nodeId,
                parent_node_id = parentNodeId,
                depth,
                fingerprint,
                surface = new
                {
                    snapshot.app_state,
                    snapshot.run_state,
                    snapshot.surface_kind,
                    snapshot.current_encounter_entry_id,
                    snapshot.current_encounter_template_id,
                    snapshot.reroll_cost,
                    snapshot.rerolls_remaining,
                    snapshot.allowed_operations,
                    snapshot.selection_rules,
                    snapshot.options,
                    snapshot.player,
                },
            });
        }
        if (previous?.active == true || snapshot.active)
        {
            AppendDecisionRecord(new
            {
                schema = "bazaarlab-decision-trace-v1",
                record_type = "transition",
                session_id = _decisionTraceSessionId,
                sequence = NextDecisionSequence(),
                recorded_at_utc = DateTime.UtcNow.ToString("O"),
                frame = Time.frameCount,
                from_node_id = previousNodeId,
                to_node_id = nodeId,
                action_id = _decisionPendingActionId,
                inferred_relation = relation,
                inference_confidence = _decisionPendingActionId is null ? "low" : "medium",
            });
        }
        _decisionCommitted = snapshot;
        _decisionCommittedFingerprint = fingerprint;
        _decisionCurrentNodeId = nodeId;
        _decisionCurrentParentNodeId = parentNodeId;
        _decisionCurrentDepth = depth;
        _decisionPendingActionId = null;
        _decisionPendingActionKind = null;
    }

    private static string InferDecisionRelation(DecisionSurfaceSnapshot? previous,
        DecisionSurfaceSnapshot current, string? actionKind)
    {
        if (previous?.active != true && current.active) return "open_root";
        if (previous?.active == true && !current.active) return "close";
        if (actionKind == "reroll") return "refresh";
        if (actionKind is "buy_item" or "sell_item") return "same_surface_mutation";
        if (actionKind == "exit_surface") return "return_or_close";
        if (previous is not null && (!string.Equals(previous.app_state, current.app_state,
                StringComparison.Ordinal) || !string.Equals(previous.current_encounter_template_id,
                current.current_encounter_template_id, StringComparison.Ordinal)))
            return "open_child";
        return "same_surface_update";
    }

    private void RecordDecisionAction(string evidence, string actionKind, bool? accepted,
        IReadOnlyList<object> arguments)
    {
        if (string.IsNullOrEmpty(_decisionTracePath)) return;
        string? confirmsActionId = string.Equals(evidence, "confirmation",
            StringComparison.Ordinal) ? _decisionPendingActionId : null;
        string actionId = _decisionTraceSessionId + ":action:" +
            NextDecisionSequence().ToString("D6");
        object[] argumentSnapshots = arguments.Select(SnapshotDecisionArgument).ToArray();
        AppendDecisionRecord(new
        {
            schema = "bazaarlab-decision-trace-v1",
            record_type = "action",
            session_id = _decisionTraceSessionId,
            sequence = NextDecisionSequence(),
            recorded_at_utc = DateTime.UtcNow.ToString("O"),
            frame = Time.frameCount,
            action_id = actionId,
            from_node_id = _decisionCurrentNodeId,
            evidence,
            action_kind = actionKind,
            accepted,
            confirms_action_id = confirmsActionId,
            arguments = argumentSnapshots,
            app_state = AppState.CurrentState?.GetType().Name,
            run_state = Data.CurrentState?.StateName.ToString(),
            current_encounter_template_id = Data.CurrentEncounterId?.ToString("D"),
        });
        if (confirmsActionId is null)
        {
            _decisionPendingActionId = actionId;
            _decisionPendingActionKind = actionKind;
        }
    }

    private static object SnapshotDecisionArgument(object value)
    {
        if (value is Card card)
        {
            return new
            {
                kind = "card",
                instance_id = card.InstanceId.Value,
                template_id = card.TemplateId.ToString("D"),
                card_type = card.Type.ToString(),
                internal_name = ReadProperty(card.Template, "InternalName")?.ToString() ??
                    string.Empty,
                tier = card.Tier.ToString(),
            };
        }
        if (value is InstanceId instanceId)
            return new { kind = "instance_id", value = instanceId.Value };
        return new { kind = value?.GetType().Name ?? "null", value = value?.ToString() };
    }

    private static Dictionary<string, object?> SnapshotSimpleObject(object? value)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (value is null) return result;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        foreach (PropertyInfo property in value.GetType().GetProperties(flags)
            .Where(property => property.GetIndexParameters().Length == 0))
        {
            try { result[property.Name] = SimplifyDecisionValue(property.GetValue(value)); }
            catch (Exception) { result[property.Name] = "<unreadable>"; }
        }
        foreach (FieldInfo field in value.GetType().GetFields(flags))
        {
            try { result[field.Name] = SimplifyDecisionValue(field.GetValue(value)); }
            catch (Exception) { result[field.Name] = "<unreadable>"; }
        }
        return result;
    }

    private static object? SimplifyDecisionValue(object? value)
    {
        if (value is null || value is string || value is bool || value is byte ||
            value is short || value is int || value is long || value is uint ||
            value is ulong || value is float || value is double || value is decimal)
            return value;
        if (value.GetType().IsEnum || value is Guid) return value.ToString();
        if (value is IEnumerable enumerable)
        {
            var entries = new List<string>();
            foreach (object? entry in enumerable) entries.Add(entry?.ToString() ?? string.Empty);
            return entries.ToArray();
        }
        return value.ToString();
    }

    private long NextDecisionSequence() => ++_decisionTraceSequence;

    private void AppendDecisionRecord(object record)
    {
        if (string.IsNullOrEmpty(_decisionTracePath)) return;
        string line = JsonSerializer.Serialize(record);
        lock (_decisionTraceLock)
        {
            File.AppendAllText(_decisionTracePath, line + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static string StableDecisionHash(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte octet in Encoding.UTF8.GetBytes(value))
        {
            hash ^= octet;
            hash *= prime;
        }
        return hash.ToString("x16");
    }
}
