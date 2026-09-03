using System.Text.Json;

namespace BazaarLab.Combat;

public sealed class CombatRuleRuntime
{
    private long _replayExecutionSequence;
    private const int ScheduledEffectLaneIntervalTicks = 5;
    private const int MaximumEffectsPerDispatch = 10_000;
    private readonly CombatState _state;
    private readonly XorShiftCombatRandom _random;
    private readonly CombatAuraRuntime _auras;
    private readonly HashSet<int> _processedForceUseEventIndexes = [];
    private readonly HashSet<CombatEvent> _processedNestedEvents =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<long> _completedReadyScopes = [];
    private readonly Dictionary<MaterializedEffectDefinition, int>
        _scheduledEffectLaneNextTicks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CombatCardState, int> _chargeReadyPendingTicks =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CombatCardState> _pendingDisableTargets =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CombatCardState> _forceActiveCards =
        new(ReferenceEqualityComparer.Instance);
    private int _executedEffects;
    private int _dispatchDepth;
    private int _deferNonImmediateDepth;
    private long _nextReadyScopeId;

    public CombatRuleRuntime(CombatState state, XorShiftCombatRandom random)
    {
        _state = state;
        _random = random;
        _auras = new CombatAuraRuntime(state, random);
        _auras.Recompute();
    }

    public int FireCard(CombatCardState card)
    {
        _executedEffects = 0;
        FireCardCore(card, force: false, scheduleReadySignals: false);
        return _executedEffects;
    }

    public int FireCardScheduled(CombatCardState card)
    {
        _executedEffects = 0;
        _deferNonImmediateDepth++;
        try
        {
            FireCardCore(card, force: false, scheduleReadySignals: true);
            return _executedEffects;
        }
        finally
        {
            _deferNonImmediateDepth--;
        }
    }

    private void FireCardCore(
        CombatCardState card,
        bool force,
        bool scheduleReadySignals,
        bool bypassOwnEffectScheduling = false)
    {
        if ((card.IsDisabled && !force) || card.IsDestroyed)
        {
            return;
        }
        int ammoMaximum = Math.Max(0, card.Attributes.GetValueOrDefault("AmmoMax"));
        int ammo = card.IntrinsicAttributes.GetValueOrDefault("Ammo");
        // A queued ForceUse still enters the normal UseItem path.  The worker
        // keeps emitting the force-use action when the target is empty, but
        // the item itself does not fire (and therefore produces no CardUsed,
        // fired effects, or ItemUsed signal) until it has ammo again.
        if (ammoMaximum > 0 && ammo <= 0)
        {
            return;
        }
        // UseItem snapshots this activation's multicast before consuming Ammo.
        // Ammo-backed multicast auras update for the next activation, not for
        // the cast batch that is already being opened.
        int multicast = Math.Max(1, card.Attributes.GetValueOrDefault("Multicast", 1));
        if (ammoMaximum > 0 && ammo > 0)
        {
            int afterAmmo = ammo - 1;
            card.SetIntrinsicAttribute("Ammo", afterAmmo);
            _state.Events.Add(new CombatEvent(
                _state.Tick, "CardAttribute:Ammo", card.InstanceId,
                afterAmmo, ammo, card.InstanceId));
            _auras.Recompute();
            DispatchAttributeChanged(card, "Ammo", ammo, afterAmmo);
        }
        for (int cast = 0; cast < multicast; cast++)
        {
            long? readyScopeId = scheduleReadySignals ? ++_nextReadyScopeId : null;
            DispatchCardSubjectTrigger("TTriggerOnBeforeItemUsed", card);
            _state.Events.Add(new CombatEvent(
                _state.Tick, "CardUsed", card.InstanceId, SourceId: card.InstanceId));
            int critChance = Math.Max(0, card.Attributes.GetValueOrDefault("CritChance"));
            bool canCrit = TargetResolver.CanCardCrit(card);
            bool critical = canCrit && (critChance >= 100 ||
                critChance > 0 && _random.Next(100) < critChance);
            List<(CombatCardState Card, MaterializedEffectDefinition Effect)> effects = ActiveEffects(card)
                .Where(effect => effect.Kind == "Ability" &&
                    HasTrigger(effect, "TTriggerOnCardFired"))
                .Select(effect => (card, effect))
                .ToList();
            ExecuteOrdered(
                effects,
                card,
                null,
                critical,
                criticalCompletionCard: critical && scheduleReadySignals ? card : null,
                readyScopeId: readyScopeId,
                bypassScheduling: bypassOwnEffectScheduling);
            if (critical)
            {
                _state.Events.Add(new CombatEvent(_state.Tick, "CardCrit", card.InstanceId));
                if (!scheduleReadySignals)
                {
                    DispatchCardSubjectTrigger("TTriggerOnCardCritted", card);
                }
            }
            if (scheduleReadySignals)
            {
                // Worker UseItem appends signal 2 (ItemUsed) to its 40-byte
                // ready-signal queue. The first cast is due next tick and
                // multicast completion signals are staggered by five ticks.
                _state.ScheduledReadySignals.Add(new ScheduledReadySignal(
                    card,
                    "TTriggerOnItemUsed",
                    checked(_state.Tick + cast * 5 + 1),
                    readyScopeId));
            }
            else
            {
                // Keep the direct API synchronous for deterministic unit use;
                // the formal CombatScheduler path uses the Worker queue above.
                DispatchCardSubjectTrigger("TTriggerOnItemUsed", card);
            }
        }
    }

    public int StartFight()
    {
        _executedEffects = 0;
        DispatchGlobalTrigger("TTriggerOnFightStarted");
        return _executedEffects;
    }

    public int StartFightScheduled()
    {
        _executedEffects = 0;
        _deferNonImmediateDepth++;
        try
        {
            DispatchGlobalTrigger("TTriggerOnFightStarted");
            return _executedEffects;
        }
        finally
        {
            _deferNonImmediateDepth--;
        }
    }

    public int RecomputeAuras() => _auras.Recompute();

