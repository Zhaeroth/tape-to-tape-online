namespace T2TOnline.Core.Diagnostics;

/// <summary>ASCII character classes (the runtime's own arrive with .NET 7; the game runs .NET 6).</summary>
internal static class Ascii
{
    /// <summary>'0' to '9'.</summary>
    public static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>'0' to '9', 'a' to 'f', 'A' to 'F'.</summary>
    public static bool IsHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>'a' to 'z', 'A' to 'Z'.</summary>
    public static bool IsLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
