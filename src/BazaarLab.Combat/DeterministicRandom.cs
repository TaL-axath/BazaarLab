namespace BazaarLab.Combat;

public static class SeedMixer
{
    public static uint Mix(uint masterSeed, int runIndex)
    {
        unchecked
        {
            uint value = masterSeed + (uint)(runIndex + 1) * 0x9E3779B9u;
            value ^= value >> 16;
            value *= 0x85EBCA6Bu;
            value ^= value >> 13;
            value *= 0xC2B2AE35u;
            value ^= value >> 16;
            return value != 0 ? value : 0xA341316Cu;
        }
    }
}

public sealed class XorShiftCombatRandom
{
    private uint _state;

    public XorShiftCombatRandom(uint seed) => _state = seed;

    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            return 0;
        }

        unchecked
        {
            uint value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return (int)(value % (uint)maxExclusive);
        }
    }
}