    public int ProcessScheduledForceUses()
    {
        ScheduledForceUse[] due = _state.ScheduledForceUses
            .Where(value => value.DueTick <= _state.Tick)
            .OrderBy(value => value.DueTick)
            .ThenBy(value => value.Card.Owner.Cards.IndexOf(value.Card))
            .ThenBy(value => value.Card.BoardPosition)
            .ToArray();
        _deferNonImmediateDepth++;
        try
        {
            foreach (ScheduledForceUse scheduled in due)
            {
                _state.ScheduledForceUses.Remove(scheduled);
                if (scheduled.Card.Attributes.GetValueOrDefault("Freeze") > 0)
                {
                    _state.Events.Add(new CombatEvent(
                        _state.Tick, "ForceUseBlockedByFreeze", scheduled.Card.InstanceId));
                }
                else if (scheduled.Card.IsDisabled && !scheduled.AllowDisabled)
                {
                    _state.Events.Add(new CombatEvent(
                        _state.Tick, "ForceUseBlockedByDisable", scheduled.Card.InstanceId));
                }
                else
                {
                    // Worker releases a queued force-use into UseItem on its
                    // due frame. Its own fired effects execute immediately,
                    // while nested/critical reactions remain in their lanes.
                    if (scheduled.AllowDisabled)
                    {
                        _forceActiveCards.Add(scheduled.Card);
                    }
                    try
                    {
                        FireCardCore(
                            scheduled.Card,
                            force: true,
                            scheduleReadySignals: true,
                            bypassOwnEffectScheduling: true);
                    }
                    finally
                    {
                        _forceActiveCards.Remove(scheduled.Card);
                    }
                }
            }
        }
        finally
        {
            _deferNonImmediateDepth--;
        }
        return due.Length;
    }

    public int ProcessScheduledChargeReadyUses()
    {
        ScheduledChargeReadyUse[] due = _state.ScheduledChargeReadyUses
            .Where(value => value.DueTick <= _state.Tick)
            .OrderBy(value => value.DueTick)
            .ThenBy(value => value.Card.Owner.Cards.IndexOf(value.Card))
            .ThenBy(value => value.Card.BoardPosition)
            .ToArray();
        int fired = 0;
        _deferNonImmediateDepth++;
        try
        {
            foreach (ScheduledChargeReadyUse scheduled in due)
            {
                _state.ScheduledChargeReadyUses.Remove(scheduled);
                CombatCardState card = scheduled.Card;
                if (card.IsDisabled || card.IsDestroyed)
                {
                    continue;
                }
                if (card.Attributes.GetValueOrDefault("Freeze") > 0)
                {
                    _state.ScheduledChargeReadyUses.Add(
                        new ScheduledChargeReadyUse(card, checked(_state.Tick + 1)));
                    continue;
                }
                int ammoMaximum = Math.Max(0,
                    card.Attributes.GetValueOrDefault("AmmoMax"));
                if (ammoMaximum > 0 &&
                    card.IntrinsicAttributes.GetValueOrDefault("Ammo") <= 0)
                {
                    continue;
                }
                FireCardCore(card, force: false, scheduleReadySignals: true);
                fired++;
            }
        }
        finally
        {
            _deferNonImmediateDepth--;
        }
        return fired;
    }

    public int ProcessScheduledRuleEffects()
    {
        ScheduledRuleEffect[] released = _state.ScheduledRuleEffects
            .Where(value => value.DueTick <= _state.Tick)
            .GroupBy(
                value => value.Effect,
                (IEqualityComparer<MaterializedEffectDefinition>)ReferenceEqualityComparer.Instance)
            .Where(group => _state.Tick >=
                _scheduledEffectLaneNextTicks.GetValueOrDefault(group.Key))
            .Select(group => group.OrderBy(value => value.DueTick).First())
            .OrderByDescending(value => Priority(value.Effect))
            .ThenBy(value => value.DueTick)
            .ToArray();
        int before = _executedEffects;
        _deferNonImmediateDepth++;
        try
        {
            foreach (ScheduledRuleEffect scheduled in released)
            {
                _state.ScheduledRuleEffects.Remove(scheduled);
                _scheduledEffectLaneNextTicks[scheduled.Effect] = checked(
                    _state.Tick + ScheduledEffectLaneIntervalTicks);
                ExecuteOrdered(
                    new List<(CombatCardState Card, MaterializedEffectDefinition Effect)>
                        { (scheduled.Card, scheduled.Effect) },
                    scheduled.TriggerSource,
                    scheduled.TriggerTarget,
                    scheduled.Critical,
                    scheduled.AttributeDelta,
                    bypassScheduling: true,
                    prerequisitesAlreadySatisfied: true);
                if (scheduled.EmitCardCrittedAfterCompletion)
                {
                    DispatchCardSubjectTrigger("TTriggerOnCardCritted", scheduled.Card);
                }
                if (scheduled.CompletesReadyScope && scheduled.ReadyScopeId is long scopeId)
                {
                    _completedReadyScopes.Add(scopeId);
                    ProcessReadySignals(scopeId);
                }
            }
        }
        finally
        {
            _deferNonImmediateDepth--;
        }
        return _executedEffects - before;
    }

    public int ProcessReadySignals() => ProcessReadySignals(null);

    private int ProcessReadySignals(long? scopedReadyId)
    {
        ScheduledReadySignal[] due = _state.ScheduledReadySignals
            .Where(value => value.DueTick <= _state.Tick &&
                (scopedReadyId is null || value.ReadyScopeId == scopedReadyId) &&
                (value.ReadyScopeId is null ||
                    _completedReadyScopes.Contains(value.ReadyScopeId.Value)))
            .OrderBy(value => value.DueTick)
            .ToArray();
        int before = _executedEffects;
        _deferNonImmediateDepth++;
        try
        {
            foreach (ScheduledReadySignal signal in due)
            {
                _state.ScheduledReadySignals.Remove(signal);
                DispatchCardSubjectTrigger(signal.TriggerType, signal.Card);
            }
        }
        finally
        {
            _deferNonImmediateDepth--;
        }
        return _executedEffects - before;
    }

