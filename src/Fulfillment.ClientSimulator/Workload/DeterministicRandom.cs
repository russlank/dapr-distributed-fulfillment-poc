namespace Fulfillment.ClientSimulator.Workload;

/// <summary>
/// SplitMix64: a small, fully specified pseudo-random generator. <see cref="Random"/> with a seed is
/// not guaranteed to produce the same sequence across .NET versions, so workloads use this one:
/// the same seed gives the same values on every platform and runtime.
/// </summary>
public sealed class DeterministicRandom(ulong seed)
{
    private ulong state = seed;

    /// <summary>A generator for one item of a workload: independent of how many other items were drawn.</summary>
    public static DeterministicRandom ForItem(ulong seed, long index) => new(Mix(seed ^ unchecked((ulong)index * 0x9E3779B97F4A7C15UL)));

    public ulong NextUInt64()
    {
        state = unchecked(state + 0x9E3779B97F4A7C15UL);
        return Mix(state);
    }

    /// <summary>A value in [0, maxExclusive) without modulo bias.</summary>
    public int Next(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        var bound = (ulong)maxExclusive;
        var limit = ulong.MaxValue - (ulong.MaxValue % bound);
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value >= limit);

        return (int)(value % bound);
    }

    /// <summary>A value in [0, 1) with 53 bits of precision.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    private static ulong Mix(ulong z)
    {
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }
}
