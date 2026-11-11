using System;
using System.Collections.Generic;
using System.Text;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// What a host's run file may name for Easy Save 3 to create. A friend's game loads the host's
/// run file with the game's own loader, and Easy Save makes an object of whatever type a <c>"__type"</c> names (its type
/// lookup and <c>CreateInstance</c>), so a modified host could make a friend's game create any type there is. Before the
/// bytes reach the game, every <c>"__type"</c> must be one a real run file holds: the run's model
/// (<c>State.RunDataModelV2</c> and every class its public fields reach, with the subclasses a field can hold), the run's
/// stats (<c>ScorersStats</c>, <c>MatchScore</c>, <c>Tape2Tape.Hockey.GoalieStats</c>), Easy Save's names for primitives,
/// and the generic collections of those (<c>List`1</c>, <c>HashSet`1</c>, <c>Dictionary`2</c>).
/// <para>
/// Easy Save's type strings (<c>ES3Reflection.GetTypeString</c>, Oct 29 build, checked in the disassembly and against
/// real save files): a short name for primitives and a few engine types (<c>"float"</c>, <c>"Vector2"</c>), else the
/// type's full name, a comma and its assembly's name (<c>"State.RunDataModelV2,MainScriptsAssembly"</c>, nested types
/// with <c>+</c>); a generic's arguments are assembly-qualified with version and key
/// (<c>"System.Collections.Generic.List`1[[MatchScore, MainScriptsAssembly, Version=0.0.0.0, Culture=neutral,
/// PublicKeyToken=null]],mscorlib"</c>).
/// </para>
/// <para>
/// Easy Save's reader isn't a JSON reader (<c>ES3JSONReader</c>, Oct 29 build). It takes U+201C and U+201D for quotes as
/// well as <c>"</c>, at either end of a text (<c>Read_string</c>, <c>ReadString</c>, <c>ReadPropertyName</c>),
/// and any <c>\x</c> as x but for <c>\b \f \n \r \t</c>, with no <c>\u</c>; it skips a bare value up to
/// <c>IsEndOfValue</c>, a quote or a brace in it included; and where it expects a text, a bare <c>n</c> that isn't the start
/// of <c>null</c> counts as an opening quote, three characters later (<c>ReadQuotationMarkOrNullIgnoreWhitespace</c>, Nov 6
/// build; <c>ReadPropertyName</c> takes a comma before the name). So this check doesn't read JSON: it reads only what
/// Easy Save's writer writes (<c>ES3JSONWriter.WritePrimitive(string)</c>: each quote of the three, the backslash and
/// the slash with a backslash; <c>\b \f \n \r \t</c>; everything else as it is), and refuses everything else, where the two
/// readers could see different tokens.
/// </para>
/// The check reads the bytes as those tokens (texts with their escapes, nesting, bare values), so it never throws and isn't fooled
/// by an escaped key; anything it can't read is refused.
/// </summary>
public static class RunFileTypes
{
    /// <summary>Deepest nesting of objects and arrays a run file may have (a real one is about ten deep).</summary>
    public const int MaxDepth = 64;

    /// <summary>Largest run file checked (the largest checkpoint part).</summary>
    public const int MaxBytes = RunCheckpoint.MaxPartBytes;

    /// <summary>The game's assembly, as Easy Save names it.</summary>
    public const string GameAssembly = "MainScriptsAssembly";

    private const string TypeKey = "__type";
    private const string ReferenceKey = "_ES3Ref";

