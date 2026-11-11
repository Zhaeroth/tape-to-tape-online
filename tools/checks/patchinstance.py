#!/usr/bin/env python3
"""Harmony patch check: no patch asks for __instance on a static game method.

Harmony gives a patch's __instance parameter as null when the patched method is static (HarmonyManipulator emits
`ldnull` for it when the original is static), so a patch that uses it reads a null object on every call: a
NullReferenceException in the game's callback, caught and logged, and the feature never runs.

Reads every .cs file under the plugin's source folder. A patch registration is a call that names its target as
typeof(Type) followed by a method name, as a string ("Method") or nameof(Type.Method), and names its patches by
nameof(...): `Patch(harmony, failed, typeof(T), "M", nameof(Prefix))`, `AccessTools.Method(typeof(T), nameof(T.M))` inside a
Patch(...) call whose other arguments are nameof(Prefix), the chained forms (`Patch(...) & Patch(...)`) each by their own
call. The patch methods that take a parameter named __instance are the ones judged. The target is looked up in the
decompiled listing's dump.cs by the type's short name and the method's name; it fails when the listing declares the
method and every declaration of that name in the type is static. A type or method the listing doesn't hold (an engine
member, a generic type, a type named through an alias, a name built from parts) can't be judged and is counted as
unchecked, and a listing in which none of the targets is found is an error, never a pass (the wrong dump.cs); a pair that
names the patch itself (typeof(PatchClass), nameof(Patch), as some registration helpers take it) is no target and isn't
counted; one with a static and an instance overload is
listed as mixed (the patch can't say which it means: read it). A patch with __instance that no registration names is
unreached (patched through a helper or a loop this check can't read): a failure, unless the allow file (tools/checks/
patchinstance_allow.txt: '<patch> | <Type> | <Method>[, <Method>] | <reason>') names the patch's target, which is then
judged like any registration; an entry for a patch that is now reached, or that no longer takes __instance, fails too.

Usage:
    patchinstance.py <plugin source folder> <dump.cs> [<allow file>]
                                                        (exit 0: clean; 1: a static target, an unreached patch or a stale
                                                         allow entry; 2: bad usage, an unreadable input, no patch call found,
                                                         or a listing that holds none of the patched methods)
    patchinstance.py --selftest                         the checker's own tests
"""

from __future__ import annotations

import os
import re
import sys
import tempfile
import unittest
from collections import defaultdict

TARGET = re.compile(r"typeof\(\s*([\w.]+)\s*\)\s*,\s*(?:\"(\w+)\"|nameof\(\s*[\w.]*?(\w+)\s*\))")
PATCH_NAME = re.compile(r"nameof\(\s*(?:[\w]+\.)*(\w+)\s*\)")
DEFINITION = re.compile(r"\bstatic\s+[\w<>\[\]?,.]+\s+(\w+)\s*\(([^)]*)\)")
INSTANCE = re.compile(r"\b__instance\b")


def mask(text: str) -> str:
    """The text with comments blanked and every string's content replaced by x (same length, newlines kept), so the
    brackets that are left are the code's."""
    out = list(text)
    i, n = 0, len(text)

    def blank(a: int, b: int, keep: str | None = None) -> None:
        for k in range(a, b):
            if text[k] not in "\r\n":
                out[k] = keep or " "

    while i < n:
        c = text[i]
        two = text[i:i + 2]
        if two == "//":
            j = text.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
        elif two == "/*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
        elif c == "'":
            j = i + 1
            while j < n and text[j] != "'" and text[j] != "\n":
                j += 2 if text[j] == "\\" else 1
            blank(i + 1, min(j, n), "x")
            i = j + 1
        elif c == '"' or (c in "@$" and re.match(r"[@$]{1,2}\"", text[i:i + 3])):
            m = re.match(r"[@$]{0,2}\"", text[i:])
            verbatim = "@" in m.group(0)
            j = i + len(m.group(0))
            start = j
            while j < n:
                if verbatim:
                    if text[j] == '"':
                        if text[j + 1:j + 2] == '"':
                            j += 2
                            continue
                        break
                    j += 1
                else:
                    if text[j] == "\\":
                        j += 2
                        continue
                    if text[j] == '"' or text[j] == "\n":
                        break
                    j += 1
            blank(start, min(j, n), "x")
            i = j + 1
        else:
            i += 1
    return "".join(out)


