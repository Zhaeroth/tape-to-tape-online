using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class RunFileTypesTests
{
    private const string Asm = "MainScriptsAssembly, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string Corlib = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

    // Written the way Easy Save writes a run file (tabs, CRLF, " : ", each key's value wrapped with its __type, subclasses
    // in lists with their own __type); the values are made up.
    private static readonly string RunFile = string.Join("\r\n",
        "{",
        "\t\"runData\" : {",
        "\t\t\"__type\" : \"State.RunDataModelV2,MainScriptsAssembly\",",
        "\t\t\"value\" : {",
        "\t\t\t\"isRunFailed\" : false,",
        "\t\t\t\"team\" : {",
        "\t\t\t\t\"id\" : \"vanilla\",",
        "\t\t\t\t\"skaters\" : [",
        "\t\t\t\t{",
        "\t\t\t\t\t\"__type\" : \"State.ForwardDataModelV2,MainScriptsAssembly\",",
        "\t\t\t\t\t\"id\" : \"f1\", \"name\" : \"Le \\\"Mur\\\" Étienne\", \"injuryCounter\" : NaN",
        "\t\t\t\t},{",
        "\t\t\t\t\t\"__type\" : \"State.GoaltenderDataModelV2,MainScriptsAssembly\",",
        "\t\t\t\t\t\"id\" : \"g1\"",
        "\t\t\t\t}",
        "\t\t\t\t]",
        "\t\t\t},",
        "\t\t\t\"teamState\" : {",
        "\t\t\t\t\"relicStates\" : [ { \"__type\" : \"Rogue.Relics.States.SingleIntState,MainScriptsAssembly\", \"Id\" : \"r\", \"bonus\" : 2 } ],",
        "\t\t\t\t\"goaltenderState\" : { \"Id\" : \"g1\", \"abilityState\" : { \"__type\" : \"Rogue.SlowmoState,MainScriptsAssembly\", \"id\" : \"s\" } }",
        "\t\t\t},",
        "\t\t\t\"currentNodePoint\" : { \"x\" : 1, \"y\" : 2 },",
        "\t\t\t\"currentMap\" : \"{\\\"nodes\\\":[{\\\"__type\\\":\\\"not a key here\\\"}]}\",",
        "\t\t\t\"storedUnlocks\" : [ \"a\", \"b\" ]",
        "\t\t}",
        "\t},",
        "\t\"runScorersStats\" : {",
        $"\t\t\"__type\" : \"System.Collections.Generic.List`1[[ScorersStats, {Asm}]],mscorlib\",",
        "\t\t\"value\" : [ { \"skaterId\" : \"f1\", \"goals\" : 3 } ]",
        "\t},",
        "\t\"runMatchScores\" : {",
        $"\t\t\"__type\" : \"System.Collections.Generic.List`1[[MatchScore, {Asm}]],mscorlib\",",
        "\t\t\"value\" : [ { \"act\" : 1, \"scorersStats\" : { \"f1\" : { \"goals\" : 1 } } } ]",
        "\t},",
        "\t\"runTime\" : {",
        "\t\t\"__type\" : \"float\",",
        "\t\t\"value\" : 1234.5",
        "\t},",
        "\t\"goalieStats\" : {",
        "\t\t\"__type\" : \"Tape2Tape.Hockey.GoalieStats,MainScriptsAssembly\",",
        "\t\t\"value\" : { \"skaterId\" : \"g1\", \"gamesPlayed\" : 4 }",
        "\t}",
        "}");

    private static bool Check(string text, out string problem) => RunFileTypes.TryCheck(Encoding.UTF8.GetBytes(text), RunFileTypes.RunFile, out problem);

    [Fact]
    public void A_run_file_as_easy_save_writes_it_passes()
    {
        Assert.True(Check(RunFile, out string problem), problem);
        Assert.True(RunFileTypes.TryCheck(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(RunFile)).ToArray(), RunFileTypes.RunFile, out problem), problem);
    }

    [Theory]
    [InlineData("State.RunDataModelV2,MainScriptsAssembly", true)]
    [InlineData("Rogue.Relics.States.ViolentDiarrheaState,MainScriptsAssembly", true)]
    [InlineData("float", true)]
    [InlineData("string", true)]
    [InlineData("System.Collections.Generic.List`1[[ScorersStats, " + Asm + "]],mscorlib", true)]
    [InlineData("System.Collections.Generic.HashSet`1[[System.String, " + Corlib + "]],System.Core", true)]
    [InlineData("System.Collections.Generic.Dictionary`2[[System.String, " + Corlib + "],[ScorersStats, " + Asm + "]],mscorlib", true)]
    [InlineData("System.Collections.Generic.List`1[[System.Collections.Generic.List`1[[System.Int32, " + Corlib + "]], " + Corlib + "]],mscorlib", true)]
    [InlineData("GameObject", false)]
    [InlineData("Transform", false)]
    [InlineData("System.Object", false)]
    [InlineData("UnityEngine.Object", false)]
    [InlineData("State.RunDataModelV2", false)]
    [InlineData("State.RunDataModelV2,SomeOtherAssembly", false)]
    [InlineData("UnityEngine.GameObject,UnityEngine.CoreModule", false)]
    [InlineData("System.Diagnostics.Process,System", false)]
    [InlineData("ProfileData,MainScriptsAssembly", false)]
    [InlineData("System.Collections.Generic.List`1[[UnityEngine.GameObject, UnityEngine.CoreModule, Version=0.0.0.0]],mscorlib", false)]
    [InlineData("System.Collections.Generic.List`1[[ScorersStats, SomeOtherAssembly]],mscorlib", false)]
    [InlineData("System.Collections.Generic.SortedList`2[[System.String, " + Corlib + "],[ScorersStats, " + Asm + "]],System", false)]
    [InlineData("System.Collections.Generic.Dictionary`2[[System.String, " + Corlib + "]],mscorlib", false)]
    [InlineData("System.Collections.Generic.List`1[[ScorersStats, " + Asm + "]],SomeOtherAssembly", false)]
    [InlineData("System.Collections.Generic.List`1[ScorersStats],mscorlib", false)]
    [InlineData("System.Collections.Generic.List`1[[ScorersStats, " + Asm + "]x],mscorlib", false)]
    [InlineData("", false)]
    [InlineData(",", false)]
    [InlineData("]],[[", false)]
    public void Only_the_run_files_types_are_allowed(string type, bool allowed)
    {
        Assert.Equal(allowed, RunFileTypes.IsAllowed(type, RunFileTypes.RunFile));
    }

    [Theory]
    [InlineData("\"__type\" : \"State.RunDataModelV2,MainScriptsAssembly\"", "\"__type\" : \"UnityEngine.GameObject,UnityEngine.CoreModule\"", "never holds")]
    [InlineData("\"__type\" : \"Rogue.SlowmoState,MainScriptsAssembly\"", "\"\\u005f\\u005ftype\" : \"GameObject\"", "text")]
    [InlineData("\"__type\" : \"float\"", "\"__type\" : 5", "isn't a text")]
    [InlineData("\"id\" : \"g1\"\r\n", "\"_ES3Ref\" : \"123\"\r\n", "object reference")]
    [InlineData("\"__type\" : \"float\"", "__type : \"float\"", "unquoted")]
    public void A_run_file_with_something_else_in_it_is_refused(string from, string to, string why)
    {
        string changed = RunFile.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(RunFile, changed);
        Assert.False(Check(changed, out string problem));
        Assert.Contains(why, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UnityEngine.GameObject,UnityEngine.CoreModule", "\"UnityEngine.GameObject,UnityEngine.CoreModule\"")] // a type's shape: named
    [InlineData("Rogue.Relics.NewState,MainScriptsAssembly", "\"Rogue.Relics.NewState,MainScriptsAssembly\"")] // a class a newer game build added
    [InlineData("Riley Jones, 12 Main St", "a name of 23 characters that isn't shaped like a type's")] // words someone wrote there
    [InlineData("x\\\"Riley Jones lives at 12 Main St", "a name of 33 characters that isn't shaped like a type's")] // a quote inside it ends nothing
    [InlineData("\\\"\\\"Riley\\\"", "a name of 8 characters that isn't shaped like a type's")]
    public void A_refusal_for_the_log_of_the_pc_that_got_the_file_names_a_type_only_by_its_shape(string type, string logged)
    {
        // The type name is whatever the file's sender wrote: the PC that made the file logs it as it is, the PC that got the
        // file from another only when it's shaped like an identifier.
        Assert.False(Check($"{{\"__type\":\"float\",\"a\":{{\"__type\":\"{type}\"}}}}", out string problem));
        Assert.StartsWith("it names a type a run file never holds: ", problem, StringComparison.Ordinal);

        string forLog = RunFileTypes.ProblemForLog(problem);
        Assert.Equal("it names a type a run file never holds: " + logged, forLog);
        Assert.DoesNotContain("Riley", forLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Main St", forLog, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_refusal_is_logged_as_it_is()
    {
        Assert.False(Check("{\"__type\":\"float\",\"a\":nul}", out string bare));
        Assert.Equal(bare, RunFileTypes.ProblemForLog(bare));
        Assert.False(Check("[1]", out string notObject));
        Assert.Equal(notObject, RunFileTypes.ProblemForLog(notObject));
        Assert.Equal(string.Empty, RunFileTypes.ProblemForLog(null));
        Assert.Equal(string.Empty, RunFileTypes.ProblemForLog(string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[{\"__type\":\"float\"}]")]
    [InlineData("{}")]
    [InlineData("{\"a\":{\"__type\":\"float\"}")]
    [InlineData("{\"a\":{\"__type\":\"float\"}}}")]
    [InlineData("{\"a\":{\"__type\":\"float\"}} {}")]
    [InlineData("{\"a\":\"no end")]
    [InlineData("{\"a\":\"bad \\q escape\"}")]
    [InlineData("{\"a\":\"short \\u12\"}")]
    public void Anything_that_isnt_one_easy_save_object_is_refused(string text)
    {
        Assert.False(Check(text, out string problem));
        Assert.NotEmpty(problem);
    }

    // Easy Save's reader is not a JSON reader (ES3JSONReader, Oct 29 build): Read_string and ReadString take
    // U+201C and U+201D for quotes as well as ", at either end of a text, and any \x as x but for \b \f \n \r \t, with no \u.
    // These got past the check and Easy Save reads a __type out of each.
    [Theory]
    [InlineData("{\"__type\":\"float\",\"a\":\"x\u201C,\u201C__type\u201D:\u201CUnityEngine.GameObject,UnityEngine.CoreModule\u201D,\u201Cvalue\u201D:\u201Cy\"}")]
    [InlineData("{\"__type\":\"float\",\u201C_\\_type\u201D:\u201CUnityEngine.GameObject,UnityEngine.CoreModule\u201D,\"value\":{}}")]
    [InlineData("{\"__type\":\"float\",\"a\":\u201C_\\ES3Ref\u201D:1}")]
    [InlineData("{\"__type\":\"float\",\"a\":\"x\u201C:1}")]
    [InlineData("{\"__type\":\"float\",\"a\":\"x\u201D:1}")]
    [InlineData("{\"__type\":\"float\",\"a\":\u201Cx\"}")]
    [InlineData("{\"__type\":\"float\",\"_\\_type\":\"UnityEngine.GameObject,UnityEngine.CoreModule\"}")]
    public void What_easy_saves_reader_takes_for_a_quote_is_refused(string text)
    {
        Assert.False(Check(text, out string problem));
        Assert.NotEmpty(problem);
    }

    [Theory]
    [InlineData("{\"__type\":\"float\",\"a\":1\"b\"}")] // Easy Save skips a bare value up to its end, quote and all, where this would start a text
    [InlineData("{\"__type\":\"float\",\"a\":1{\"b\":2}}")]
    [InlineData("{\"__type\":\"float\",\"a\":\u00A01}")]
    [InlineData("{\"__type\":\"float\",\"a\":1}\uFEFF")]
    [InlineData("{\uFEFF\"__type\":\"float\"}")]
    [InlineData("{\"__type\":\"float\",\"a\":'b'}")]
    [InlineData("{\"__type\":\"float\",\"a\":\\\"b\"}")]
    [InlineData("{\"__type\":\"float\",\"a\":\"\\u0041\"}")] // the game's writer never writes it, and its reader takes it for "u0041"
    public void Anything_outside_what_easy_saves_writer_writes_is_refused(string text)
    {
        Assert.False(Check(text, out string problem));
        Assert.NotEmpty(problem);
    }

    // Where Easy Save's reader expects a text and finds a bare n (ReadQuotationMarkOrNullIgnoreWhitespace, Nov 6 build), it
    // reads three more characters: "ull" is null, and after anything else it goes on as if it had read an opening quote. Its
    // ReadPropertyName takes a comma before a name, so after ",n" and three characters, what stands before the next quote is a
    // property name to it: a __type (or an _ES3Ref) where this check saw another key, or a text.
    [Theory]
    [InlineData("{\"__type\":\"float\",\"value\":{,n,\"a__type\":\"UnityEngine.GameObject,UnityEngine.CoreModule\",\"b\":1}}")]
    [InlineData("{\"__type\":\"float\",\"value\":{,n:\"a__type\":\"UnityEngine.GameObject,UnityEngine.CoreModule\"}}")]
    [InlineData("{\"__type\":\"float\",\"value\":{,n \"a__type\":\"UnityEngine.GameObject,UnityEngine.CoreModule\"}}")]
    [InlineData("{\"__type\":\"float\",\"value\":[{\"a\":1},{,n\t\"a__type\":\"System.Diagnostics.Process,System\"}]}")]
    [InlineData("{\"__type\":\"float\",\"value\":{,n,\"a_ES3Ref\":\"123\"}}")]
    [InlineData("{\"__type\":\"float\",\"a\":n}")]
    [InlineData("{\"__type\":\"float\",\"a\":nul}")]
    [InlineData("{\"__type\":\"float\",\"a\":nulls}")]
    [InlineData("{\"__type\":\"float\",\"a\":nan}")]
    public void A_bare_value_that_starts_like_null_and_isnt_is_refused(string text)
    {
        Assert.False(Check(text, out string problem));
        Assert.Contains("null", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("-12")]
    [InlineData("1.5E-05")]
    [InlineData("3.402823E+38")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void The_bare_values_easy_saves_writer_writes_pass(string value)
    {
        string changed = RunFile.Replace("\"injuryCounter\" : NaN", $"\"injuryCounter\" : {value}", StringComparison.Ordinal);
        Assert.True(Check(changed, out string problem), problem);
        Assert.True(Check($"{{\"__type\":\"float\",\"a\":{value},\"b\":[{value},{value}],\"c\":{value}}}", out problem), problem);
    }

    [Fact]
    public void Bytes_that_arent_utf8_are_refused()
    {
        byte[] overlongQuote = new byte[] { 0xC0, 0xA2 };
        byte[] bytes = Encoding.UTF8.GetBytes("{\"__type\":\"float\",\"a\":\"").Concat(overlongQuote).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        Assert.False(RunFileTypes.TryCheck(bytes, RunFileTypes.RunFile, out string problem));
        Assert.NotEmpty(problem);
    }

    [Theory]
    [InlineData("The \\“Wall\\” Etienne")] // WritePrimitive(string) writes each with a backslash
    [InlineData("a\\\\b\\/c\\\"d\\b\\f\\n\\r\\t")]
    [InlineData("\u00C9tienne \u5927 \U0001F3D2 \u0001")] // everything else as it is, control characters too
    public void Any_name_easy_saves_writer_writes_passes(string name)
    {
        string changed = RunFile.Replace("\"id\" : \"f1\"", $"\"id\" : \"{name}\"", StringComparison.Ordinal);
        Assert.NotEqual(RunFile, changed);
        Assert.True(Check(changed, out string problem), problem);
    }

    [Fact]
    public void Nesting_and_size_have_limits()
    {
        string deep = "{\"__type\":\"float\",\"v\":" + new string('[', RunFileTypes.MaxDepth) + new string(']', RunFileTypes.MaxDepth) + "}";
        Assert.False(Check(deep, out string problem));
        Assert.Contains("nested", problem, StringComparison.Ordinal);
        string fits = "{\"__type\":\"float\",\"v\":" + new string('[', RunFileTypes.MaxDepth - 2) + new string(']', RunFileTypes.MaxDepth - 2) + "}";
        Assert.True(Check(fits, out problem), problem);

        byte[] big = new byte[RunFileTypes.MaxBytes + 1];
        Assert.False(RunFileTypes.TryCheck(big, RunFileTypes.RunFile, out problem));
        Assert.Contains("bytes", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Damaged_run_files_never_throw()
    {
        // Fuzz: truncations, flipped bytes and spliced garbage of a real-shaped run file. Every answer is a bool and a reason.
        byte[] good = Encoding.UTF8.GetBytes(RunFile);
        var random = new Random(20261030);
        for (int i = 0; i < 4000; i++)
        {
            byte[] bytes = (byte[])good.Clone();
            switch (i % 4)
            {
                case 0:
                    bytes = bytes[..random.Next(bytes.Length)];
                    break;
                case 1:
                    for (int k = 0; k < 1 + random.Next(8); k++)
                    {
                        bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                    }

                    break;
                case 2:
                    int at = random.Next(bytes.Length);
                    byte[] junk = new byte[random.Next(1, 32)];
                    random.NextBytes(junk);
                    bytes = bytes[..at].Concat(junk).Concat(bytes[at..]).ToArray();
                    break;
                default:
                    bytes = new byte[random.Next(64)];
                    random.NextBytes(bytes);
                    break;
            }

            bool ok = RunFileTypes.TryCheck(bytes, RunFileTypes.RunFile, out string problem);
            Assert.True(ok || problem.Length > 0);
        }
    }

    [Fact]
    public void The_allow_list_holds_the_run_files_roots_and_no_engine_objects()
    {
        foreach (string root in new[] { "State.RunDataModelV2", "ScorersStats", "MatchScore", "Tape2Tape.Hockey.GoalieStats", "State.TeamState", "Rogue.FreeAgents.FreeAgent" })
        {
            Assert.Contains(root, RunFileTypes.RunFile);
        }

        Assert.DoesNotContain(RunFileTypes.RunFile, n => n.StartsWith("UnityEngine.", StringComparison.Ordinal) || n.StartsWith("System.", StringComparison.Ordinal));
        Assert.Throws<ArgumentNullException>(() => RunFileTypes.IsAllowed(null!, RunFileTypes.RunFile));
        Assert.False(RunFileTypes.TryCheck(Encoding.UTF8.GetBytes(RunFile), new HashSet<string>(), out _));
    }
}
