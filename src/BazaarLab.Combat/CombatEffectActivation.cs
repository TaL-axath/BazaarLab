namespace BazaarLab.Combat;

internal static class CombatEffectActivation
{
    public static bool IsActive(
        MaterializedEffectDefinition effect,
        CombatCardState card)
    {
        // EEffectWorksIn is a flags enum: CombatOnly=1,
        // OutOfCombatOnly=2 and Anywhere=3.
        string? worksIn = effect.Definition.GetStringOrNull("WorksIn");
        if (worksIn == "OutOfCombatOnly")
        {
            return false;
        }

        // The native/BPP predicate applies EEffectActiveIn only to Item cards.
        // Skills and socket/player effects have no inventory section in the
        // native model and remain active regardless of their serialized value.
        if (card.Definition.Type != "TCardItem")
        {
            return true;
        }

        return effect.Definition.GetStringOrNull("ActiveIn") switch
        {
            "HandOnly" => card.Section == "Hand",
            "StashOnly" => card.Section == "Stash",
            "HandAndStash" => card.Section is "Hand" or "Stash",
            null => true,
            _ => true,
        };
    }
}
