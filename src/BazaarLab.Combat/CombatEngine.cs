namespace BazaarLab.Combat;

public static class CombatEngine
{
    public const int TickMilliseconds = 50;

    public static void AdvanceOneTick(CombatState state)
    {
        BeginTick(state);
        ApplyPeriodicEffects(state);
        ApplySandstorm(state);
    }

    public static void BeginTick(CombatState state)
    {
        checked { state.Tick++; }
    }

    public static DamageResult DealDamage(
        CombatState state,
        CombatantState target,
        int requestedDamage,
        bool applyDamageReduction = true,
        bool ignoreShield = false,
        string eventKind = "Damage",
        string? sourceId = null)
    {
        int amount = Math.Max(0, requestedDamage);
        if (applyDamageReduction)
        {
            int reduction = Math.Clamp(target.DamageReductionPercent, 0, 100);
            amount = checked((int)((long)amount * (100 - reduction) / 100));
            amount = Math.Max(0, amount - Math.Max(0, target.FlatDamageReduction));
        }

        int shieldAbsorbed = ignoreShield ? 0 : Math.Min(target.Shield, amount);
        int healthDamage = amount - shieldAbsorbed;
        int healthBefore = target.Health;
        int shieldBefore = target.Shield;
        target.Shield -= shieldAbsorbed;
        target.Health -= healthDamage;
        state.Events.Add(new CombatEvent(
            state.Tick, eventKind, target.Id, healthDamage, shieldAbsorbed, sourceId));
        if (target.Health != healthBefore)
        {
            state.Events.Add(new CombatEvent(
                state.Tick, "PlayerAttribute:Health", target.Id, target.Health, healthBefore));
        }
        if (target.Shield != shieldBefore)
        {
            state.Events.Add(new CombatEvent(
                state.Tick, "PlayerAttribute:Shield", target.Id, target.Shield, shieldBefore));
        }
        return new DamageResult(shieldAbsorbed, healthDamage);
    }

    public static void DealBurnDamage(
        CombatState state,
        CombatantState target,
        int burn)
    {
        int reduction = Math.Clamp(target.DamageReductionPercent, 0, 100);
        int reduced = checked((int)((long)Math.Max(0, burn) * (100 - reduction) / 100));
        reduced = Math.Max(0, reduced - Math.Max(0, target.FlatDamageReduction));
        if (reduced <= 0)
        {
            return;
        }

        if (target.Shield <= 0)
        {
            DealDamage(state, target, reduced, false, false, "Burn");
            return;
        }

        int oldShield = target.Shield;
        int shieldPortion = Math.Min(oldShield, (int)((uint)reduced >> 1));
        if (shieldPortion > 0)
        {
            DealDamage(state, target, shieldPortion, false, false, "BurnShield");
        }

        int remainingHealthDamage = Math.Max(0, reduced - checked(oldShield * 2));
        if (remainingHealthDamage > 0)
        {
            DealDamage(state, target, remainingHealthDamage, false, false, "Burn");
        }
    }

    public static void StartSandstorm(CombatState state, bool forced = false)
    {
        SandstormState storm = state.Sandstorm;
        if (forced)
        {
            storm.Enabled = true;
        }

        if (!storm.Enabled || storm.Started)
        {
            return;
        }

        storm.Started = true;
        storm.ElapsedMilliseconds = 0;
        state.Events.Add(new CombatEvent(state.Tick, "SandstormStarted"));
        ApplySandstormDamage(state, storm.Damage);
        storm.IntervalMilliseconds -= 10;
    }

    public static void ApplyPeriodicEffects(CombatState state)
    {
        int timeMilliseconds = checked((state.Tick - 1) * TickMilliseconds);
        if (timeMilliseconds % 1000 == 0)
        {
            foreach (CombatantState target in state.Combatants)
            {
                if (target.Poison > 0)
                {
                    DealDamage(state, target, target.Poison, false, true, "Poison");
                }
            }
        }

        if (timeMilliseconds % 500 == 0)
        {
            foreach (CombatantState target in state.Combatants)
            {
                if (target.Burn <= 0)
                {
                    continue;
                }

                DealBurnDamage(state, target, target.Burn);
                int decay = Math.Max(1, checked(target.Burn * 3) / 100);
                target.Burn = Math.Max(0, target.Burn - decay);
            }
        }

        if (timeMilliseconds % 1000 == 0)
        {
            foreach (CombatantState target in state.Combatants)
            {
                if (target.Regen <= 0)
                {
                    continue;
                }

                int healed = Math.Min(target.Regen, target.MaxHealth - target.Health);
                target.Health = Math.Min(target.MaxHealth, target.Health + target.Regen);
                state.Events.Add(new CombatEvent(state.Tick, "Regen", target.Id, healed));
            }
        }
    }

    public static void ApplySandstorm(CombatState state)
    {
        SandstormState storm = state.Sandstorm;
        if (!storm.Enabled)
        {
            return;
        }

        if (!storm.Started)
        {
            if (state.Tick == 601)
            {
                StartSandstorm(state);
            }
            return;
        }

        storm.ElapsedMilliseconds += TickMilliseconds;
        if (storm.ElapsedMilliseconds < storm.IntervalMilliseconds)
        {
            return;
        }

        ApplySandstormDamage(state, storm.Damage);
        if (storm.IntervalMilliseconds > 150)
        {
            storm.IntervalMilliseconds -= 10;
        }
        else
        {
            checked { storm.Damage += 2; }
        }

        storm.ElapsedMilliseconds = 0;
    }

    private static void ApplySandstormDamage(CombatState state, int damage)
    {
        foreach (CombatantState target in state.Combatants)
        {
            DealDamage(state, target, damage, false, false, "Sandstorm");
        }
    }
}