    // How a refusal for a type name starts (TryCheck); the name follows in quotes, cut at 160 characters.
    private const string UnknownType = "it names a type a run file never holds: ";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Easy Save's own short names that are values, not objects with behaviour (Transform, GameObject, Component, Material,
    // MeshFilter, Texture2D, UnityEngine.Object and System.Object are deliberately missing).
    private static readonly HashSet<string> ShortNames = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort", "string",
        "Vector2", "Vector3", "Vector4", "Color",
    };

    // The generic collections a run file uses, by full name, with the assemblies Easy Save may name for them.
    private static readonly Dictionary<string, int> Collections = new(StringComparer.Ordinal)
    {
        ["System.Collections.Generic.List`1"] = 1,
        ["System.Collections.Generic.HashSet`1"] = 1,
        ["System.Collections.Generic.Dictionary`2"] = 2,
    };

    private static readonly HashSet<string> CollectionAssemblies = new(StringComparer.Ordinal) { "mscorlib", "System.Core", "netstandard", "System.Private.CoreLib" };

    // A generic argument from the system assembly: only plain values.
    private static readonly HashSet<string> SystemValues = new(StringComparer.Ordinal)
    {
        "System.String", "System.Int32", "System.Int64", "System.Single", "System.Double", "System.Boolean", "System.Byte",
        "System.Int16", "System.UInt32", "System.UInt64", "System.UInt16", "System.Char", "System.Decimal",
    };

    /// <summary>
    /// The game's classes a run file (<c>RunDataV2.es3</c>) can name, by full name (Oct 29 build): every class reachable
    /// from <c>State.RunDataModelV2</c>, <c>ScorersStats</c>, <c>MatchScore</c> and <c>Tape2Tape.Hockey.GoalieStats</c>
    /// through public fields, and every subclass of one (the relic, power-up and leaving-condition states are written as
    /// their subclasses). Built from the build's type listing; a new game build that adds a state class needs it added.
    /// </summary>
    public static IReadOnlySet<string> RunFile { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Data.ColorScheme", "Data.TeamColorsData", "MatchScore", "Rogue.AssistCountGoalOvrState", "Rogue.AssistedByPlaymakerState",
        "Rogue.BenchBonusState", "Rogue.BloodBonusState", "Rogue.CannonArmState", "Rogue.DoubleRewardState", "Rogue.FreeAgents.FreeAgent",
        "Rogue.FreeAgents.InjuryCondition", "Rogue.FreeAgents.LeavingCondition", "Rogue.FreeAgents.MaxGamesCondition",
        "Rogue.FreeAgents.MaxGoalsCondition", "Rogue.FreeAgents.PreGeneratedFreeAgentData", "Rogue.FreeAgents.RandomCondition",
        "Rogue.GoalBasedStatChangeState", "Rogue.GoalCritAccumulatorState", "Rogue.GoalieAssistState", "Rogue.GrowingAngerState",
        "Rogue.HatTrickState", "Rogue.HitStreakState", "Rogue.IndecisiveState", "Rogue.InflatedEgoState", "Rogue.KettleState",
        "Rogue.KrillState", "Rogue.MomentumState", "Rogue.ParticipationTrophyState", "Rogue.PitilessState", "Rogue.PlaymakerState",
        "Rogue.Powerups.Chaos.LiquidationState", "Rogue.Powerups.Chaos.MonkeysPawState", "Rogue.Powerups.States.BoolState",
        "Rogue.Powerups.States.NullPowerUpState", "Rogue.Powerups.States.PowerUpState", "Rogue.RareBerryState",
        "Rogue.Relics.ChaosRelics.YipsState", "Rogue.Relics.MetaRelics.DefenseMeta.BrickiePlushieState",
        "Rogue.Relics.MetaRelics.TrioMeta.TripleThreatState", "Rogue.Relics.States.ChaosRelicState", "Rogue.Relics.States.NullRelicState",
        "Rogue.Relics.States.RelicState", "Rogue.Relics.States.ShrinkSerumOpponentState", "Rogue.Relics.States.SingleIntState",
        "Rogue.Relics.States.SingleIntStatsState", "Rogue.Relics.States.ViolentDiarrheaState", "Rogue.Relics.TrainingWhistleState",
        "Rogue.ShatterStrikeState", "Rogue.ShortContractState", "Rogue.ShutoutState", "Rogue.SlowmoState", "Rogue.SplitPersonalityState",
        "Rogue.StatStufferState", "Rogue.StethoscopeState", "Rogue.TargetPracticeState", "Rogue.TryHardState", "Rogue.TwigTaxState",
        "Rogue.VanillaTrainingState", "Rogue.ZoomerState", "ScorersStats", "State.AbilityDataModelV2", "State.BenchQuestRunDataModel",
        "State.ForwardDataModelV2", "State.GoaltenderDataModelV2", "State.InjuryPacingDataModel", "State.LineupDataModelV2",
        "State.PowerUpModelV2", "State.RelicDataModelV2", "State.RunDataModelV2", "State.SkaterDataModelV2", "State.SkaterState",
        "State.TeamDataModelV2", "State.TeamState", "Tape2Tape.Hockey.GoalieStats",
    };

    /// <summary>
    /// True when <paramref name="bytes"/> are one object as Easy Save's writer writes it (UTF-8, after any byte order mark), no
    /// bigger than <see cref="MaxBytes"/> and no deeper than <see cref="MaxDepth"/>, whose every <c>"__type"</c> is allowed
    /// (<see cref="IsAllowed"/>) and which holds no Easy Save object reference (<c>_ES3Ref</c>: a run file holds values only).
    /// Else false, with why. Never throws.
    /// </summary>
    public static bool TryCheck(ReadOnlySpan<byte> bytes, IReadOnlySet<string> allowed, out string problem)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (bytes.Length > MaxBytes)
        {
            problem = $"it's {bytes.Length} bytes, more than {MaxBytes}";
            return false;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        var scanner = new Scanner(bytes);
        try
        {
            _ = StrictUtf8.GetCharCount(bytes); // Easy Save's reader would turn what isn't into replacement characters
            return scanner.Run(allowed, out problem);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IndexOutOfRangeException)
        {
            problem = $"it couldn't be read ({ex.GetType().Name})";
            return false;
        }
    }

    /// <summary>
    /// A refusal of <see cref="TryCheck"/> (<paramref name="problem"/>) for the log of the PC that got the file from
    /// another: the same words, but the type name the file holds, which is whatever its sender wrote there, only when it
    /// has an identifier's shape (<see cref="Diagnostics.HostText.IsKey"/>: <c>UnityEngine.GameObject,UnityEngine.CoreModule</c>,
    /// a game class a newer build added), else as its length. Every other refusal is this check's own words and numbers.
    /// The PC that made the file logs the refusal as it is (the name is its own file's).
    /// </summary>
    public static string ProblemForLog(string? problem)
    {
        if (string.IsNullOrEmpty(problem) || !problem.StartsWith(UnknownType, StringComparison.Ordinal))
        {
            return problem ?? string.Empty;
        }

        string type = problem[UnknownType.Length..];
        if (type.Length >= 2 && type[0] == '"' && type[^1] == '"')
        {
            type = type[1..^1];
        }

        return UnknownType + (Diagnostics.HostText.IsKey(type)
            ? $"\"{type}\""
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"a name of {type.Length} characters that isn't shaped like a type's"));
    }

    /// <summary>
    /// True when Easy Save type string <paramref name="type"/> names only allowed things: one of Easy Save's value short
    /// names, a class in <paramref name="allowed"/> from the game's assembly, or a list, set or dictionary of those (or of
    /// plain system values).
    /// </summary>
    public static bool IsAllowed(string type, IReadOnlySet<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(allowed);
        return ShortNames.Contains(type) || IsAllowedNamed(type, allowed, depth: 0);
    }

    // "FullName,Assembly" (Easy Save's form), the assembly part required.
    private static bool IsAllowedNamed(string type, IReadOnlySet<string> allowed, int depth)
    {
        int comma = TopLevelComma(type);
        if (comma < 0)
        {
            return false;
        }

        return IsAllowedType(type[..comma].Trim(), type[(comma + 1)..].Trim(), allowed, depth);
    }

    private static bool IsAllowedType(string fullName, string assembly, IReadOnlySet<string> allowed, int depth)
    {
        if (depth > 4 || fullName.Length == 0)
        {
            return false;
        }

        int open = fullName.IndexOf('[', StringComparison.Ordinal);
        if (open < 0)
        {
            return (string.Equals(assembly, GameAssembly, StringComparison.Ordinal) && allowed.Contains(fullName)) ||
                (CollectionAssemblies.Contains(assembly) && SystemValues.Contains(fullName));
        }

        // A generic: "Name`n[[arg, assembly, ...],[arg, assembly, ...]]".
        string definition = fullName[..open];
        if (!Collections.TryGetValue(definition, out int arity) || !CollectionAssemblies.Contains(assembly) ||
            !TrySplitArguments(fullName[open..], out List<string> arguments) || arguments.Count != arity)
        {
            return false;
        }

        foreach (string argument in arguments)
        {
            // An argument is assembly-qualified: "FullName, Assembly, Version=..., Culture=..., PublicKeyToken=...".
            int comma = TopLevelComma(argument);
            if (comma < 0)
            {
                return false;
            }

            string rest = argument[(comma + 1)..];
            int next = rest.IndexOf(',', StringComparison.Ordinal);
            string argumentAssembly = (next < 0 ? rest : rest[..next]).Trim();
            if (!IsAllowedType(argument[..comma].Trim(), argumentAssembly, allowed, depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first comma outside brackets, or -1.</summary>
    private static int TopLevelComma(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth < 0)
                    {
                        return -1;
                    }

                    break;
                case ',' when depth == 0:
                    return i;
            }
        }

        return -1;
    }

    /// <summary>Splits "[[a],[b]]" into "a" and "b". False when the brackets don't match that shape.</summary>
    private static bool TrySplitArguments(string text, out List<string> arguments)
    {
        arguments = new List<string>();
        if (text.Length < 4 || text[0] != '[' || text[^1] != ']')
        {
            return false;
        }

        string inner = text[1..^1];
        int depth = 0;
        int start = -1;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '[')
            {
                if (depth == 0)
                {
                    start = i + 1;
                }

                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }

                if (depth == 0)
                {
                    arguments.Add(inner[start..i]);
                }
            }
            else if (depth == 0 && c != ',' && !char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return depth == 0 && arguments.Count > 0;
    }

    /// <summary>A reader of Easy Save's tokens: texts (escapes decoded), nesting, bare values. Anything its writer doesn't write is refused.</summary>
    private ref struct Scanner
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _at;

        public Scanner(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _at = 0;
        }

        public bool Run(IReadOnlySet<string> allowed, out string problem)
        {
            SkipSpace();
            if (_at >= _bytes.Length || _bytes[_at] != (byte)'{')
            {
                problem = "it isn't a JSON object";
                return false;
            }

            int depth = 0;
            int types = 0;
            while (_at < _bytes.Length)
            {
                byte b = _bytes[_at];
                switch (b)
                {
                    case (byte)'{':
                    case (byte)'[':
                        if (++depth > MaxDepth)
                        {
                            problem = $"it's nested more than {MaxDepth} deep";
                            return false;
                        }

                        _at++;
                        break;
                    case (byte)'}':
                    case (byte)']':
                        if (--depth < 0)
                        {
                            problem = "its brackets don't match";
                            return false;
                        }

                        _at++;
                        break;
                    case (byte)',':
                    case (byte)':':
                    case (byte)' ':
                    case (byte)'\t':
                    case (byte)'\r':
                    case (byte)'\n':
                        _at++;
                        break;
                    case (byte)'"':
                        if (!TryReadString(out string text))
                        {
                            problem = "a text in it doesn't end, or holds what Easy Save's writer never writes";
                            return false;
                        }

                        SkipSpace();
                        bool key = _at < _bytes.Length && _bytes[_at] == (byte)':';
                        if (!key)
                        {
                            break;
                        }

                        if (string.Equals(text, ReferenceKey, StringComparison.Ordinal))
                        {
                            problem = "it holds an Easy Save object reference (a run file holds values only)";
                            return false;
                        }

                        if (!string.Equals(text, TypeKey, StringComparison.Ordinal))
                        {
                            break;
                        }

                        _at++; // the colon
                        SkipSpace();
                        if (_at >= _bytes.Length || _bytes[_at] != (byte)'"' || !TryReadString(out string type))
                        {
                            problem = "a __type in it isn't a text";
                            return false;
                        }

                        types++;
                        if (!IsAllowed(type, allowed))
                        {
                            problem = $"{UnknownType}\"{Shorten(type)}\"";
                            return false;
                        }

                        break;
                    default:
                        // A bare word (a number, true, false, null, or Easy Save's NaN): an unquoted __type is refused. Nothing else stands
                        // outside a text in what Easy Save writes, and a word ends where its reader's would.
                        if (!IsWordByte(b))
                        {
                            problem = $"it has a character outside its texts that Easy Save's writer never writes (0x{b:X2})";
                            return false;
                        }

                        int start = _at;
                        while (_at < _bytes.Length && IsWordByte(_bytes[_at]))
                        {
                            _at++;
                        }

                        if (_bytes[start.._at].IndexOf("__type"u8) >= 0 || _bytes[start.._at].IndexOf("_ES3Ref"u8) >= 0)
                        {
                            problem = "it has an unquoted __type or _ES3Ref";
                            return false;
                        }

                        // Where Easy Save's reader expects a text (a property's name after a comma, a text's value) and finds a
                        // bare n, it reads three more characters: "ull" is null, and after anything else it reads on as if an
                        // opening quote stood there, up to the next quote (ReadQuotationMarkOrNullIgnoreWhitespace). So a word
                        // that starts with n and isn't null would let it read a name or a text where this check saw none: a
                        // __type made of the end of the next text. Its writer writes no such word (NaN has a capital).
                        if (b == (byte)'n' && !_bytes[start.._at].SequenceEqual("null"u8))
                        {
                            problem = "a bare value in it starts like null and isn't one (Easy Save's reader would read on from it as a text)";
                            return false;
                        }

                        if (_at < _bytes.Length && !IsEndOfWord(_bytes[_at]))
                        {
                            problem = "a bare value in it runs into something else";
                            return false;
                        }

                        break;
                }

                if (depth == 0)
                {
                    break;
                }
            }

            if (depth != 0)
            {
                problem = "it ends before its brackets close";
                return false;
            }

            SkipSpace();
            if (_at < _bytes.Length)
            {
                problem = "there's more after its end";
                return false;
            }

            if (types == 0)
            {
                problem = "it names no types at all (not an Easy Save file)";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static bool IsWordByte(byte b) =>
            b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_' or (byte)'-' or (byte)'+' or (byte)'.';

        // Easy Save's IsEndOfValue, its end of stream aside.
        private static bool IsEndOfWord(byte b) => b is (byte)'}' or (byte)']' or (byte)',' or (byte)':' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

        // U+201C or U+201D at the cursor: E2 80 9C or E2 80 9D in UTF-8, which nothing else can look like.
        private readonly bool IsCurlyQuoteAt(int at) =>
            at + 2 < _bytes.Length && _bytes[at] == 0xE2 && _bytes[at + 1] == 0x80 && _bytes[at + 2] is 0x9C or 0x9D;

        private void SkipSpace()
        {
            while (_at < _bytes.Length && _bytes[_at] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                _at++;
            }
        }

        /// <summary>
        /// Reads the text starting at the quote under the cursor, decoding the escapes Easy Save's writer writes. False when it doesn't
        /// end, an escape is another, or it holds a bare U+201C or U+201D, where Easy Save's reader would end it.
        /// </summary>
        private bool TryReadString(out string text)
        {
            text = string.Empty;
            _at++; // the opening quote
            var builder = new StringBuilder();
            int run = _at;
            while (_at < _bytes.Length)
            {
                byte b = _bytes[_at];
                if (b == (byte)'"')
                {
                    builder.Append(Encoding.UTF8.GetString(_bytes[run.._at]));
                    _at++;
                    text = builder.ToString();
                    return true;
                }

                if (IsCurlyQuoteAt(_at))
                {
                    return false;
                }

                if (b != (byte)'\\')
                {
                    _at++;
                    continue;
                }

                builder.Append(Encoding.UTF8.GetString(_bytes[run.._at]));
                if (_at + 1 >= _bytes.Length)
                {
                    return false;
                }

                byte escape = _bytes[_at + 1];
                _at += 2;
                switch (escape)
                {
                    case (byte)'"':
                    case (byte)'\\':
                    case (byte)'/':
                        builder.Append((char)escape);
                        break;
                    case (byte)'b':
                        builder.Append('\b');
                        break;
                    case (byte)'f':
                        builder.Append('\f');
                        break;
                    case (byte)'n':
                        builder.Append('\n');
                        break;
                    case (byte)'r':
                        builder.Append('\r');
                        break;
                    case (byte)'t':
                        builder.Append('\t');
                        break;
                    case 0xE2 when IsCurlyQuoteAt(_at - 1):
                        builder.Append(_bytes[_at + 1] == 0x9C ? '“' : '”');
                        _at += 2;
                        break;
                    default:
                        return false;
                }

                run = _at;
            }

            return false;
        }

        private static string Shorten(string type) => type.Length <= 160 ? type : type[..160] + "...";
    }
}
