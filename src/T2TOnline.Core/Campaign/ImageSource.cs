using System;
using System.Globalization;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>Where an event choice's image came from on the host's game. Values are part of the protocol: only add.</summary>
public enum ImageSourceKind : byte
{
    /// <summary>Nowhere this version can name: a friend looks for the one loaded sprite with its name, texture and rect.</summary>
    Named = 0,

    /// <summary>One of the event's own sprites (a training's or Fan Number 1's stat icons, Fan Number 1's size icons).</summary>
    Event = 1,

    /// <summary>The icon of one of the choice's objects (<see cref="ImageSource.Target"/>; the General Manager's are its agent's talent icons).</summary>
    ObjectIcon = 2,
}

/// <summary>
/// Where one of an event choice's images (or a preview's icon) came from on the host's game, so a friend's game takes the
/// same sprite: the exact source, and always the sprite's identity besides
/// its name (its texture's name and its rect in that texture), which a friend matches whole when the source isn't there.
/// Immutable.
/// <code>
/// u8 kind · [object icon: ref] · optional texture name · f32 x · f32 y · f32 width · f32 height
/// </code>
/// </summary>
/// <param name="Kind">Where it came from.</param>
/// <param name="Target">The object whose icon it is (<see cref="ImageSourceKind.ObjectIcon"/>), else none.</param>
/// <param name="TextureName">The sprite's texture's name, or null for none.</param>
/// <param name="X">The sprite's rect in its texture: left.</param>
/// <param name="Y">Bottom.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public sealed record ImageSource(ImageSourceKind Kind, ChoiceObjectRef Target, string? TextureName, float X, float Y, float Width, float Height)
{
    /// <summary>Largest rect coordinate or size (Unity's textures are at most 16384 pixels a side).</summary>
    public const float MaxRect = 16384;

    /// <summary>A sprite only its name, texture and rect identify.</summary>
    public static ImageSource Named(string? texture, float x, float y, float width, float height) => new(ImageSourceKind.Named, ChoiceObjectRef.None, texture, x, y, width, height);

    /// <summary>One of the event's own sprites.</summary>
    public static ImageSource Event(string? texture, float x, float y, float width, float height) => new(ImageSourceKind.Event, ChoiceObjectRef.None, texture, x, y, width, height);

    /// <summary>The icon of the choice's object <paramref name="target"/>.</summary>
    public static ImageSource Icon(ChoiceObjectRef target, string? texture, float x, float y, float width, float height) => new(ImageSourceKind.ObjectIcon, target, texture, x, y, width, height);

    /// <summary>True when a sprite with <paramref name="texture"/> and this rect (to a hundredth of a pixel) is this one's identity.</summary>
    public bool SameIdentity(string? texture, float x, float y, float width, float height) =>
        string.Equals(texture, TextureName, StringComparison.Ordinal) && Near(x, X) && Near(y, Y) && Near(width, Width) && Near(height, Height);

    /// <summary>For logs: "the icon of object 1", "the event's own", "by name (atlas_ui 0,64 32x32)".</summary>
    public override string ToString() => Kind switch
    {
        ImageSourceKind.ObjectIcon => $"the icon of {Target}",
        ImageSourceKind.Event => "the event's own",
        _ => string.Create(CultureInfo.InvariantCulture, $"by name ({Diagnostics.HostText.KeyForLog(TextureName)} {X:0.#},{Y:0.#} {Width:0.#}x{Height:0.#})"),
    };

    /// <summary>Why it can't be sent (a kind this version doesn't have, an object where there's none, a rect out of range), or null.</summary>
    internal string? Check()
    {
        if (Kind is not (ImageSourceKind.Named or ImageSourceKind.Event or ImageSourceKind.ObjectIcon))
        {
            return $"an image source of kind {Kind}";
        }

        if (!Target.IsValid || (Kind == ImageSourceKind.ObjectIcon) == Target.IsNone)
        {
            return $"an image source's object ({Target}) doesn't fit its kind";
        }

        return EventWire.CheckName(TextureName, "an image's texture", required: false, allowEmpty: true) ??
            (Rect(X) && Rect(Y) && Rect(Width) && Rect(Height) ? null : "an image's rect is out of range");
    }

    internal void Write(BitWriter writer)
    {
        string? problem = Check();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteByte((byte)Kind);
        if (Kind == ImageSourceKind.ObjectIcon)
        {
            Target.Write(writer);
        }

        EventWire.OptionalName(writer, TextureName);
        writer.WriteSingle(X);
        writer.WriteSingle(Y);
        writer.WriteSingle(Width);
        writer.WriteSingle(Height);
    }

    internal static bool TryRead(ref BitReader reader, out ImageSource? source)
    {
        source = null;
        var kind = (ImageSourceKind)reader.ReadByte();
        ChoiceObjectRef target = ChoiceObjectRef.None;
        if (reader.IsOverrun || (kind == ImageSourceKind.ObjectIcon && !ChoiceObjectRef.TryRead(ref reader, out target)) ||
            !EventWire.TryReadOptionalName(ref reader, out string? texture))
        {
            return false;
        }

        var read = new ImageSource(kind, target, texture, reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (reader.IsOverrun || read.Check() != null)
        {
            return false;
        }

        source = read;
        return true;
    }

    private static bool Rect(float value) => float.IsFinite(value) && value is >= -MaxRect and <= MaxRect;

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;
}