    public int ProcessSandstormEvents(int firstEvent)
    {
        if (!_state.Events.Skip(firstEvent).Any(value => value.Kind == "Sandstorm"))
        {
            return 0;
        }
        int before = _executedEffects;
        DispatchGlobalTrigger("TTriggerOnSandstorm");
        return _executedEffects - before;
    }

    public void ProcessEnginePlayerEvents(int firstEvent) =>
        DispatchNewPlayerAttributeEvents(firstEvent);

    public int ResolvePlayerDeaths()
    {
        int before = _executedEffects;
        foreach (CombatantState combatant in _state.Combatants.ToArray())
        {
            if (combatant.Health <= 0)
            {
                DispatchPlayerSubjectTrigger("TTriggerOnPlayerDied", combatant);
            }
        }
        return _executedEffects - before;
    }

    public void AdvancePlayerStateOneTick()
    {
        AdvanceEnrageOneTick();
        ApplyTempoGainOneTick();
    }

    public void AdvanceEnrageOneTick()
    {
        foreach (CombatantState combatant in _state.Combatants)
        {
            int enragedDuration = combatant.Attributes.GetValueOrDefault("EnragedDuration");
            if (enragedDuration > 0)
            {
                int next = Math.Max(0, enragedDuration - CombatEngine.TickMilliseconds);
                combatant.SetIntrinsicAttribute("EnragedDuration", Math.Max(0, checked(
                    combatant.IntrinsicAttributes.GetValueOrDefault("EnragedDuration") -
                    (enragedDuration - next))));
                _state.Events.Add(new CombatEvent(
                    _state.Tick, "PlayerAttribute:EnragedDuration", combatant.Id,
                    next, enragedDuration));
                if (next == 0)
                {
                    int oldEnraged = combatant.Attributes.GetValueOrDefault("Enraged");
                    combatant.SetIntrinsicAttribute("Enraged", 0);
                    combatant.SetIntrinsicAttribute("Rage", 0);
                    _state.Events.Add(new CombatEvent(
                        _state.Tick, "PlayerAttribute:Enraged", combatant.Id, 0, oldEnraged));
                    _auras.Recompute();
                    DispatchPlayerSubjectTrigger("TTriggerOnPlayerEnrageEnded", combatant);
                }
            }
        }
    }

    public void ApplyTempoGainOneTick()
    {
        foreach (CombatantState combatant in _state.Combatants)
        {
            if (combatant.TempoCooldownRemainingMilliseconds > CombatEngine.TickMilliseconds)
            {
                combatant.TempoCooldownRemainingMilliseconds -= CombatEngine.TickMilliseconds;
                continue;
            }
            int maximum = Math.Max(0,
                combatant.Attributes.GetValueOrDefault("TempoGainCooldownMax"));
            if (maximum >= 100_000_000)
            {
                combatant.TempoCooldownRemainingMilliseconds = maximum;
                continue;
            }
            int before = combatant.Attributes.GetValueOrDefault("Tempo");
            int after = checked(before + 1);
            combatant.SetIntrinsicAttribute("Tempo", checked(
                combatant.IntrinsicAttributes.GetValueOrDefault("Tempo") + 1));
            _state.Events.Add(new CombatEvent(
                _state.Tick, "PlayerAttribute:Tempo", combatant.Id, after, before));
            _auras.Recompute();
            DispatchPlayerAttributeChanged(combatant, "Tempo", before, after);
            int flat = combatant.Attributes.GetValueOrDefault("FlatTempoGainCooldownReduction");
            int percent = Math.Clamp(
                combatant.Attributes.GetValueOrDefault("PercentTempoGainCooldownReduction"), -100, 100);
            combatant.TempoCooldownRemainingMilliseconds = Math.Max(1,
                checked((int)((long)Math.Max(1, maximum - flat) * (100 - percent) / 100)));
        }
    }

    private void DispatchGlobalTrigger(string triggerType)
    {
        List<(CombatCardState Card, MaterializedEffectDefinition Effect)> effects = _state.Combatants
            .SelectMany(combatant => combatant.Cards)
            .Where(card => !card.IsDisabled && !card.IsDestroyed)
            .SelectMany(card => ActiveEffects(card)
                .Where(effect => effect.Kind == "Ability" && HasTrigger(effect, triggerType))
                .Select(effect => (card, effect)))
            .ToList();
        ExecuteOrdered(effects, null, null);
    }

    private void DispatchCardSubjectTrigger(string triggerType, CombatCardState subject)
    {
        var effects = new List<(CombatCardState Card, MaterializedEffectDefinition Effect)>();
        foreach (CombatCardState owner in ActiveCards())
        {
            foreach (MaterializedEffectDefinition effect in ActiveEffects(owner))
            {
                if (effect.Kind != "Ability" ||
                    !TryGetMatchingTrigger(effect, triggerType, out JsonElement trigger))
                {
                    continue;
                }
                var context = new CombatActionContext(
                    _state, owner, _random, TriggerSource: subject, TriggerTarget: subject);
                List<CombatCardState> subjects = TargetResolver.ResolveCardTarget(
                    trigger.GetObjectOrNull("Subject"), context, null);
                if (subjects.Contains(subject))
                {
                    effects.Add((owner, effect));
                }
            }
        }
        ExecuteOrdered(effects, subject, subject);
    }

