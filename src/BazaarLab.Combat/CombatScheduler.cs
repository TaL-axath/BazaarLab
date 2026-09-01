namespace BazaarLab.Combat;

public sealed class CombatScheduler
{
    private readonly CombatState _state;
    private readonly CombatRuleRuntime _rules;
    private readonly XorShiftCombatRandom? _random;
    private bool _started;

    public CombatScheduler(
        CombatState state, CombatRuleRuntime rules, XorShiftCombatRandom? random = null)
    {
        _state = state;
        _rules = rules;
        _random = random;
    }

    public int StartFight()
    {
        if (_started)
        {
            return 0;
        }
        _started = true;
        _rules.RecomputeAuras();
        foreach (CombatCardState card in CardsInOrder())
        {
            card.CooldownRemainingMilliseconds = card.GetEffectiveCooldownMilliseconds();
            int ammoMaximum = Math.Max(0, card.Attributes.GetValueOrDefault("AmmoMax"));
            if (ammoMaximum > 0 && !card.IntrinsicAttributes.ContainsKey("Ammo"))
            {
                card.SetIntrinsicAttribute("Ammo", ammoMaximum);
            }
        }
        foreach (CombatantState combatant in _state.Combatants)
        {
            int cooldown = EffectiveTempoCooldown(combatant);
            // A live adapter can provide the exact opening remainder. Historic
            // BPP snapshots omit it; their replays show first Jules gains
            // throughout the 1..20 tick interval followed by full periods, so
            // Monte Carlo marginalizes that missing opening phase by seed.
            combatant.TempoCooldownRemainingMilliseconds =
                combatant.InitialTempoCooldownMilliseconds is int supplied
                    ? Math.Clamp(supplied, 1, Math.Max(1, cooldown))
                    : _random is not null && combatant.Hero == "Hero8" && cooldown < 100_000_000
                    // Official zero-based traces contain the first natural
                    // Tempo gain at every frame from 0 through one full
                    // cooldown (frame 20 for a 1000 ms cooldown). Include both
                    // endpoints; the final phase therefore starts one tick
                    // beyond the nominal cooldown remainder.
                    ? checked((_random.Next(Math.Max(1,
                            (cooldown + CombatEngine.TickMilliseconds - 1) /
                            CombatEngine.TickMilliseconds) + 1) + 1) *
                        CombatEngine.TickMilliseconds)
                    : cooldown;
        }
        return _rules.StartFightScheduled();
    }

    public int AdvanceOneTick()
    {
        if (!_started)
        {
            StartFight();
        }
        CombatEngine.BeginTick(_state);
        // RunWithWorkspace's tick-one branch moves scheduled effects before
        // the otherwise common item/enrage/aura/periodic/tempo phase sequence.
        if (_state.Tick == 1)
        {
            ProcessScheduledEffects();
        }
        int fired = AdvanceItems();
        _rules.AdvanceEnrageOneTick();
        _rules.RecomputeAuras();
        int firstPeriodicEvent = _state.Events.Count;
        CombatEngine.ApplyPeriodicEffects(_state);
        _rules.ProcessEnginePlayerEvents(firstPeriodicEvent);
        _rules.ApplyTempoGainOneTick();
        if (_state.Tick != 1)
        {
            int firstSandstormEvent = _state.Events.Count;
            CombatEngine.ApplySandstorm(_state);
            _rules.ProcessEnginePlayerEvents(firstSandstormEvent);
            _rules.ProcessSandstormEvents(firstSandstormEvent);
            ProcessScheduledEffects();
        }
        _rules.ResolvePlayerDeaths();
        return fired;
    }

    private void ProcessScheduledEffects()
    {
        bool attributesExpired = _state.TimedCardModifiers.RemoveAll(modifier =>
                modifier.ExpiresAtTick <= _state.Tick) > 0;
        attributesExpired |= _state.TimedPlayerModifiers.RemoveAll(modifier =>
                modifier.ExpiresAtTick <= _state.Tick) > 0;
        if (attributesExpired)
        {
            _rules.RecomputeAuras();
        }
        _rules.ProcessScheduledRuleEffects();
        _rules.ProcessScheduledForceUses();
        _rules.ProcessScheduledChargeReadyUses();
        // ExecuteScheduledEffects drains ready signals only after the due
        // effect batch (and its item-completion accounting) has finished.
        _rules.ProcessReadySignals();
    }