def match_forward(masked: str, open_at: int) -> int:
    """The index of the ')' that closes the '(' at open_at (the text's end when it doesn't)."""
    depth = 0
    for k in range(open_at, len(masked)):
        if masked[k] == "(":
            depth += 1
        elif masked[k] == ")":
            depth -= 1
            if depth == 0:
                return k
    return len(masked)


def enclosing_calls(masked: str, pos: int):
    """The spans (open, close) of the parentheses around pos, innermost first, up to the statement's start."""
    depth = 0
    k = pos - 1
    while k >= 0:
        c = masked[k]
        if c == ")":
            depth += 1
        elif c == "(":
            if depth == 0:
                yield k, match_forward(masked, k)
            else:
                depth -= 1
        elif c in ";{}" and depth == 0:
            return
        k -= 1


class Registration:
    def __init__(self, path: str, type_name: str, method: str, patches: list[str]) -> None:
        self.path, self.type_name, self.method, self.patches = path, type_name, method, patches


def read_source(folder: str):
    """(definitions: patch method name -> True when it takes __instance, registrations, files read). Every file is read
    for its patch definitions before any registration is, so a patch may be defined anywhere."""
    defs: dict[str, bool] = {}
    sources: list[tuple[str, str, str]] = []
    for root, _, names in os.walk(folder):
        if os.sep + "obj" + os.sep in root + os.sep or os.sep + "bin" + os.sep in root + os.sep:
            continue
        for name in sorted(names):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(root, name)
            with open(path, encoding="utf-8") as handle:
                text = handle.read()
            masked = mask(text)
            sources.append((os.path.relpath(path, folder), text, masked))
            for m in DEFINITION.finditer(masked):
                defs[m.group(1)] = defs.get(m.group(1), False) or bool(INSTANCE.search(m.group(2)))
    registrations: list[Registration] = []
    for path, text, masked in sources:
        for m in TARGET.finditer(masked):
            # The method's name is a string in the real text and x's in the masked one.
            real = TARGET.match(text, m.start())
            if real is None:
                continue
            patches: list[str] = []
            for a, b in enclosing_calls(masked, m.start()):
                found = [p for p in PATCH_NAME.findall(masked[a:b + 1]) if p in defs]
                if found:
                    patches = found
                    break
            registrations.append(Registration(path, m.group(1).split(".")[-1], real.group(2) or real.group(3), patches))
    return defs, registrations, len(sources)