    private void DispatchAttributeChanged(
        CombatCardState changedCard,
        string attribute,
        int previous,
        int current)
    {
        if (current == previous)
        {
            return;
        }
        var effects = new List<(CombatCardState Card, MaterializedEffectDefinition Effect)>();
        foreach (CombatCardState owner in ActiveCards())
        {
            foreach (MaterializedEffectDefinition effect in ActiveEffects(owner))
            {
                if (effect.Kind != "Ability" || !TryGetMatchingTrigger(
                    effect, "TTriggerOnCardAttributeChanged", out JsonElement trigger))
                {
                    continue;
                }
                if (!string.Equals(
                    trigger.GetStringOrNull("AttributeChanged"), attribute, StringComparison.Ordinal))
                {
                    continue;
                }
                string changeType = trigger.GetStringOrNull("ChangeType") ?? "Gain";
                bool statusAttribute = attribute is "Haste" or "Slow" or "Freeze" or "Flying";
                bool gained = statusAttribute ? previous <= 0 && current > 0 : current > previous;
                bool lost = statusAttribute ? previous > 0 && current <= 0 : current < previous;
                if (changeType == "Gain" && !gained ||
                    changeType == "Loss" && !lost)
                {
                    continue;
                }
                var context = new CombatActionContext(
                    _state, owner, _random, TriggerSource: changedCard, TriggerTarget: changedCard);
                List<CombatCardState> subjects = TargetResolver.ResolveCardTarget(
                    trigger.GetObjectOrNull("Subject"), context, null);
                if (subjects.Contains(changedCard))
                {
                    effects.Add((owner, effect));
                }
            }
        }
        ExecuteOrdered(effects, changedCard, changedCard,
            attributeDelta: checked(current - previous));
    }

    private void ExecuteOrdered(
        List<(CombatCardState Card, MaterializedEffectDefinition Effect)> effects,
        CombatCardState? triggerSource,
        CombatCardState? triggerTarget,
        bool critical = false,
        int? attributeDelta = null,
        bool bypassScheduling = false,
        CombatCardState? criticalCompletionCard = null,
        long? readyScopeId = null,
        bool prerequisitesAlreadySatisfied = false)
    {
        if (++_dispatchDepth > 128)
        {
            _dispatchDepth--;
            string tail = string.Join(" | ", _state.Events.TakeLast(24).Select(value =>
                $"{value.Kind}:{value.TargetId}:{value.SecondaryAmount}->{value.Amount}"));
            throw new InvalidOperationException(
                "Combat trigger recursion exceeded its safety limit. Tail=" + tail);
        }
        try
        {
        List<(CombatCardState Card, MaterializedEffectDefinition Effect)> eligible =
            prerequisitesAlreadySatisfied
                ? effects
                : effects.Where(item => RulePrerequisiteEvaluator.AreSatisfied(
                    item.Effect,
                    new CombatActionContext(
                        _state, item.Card, _random, triggerSource, triggerTarget, critical,
                        attributeDelta)))
                    .ToList();
        (CombatCardState Card, MaterializedEffectDefinition Effect)[] ordered = eligible
            .OrderByDescending(item => Priority(item.Effect))
            .ThenBy(item => item.Card.Owner.Cards.IndexOf(item.Card))
            .ThenBy(item => item.Card.BoardPosition)
            .ThenBy(item => item.Effect.Id, StringComparer.Ordinal)
            .ToArray();
        int highestPriority = ordered.Length == 0 ? int.MinValue : Priority(ordered[0].Effect);
        int lastHighestIndex = Array.FindLastIndex(
            ordered, item => Priority(item.Effect) == highestPriority);
        (CombatCardState Card, MaterializedEffectDefinition Effect)? readyCompletion =
            readyScopeId is null || eligible.Count == 0 ? null : eligible[^1];
        int readyCompletionIndex = readyCompletion is null
            ? -1
            : Array.FindIndex(ordered, item =>
                ReferenceEquals(item.Card, readyCompletion.Value.Card) &&
                ReferenceEquals(item.Effect, readyCompletion.Value.Effect));
        if (readyScopeId is long emptyScopeId && readyCompletionIndex < 0)
        {
            _completedReadyScopes.Add(emptyScopeId);
        }
        for (int orderedIndex = 0; orderedIndex < ordered.Length; orderedIndex++)
        {
            (CombatCardState card, MaterializedEffectDefinition effect) = ordered[orderedIndex];
            bool emitCritAfter = criticalCompletionCard is not null &&
                orderedIndex == lastHighestIndex;
            bool completesReadyScope = readyScopeId is not null &&
                orderedIndex == readyCompletionIndex;
            if (!bypassScheduling && _deferNonImmediateDepth > 0 && Priority(effect) < 500)
            {
                _state.ScheduledRuleEffects.Add(new ScheduledRuleEffect(
                    card, effect, checked(_state.Tick + 1), triggerSource, triggerTarget,
                    critical, attributeDelta, emitCritAfter, readyScopeId,
                    completesReadyScope));
                continue;
            }
            var actionContext = new CombatActionContext(
                _state, card, _random, triggerSource, triggerTarget, critical, attributeDelta,
                (actionType, target) =>
                {
                    int appliedEventIndex = _state.Events.Count - 1;
                    CombatEvent appliedEvent = _state.Events[appliedEventIndex];
                    if (actionType == "TActionCardCharge")
                    {
                        ApplyChargeAndScheduleReadyUse(target);
                    }
                    _auras.Recompute();
                    int nestedAttributeEventStart = _state.Events.Count;
                    DispatchNewAttributeEvents(appliedEventIndex);
                    _processedNestedEvents.Add(appliedEvent);
                    foreach (CombatEvent nestedEvent in
                        _state.Events.Skip(nestedAttributeEventStart))
                    {
                        _processedNestedEvents.Add(nestedEvent);
                    }
                    if (PerformedTrigger(actionType) is string performedTrigger)
                    {
                        RunNestedDispatch(() =>
                            DispatchCardPerformedTrigger(performedTrigger, card, target));
                    }
                });
            if (++_executedEffects > MaximumEffectsPerDispatch)
            {
                throw new InvalidOperationException("Combat trigger dispatch exceeded its safety limit.");
            }
            int firstNewEvent = _state.Events.Count;
            ActionExecutionResult result = CombatActionDispatcher.Execute(
                effect,
                actionContext);
            int directEventEnd = _state.Events.Count;
            string executionContextId = "local:" + _state.Tick + ":" +
                (++_replayExecutionSequence) + ":" + card.InstanceId + ":" + effect.Id;
            for (int eventIndex = firstNewEvent; eventIndex < directEventEnd; eventIndex++)
            {
                CombatEvent directEvent = _state.Events[eventIndex];
                if (directEvent.ExecutionContextId is not null) continue;
                bool wasProcessedAsNested = _processedNestedEvents.Remove(directEvent);
                string directActionType = directEvent.ActionType ?? result.ActionType;
                CombatEvent annotatedEvent = directEvent with
                {
                    EffectId = effect.Id,
                    ActionType = directActionType,
                    ExecutionContextId = executionContextId,
                    TriggerSourceId = triggerSource?.InstanceId,
                    VfxOverrideKey = effect.VfxOverrideKey,
                    // The client renders the localized critical prefix from
                    // CombatSimPlayerHealthAdjustment.IsCrit.  Persist criticality
                    // on the concrete adjustment-producing event so delayed lanes
                    // do not have to infer it from a CardCrit event on an earlier tick.
                    Critical = critical && directEvent.Kind is
                        "CardDamage" or "Heal" or "Shield",
                };
                _state.Events[eventIndex] = annotatedEvent;
                if (wasProcessedAsNested)
                {
                    _processedNestedEvents.Add(annotatedEvent);
                }
            }
            if (!result.Supported)
            {
                _state.Events.Add(new CombatEvent(
                    _state.Tick, "UnsupportedAction:" + result.ActionType, card.InstanceId));
            }
            DispatchDisableRequests(firstNewEvent, card);
            DispatchDestroyRequests(firstNewEvent, card);
            _auras.Recompute();
            DispatchNewAttributeEvents(firstNewEvent);
            DispatchNewPlayerAttributeEvents(firstNewEvent);
            DispatchForceUseEvents(firstNewEvent, Priority(effect) >= 500);
            DispatchLifecycleEvents(firstNewEvent, card);
            if (UnprocessedEvents(firstNewEvent).Any(value => value.Kind == "OverHeal"))
            {
                DispatchCardSubjectTrigger("TTriggerOnCardPerformedOverHeal", card);
            }
            if (result.Supported && result.TargetCount > 0 &&
                !IsInterleavedPerformedCardAction(result.ActionType) &&
                PerformedTrigger(result.ActionType) is string performedTrigger)
            {
                string eventKind = result.ActionType.StartsWith(
                    "TActionCard", StringComparison.Ordinal)
                    ? "Card" + result.ActionType["TActionCard".Length..]
                    : string.Empty;
                CombatCardState[] performedTargets = string.IsNullOrEmpty(eventKind)
                    ? []
                    : UnprocessedEvents(firstNewEvent)
                        .Where(value => value.Kind == eventKind && value.TargetId is not null)
                        .Select(value => _state.Combatants.SelectMany(player => player.Cards)
                            .FirstOrDefault(target => target.InstanceId == value.TargetId))
                        .Where(value => value is not null).Cast<CombatCardState>()
                        .Distinct().ToArray();
                // A destroy request can be replaced or blocked by immunity.
                // In that case no destruction was performed and the performed
                // signal must not be synthesized from TargetCount alone.
                if (performedTargets.Length == 0 &&
                    result.ActionType == "TActionCardDestroy")
                {
                    continue;
                }
                if (performedTargets.Length == 0)
                {
                    DispatchCardSubjectTrigger(performedTrigger, card);
                }
                else
                {
                    // Worker ApplyEffect iterates selected card targets and calls
                    // the performed-signal mapper once inside that target loop.
                    foreach (CombatCardState performedTarget in performedTargets)
                    {
                        DispatchCardPerformedTrigger(
                            performedTrigger, card, performedTarget);
                    }
                }
            }
            if (emitCritAfter)
            {
                DispatchCardSubjectTrigger("TTriggerOnCardCritted", criticalCompletionCard!);
            }
            if (completesReadyScope && readyScopeId is long completedScopeId)
            {
                _completedReadyScopes.Add(completedScopeId);
            }
        }
        }
        finally
        {
            _dispatchDepth--;
        }
    }

