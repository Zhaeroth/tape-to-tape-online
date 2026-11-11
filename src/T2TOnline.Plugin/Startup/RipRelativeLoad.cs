using System;
using System.Buffers.Binary;

namespace T2TOnline.Plugin.Startup;

// Plain .NET only, like CrashReporterSwitches (its user); linked into the Core tests.

/// <summary>
/// Decodes the first instruction of a small x64 engine function that loads a global pointer:
/// <c>mov rax, qword ptr [rip + disp32]</c> (<c>48 8B 05</c>) or the same into <c>rcx</c> (<c>48 8B 0D</c>). The global's
/// address is the instruction's end plus the displacement. Unity's one-line getters on a singleton start this way and
/// read the object without checking it exists, so reading that global first tells whether calling them is safe.
/// </summary>
internal static class RipRelativeLoad
{
    /// <summary>The instruction's length in bytes: what <see cref="TryTarget"/> needs to see.</summary>
    public const int Length = 7;

    /// <summary>
    /// The address of the global that the instruction at <paramref name="address"/> (whose bytes start
    /// <paramref name="code"/>) loads, or false when the code doesn't start with one of the two loads.
    /// </summary>
    public static bool TryTarget(ReadOnlySpan<byte> code, long address, out long target)
    {
        target = 0;
        if (code.Length < Length || code[0] != 0x48 || code[1] != 0x8B || (code[2] != 0x05 && code[2] != 0x0D))
        {
            return false;
        }

        target = address + Length + BinaryPrimitives.ReadInt32LittleEndian(code.Slice(3, 4));
        return true;
    }
}