def read_listing(dump_path: str):
    """short type name -> list of (full name, [(method name, is static)])."""
    types: dict[str, list[tuple[str, list[tuple[str, bool]]]]] = defaultdict(list)
    current = None
    head = re.compile(r"^(?:class|struct|interface)\s+([^\s:]+)")
    with open(dump_path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line = line.rstrip("\n")
            m = head.match(line)
            if m:
                full = m.group(1)
                current = (full, [])
                types[re.sub(r"<.*", "", full).split(".")[-1]].append(current)
            elif current is not None and line.startswith("\t") and "RVA" in line and "(" in line:
                before = line.split("(", 1)[0]
                name = re.search(r"(\w+)(?:<[^>]*>)?\s*$", before)
                if name:
                    current[1].append((name.group(1), re.search(r"\bstatic\b", before) is not None))
    return types


def read_allow(path: str | None):
    """patch method name -> (type's short name, [methods], reason), from lines '<patch> | <Type> | <Method>[, <Method>] | <reason>'."""
    allow: dict[str, tuple[str, list[str], str]] = {}
    if path is None:
        return allow
    with open(path, encoding="utf-8") as handle:
        for number, raw in enumerate(handle, 1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            fields = [f.strip() for f in line.split("|", 3)]
            if len(fields) != 4 or not all(fields) or fields[0] in allow:
                raise ValueError(f"{os.path.basename(path)}:{number}: expected '<patch> | <Type> | <Method>[, <Method>] | <reason>' once per patch, every field filled")
            allow[fields[0]] = (fields[1], [m.strip() for m in fields[2].split(",") if m.strip()], fields[3])
    return allow


def judge(types, type_name: str, method: str):
    """(static overloads, instance overloads) of the listing's methods of that name on the type."""
    statics, instances = 0, 0
    for _, methods in types.get(type_name, []):
        for name, is_static in methods:
            if name == method:
                if is_static:
                    statics += 1
                else:
                    instances += 1
    return statics, instances


def check(source: str, dump: str, out, allow_path: str | None = None) -> int:
    defs, registrations, files = read_source(source)
    types = read_listing(dump)
    allow = read_allow(allow_path)
    reached: set[str] = set()
    static_hits, mixed, stale, unchecked, read = [], [], [], 0, 0

    def judged(where: str, type_name: str, method: str, asking: list[str]) -> None:
        nonlocal unchecked, read
        read += 1
        statics, instances = judge(types, type_name, method)
        if statics and not instances:
            static_hits.append(f"STATIC  {where}: {type_name}.{method} is static in the listing; {', '.join(asking)} asks for __instance, which Harmony passes as null")
        elif statics:
            mixed.append(f"MIXED   {where}: {type_name}.{method} has {statics} static and {instances} instance overload(s); {', '.join(asking)} asks for __instance (read it)")
        elif not instances:
            unchecked += 1

    for reg in registrations:
        reached.update(reg.patches)
        asking = [p for p in reg.patches if defs.get(p)]
        if reg.method in reg.patches and reg.type_name not in types:
            # typeof(PatchClass), nameof(Patch) in a call that names the patch by its class too
            # (Prefix(h, typeof(T), nameof(T.M), typeof(Own), nameof(OwnPrefix))): the patch's own pair, not a target.
            # The target is the call's other pair, judged by itself.
            continue
        if asking:
            judged(reg.path, reg.type_name, reg.method, asking)
    # Patches registered by a helper or a loop this check can't read are named in the allow file with their target, and
    # judged against it like any other; an entry that no longer describes a patch fails.
    for patch, (type_name, methods, _) in sorted(allow.items()):
        if not defs.get(patch):
            stale.append(f"STALE   the allow file names {patch}, which no longer takes __instance: remove the entry")
        elif patch in reached:
            stale.append(f"STALE   the allow file names {patch}, which a patch call this check reads now covers: remove the entry")
        else:
            for method in methods:
                judged("allow file", type_name, method, [patch])
    unreached = sorted(p for p, asks in defs.items() if asks and p not in reached and p not in allow)
    if not registrations:
        print(f"patchinstance: no patch call found in {files} file(s): the check read nothing", file=out)
        return 2
    if read and unchecked == read:
        # Not one target was found in the listing (an empty or a wrong dump.cs): nothing was judged, so nothing passed.
        print(f"patchinstance: the listing holds none of the {read} patched method(s) that take __instance: the check judged nothing",
              file=out)
        return 2
    for line in static_hits + mixed + stale:
        print(line, file=out)
    for p in unreached:
        print(f"UNREACHED {p} takes __instance but no patch call names it: this check can't tell what it patches "
              "(name its target in the allow file, with the reason)", file=out)
    print(f"patchinstance: {files} file(s), {len(registrations)} patch call(s) read, {read} with an __instance patch, "
          f"{len(static_hits)} on a static method, {len(mixed)} mixed, {unchecked} not in the listing, "
          f"{len(unreached)} unreached, {len(stale)} stale allow entries", file=out)
    return 1 if static_hits or unreached or stale else 0


class SelfTest(unittest.TestCase):
    """Each case writes a small source folder and listing and runs the real check on them."""

    DUMP = (
        "class Game.Rogue : Base\n{\n"
        "\tpublic static void Roll() // RVA 0x10\n"
        "\tpublic void Pick(int n) // RVA 0x20\n"
        "\tpublic static void Both() // RVA 0x30\n"
        "\tpublic void Both(int n) // RVA 0x40\n"
        "\tprivate static void Gen<T>(T t) // RVA 0x50\n"
        "}\n"
    )

    def run_check(self, source: str, dump: str | None = None, allow: str | None = None):
        with tempfile.TemporaryDirectory() as folder:
            os.makedirs(os.path.join(folder, "src"))
            with open(os.path.join(folder, "src", "P.cs"), "w", encoding="utf-8") as h:
                h.write(source)
            with open(os.path.join(folder, "dump.cs"), "w", encoding="utf-8") as h:
                h.write(self.DUMP if dump is None else dump)
            allow_path = None
            if allow is not None:
                allow_path = os.path.join(folder, "allow.txt")
                with open(allow_path, "w", encoding="utf-8") as h:
                    h.write(allow)
            import io
            buffer = io.StringIO()
            code = check(os.path.join(folder, "src"), os.path.join(folder, "dump.cs"), buffer, allow_path)
            return code, buffer.getvalue()

    def test_a_patch_asking_for_instance_on_a_static_method_fails(self):
        code, text = self.run_check('class P { void S(H h) { Patch(h, f, typeof(Rogue), "Roll", nameof(RollPostfix)); }'
                                    ' static void RollPostfix(Rogue __instance) { } }')
        self.assertEqual(code, 1, text)
        self.assertIn("STATIC", text)
        self.assertIn("Rogue.Roll", text)

    def test_the_same_through_accesstools_and_nameof(self):
        code, text = self.run_check('class P { void S(H h) { Patch(h, f, AccessTools.Method(typeof(Rogue), nameof(Rogue.Roll)), nameof(Pre), null); }'
                                    ' static bool Pre(Rogue __instance) => true; }')
        self.assertEqual(code, 1, text)
        self.assertIn("STATIC", text)

    def test_an_instance_target_passes(self):
        code, text = self.run_check('class P { void S(H h) { Patch(h, f, typeof(Rogue), "Pick", nameof(PickPostfix)); }'
                                    ' static void PickPostfix(Rogue __instance) { } }')
        self.assertEqual(code, 0, text)
        self.assertIn("1 with an __instance patch, 0 on a static", text)

    def test_a_static_target_with_no_instance_in_its_patch_passes(self):
        code, text = self.run_check('class P { void S(H h) { Patch(h, f, typeof(Rogue), "Roll", nameof(RollPostfix)); }'
                                    ' static void RollPostfix(ref int __result) { } }')
        self.assertEqual(code, 0, text)

    def test_chained_calls_are_judged_each_by_its_own_patch(self):
        # A static target patched by a patch with no __instance, chained with an instance target whose patch has one.
        code, text = self.run_check('class P { bool S(H h) { return Patch(h, typeof(Rogue), "Roll", nameof(RollPostfix))'
                                    ' & Patch(h, typeof(Rogue), "Pick", nameof(PickPostfix)); }'
                                    ' static void RollPostfix(ref int __result) { } static void PickPostfix(Rogue __instance) { } }')
        self.assertEqual(code, 0, text)

    def test_the_patch_may_be_defined_after_its_registration_and_in_another_file(self):
        with tempfile.TemporaryDirectory() as folder:
            os.makedirs(os.path.join(folder, "src"))
            with open(os.path.join(folder, "src", "A.cs"), "w", encoding="utf-8") as h:
                h.write('class A { void S(H h) { Patch(h, f, typeof(Rogue), "Roll", nameof(B.RollPostfix)); } }')
            with open(os.path.join(folder, "src", "B.cs"), "w", encoding="utf-8") as h:
                h.write("class B { internal static void RollPostfix(Rogue __instance) { } }")
            with open(os.path.join(folder, "dump.cs"), "w", encoding="utf-8") as h:
                h.write(self.DUMP)
            import io
            buffer = io.StringIO()
            self.assertEqual(check(os.path.join(folder, "src"), os.path.join(folder, "dump.cs"), buffer), 1, buffer.getvalue())

    def test_a_static_and_an_instance_overload_is_listed_but_does_not_fail(self):
        code, text = self.run_check('class P { void S(H h) { Patch(h, f, typeof(Rogue), "Both", nameof(BothPrefix)); }'
                                    ' static bool BothPrefix(Rogue __instance) => true; }')
        self.assertEqual(code, 0, text)
        self.assertIn("MIXED", text)

    def test_a_generic_static_method_is_read_too(self):
        code, text = self.run_check('class P { void S(H h) { Patch(h, f, typeof(Rogue), "Gen", nameof(GenPrefix)); }'
                                    ' static bool GenPrefix(Rogue __instance) => true; }')
        self.assertEqual(code, 1, text)

    def test_a_type_the_listing_lacks_is_counted_not_failed(self):
        code, text = self.run_check('class P { void S(H h) { Patch(h, f, typeof(Camera), "Render", nameof(RenderPrefix));'
                                    ' Patch(h, f, typeof(Rogue), "Pick", nameof(PickPostfix)); }'
                                    ' static bool RenderPrefix(Camera __instance) => true;'
                                    ' static void PickPostfix(Rogue __instance) { } }')
        self.assertEqual(code, 0, text)
        self.assertIn("2 with an __instance patch", text)
        self.assertIn("1 not in the listing", text)

    def test_a_listing_that_holds_no_patched_method_is_an_error_not_a_pass(self):
        source = ('class P { void S(H h) { Patch(h, f, typeof(Rogue), "Roll", nameof(RollPostfix)); }'
                  ' static void RollPostfix(Rogue __instance) { } }')
        for dump in ("", "class Other.Thing\n{\n\tpublic void Pick(int n) // RVA 0x20\n}\n"):
            code, text = self.run_check(source, dump=dump)
            self.assertEqual(code, 2, text)
            self.assertIn("judged nothing", text)

    def test_a_patch_named_by_its_own_class_is_not_read_as_a_target(self):
        # Prefix(h, f, typeof(Target), nameof(Target.Method), typeof(Own), nameof(OwnPrefix)): the second pair names the
        # patch, and only the first is judged (and counted).
        own = ('class Own { void S(H h) { Scene.Prefix(h, f, typeof(Rogue), nameof(Rogue.%s), typeof(Own), nameof(OwnPrefix)); }'
               ' static bool OwnPrefix(Rogue __instance) => true; }')
        code, text = self.run_check(own % "Pick")
        self.assertEqual(code, 0, text)
        self.assertIn("1 with an __instance patch", text)
        self.assertIn("0 not in the listing", text)
        code, text = self.run_check(own % "Roll")
        self.assertEqual(code, 1, text)
        self.assertIn("Rogue.Roll is static", text)

    def test_a_patch_with_instance_that_no_call_names_is_unreached_and_fails(self):
        code, text = self.run_check('class P { void S(H h) { Patch(h, f, typeof(Rogue), "Pick", nameof(PickPostfix)); }'
                                    ' static void PickPostfix(Rogue __instance) { } static void Lost(Rogue __instance) { } }')
        self.assertEqual(code, 1, text)
        self.assertIn("UNREACHED Lost", text)

    def test_a_call_in_a_comment_or_a_string_is_not_a_call(self):
        code, text = self.run_check('class P { void S(H h) { // Patch(h, typeof(Rogue), "Roll", nameof(RollPostfix));\n'
                                    ' var s = "Patch(h, typeof(Rogue), \\"Roll\\", nameof(RollPostfix))";\n'
                                    ' Patch(h, f, typeof(Rogue), "Pick", nameof(PickPostfix)); }'
                                    ' static void RollPostfix(Rogue __instance) { } static void PickPostfix(Rogue __instance) { } }')
        # RollPostfix is named only in a comment and a string: unreached (so the failure is the unreached one, never a hidden pass).
        self.assertEqual(code, 1, text)
        self.assertIn("UNREACHED RollPostfix", text)
        self.assertNotIn("STATIC", text)

    def test_a_tree_with_no_patch_call_is_an_error_never_a_pass(self):
        code, text = self.run_check("class P { }")
        self.assertEqual(code, 2, text)

    HELPER = ('class P { void S(H h) { Patch(h, f, typeof(Rogue), "Pick", nameof(PickPostfix)); }'
              ' static void PickPostfix(Rogue __instance) { } static void Lost(Rogue __instance) { } }')

    def test_an_unreached_patch_named_in_the_allow_file_is_judged_against_its_target(self):
        code, text = self.run_check(self.HELPER, allow="Lost | Rogue | Pick | patched by a loop over names\n")
        self.assertEqual(code, 0, text)
        self.assertNotIn("UNREACHED", text)
        code, text = self.run_check(self.HELPER, allow="Lost | Rogue | Roll | patched by a loop over names\n")
        self.assertEqual(code, 1, text)
        self.assertIn("STATIC  allow file: Rogue.Roll", text)

    def test_an_allow_entry_for_a_patch_that_is_reached_or_gone_is_stale_and_fails(self):
        code, text = self.run_check(self.HELPER, allow="Lost | Rogue | Pick | x\nPickPostfix | Rogue | Pick | x\n")
        self.assertEqual(code, 1, text)
        self.assertIn("STALE   the allow file names PickPostfix", text)
        code, text = self.run_check(self.HELPER, allow="Lost | Rogue | Pick | x\nGone | Rogue | Pick | x\n")
        self.assertEqual(code, 1, text)
        self.assertIn("names Gone, which no longer takes __instance", text)

    def test_an_allow_entry_with_no_reason_or_a_second_entry_for_a_patch_is_refused(self):
        with self.assertRaises(ValueError):
            self.run_check(self.HELPER, allow="Lost | Rogue | Pick | \n")
        with self.assertRaises(ValueError):
            self.run_check(self.HELPER, allow="Lost | Rogue | Pick | x\nLost | Rogue | Roll | y\n")

    def test_the_listing_reads_static_and_instance_declarations(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "dump.cs")
            with open(path, "w", encoding="utf-8") as h:
                h.write(self.DUMP)
            listing = read_listing(path)
            methods = dict()
            for _, found in listing["Rogue"]:
                for name, is_static in found:
                    methods.setdefault(name, set()).add(is_static)
            self.assertEqual(methods["Roll"], {True})
            self.assertEqual(methods["Pick"], {False})
            self.assertEqual(methods["Both"], {True, False})
            self.assertEqual(methods["Gen"], {True})


def main(argv: list[str]) -> int:
    if argv == ["--selftest"]:
        result = unittest.TextTestRunner(stream=sys.stderr, verbosity=0).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTest))
        print(f"patchinstance selftest: {result.testsRun} tests, {len(result.failures) + len(result.errors)} failed", file=sys.stderr)
        return 0 if result.wasSuccessful() else 1
    if len(argv) not in (2, 3):
        print(__doc__)
        return 2
    source, dump = argv[0], argv[1]
    allow = argv[2] if len(argv) == 3 else None
    if not os.path.isdir(source) or not os.path.isfile(dump) or (allow is not None and not os.path.isfile(allow)):
        print(f"patchinstance: needs the plugin's source folder, the listing's dump.cs and, if given, the allow file ('{source}', '{dump}')")
        return 2
    try:
        return check(source, dump, sys.stdout, allow)
    except (OSError, ValueError) as ex:
        print(f"patchinstance: {ex}")
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