    private void ApplyChargeAndScheduleReadyUse(CombatCardState card)
    {
        int charge = Math.Max(0,
            card.IntrinsicAttributes.GetValueOrDefault("Charge"));
        int before = card.CooldownRemainingMilliseconds;
        if (charge <= 0)
        {
            return;
        }

        if (before <= 0)
        {
            if (_chargeReadyPendingTicks.GetValueOrDefault(card) != _state.Tick ||
                card.Attributes.GetValueOrDefault("Freeze") > 0 ||
                card.IsDisabled || card.IsDestroyed ||
                card.GetEffectiveCooldownMilliseconds() <= 0)
            {
                // A previously-ready or frozen card keeps the charge for the
                // normal item phase. Only a second independent charge in this
                // same effect frame is proven to enter the reset cycle.
                return;
            }
            card.CooldownRemainingMilliseconds = card.GetEffectiveCooldownMilliseconds();
            _chargeReadyPendingTicks.Remove(card);
            if (!_state.ScheduledChargeReadyUses.Any(value => ReferenceEquals(value.Card, card)))
            {
                _state.ScheduledChargeReadyUses.Add(
                    new ScheduledChargeReadyUse(card, checked(_state.Tick + 1)));
            }
            before = card.CooldownRemainingMilliseconds;
        }

        card.CooldownRemainingMilliseconds = Math.Max(0, before - charge);
        card.SetIntrinsicAttribute("Charge", 0);
        if (card.CooldownRemainingMilliseconds > 0)
        {
            return;
        }
        // Do not reset on the first readying charge. If another independent
        // charge targets this card in the same frame, the branch above resets
        // first and applies that later charge to the new cooldown cycle.
        _chargeReadyPendingTicks[card] = _state.Tick;
    }

