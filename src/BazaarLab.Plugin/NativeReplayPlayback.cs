using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BazaarGameShared;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarPlusPlus.Game.PvpBattles;
using BepInEx;
using MessagePack;
using MessagePack.Resolvers;
using TheBazaar;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    public static string ProbeNativeReplaySerialization(
        string templatePath, string projectionPath)
    {
        PvpReplayPayload template = LoadReplayTemplate(templatePath);
        NetMessageGameSim spawn = MessagePackSerializer.Deserialize<NetMessageGameSim>(
            template.SpawnMessageBytes, MessagePackConfig.Options);
        NetMessageGameSim despawn = MessagePackSerializer.Deserialize<NetMessageGameSim>(
            template.DespawnMessageBytes, MessagePackConfig.Options);
        NativeProjectionDto projection = JsonSerializer.Deserialize<NativeProjectionDto>(
            File.ReadAllText(projectionPath), LineupJsonOptions()) ??
            throw new InvalidDataException("projection is empty");
        NetMessageCombatSim combat = BuildCombatMessage(projection, "probe-local-replay");
        byte[] encoded = MessagePackSerializer.Serialize(combat, MessagePackConfig.Options);
        NetMessageCombatSim decoded = MessagePackSerializer.Deserialize<NetMessageCombatSim>(
            encoded, MessagePackConfig.Options);
        return JsonSerializer.Serialize(new
        {
            TemplateSpawnEvents = spawn.Data.Events.Count,
            TemplateDespawnEvents = despawn.Data.Events.Count,
            ProjectionFrames = projection.FrameCount,
            EncodedBytes = encoded.Length,
            DecodedFrames = decoded.Data.Frames.Count,
            ExecutedEffects = decoded.Data.Frames.Sum(frame =>
                frame.Events.OfType<CombatSimEventEffectExecuted>().Count()),
            TriggeredEffects = decoded.Data.Frames.Sum(frame =>
                frame.Events.OfType<CombatSimEventEffectTriggered>().Count()),
            VfxKeys = decoded.Data.VfxKeys.Count,
            VfxOverrides = decoded.Data.Frames.SelectMany(frame => frame.Events)
                .OfType<CombatSimEventEffectExecuted>().Count(effect => effect.VfxIndex.HasValue),
            CooldownUpdates = decoded.Data.Frames.Sum(frame => frame.CardUpdates.Values.Count(
                update => update.Attributes.ContainsKey(ECardAttributeType.Cooldown))),
            StateUpdates = decoded.Data.Frames.Sum(frame => frame.CardUpdates.Values.Count(
                update => update.State is not null)),
            decoded.Data.Winner,
            decoded.Data.Loser,
        });
    }

    private void StartNativeLineupReplay(LineupEnvelopeDto a, LineupEnvelopeDto b,
        string duelInputPath, string projectionPath)
    {
        try
        {
            if (Data.IsInCombat)
                throw new InvalidOperationException("finish the current combat first");
            string replayDirectory = Path.Combine(Paths.GameRootPath,
                "BazaarPlusPlusV5", "CombatReplays");
            string? templatePath = Directory.Exists(replayDirectory)
                ? Directory.EnumerateFiles(replayDirectory, "*.payload.mpack.gz")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (templatePath is null)
                throw new FileNotFoundException("BPP has no saved replay template yet");

            PvpReplayPayload template = LoadReplayTemplate(templatePath);
            NetMessageGameSim spawn = MessagePackSerializer.Deserialize<NetMessageGameSim>(
                template.SpawnMessageBytes, MessagePackConfig.Options);
            NetMessageGameSim despawn = MessagePackSerializer.Deserialize<NetMessageGameSim>(
                template.DespawnMessageBytes, MessagePackConfig.Options);
            NativeProjectionDto projection = JsonSerializer.Deserialize<NativeProjectionDto>(
                File.ReadAllText(projectionPath), LineupJsonOptions()) ??
                throw new InvalidDataException("replay projection is empty");
            string battleId = "local-lineup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");

            PrepareSpawnMessage(spawn, a, b, battleId);
            PrepareDespawnMessage(despawn, spawn, battleId);
            NetMessageCombatSim combat = BuildCombatMessage(projection, battleId);
            var payload = new PvpReplayPayload
            {
                BattleId = battleId,
                Version = 1,
                SpawnMessageBytes = MessagePackSerializer.Serialize(spawn,
                    MessagePackConfig.Options),
                CombatMessageBytes = MessagePackSerializer.Serialize(combat,
                    MessagePackConfig.Options),
                DespawnMessageBytes = MessagePackSerializer.Serialize(despawn,
                    MessagePackConfig.Options),
            };
            PvpBattleManifest manifest = BuildReplayManifest(a, b, projection, battleId);
            PublishAtomic("local-duels/latest-native-replay.json", JsonSerializer.Serialize(new
            {
                battle_id = battleId,
                template = templatePath,
                duel_input = duelInputPath,
                projection = projectionPath,
                frames = projection.FrameCount,
                winner = projection.WinnerId,
            }, new JsonSerializerOptions { WriteIndented = true }));

            Assembly bpp = typeof(PvpReplayPayload).Assembly;
            Type runtimeType = bpp.GetType(
                "BazaarPlusPlus.Game.CombatReplay.CombatReplayRuntime", throwOnError: true)!;
            object runtime = runtimeType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ??
                throw new InvalidOperationException("BPP replay runtime is unavailable");
            MethodInfo replay = runtimeType.GetMethod("ReplayImportedBattle",
                    BindingFlags.Public | BindingFlags.Instance) ??
                throw new MissingMethodException(runtimeType.FullName, "ReplayImportedBattle");
            bool accepted = (bool)(replay.Invoke(runtime,
                new object[] { manifest, payload, false }) ?? false);
            if (!accepted)
                throw new InvalidOperationException(
                    "BPP rejected playback; return to the main menu and retry");
            _lineupStatus = "BPP accepted local replay; entering playback...";
        }
        catch (TargetInvocationException exception)
        {
            Exception actual = exception.InnerException ?? exception;
            _lineupStatus = "Cannot start BPP playback: " + actual.Message;
            Logger.LogError("native local replay failed: " + actual);
        }
        catch (Exception exception)
        {
            _lineupStatus = "Cannot start BPP playback: " + exception.Message;
            Logger.LogError("native local replay failed: " + exception);
        }
    }

    private static PvpReplayPayload LoadReplayTemplate(string path)
    {
        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        gzip.CopyTo(memory);
        MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolverAllowPrivate.Instance);
        return MessagePackSerializer.Deserialize<PvpReplayPayload>(memory.ToArray(), options);
    }

    private static void PrepareSpawnMessage(NetMessageGameSim message, LineupEnvelopeDto a,
        LineupEnvelopeDto b, string battleId)
    {
        message.MessageId = battleId;
        GameSim data = message.Data ??
            throw new InvalidDataException("template spawn has no data");
        data.Player = BuildOpeningPlayer(ECombatantId.Player, a.payload.attributes);
        data.Opponent = BuildOpeningPlayer(ECombatantId.Opponent, b.payload.attributes);
        data.Cards = new Dictionary<string, SimUpdateCard>(StringComparer.Ordinal);
        var events = new List<IGameSimEvent>();
        events.Add(BuildPlayerInitialized(ECombatantId.Player, a.payload.hero));
        events.Add(BuildPlayerInitialized(ECombatantId.Opponent, b.payload.hero));
        AddOpeningCards(data.Cards, events, a.payload.board, ECombatantId.Player, "p");
        AddOpeningCards(data.Cards, events, a.payload.skills, ECombatantId.Player, "ps");
        AddOpeningCards(data.Cards, events, b.payload.board, ECombatantId.Opponent, "o");
        AddOpeningCards(data.Cards, events, b.payload.skills, ECombatantId.Opponent, "os");
        data.Events = events;
        data.VfxKeys ??= new List<string>();
        data.Run ??= new SimUpdateRun { Day = 1, Hour = 1, DataVersion = "local-lineup" };
    }

    private static void PrepareDespawnMessage(NetMessageGameSim despawn,
        NetMessageGameSim spawn, string battleId)
    {
        despawn.MessageId = battleId;
        if (despawn.Data is null)
            throw new InvalidDataException("template despawn has no data");
        despawn.Data.Player = spawn.Data.Player;
        despawn.Data.Opponent = spawn.Data.Opponent;
        despawn.Data.Run = spawn.Data.Run;
        despawn.Data.Cards = new Dictionary<string, SimUpdateCard>();
        despawn.Data.Events = new List<IGameSimEvent>();
        despawn.Data.VfxKeys ??= new List<string>();
    }

    private static SimUpdatePlayer BuildOpeningPlayer(ECombatantId id,
        IReadOnlyDictionary<string, int> attributes)
    {
        var result = new SimUpdatePlayer
        {
            CombatantId = id,
            Attributes = new Dictionary<EPlayerAttributeType, int>(),
        };
        foreach (KeyValuePair<string, int> pair in attributes)
            if (Enum.TryParse(pair.Key, false, out EPlayerAttributeType attribute))
                result.Attributes[attribute] = pair.Value;
        if (!result.Attributes.ContainsKey(EPlayerAttributeType.Level))
            result.Attributes[EPlayerAttributeType.Level] = 1;
        return result;
    }

    private static GameSimEventPlayerInitialized BuildPlayerInitialized(
        ECombatantId id, string hero)
    {
        if (!Enum.TryParse(hero, false, out EHero parsed))
            throw new InvalidDataException("unknown hero " + hero);
        return new GameSimEventPlayerInitialized { CombatantId = id, Hero = parsed };
    }

    private static void AddOpeningCards(IDictionary<string, SimUpdateCard> cards,
        ICollection<IGameSimEvent> events, IReadOnlyList<LineupCardDto> source,
        ECombatantId owner, string prefix)
    {
        bool skills = prefix.EndsWith("s", StringComparison.Ordinal);
        for (int index = 0; index < source.Count; index++)
        {
            LineupCardDto card = source[index];
            string id = "duel:" + prefix + ":" + index + ":" + card.instance_id;
            if (!Enum.TryParse(card.type, false, out ECardType type))
                throw new InvalidDataException("unknown card type " + card.type);
            EContainerSocketId socket = skills ? EContainerSocketId.Socket_0 :
                ParseSocket(card.socket);
            var placement = new CardDeltaPlacement
            {
                Owner = owner,
                Section = EInventorySection.Hand,
                Socket = socket,
            };
            var update = new SimUpdateCard
            {
                InstanceId = id,
                Placement = placement,
                Attributes = new Dictionary<ECardAttributeType, AttributeDelta>(),
                Tags = ParseSet<ECardTag>(card.tags),
                HiddenTags = new HashSet<EHiddenTag>(),
                Heroes = new HashSet<EHero>(),
            };
            if (Enum.TryParse(card.size, false, out ECardSize size)) update.Size = size;
            if (Enum.TryParse(card.tier, false, out ETier tier)) update.Tier = tier;
            if (Enum.TryParse(card.enchant, false, out EEnchantmentType enchant))
                update.Enchantment = enchant;
            foreach (KeyValuePair<string, int> pair in card.attributes)
                if (Enum.TryParse(pair.Key, false, out ECardAttributeType attribute))
                    update.Attributes[attribute] = new AttributeDelta
                    {
                        DeltaType = EAttributeDeltaType.Update,
                        Value = pair.Value,
                    };
            cards[id] = update;
            events.Add(new GameSimEventCardSpawned(id, card.template_id, type, owner,
                EInventorySection.Hand, socket));
            if (skills) events.Add(new GameSimEventPlayerSkillEquipped(id, owner));
        }
    }

    private static HashSet<T> ParseSet<T>(IEnumerable<string> values) where T : struct
    {
        var result = new HashSet<T>();
        foreach (string value in values)
            if (Enum.TryParse(value, false, out T parsed)) result.Add(parsed);
        return result;
    }

    private static EContainerSocketId ParseSocket(string value)
    {
        if (Enum.TryParse(value, false, out EContainerSocketId socket)) return socket;
        int index = SocketIndex(value);
        if (index >= 0 && index <= 9) return (EContainerSocketId)index;
        throw new InvalidDataException("invalid socket " + value);
    }

    private static NetMessageCombatSim BuildCombatMessage(
        NativeProjectionDto projection, string battleId)
    {
        var simulation = new CombatSim
        {
            Frames = projection.Frames.Select(frame =>
                BuildCombatFrame(frame, projection.VfxKeys)).ToList(),
            Winner = projection.WinnerId == "opponent"
                ? ECombatantId.Opponent : ECombatantId.Player,
            Loser = projection.WinnerId == "opponent"
                ? ECombatantId.Player : ECombatantId.Opponent,
            OpponentHealthThresholdsForGold = new List<float>(),
            OpponentHealthThresholdsForXp = new List<float>(),
            CardStats = new Dictionary<string, Dictionary<ECardStats, int>>(),
            VfxKeys = new List<string>(projection.VfxKeys),
            PortraitKeys = new List<string>(),
        };
        return new NetMessageCombatSim(simulation) { MessageId = battleId };
    }

    private static CombatSimFrame BuildCombatFrame(
        NativeFrameDto source, IReadOnlyList<string> vfxKeys)
    {
        var frame = new CombatSimFrame
        {
            Events = new List<ICombatSimEvent>(),
            CardUpdates = new Dictionary<InstanceId, CombatSimCardUpdate>(),
        };
        frame.PlayerUpdates = BuildPlayerUpdate(source.PlayerAttributes,
            source.PlayerHealth, source.Died == "player");
        frame.OpponentUpdates = BuildPlayerUpdate(source.OpponentAttributes,
            source.OpponentHealth, source.Died == "opponent");
        foreach (NativeCardTransitionDto transition in source.CardAttributes)
        {
            if (!Enum.TryParse(transition.Attribute, false, out ECardAttributeType attribute))
                continue;
            var id = new InstanceId(transition.CardId);
            if (!frame.CardUpdates.TryGetValue(id, out CombatSimCardUpdate? update))
            {
                update = new CombatSimCardUpdate
                {
                    CardInstanceId = id,
                    Attributes = new Dictionary<ECardAttributeType,
                        CombatSimCardAttributeUpdate>(),
                };
                frame.CardUpdates[id] = update;
            }
            update.Attributes[attribute] = new CombatSimCardAttributeUpdate
            {
                AttributeType = attribute,
                PreviousValue = transition.Previous,
                CurrentValue = transition.Current,
            };
        }
        foreach (NativeCardStateTransitionDto transition in source.CardStates)
        {
            if (!Enum.TryParse(transition.Previous, false, out ECardState previous) ||
                !Enum.TryParse(transition.Current, false, out ECardState current))
                continue;
            var id = new InstanceId(transition.CardId);
            if (!frame.CardUpdates.TryGetValue(id, out CombatSimCardUpdate? update))
            {
                update = new CombatSimCardUpdate
                {
                    CardInstanceId = id,
                    Attributes = new Dictionary<ECardAttributeType,
                        CombatSimCardAttributeUpdate>(),
                };
                frame.CardUpdates[id] = update;
            }
            update.State = new CombatSimCardStateUpdate
            {
                PreviousValue = previous,
                CurrentValue = current,
            };
        }
        int effectIndex = 0;
        foreach (NativeEffectDto effect in source.Effects)
        {
            EActionCommandType action = ParseAction(effect.ActionType, effect.Kind);
            if (action == EActionCommandType.None) continue;
            IEffectTarget target = BuildEffectTarget(effect.TargetId);
            string executionContext = string.IsNullOrWhiteSpace(effect.ExecutionContextId)
                ? "local:" + source.Frame + ":" + effectIndex
                : effect.ExecutionContextId!;
            InstanceId? sourceId = string.IsNullOrWhiteSpace(effect.SourceId)
                ? null : new InstanceId(effect.SourceId!);
            InstanceId? triggerSource = string.IsNullOrWhiteSpace(effect.TriggerSourceId)
                ? null : new InstanceId(effect.TriggerSourceId!);
            frame.Events.Add(new CombatSimEventEffectTriggered
            {
                ExecutionContextId = executionContext,
                EffectId = effect.EffectId ?? effectIndex.ToString(),
                Source = sourceId,
                TriggerSource = triggerSource,
                Targets = new List<IEffectTarget> { target },
            });
            frame.Events.Add(new CombatSimEventEffectExecuted
            {
                ExecutionContextId = executionContext,
                EffectId = effect.EffectId ?? effectIndex.ToString(),
                ActionType = action,
                Source = sourceId,
                TriggerSource = triggerSource,
                Target = target,
                VfxIndex = string.IsNullOrWhiteSpace(effect.VfxOverrideKey)
                    ? null : ResolveVfxIndex(effect.VfxOverrideKey!),
            });
            effectIndex++;
        }
        if (source.Died is not null)
            frame.Events.Add(new CombatSimEventCombatantDied
            {
                CombatantId = source.Died == "opponent"
                    ? ECombatantId.Opponent : ECombatantId.Player,
            });
        return frame;

        int? ResolveVfxIndex(string key)
        {
            for (int index = 0; index < vfxKeys.Count; index++)
                if (string.Equals(vfxKeys[index], key, StringComparison.Ordinal)) return index;
            return null;
        }
    }

    private static CombatSimPlayerUpdate? BuildPlayerUpdate(
        IReadOnlyList<NativeAttributeTransitionDto> attributes,
        IReadOnlyList<NativeHealthTransitionDto> health, bool died)
    {
        if (attributes.Count == 0 && health.Count == 0 && !died) return null;
        var result = new CombatSimPlayerUpdate
        {
            IsPlayerDead = died,
            Attributes = new Dictionary<EPlayerAttributeType, CombatSimPlayerAttributeUpdate>(),
            HealthAdjustments = new List<CombatSimPlayerHealthAdjustment>(),
        };
        foreach (NativeAttributeTransitionDto transition in attributes)
            if (Enum.TryParse(transition.Attribute, false, out EPlayerAttributeType attribute))
                result.Attributes[attribute] = new CombatSimPlayerAttributeUpdate
                {
                    AttributeType = attribute,
                    PreviousValue = transition.Previous,
                    CurrentValue = transition.Current,
                };
        foreach (NativeHealthTransitionDto transition in health)
        {
            EDamageType damageType = Enum.TryParse(
                transition.Kind, false, out EDamageType parsedDamageType)
                ? parsedDamageType
                : transition.Current >= transition.Previous
                    ? EDamageType.Heal : EDamageType.Damage;
            result.HealthAdjustments.Add(new CombatSimPlayerHealthAdjustment
            {
                DamageType = damageType,
                AttributeChanged = transition.Pool == "Shield"
                    ? EPlayerHealthChangeType.Shield : EPlayerHealthChangeType.Health,
                Amount = transition.Current - transition.Previous,
                IsCrit = transition.Critical,
                IsDamageReduced = false,
            });
        }
        return result;
    }

    private static IEffectTarget BuildEffectTarget(string? target) =>
        target == "player" || target == "opponent"
            ? new EffectTargetPlayer
            {
                Target = target == "opponent" ? ECombatantId.Opponent : ECombatantId.Player,
            }
            : new EffectTargetCard { Target = new InstanceId(target ?? string.Empty) };

    private static EActionCommandType MapAction(string kind) => kind switch
    {
        "Damage" => EActionCommandType.PlayerDamage,
        "Heal" or "Regen" => EActionCommandType.PlayerHeal,
        "Shield" => EActionCommandType.PlayerShieldApply,
        "Burn" => EActionCommandType.PlayerBurnApply,
        "Poison" => EActionCommandType.PlayerPoisonApply,
        "Charge" => EActionCommandType.CardCharge,
        "Haste" => EActionCommandType.CardHaste,
        "Slow" => EActionCommandType.CardSlow,
        "Freeze" => EActionCommandType.CardFreeze,
        "ForceUse" or "Use" => EActionCommandType.CardForceUse,
        _ => EActionCommandType.None,
    };

    private static EActionCommandType ParseAction(string? actionType, string fallbackKind)
    {
        if (!string.IsNullOrWhiteSpace(actionType) &&
            Enum.TryParse(actionType, false, out EActionCommandType parsed)) return parsed;
        return MapAction(fallbackKind);
    }

    private static PvpBattleManifest BuildReplayManifest(LineupEnvelopeDto a,
        LineupEnvelopeDto b, NativeProjectionDto projection, string battleId) => new()
    {
        BattleId = battleId,
        RecordedAtUtc = DateTimeOffset.UtcNow,
        CombatKind = "local-lineup-duel",
        Day = 1,
        Hour = 1,
        Participants = new PvpBattleParticipants
        {
            PlayerName = "Lineup A", PlayerHero = a.payload.hero,
            PlayerLevel = a.payload.attributes.GetValueOrDefault("Level", 1),
            OpponentName = "Lineup B", OpponentHero = b.payload.hero,
            OpponentLevel = b.payload.attributes.GetValueOrDefault("Level", 1),
        },
        Outcome = new PvpBattleOutcome
        {
            Result = projection.WinnerId == "player" ? "win" : "loss",
            WinnerCombatantId = projection.WinnerId,
            LoserCombatantId = projection.WinnerId == "player" ? "opponent" : "player",
        },
        Snapshots = new PvpBattleSnapshots
        {
            PlayerHand = SnapshotSet(a.payload.board, "p", false),
            PlayerSkills = SnapshotSet(a.payload.skills, "ps", true),
            OpponentHand = SnapshotSet(b.payload.board, "o", false),
            OpponentSkills = SnapshotSet(b.payload.skills, "os", true),
        },
    };

    private static PvpBattleCardSetCapture SnapshotSet(
        IReadOnlyList<LineupCardDto> cards, string prefix, bool skills) => new()
    {
        Status = cards.Count == 0 ? PvpBattleCaptureStatus.CapturedEmpty :
            PvpBattleCaptureStatus.Captured,
        Source = PvpBattleCaptureSource.LiveRetry,
        Items = cards.Select((card, index) => new PvpBattleCardSnapshot
        {
            InstanceId = "duel:" + prefix + ":" + index + ":" + card.instance_id,
            TemplateId = card.template_id,
            Type = Enum.TryParse(card.type, false, out ECardType type) ? type : ECardType.Item,
            Size = Enum.TryParse(card.size, false, out ECardSize size) ? size : ECardSize.Small,
            Section = EInventorySection.Hand,
            Socket = skills ? null : ParseSocket(card.socket),
            Name = card.name, Tier = card.tier, Enchant = card.enchant,
            Tags = new List<string>(card.tags),
            Attributes = new Dictionary<string, int>(card.attributes),
        }).ToList(),
    };

    private sealed class NativeProjectionDto
    {
        public int FrameCount { get; set; }
        public string? WinnerId { get; set; }
        public List<NativeFrameDto> Frames { get; set; } = new();
        public List<string> VfxKeys { get; set; } = new();
    }

    private sealed class NativeFrameDto
    {
        public int Frame { get; set; }
        public List<NativeAttributeTransitionDto> PlayerAttributes { get; set; } = new();
        public List<NativeAttributeTransitionDto> OpponentAttributes { get; set; } = new();
        public List<NativeHealthTransitionDto> PlayerHealth { get; set; } = new();
        public List<NativeHealthTransitionDto> OpponentHealth { get; set; } = new();
        public List<NativeCardTransitionDto> CardAttributes { get; set; } = new();
        public List<NativeCardStateTransitionDto> CardStates { get; set; } = new();
        public List<NativeEffectDto> Effects { get; set; } = new();
        public string? Died { get; set; }
    }

    private sealed class NativeAttributeTransitionDto
    { public string Attribute { get; set; } = string.Empty; public int Previous { get; set; }
        public int Current { get; set; } }
    private sealed class NativeHealthTransitionDto
    { public string Pool { get; set; } = string.Empty; public int Previous { get; set; }
        public int Current { get; set; } public string Kind { get; set; } = string.Empty;
        public bool Critical { get; set; } }
    private sealed class NativeCardTransitionDto
    { public string CardId { get; set; } = string.Empty;
        public string Attribute { get; set; } = string.Empty; public int Previous { get; set; }
        public int Current { get; set; } }
    private sealed class NativeCardStateTransitionDto
    { public string CardId { get; set; } = string.Empty;
        public string Previous { get; set; } = string.Empty;
        public string Current { get; set; } = string.Empty; }
    private sealed class NativeEffectDto
    { public string Kind { get; set; } = string.Empty; public string? SourceId { get; set; }
        public string? TargetId { get; set; } public string? EffectId { get; set; }
        public string? ActionType { get; set; } public string? ExecutionContextId { get; set; }
        public string? TriggerSourceId { get; set; } public string? VfxOverrideKey { get; set; } }
}