    private int AdvanceItems()
    {
        int fired = 0;
        foreach (CombatCardState card in CardsInOrder().ToArray())
        {
            if (card.IsDisabled || card.IsDestroyed)
            {
                TickStatusDurations(card);
                continue;
            }
            ConsumeCharge(card);
            int cooldown = card.GetEffectiveCooldownMilliseconds();
            if (card.Attributes.GetValueOrDefault("AmmoMax") > 0 &&
                card.Attributes.GetValueOrDefault("Ammo") <= 0)
            {
                TickStatusDurations(card);
                continue;
            }
            if (cooldown <= 0)
            {
                TickStatusDurations(card);
                continue;
            }
            bool wasFrozen = card.Attributes.GetValueOrDefault("Freeze") > 0;
            int progress = CooldownProgress(card);
            TickStatusDurations(card);
            card.CooldownRemainingMilliseconds -= progress;
            if (wasFrozen || card.CooldownRemainingMilliseconds > 0)
            {
                continue;
            }
            // Worker UseItem resets the clock before dispatching the use and its
            // effects. AdvanceItemClocks clamps at zero, so excess tick progress
            // is intentionally not carried into the next cooldown.
            card.CooldownRemainingMilliseconds = Math.Max(
                1, card.GetEffectiveCooldownMilliseconds());
            _rules.FireCardScheduled(card);
            fired++;
        }
        return fired;
    }

    private static int EffectiveTempoCooldown(CombatantState combatant)
    {
        int maximum = Math.Max(0, combatant.Attributes.GetValueOrDefault("TempoGainCooldownMax"));
        int flat = combatant.Attributes.GetValueOrDefault("FlatTempoGainCooldownReduction");
        int afterFlat = Math.Max(1, checked(maximum - flat));
        int percent = Math.Clamp(
            combatant.Attributes.GetValueOrDefault("PercentTempoGainCooldownReduction"), -100, 100);
        return Math.Max(1, checked((int)((long)afterFlat * (100 - percent) / 100)));
    }

    private IEnumerable<CombatCardState> CardsInOrder() => _state.Combatants
        .SelectMany(combatant => combatant.Cards)
        .Where(card => card.Section == "Hand" && !card.IsDestroyed)
        .OrderByDescending(card => card.Definition.ActivationPriority)
        .ThenBy(card => _state.Combatants.IndexOf(card.Owner))
        .ThenBy(card => card.BoardPosition)
        .ThenBy(card => card.InstanceId, StringComparer.Ordinal);

    private void ConsumeCharge(CombatCardState card)
    {
        int charge = Math.Max(0, card.IntrinsicAttributes.GetValueOrDefault("Charge"));
        if (charge == 0)
        {
            return;
        }
        card.CooldownRemainingMilliseconds = Math.Max(
            0, card.CooldownRemainingMilliseconds - charge);
        card.SetIntrinsicAttribute("Charge", 0);
        _rules.RecomputeAuras();
    }

    private static int CooldownProgress(CombatCardState card)
    {
        if (card.Attributes.GetValueOrDefault("Freeze") > 0)
        {
            return 0;
        }
        bool hasted = card.Attributes.GetValueOrDefault("Haste") > 0;
        bool slowed = card.Attributes.GetValueOrDefault("Slow") > 0;
        if (hasted == slowed)
        {
            return CombatEngine.TickMilliseconds;
        }
        return hasted ? CombatEngine.TickMilliseconds * 2 : CombatEngine.TickMilliseconds / 2;
    }

    private void TickStatusDurations(CombatCardState card)
    {
        bool changed = false;
        foreach (string attribute in new[] { "Haste", "Slow", "Freeze" })
        {
            int value = card.IntrinsicAttributes.GetValueOrDefault(attribute);
            if (value <= 0)
            {
                continue;
            }
            card.IntrinsicAttributes[attribute] = Math.Max(0, value - CombatEngine.TickMilliseconds);
            changed = true;
        }
        if (changed)
        {
            _rules.RecomputeAuras();
        }
    }
}
