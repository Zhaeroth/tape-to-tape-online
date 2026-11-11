using System;
using System.Buffers;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The numbers Easy Save 3 writes that JSON has no word for. Its writer formats a float with the invariant culture, so a
/// value that isn't finite goes into a save file as the bare word <c>NaN</c>, <c>Infinity</c> or <c>-Infinity</c>, and
/// its own reader takes them back. A JSON reader refuses the whole file at the first one. The run file check
/// (<see cref="RunFileTypes"/>) reads Easy Save's tokens itself and lets them pass, so a run file holding one is a run the
/// game loads; the mod's readers of a run file read it through here when plain JSON parsing fails.
/// Game-independent; never throws.
/// </summary>
public static class EasySaveNumbers
{
    private static ReadOnlySpan<byte> Nan => "NaN"u8;

    private static ReadOnlySpan<byte> Infinity => "Infinity"u8;

    private static ReadOnlySpan<byte> Null => "null"u8;

    private static ReadOnlySpan<byte> Quote => "\""u8;

    /// <summary>
    /// <paramref name="json"/> with every bare <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> (outside a text, standing
    /// as a whole value) written as <c>null</c>, or null when it holds none (nothing to read again). The facts the mod
    /// reads from a run file (ids, the act, the floor, a skater's name and number) are never such a value, so what they
    /// stood for doesn't matter to it.
    /// </summary>
    public static byte[]? WithoutNonFinite(ReadOnlySpan<byte> json) => Replace(json, asText: false);

    /// <summary>
    /// <paramref name="json"/> with every bare <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> (as <see cref="WithoutNonFinite"/>
    /// finds them) written as a JSON text of the same word (<c>"NaN"</c>), or null when it holds none: for a reader that
    /// compares values, to which a NaN and an Infinity must stay two different things.
    /// </summary>
    public static byte[]? WithNonFiniteAsText(ReadOnlySpan<byte> json) => Replace(json, asText: true);

    private static byte[]? Replace(ReadOnlySpan<byte> json, bool asText)
    {
        ArrayBufferWriter<byte>? output = null;
        int copied = 0;
        bool inText = false;
        for (int i = 0; i < json.Length; i++)
        {
            byte b = json[i];
            if (inText)
            {
                if (b == (byte)'\\')
                {
                    i++; // the escaped character, whatever it is
                }
                else if (b == (byte)'"')
                {
                    inText = false;
                }

                continue;
            }

            if (b == (byte)'"')
            {
                inText = true;
                continue;
            }

            int length = WordAt(json, i);
            if (length == 0)
            {
                continue;
            }

            output ??= new ArrayBufferWriter<byte>(json.Length + 16);
            output.Write(json[copied..i]);
            if (asText)
            {
                output.Write(Quote);
                output.Write(json.Slice(i, length));
                output.Write(Quote);
            }
            else
            {
                output.Write(Null);
            }

            copied = i + length;
            i = copied - 1;
        }

        if (output == null)
        {
            return null;
        }

        output.Write(json[copied..]);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>The length of a non-finite number's word starting at <paramref name="at"/> as a whole value, or 0.</summary>
    private static int WordAt(ReadOnlySpan<byte> json, int at)
    {
        // A value starts after a colon, a comma or an opening bracket (white space between), never inside another word.
        if (at > 0 && !IsBreak(json[at - 1]))
        {
            return 0;
        }

        ReadOnlySpan<byte> rest = json[at..];
        int length = rest.StartsWith(Nan) ? Nan.Length
            : rest.StartsWith(Infinity) ? Infinity.Length
            : rest.Length > 0 && rest[0] == (byte)'-' && rest[1..].StartsWith(Infinity) ? Infinity.Length + 1
            : 0;
        return length > 0 && (length == rest.Length || IsBreak(rest[length])) ? length : 0;
    }

    private static bool IsBreak(byte b) =>
        b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',' or (byte)':' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}';
}