    private void DispatchNewPlayerAttributeEvents(int firstEvent)
    {
        foreach (CombatEvent combatEvent in UnprocessedEvents(firstEvent))
        {
            string? attribute = combatEvent.Kind.StartsWith(
                "PlayerModifyAttribute:", StringComparison.Ordinal)
                ? combatEvent.Kind["PlayerModifyAttribute:".Length..]
                : combatEvent.Kind.StartsWith("PlayerAttribute:", StringComparison.Ordinal)
                    ? combatEvent.Kind["PlayerAttribute:".Length..]
                    : null;
            if (attribute is null || combatEvent.TargetId is null)
            {
                continue;
            }
            CombatantState? changed = _state.Combatants
                .FirstOrDefault(player => player.Id == combatEvent.TargetId);
            if (changed is not null)
            {
                DispatchPlayerAttributeChanged(
                    changed, attribute, combatEvent.SecondaryAmount, combatEvent.Amount);
                if (attribute == "Rage" && combatEvent.Amount > combatEvent.SecondaryAmount)
                {
                    bool wasEnraged = changed.Attributes.GetValueOrDefault("Enraged") > 0;
                    DispatchPlayerSubjectTrigger("TTriggerOnPlayerRaged", changed);
                    if (wasEnraged)
                    {
                        DispatchPlayerSubjectTrigger("TTriggerOnPlayerRagedWhileEnraged", changed);
                    }
                    else if (combatEvent.Amount >= Math.Max(1,
                        changed.Attributes.GetValueOrDefault("RageMax", 100)))
                    {
                        changed.SetIntrinsicAttribute("Enraged", 1);
                        int duration = Math.Max(1,
                            changed.Attributes.GetValueOrDefault("EnragedDurationMax", 5000));
                        changed.SetIntrinsicAttribute("EnragedDuration", duration);
                        _state.Events.Add(new CombatEvent(
                            _state.Tick, "PlayerAttribute:Enraged", changed.Id, 1, 0));
                        _state.Events.Add(new CombatEvent(
                            _state.Tick, "PlayerAttribute:EnragedDuration", changed.Id, duration, 0));
                        _auras.Recompute();
                        DispatchPlayerSubjectTrigger("TTriggerOnPlayerEnraged", changed);
                    }
                }
            }
        }
    }

    private void DispatchPlayerSubjectTrigger(string triggerType, CombatantState subject)
    {
        var effects = new List<(CombatCardState Card, MaterializedEffectDefinition Effect)>();
        foreach (CombatCardState owner in ActiveCards())
        {
            foreach (MaterializedEffectDefinition effect in ActiveEffects(owner))
            {
                if (effect.Kind != "Ability" ||
                    !TryGetMatchingTrigger(effect, triggerType, out JsonElement trigger))
                {
                    continue;
                }
                var context = new CombatActionContext(_state, owner, _random);
                if (TargetResolver.ResolvePlayers(
                    trigger.GetObjectOrNull("Subject"), context).Contains(subject))
                {
                    effects.Add((owner, effect));
                }
            }
        }
        ExecuteOrdered(effects, null, null);
    }

    private void DispatchCardPerformedTrigger(
        string triggerType,
        CombatCardState performer,
        CombatCardState affectedTarget)
    {
        var effects = new List<(CombatCardState Card, MaterializedEffectDefinition Effect)>();
        foreach (CombatCardState owner in ActiveCards())
        {
            foreach (MaterializedEffectDefinition effect in ActiveEffects(owner))
            {
                if (effect.Kind != "Ability" ||
                    !TryGetMatchingTrigger(effect, triggerType, out JsonElement trigger))
                {
                    continue;
                }
                var context = new CombatActionContext(
                    _state, owner, _random,
                    TriggerSource: performer, TriggerTarget: affectedTarget);
                JsonElement? subjectDefinition = trigger.GetObjectOrNull("Subject");
                bool subjectMatches = subjectDefinition is null ||
                    TargetResolver.ResolveCardTarget(
                        subjectDefinition, context, null).Contains(performer);
                JsonElement? targetDefinition = trigger.GetObjectOrNull("Target");
                bool targetMatches = targetDefinition is null ||
                    TargetResolver.ResolveCardTarget(targetDefinition, context, null)
                        .Contains(affectedTarget);
                if (subjectMatches && targetMatches)
                {
                    effects.Add((owner, effect));
                }
            }
        }
        ExecuteOrdered(effects, performer, affectedTarget);
    }

    private void DispatchPlayerAttributeChanged(
        CombatantState changed,
        string attribute,
        int previous,
        int current)
    {
        var effects = new List<(CombatCardState Card, MaterializedEffectDefinition Effect)>();
        foreach (CombatCardState owner in ActiveCards())
        {
            foreach (MaterializedEffectDefinition effect in ActiveEffects(owner))
            {
                if (effect.Kind != "Ability" || !TryGetMatchingTrigger(
                    effect, "TTriggerOnPlayerAttributeChanged", out JsonElement trigger))
                {
                    continue;
                }
                if (!string.Equals(trigger.GetStringOrNull("AttributeType"), attribute,
                    StringComparison.Ordinal))
                {
                    continue;
                }
                string changeType = trigger.GetStringOrNull("ChangeType") ?? "Gain";
                if (changeType == "Gain" && current <= previous ||
                    changeType == "Loss" && current >= previous)
                {
                    continue;
                }
                var context = new CombatActionContext(_state, owner, _random);
                if (TargetResolver.ResolvePlayers(
                    trigger.GetObjectOrNull("Subject"), context).Contains(changed))
                {
                    effects.Add((owner, effect));
                }
            }
        }
        ExecuteOrdered(effects, null, null,
            attributeDelta: checked(current - previous));
    }

