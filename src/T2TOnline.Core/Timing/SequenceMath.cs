namespace T2TOnline.Core.Timing;

/// <summary>Comparisons for counters that wrap around (frame numbers, sequence numbers).</summary>
public static class SequenceMath
{
    /// <summary>Signed distance from <paramref name="from"/> to <paramref name="to"/>, correct across wraparound.</summary>
    public static int Distance(uint from, uint to) => unchecked((int)(to - from));

    /// <summary>Signed distance from <paramref name="from"/> to <paramref name="to"/>, correct across wraparound.</summary>
    public static int Distance(ushort from, ushort to) => unchecked((short)(ushort)(to - from));

    /// <summary>True when <paramref name="candidate"/> comes after <paramref name="reference"/>.</summary>
    public static bool IsNewer(uint candidate, uint reference) => Distance(reference, candidate) > 0;

    /// <summary>True when <paramref name="candidate"/> comes after <paramref name="reference"/>.</summary>
    public static bool IsNewer(ushort candidate, ushort reference) => Distance(reference, candidate) > 0;
}
