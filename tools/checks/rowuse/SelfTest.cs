// The check's own tests, run on every gate before the check itself. Each plants a case in the real inputs (the plugin's
// metadata, the hook lists, the interop types) and asserts the analysis fails on it, so a check that stopped looking
// would fail here instead of passing quietly. The rules' small cases use real interop types and tiny row and use sets.
using System;
using System.Collections.Generic;
using System.Linq;

internal static class SelfTest
{
    private const string PlantedMember = "T2TSelfTestPlantedMember";

    /// <summary>Runs every test; returns the failures (empty when all passed) and the number of tests run.</summary>
    public static (List<string> Failures, int Run) Run(IReadOnlyList<Row> rows, UseData data)
    {
        var failures = new List<string>();
        int run = 0;

        void Expect(string name, bool condition, string detail = "")
        {
            run++;
            if (!condition)
            {
                failures.Add($"selftest FAILED: {name}{(detail.Length > 0 ? " (" + detail + ")" : string.Empty)}");
            }
        }

        Row? own = rows.FirstOrDefault(r => r.Kind == RowKind.Own && r.Type != null);
        Expect("the inputs have rows of the plugin's own lists that resolve to interop types", own != null);
        Expect("the plugin's metadata holds member references into the interop", data.Uses.Count > 100, $"{data.Uses.Count} found");
        if (own == null)
        {
            return (failures, run);
        }

        // The analysis of a plugin that refers to nothing is not a pass.
        Expect("no member reference at all is reported as nothing read", Analyzer.Analyze(rows, new UseData(), Array.Empty<AllowEntry>()).ReadNothing);
        Expect("no rows at all is reported as nothing read", Analyzer.Analyze(Array.Empty<Row>(), data, Array.Empty<AllowEntry>()).ReadNothing);

        // 1. A row removed that the code uses: the use is unlisted. (A use may be covered by more than one row: try uses until
        //    removing every row that lists one leaves it unlisted; the first always does unless a base type's row covers it.)
        Report baseline = Analyzer.Analyze(rows, data, Array.Empty<AllowEntry>());
        Dictionary<(Type, string), List<Row>> byTarget = Analyzer.Index(rows);
        bool planted = false;
        foreach (KeyValuePair<(string Type, string Member), int> use in data.Uses.OrderBy(u => u.Key.Type, StringComparer.Ordinal).ThenBy(u => u.Key.Member, StringComparer.Ordinal))
        {
            if (baseline.UnlistedKeys.Contains(use.Key) || use.Key.Member is ".ctor" or ".cctor")
            {
                continue;
            }

            List<Row> listing = Analyzer.Listing(rows, byTarget, Types.Index.GetValueOrDefault(use.Key.Type), use.Key.Member);
            if (listing.Count == 0 || listing.Any(r => r.Kind != RowKind.Own))
            {
                continue;
            }

            var without = rows.Where(r => !listing.Contains(r)).ToList();
            Report after = Analyzer.Analyze(without, data, Array.Empty<AllowEntry>());
            if (after.UnlistedKeys.Contains(use.Key))
            {
                planted = true;
                Expect("removing the rows of a member the code uses makes it unlisted and the report not clean", !after.Clean && !baseline.UnlistedKeys.Contains(use.Key),
                    Analyzer.Display(use.Key.Type) + "::" + use.Key.Member);
                break;
            }
        }

        Expect("a used member's rows could be removed to plant the case", planted);

        // 2. A use with no row at all.
        Type planting = own.Type!;
        UseData more = data.Copy();
        more.Uses[(planting.FullName!, PlantedMember)] = 1;
        Report withUse = Analyzer.Analyze(rows, more, Array.Empty<AllowEntry>());
        Expect("a member the code uses with no row is unlisted", withUse.UnlistedKeys.Contains((planting.FullName!, PlantedMember)) && !withUse.Clean);
        Expect("planting one use changes only that use", withUse.UnlistedKeys.Count == baseline.UnlistedKeys.Count + 1);

        // 3. A row nothing uses.
        var withRow = rows.ToList();
        withRow.Add(new Row("hooks.txt", 0, RowKind.Own, own.TypeName, PlantedMember, string.Empty) { Type = own.Type });
        Report withUnused = Analyzer.Analyze(withRow, data, Array.Empty<AllowEntry>());
        Expect("a row of the plugin's own lists that nothing uses is reported", withUnused.UnusedKeys.Contains(own.TypeName + "::" + PlantedMember) && !withUnused.Clean);
        Expect("planting one row changes only that row", withUnused.UnusedKeys.Count == baseline.UnusedKeys.Count + 1);

        // 4. The allow file: it holds a row only with its reason, is judged for staleness, and covers nothing else.
        AllowEntry keep = new("row", own.TypeName + "::" + PlantedMember, "kept for the test", 1);
        Report allowed = Analyzer.Analyze(withRow, data, new[] { keep });
        Expect("an allow entry covers its own unused row", !allowed.UnusedKeys.Contains(keep.Key) && allowed.Stale.Count == 0 && allowed.AllowedRows == 1);
        Report staleRow = Analyzer.Analyze(rows, data, new[] { keep });
        Expect("an allow entry for a row that is not unused is stale and fails", staleRow.Stale.Count == 1 && !staleRow.Clean);
        Report staleUse = Analyzer.Analyze(rows, data, new[] { new AllowEntry("use", "Nowhere.Type::Nothing", "x", 2) });
        Expect("an allow entry for a use that does not exist is stale and fails", staleUse.Stale.Count == 1 && !staleUse.Clean);
        AllowEntry plantedUse = new("use", Analyzer.Display(planting.FullName!) + "::" + PlantedMember, "kept for the test", 3);
        Report allowedUse = Analyzer.Analyze(rows, more, new[] { plantedUse });
        Expect("an allow entry covers its own unlisted use", !allowedUse.UnlistedKeys.Contains((planting.FullName!, PlantedMember)) && allowedUse.Stale.Count == 0);
        Expect("a stale entry is not hidden by a covered one", Analyzer.Analyze(withRow, data, new[] { keep, new AllowEntry("row", "No.Such::Row", "x", 4) }).Stale.Count == 1);
        Expect("an allow line without a reason is refused", Throws(() => Loading.ParseAllowLine("row Some.Type::Member", 1, "allow")));
        Expect("an allow line with an empty reason is refused", Throws(() => Loading.ParseAllowLine("row Some.Type::Member |   ", 1, "allow")));
        Expect("an allow line of a kind that does not exist is refused", Throws(() => Loading.ParseAllowLine("rows Some.Type::Member | why", 1, "allow")));
        Expect("an allow line with no member is refused", Throws(() => Loading.ParseAllowLine("row Some.Type | why", 1, "allow")));
        Expect("an allow line with its reason is read", Loading.ParseAllowLine("row Some.Type::Member | why", 1, "allow") is { Kind: "row", Key: "Some.Type::Member", Reason: "why" });

        // 5. The judgment rules, on small inputs of real interop types.
        string field = "_" + own.Member + "_k__BackingField";
        var one = new List<Row> { own };
        var useOnly = new UseData();
        useOnly.Uses[(own.Type!.FullName!, "get_" + field)] = 1;
        Report viaField = Analyzer.Analyze(one, useOnly, Array.Empty<AllowEntry>());
        Expect("a property's backing field is a use of the property's row", viaField.Unlisted.Count == 0 && viaField.Unused.Count == 0);
        var useOther = new UseData();
        useOther.Uses[(own.Type.FullName!, "get_" + field + "X")] = 1;
        Expect("a backing field of another property is not", Analyzer.Analyze(one, useOther, Array.Empty<AllowEntry>()).Unlisted.Count == 1);

        Type? glueType = Types.Index.GetValueOrDefault(Analyzer.Glue[0].Type);
        Expect("the interop's glue type is in the interop", glueType != null);
        var glue = new UseData();
        foreach ((string type, string member, _) in Analyzer.Glue)
        {
            glue.Uses[(type, member)] = 1;
        }

        glue.Uses[(own.Type.FullName!, own.Member)] = 1;
        Expect("the interop's own glue needs no row", Analyzer.Analyze(one, glue, Array.Empty<AllowEntry>()).Unlisted.Count == 0);
        var notGlue = new UseData();
        notGlue.Uses[(Analyzer.Glue[0].Type, "ToString")] = 1;
        notGlue.Uses[(own.Type.FullName!, own.Member)] = 1;
        Expect("a member of the glue's type that is not glue does", Analyzer.Analyze(one, notGlue, Array.Empty<AllowEntry>()).Unlisted.Count == 1);

        Type? enumType = Types.Index.Values.FirstOrDefault(t => t.IsEnum && t.IsPublic && Enum.GetNames(t).Length > 0 && t.FullName != null && t.FullName.StartsWith("Il2Cpp", StringComparison.Ordinal));
        Expect("the interop has an enum to test the enum rule with", enumType != null);
        if (enumType != null)
        {
            string typeName = Analyzer.Display(enumType.FullName!);
            var enumRow = new List<Row> { new("hooks.txt", 1, RowKind.Own, typeName, Enum.GetNames(enumType)[0], string.Empty) { Type = enumType } };
            var named = new UseData();
            named.Uses[(own.Type.FullName!, own.Member)] = 1;
            named.TypeRefs.Add(enumType.FullName!);
            Expect("an enum member's row is used when the plugin names the enum", Analyzer.Analyze(enumRow.Concat(one).ToList(), named, Array.Empty<AllowEntry>()).Unused.Count == 0);
            named.TypeRefs.Clear();
            Expect("and unused when it does not", Analyzer.Analyze(enumRow.Concat(one).ToList(), named, Array.Empty<AllowEntry>()).Unused.Count == 1);
        }

        var both = new List<Row> { own, new("clientmode.txt", 2, RowKind.ClientMode, own.TypeName, PlantedMember, string.Empty) { Type = own.Type } };
        var own2 = new Row("hooks.txt", 3, RowKind.Own, own.TypeName, PlantedMember, string.Empty) { Type = own.Type };
        both.Add(own2);
        var anchor = new UseData();
        anchor.Uses[(own.Type.FullName!, own.Member)] = 1;
        Expect("a row that clientmode.txt lists too is used", Analyzer.Analyze(both, anchor, Array.Empty<AllowEntry>()).Unused.Count == 0);
        both.RemoveAt(1);
        Expect("and unused without that row", Analyzer.Analyze(both, anchor, Array.Empty<AllowEntry>()).Unused.Count == 1);

        var strings = anchor.Copy();
        strings.Strings.Add(PlantedMember);
        Expect("a row whose member the plugin names in a string is used", Analyzer.Analyze(both, strings, Array.Empty<AllowEntry>()).Unused.Count == 0);

        var icall = anchor.Copy();
        icall.Strings.Add("Some.Namespace.Type::get_" + PlantedMember);
        Expect("a row whose getter the plugin names as an icall string is used", Analyzer.Analyze(both, icall, Array.Empty<AllowEntry>()).Unused.Count == 0);
        var byField = anchor.Copy();
        byField.Strings.Add("<" + PlantedMember + ">k__BackingField");
        Expect("a row whose backing field the plugin looks up by name is used", Analyzer.Analyze(both, byField, Array.Empty<AllowEntry>()).Unused.Count == 0);
        var unrelated = anchor.Copy();
        unrelated.Strings.Add("Some.Namespace.Type::get_" + PlantedMember + "Other");
        unrelated.Strings.Add("Prefix" + PlantedMember);
        Expect("a string that merely contains the name does not make the row used", Analyzer.Analyze(both, unrelated, Array.Empty<AllowEntry>()).Unused.Count == 1);

        var events = new List<Row>
        {
            new("hooks.txt", 1, RowKind.Own, "Some.Bus", "OnThing", string.Empty),
            new("presentation.txt", 2, RowKind.PresentationEvent, "Bus", "OnThing", string.Empty),
            own,
        };
        Expect("an event row that presentation.txt lists too is used", Analyzer.Analyze(events, anchor, Array.Empty<AllowEntry>()).Unused.Count == 0);

        // Constructor rows: used when the plugin constructs the type, never by the string ".ctor" (one literal names them all).
        var ctorRow = new Row("hooks.txt", 4, RowKind.Own, own.TypeName, ".ctor", string.Empty) { Type = own.Type };
        var ctorRows = new List<Row> { own, ctorRow };
        var anyCtorString = anchor.Copy();
        anyCtorString.Strings.Add(".ctor");
        Expect("a constructor row is unused when the plugin never constructs the type, even with the string .ctor in it",
            Analyzer.Analyze(ctorRows, anyCtorString, Array.Empty<AllowEntry>()).UnusedKeys.SetEquals(new[] { own.TypeName + "::.ctor" }));
        var constructs = anchor.Copy();
        constructs.Uses[(own.Type.FullName!, ".ctor")] = 1;
        Report constructed = Analyzer.Analyze(ctorRows, constructs, Array.Empty<AllowEntry>());
        Expect("a constructor row is used when the plugin constructs the type", constructed.Unused.Count == 0 && constructed.Unlisted.Count == 0);
        Type? other = Types.Index.Values.FirstOrDefault(t => t != own.Type && t.FullName != null && t.FullName.StartsWith("Il2Cpp", StringComparison.Ordinal));
        Expect("the interop has another type to test a constructor of the wrong type with", other != null);
        if (other != null)
        {
            var wrongType = anchor.Copy();
            wrongType.Uses[(other.FullName!, ".ctor")] = 1;
            Expect("and not when the plugin constructs another type", Analyzer.Analyze(ctorRows, wrongType, Array.Empty<AllowEntry>()).Unused.Count == 1);
        }

        var kept = new AllowEntry("row", own.TypeName + "::.ctor", "made by the game's own CreateInstance", 5);
        Report keptCtor = Analyzer.Analyze(ctorRows, anyCtorString, new[] { kept });
        Expect("a constructor row can be kept in the allow file with its reason", keptCtor.Unused.Count == 0 && keptCtor.Stale.Count == 0 && keptCtor.AllowedRows == 1);
        Expect("and that entry is stale once the plugin constructs the type", Analyzer.Analyze(ctorRows, constructs, new[] { kept }).Stale.Count == 1);
        Expect("a constructor use needs no row", Analyzer.Analyze(one, constructs, Array.Empty<AllowEntry>()).Unlisted.Count == 0);

        // The real inputs: a stale constructor row planted beside the real ones is found, whatever strings the plugin holds.
        Type? neverBuilt = Types.Index.Values.FirstOrDefault(t => t.FullName != null && t.FullName.StartsWith("Il2Cpp", StringComparison.Ordinal) && !data.Uses.ContainsKey((t.FullName, ".ctor")));
        Expect("the interop has a type the plugin never constructs", neverBuilt != null);
        if (neverBuilt != null)
        {
            var staleCtor = rows.ToList();
            staleCtor.Add(new Row("hooks.txt", 0, RowKind.Own, Analyzer.Display(neverBuilt.FullName!), ".ctor", string.Empty) { Type = neverBuilt });
            UseData withString = data.Copy();
            withString.Strings.Add(".ctor");
            Report stale = Analyzer.Analyze(staleCtor, withString, Array.Empty<AllowEntry>());
            Expect("a stale constructor row among the real rows fails the check", stale.UnusedKeys.Contains(Analyzer.Display(neverBuilt.FullName!) + "::.ctor") && !stale.Clean);
        }

        // Names: the accessor and backing-field forms.
        Expect("a getter names its property", Analyzer.NamesOf("get_Count").Contains("Count"));
        Expect("a setter names its property", Analyzer.NamesOf("set_Count").Contains("Count"));
        Expect("a backing field names its property", Analyzer.NamesOf("get__Count_k__BackingField").Contains("Count"));
        Expect("an ordinary method names only itself", Analyzer.NamesOf("Count").SequenceEqual(new[] { "Count" }));
        return (failures, run);
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }
}