    private void DispatchForceUseEvents(int firstEvent, bool immediate)
    {
        int endEvent = _state.Events.Count;
        for (int eventIndex = firstEvent; eventIndex < endEvent; eventIndex++)
        {
            CombatEvent combatEvent = _state.Events[eventIndex];
            if (_processedNestedEvents.Contains(combatEvent))
            {
                continue;
            }
            if (combatEvent.Kind != "ForceUse" || combatEvent.TargetId is null)
            {
                continue;
            }
            if (!_processedForceUseEventIndexes.Add(eventIndex))
            {
                continue;
            }
            CombatCardState? target = _state.Combatants.SelectMany(value => value.Cards)
                .FirstOrDefault(card => card.InstanceId == combatEvent.TargetId);
            if (target is not null)
            {
                _state.ScheduledForceUses.Add(new ScheduledForceUse(
                    target, immediate ? _state.Tick : checked(_state.Tick + 1),
                    _pendingDisableTargets.Contains(target)));
            }
        }
    }

    private void DispatchDisableRequests(int firstEvent, CombatCardState performer)
    {
        foreach (CombatEvent combatEvent in UnprocessedEvents(firstEvent))
        {
            if (combatEvent.Kind != "CardDisableRequested" || combatEvent.TargetId is null)
            {
                continue;
            }
            CombatCardState? target = _state.Combatants.SelectMany(value => value.Cards)
                .FirstOrDefault(card => card.InstanceId == combatEvent.TargetId);
            if (target is null || target.IsDisabled || target.IsDestroyed)
            {
                continue;
            }

            string originalTemplateId = target.Definition.TemplateId;
            _pendingDisableTargets.Add(target);
            try
            {
                RunNestedDispatch(() => DispatchCardLifecycleTrigger(
                    "TTriggerOnBeforeCardDestroyed", performer, target));
            }
            finally
            {
                _pendingDisableTargets.Remove(target);
            }
            if (!string.Equals(originalTemplateId, target.Definition.TemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                _state.Events.Add(new CombatEvent(
                    _state.Tick, "CardDisableReplaced", target.InstanceId,
                    SourceId: performer.InstanceId));
                continue;
            }
            if (target.Attributes.GetValueOrDefault("DestroyImmunity") > 0)
            {
                _state.Events.Add(new CombatEvent(
                    _state.Tick, "CardDisableBlocked", target.InstanceId,
                    SourceId: performer.InstanceId));
                continue;
            }

            target.IsDisabled = true;
            _state.Events.Add(new CombatEvent(
                _state.Tick, "CardDisabled", target.InstanceId,
                SourceId: performer.InstanceId,
                EffectId: combatEvent.EffectId,
                ActionType: combatEvent.ActionType,
                ExecutionContextId: combatEvent.ExecutionContextId,
                TriggerSourceId: combatEvent.TriggerSourceId,
                VfxOverrideKey: combatEvent.VfxOverrideKey));
            _auras.Recompute();
            RunNestedDispatch(() => DispatchCardPerformedTrigger(
                "TTriggerOnCardPerformedDestruction", performer, target));
        }
    }

    private void DispatchLifecycleEvents(int firstEvent, CombatCardState performer)
    {
        foreach (CombatEvent combatEvent in UnprocessedEvents(firstEvent))
        {
            string? triggerType = combatEvent.Kind switch
            {
                "CardDisabled" => "TTriggerOnCardDisabled",
                "CardRepaired" => "TTriggerOnCardRepaired",
                "CardTransformed" => "TTriggerOnCardTransformed",
                "CardUpgraded" => "TTriggerOnCardUpgraded",
                _ => null,
            };
            if (triggerType is null || combatEvent.TargetId is null)
            {
                continue;
            }
            CombatCardState? target = _state.Combatants.SelectMany(value => value.Cards)
                .FirstOrDefault(card => card.InstanceId == combatEvent.TargetId);
            if (target is null)
            {
                continue;
            }
            DispatchCardLifecycleTrigger(triggerType, performer, target);
        }
    }

    private void DispatchDestroyRequests(int firstEvent, CombatCardState performer)
    {
        foreach (CombatEvent combatEvent in UnprocessedEvents(firstEvent))
        {
            if (combatEvent.Kind != "CardDestroyRequested" || combatEvent.TargetId is null)
            {
                continue;
            }
            CombatCardState? target = _state.Combatants.SelectMany(value => value.Cards)
                .FirstOrDefault(card => card.InstanceId == combatEvent.TargetId);
            if (target is null || target.IsDestroyed)
            {
                continue;
            }
            string originalTemplateId = target.Definition.TemplateId;
            RunNestedDispatch(() => DispatchCardLifecycleTrigger(
                "TTriggerOnBeforeCardDestroyed", performer, target));
            if (!string.Equals(originalTemplateId, target.Definition.TemplateId,
                StringComparison.OrdinalIgnoreCase))
            {
                _state.Events.Add(new CombatEvent(
                    _state.Tick, "CardDestroyReplaced", target.InstanceId,
                    SourceId: performer.InstanceId));
                continue;
            }
            if (target.Attributes.GetValueOrDefault("DestroyImmunity") > 0)
            {
                _state.Events.Add(new CombatEvent(
                    _state.Tick, "CardDestroyBlocked", target.InstanceId,
                    SourceId: performer.InstanceId));
                continue;
            }
            target.IsDestroyed = true;
            target.IsDisabled = true;
            _state.Events.Add(new CombatEvent(
                _state.Tick, "CardDestroy", target.InstanceId,
                SourceId: performer.InstanceId));
            _auras.Recompute();
            RunNestedDispatch(() => DispatchCardPerformedTrigger(
                "TTriggerOnCardPerformedDestruction", performer, target));
        }
    }

    private void DispatchCardLifecycleTrigger(
        string triggerType,
        CombatCardState performer,
        CombatCardState target)
    {
        var effects = new List<(CombatCardState Card, MaterializedEffectDefinition Effect)>();
        foreach (CombatCardState owner in ActiveCards())
        {
            foreach (MaterializedEffectDefinition effect in ActiveEffects(owner))
            {
                if (effect.Kind != "Ability" ||
                    !TryGetMatchingTrigger(effect, triggerType, out JsonElement trigger))
                {
                    continue;
                }
                var context = new CombatActionContext(
                    _state, owner, _random, TriggerSource: performer, TriggerTarget: target);
                JsonElement? subjectDefinition = trigger.GetObjectOrNull("Subject");
                bool subjectMatches = subjectDefinition is null || TargetResolver.ResolveCardTarget(
                    subjectDefinition, context, null).Contains(target);
                JsonElement? sourceDefinition = trigger.GetObjectOrNull("Source");
                bool sourceMatches = sourceDefinition is null || TargetResolver.ResolveCardTarget(
                    sourceDefinition, context, null).Contains(performer);
                if (subjectMatches && sourceMatches)
                {
                    effects.Add((owner, effect));
                }
            }
        }
        ExecuteOrdered(effects, performer, target);
    }

    private void DispatchNewAttributeEvents(int firstEvent)
    {
        CombatEvent[] events = UnprocessedEvents(firstEvent);
        foreach (CombatEvent combatEvent in events)
        {
            string? attribute = combatEvent.Kind.StartsWith(
                "CardModifyAttribute:", StringComparison.Ordinal)
                ? combatEvent.Kind["CardModifyAttribute:".Length..]
                : combatEvent.Kind.StartsWith("CardAttribute:", StringComparison.Ordinal)
                    ? combatEvent.Kind["CardAttribute:".Length..]
                : combatEvent.Kind.StartsWith("Card", StringComparison.Ordinal)
                    ? combatEvent.Kind["Card".Length..]
                    : null;
            if (attribute is null || combatEvent.TargetId is null)
            {
                continue;
            }
            CombatCardState? target = _state.Combatants
                .SelectMany(value => value.Cards)
                .FirstOrDefault(card => card.InstanceId == combatEvent.TargetId);
            if (target is not null)
            {
                DispatchAttributeChanged(
                    target, attribute, combatEvent.SecondaryAmount, combatEvent.Amount);
                if (attribute == "Flying")
                {
                    string flyingTrigger = combatEvent.Amount > combatEvent.SecondaryAmount
                        ? "TTriggerOnCardStartedFlying"
                        : "TTriggerOnCardStoppedFlying";
                    DispatchCardSubjectTrigger(flyingTrigger, target);
                    if (flyingTrigger == "TTriggerOnCardStartedFlying")
                    {
                        DispatchCardSubjectTrigger("TTriggerOnCardStartsFlying", target);
                    }
                }
            }
        }
    }

    private static string? PerformedTrigger(string actionType) => actionType switch
    {
        "TActionCardHaste" => "TTriggerOnCardPerformedHaste",
        "TActionCardSlow" => "TTriggerOnCardPerformedSlow",
        "TActionCardFreeze" => "TTriggerOnCardPerformedFreeze",
        "TActionPlayerBurnApply" => "TTriggerOnCardPerformedBurn",
        "TActionPlayerPoisonApply" => "TTriggerOnCardPerformedPoison",
        "TActionPlayerHeal" => "TTriggerOnCardPerformedHeal",
        "TActionPlayerShieldApply" => "TTriggerOnCardPerformedShield",
        "TActionPlayerRegenApply" => "TTriggerOnCardPerformedRegen",
        "TActionPlayerDamage" => "TTriggerOnCardPerformedDamage",
        "TActionCardReload" => "TTriggerOnCardPerformedReload",
        "TActionCardDestroy" => "TTriggerOnCardPerformedDestruction",
        _ => null,
    };

    private static bool IsInterleavedPerformedCardAction(string actionType) => actionType is
        "TActionCardHaste" or "TActionCardSlow" or "TActionCardFreeze" or
        "TActionCardReload" or "TActionCardDestroy";

    private void RunNestedDispatch(Action dispatch)
    {
        int nestedEventStart = _state.Events.Count;
        dispatch();
        foreach (CombatEvent nestedEvent in _state.Events.Skip(nestedEventStart))
        {
            _processedNestedEvents.Add(nestedEvent);
        }
    }

    private CombatEvent[] UnprocessedEvents(int firstEvent) => _state.Events
        .Skip(firstEvent)
        .Where(value => !_processedNestedEvents.Contains(value))
        .ToArray();

    private IEnumerable<CombatCardState> ActiveCards() => _state.Combatants
        .SelectMany(value => value.Cards)
        .Where(card => (!card.IsDisabled || _forceActiveCards.Contains(card)) &&
            !card.IsDestroyed);

    private static IEnumerable<MaterializedEffectDefinition> ActiveEffects(
        CombatCardState card) => card.Definition.Effects
        .Where(effect => CombatEffectActivation.IsActive(effect, card) &&
            !IsUseMarkerEffect(effect));

    private static bool IsUseMarkerEffect(MaterializedEffectDefinition effect) =>
        string.Equals(effect.Definition.GetStringOrNull("InternalName"),
            "Dummy Ability to ensure item is used", StringComparison.Ordinal);

    private static bool HasTrigger(MaterializedEffectDefinition effect, string triggerType) =>
        TryGetMatchingTrigger(effect, triggerType, out _);

    private static bool TryGetMatchingTrigger(
        MaterializedEffectDefinition effect,
        string triggerType,
        out JsonElement trigger)
    {
        JsonElement? root = effect.Definition.GetObjectOrNull("Trigger");
        if (root is not null && root.Value.GetStringOrNull("$type") == triggerType)
        {
            trigger = root.Value;
            return true;
        }
        if (root is not null && root.Value.GetStringOrNull("$type") == "TTriggerOr" &&
            root.Value.GetArrayOrNull("Triggers") is JsonElement triggers)
        {
            foreach (JsonElement candidate in triggers.EnumerateArray())
            {
                if (candidate.GetStringOrNull("$type") == triggerType)
                {
                    trigger = candidate;
                    return true;
                }
            }
        }
        trigger = default;
        return false;
    }

    private static int Priority(MaterializedEffectDefinition effect) =>
        effect.Definition.GetStringOrNull("Priority") switch
        {
            "Immediate" => 500,
            "Highest" => 400,
            "High" => 300,
            "Medium" => 200,
            "Low" => 100,
            "Lowest" => 0,
            _ => 200,
        };
}
