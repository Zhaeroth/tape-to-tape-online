#!/usr/bin/env python3
"""Release hygiene check: refuses text that matches a list of forbidden patterns.

The patterns live in a file you keep outside the repository (personal names, local paths, internal references: whatever
must never be published). Each non-empty line that doesn't start with '#' is one of:

    block|<regex>                    a match anywhere fails the check
    allow|<regex>|<glob>[,<glob>...] a match of <regex> is accepted in files whose repository path matches a glob

Regexes are Python syntax and case-insensitive. A match is reported once per file and line.

Usage:
    forbidden_strings.py --patterns FILE tree [DIR]       tracked and untracked (not ignored) files of the repo at DIR
    forbidden_strings.py --patterns FILE staged [DIR]     the staged content of the repo at DIR (for a pre-commit hook)
    forbidden_strings.py --patterns FILE message FILE     a commit message file (for a commit-msg hook)
    forbidden_strings.py --patterns FILE zip ZIP...       every entry of each zip, binaries included; with
                         [--skip-identical-to REF.zip]    --skip-identical-to, an entry whose bytes equal those of the
                                                          entry of the same path in REF.zip is left out: third-party
                                                          files checked another way, such as by the release's SHA-256
    forbidden_strings.py --patterns FILE history [DIR]    every commit of the repo at DIR: messages, author and committer
                                                          identities, time zones other than +0000, and every file version
    forbidden_strings.py --patterns FILE files PATH...    files outside any repository (a session log, a bug report's
                                                          files): each file named, and every file under each folder
                                                          named; a match is reported against the path below that folder
    forbidden_strings.py selftest                         the scanner's own tests (no patterns file)

Each line is searched once with all the block patterns joined into one regex, and only a line where that finds something
goes through the patterns one by one: the same findings, with one regex call for most lines instead of one per pattern.

Binary content is searched through its printable runs, read as ASCII and as UTF-16LE. Exit 0: nothing found; 1: matches
(listed on stdout); 2: bad usage, an unreadable input, or a patterns file with no block pattern (a scan with nothing to
look for would always pass).
"""

from __future__ import annotations

import argparse
import contextlib
import fnmatch
import io
import os
import random
import re
import subprocess
import sys
import tempfile
import unittest
import zipfile

MIN_RUN = 4
ASCII_RUN = re.compile(rb"[\x20-\x7e]{%d,}" % MIN_RUN)
UTF16_RUN = re.compile(rb"(?:[\x20-\x7e]\x00){%d,}" % MIN_RUN)

# A reference to a group (by number or name) or a condition on one: joined with the other patterns, its group numbers
# would point at another pattern's groups.
GROUP_REFERENCE = re.compile(r"\\[1-9]|\(\?P=|\(\?\(")
# A flag group that applies to the whole regex, such as "(?x)": joined, it would apply to every pattern, or not compile.
WHOLE_REGEX_FLAGS = re.compile(r"\(\?[aiLmsux]+\)")


def joined_block(block: list[re.Pattern[str]]) -> re.Pattern[str] | None:
    """One regex that finds a match in a line exactly when some block pattern does: the alternation of them all, each in
    its own group. A search for an alternation fails only where every alternative fails at every position, so a line it
    passes over holds no match of any pattern; only the lines it hits go through the patterns one by one (one search a
    line instead of one per pattern). None, and every line goes through every pattern, when joining could change what a
    pattern means (a group reference, a whole-regex flag) or the joined regex doesn't compile (a clash such as a group name
    used twice, or one more level of nesting than Python's parser can take, which a pattern alone may be just within)."""
    sources = [regex.pattern for regex in block]
    if not sources or any(GROUP_REFERENCE.search(s) or WHOLE_REGEX_FLAGS.search(s) for s in sources):
        return None
    try:
        return re.compile("|".join(f"(?:{s})" for s in sources), re.IGNORECASE)
    except (re.error, RecursionError, OverflowError):
        return None


class Patterns:
    def __init__(self, path: str) -> None:
        self.block: list[re.Pattern[str]] = []
        self.allow: list[tuple[re.Pattern[str], list[str]]] = []
        with open(path, encoding="utf-8") as handle:
            for number, raw in enumerate(handle, 1):
                line = raw.strip()
                if not line or line.startswith("#"):
                    continue
                kind, _, rest = line.partition("|")
                try:
                    if kind == "block":
                        self.block.append(re.compile(rest, re.IGNORECASE))
                    elif kind == "allow":
                        regex, _, globs = rest.rpartition("|")
                        self.allow.append((re.compile(regex, re.IGNORECASE), [g.strip() for g in globs.split(",") if g.strip()]))
                    else:
                        raise ValueError(f"unknown kind '{kind}'")
                except (re.error, ValueError) as error:
                    raise SystemExit(f"{path}:{number}: {error}")
        self.joined = joined_block(self.block)

    def allowed(self, text: str, where: str) -> bool:
        for regex, globs in self.allow:
            if regex.fullmatch(text) and any(fnmatch.fnmatch(where, g) for g in globs):
                return True
        return False

    def findings(self, text: str, where: str, joined: bool = True) -> list[tuple[int, str]]:
        """Each block match in the text that isn't allowed at this path, as (line number, match). Lines the joined regex
        passes over are skipped (joined_block); with joined False every line goes through every pattern (the
        self-test's reference)."""
        found: list[tuple[int, str]] = []
        first = self.joined if joined else None
        for number, line in enumerate(text.splitlines(), 1):
            if first is not None and first.search(line) is None:
                continue
            for regex in self.block:
                for match in regex.finditer(line):
                    if not self.allowed(match.group(0), where):
                        found.append((number, match.group(0)))
        return found


def text_of(data: bytes) -> str:
    """Readable text of a file: the whole of it when it decodes as UTF-8, else its printable runs."""
    if b"\x00" not in data[:8000]:
        try:
            return data.decode("utf-8")
        except UnicodeDecodeError:
            pass
    runs = [m.group(0).decode("ascii") for m in ASCII_RUN.finditer(data)]
    runs += [m.group(0).decode("utf-16-le") for m in UTF16_RUN.finditer(data)]
    return "\n".join(runs)


def git(repo: str, *args: str, binary: bool = False) -> bytes | str:
    result = subprocess.run(["git", "-C", repo, *args], capture_output=True, check=True)
    return result.stdout if binary else result.stdout.decode("utf-8", "replace")


def report(patterns: Patterns, where: str, data: bytes, out: list[str]) -> None:
    for line, match in patterns.findings(text_of(data), where):
        out.append(f"{where}:{line}: {match}")


def scan_tree(patterns: Patterns, repo: str, out: list[str]) -> None:
    files = git(repo, "ls-files", "-z", "--cached", "--others", "--exclude-standard").split("\0")
    for name in filter(None, files):
        report(patterns, name, name.encode("utf-8"), out)  # the path itself
        path = os.path.join(repo, name)
        if os.path.isfile(path):
            with open(path, "rb") as handle:
                report(patterns, name, handle.read(), out)


def scan_staged(patterns: Patterns, repo: str, out: list[str]) -> None:
    names = git(repo, "diff", "--cached", "--name-only", "-z", "--diff-filter=ACMR").split("\0")
    for name in filter(None, names):
        report(patterns, name, name.encode("utf-8"), out)
        report(patterns, name, git(repo, "show", f":{name}", binary=True), out)


def scan_zip(patterns: Patterns, path: str, out: list[str], reference: zipfile.ZipFile | None) -> int:
    """Scans every file entry that isn't byte for byte the entry of the same path in the reference zip; returns how many
    were left out. The bytes themselves are compared (both reads check their CRC-32), never the headers' size and CRC
    alone, which a different file can share."""
    known = {e.filename: e for e in reference.infolist() if not e.is_dir()} if reference else {}
    skipped = 0
    with zipfile.ZipFile(path) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            data = archive.read(entry)
            same = known.get(entry.filename)
            if same is not None and same.file_size == len(data) and reference.read(same) == data:
                skipped += 1
                continue
            where = f"{os.path.basename(path)}!{entry.filename}"
            report(patterns, entry.filename, entry.filename.encode("utf-8"), out)
            for line, match in patterns.findings(text_of(data), entry.filename):
                out.append(f"{where}:{line}: {match}")
    return skipped


def scan_files(patterns: Patterns, targets: list[str], out: list[str]) -> int:
    """Scans each file named and every file under each folder named (allow globs see the path below that folder, with
    forward slashes); returns how many files were read. A target that doesn't exist is an error."""
    count = 0
    for target in targets:
        if os.path.isdir(target):
            found = []
            for root, _, names in os.walk(target):
                found.extend(os.path.join(root, name) for name in names)
            pairs = [(path, os.path.relpath(path, target).replace(os.sep, "/")) for path in sorted(found)]
        elif os.path.isfile(target):
            pairs = [(target, os.path.basename(target))]
        else:
            raise OSError(f"no file or folder at {target}")
        for path, where in pairs:
            with open(path, "rb") as handle:
                data = handle.read()
            count += 1
            for _, match in patterns.findings(where, where):
                out.append(f"{where} (its path): {match}")
            report(patterns, where, data, out)
    return count


def scan_history(patterns: Patterns, repo: str, out: list[str]) -> None:
    log = git(repo, "log", "--all", "--format=%H%x00%an <%ae>%x00%cn <%ce>%x00%ad%x00%cd%x00%B%x01", "--date=iso-strict")
    for record in filter(str.strip, log.split("\x01")):
        commit, author, committer, authored, committed, message = record.strip("\n").split("\x00", 5)
        where = f"commit {commit[:10]}"
        for label, value in (("author", author), ("committer", committer), ("message", message)):
            for line, match in patterns.findings(value, where):
                out.append(f"{where} {label}:{line}: {match}")
        for label, stamp in (("author date", authored), ("commit date", committed)):
            if not stamp.endswith(("+00:00", "Z")):
                out.append(f"{where} {label}: time zone is not UTC ({stamp})")
    objects = git(repo, "rev-list", "--objects", "--all").splitlines()
    seen: set[str] = set()
    for entry in objects:
        sha, _, name = entry.partition(" ")
        if not name or sha in seen:
            continue
        seen.add(sha)
        if git(repo, "cat-file", "-t", sha).strip() != "blob":
            continue
        report(patterns, name, name.encode("utf-8"), out)
        report(patterns, name, git(repo, "cat-file", "blob", sha, binary=True), out)


class SelfTest(unittest.TestCase):
    """The joined regex skips only lines that hold no match: the same findings as every line through every pattern."""

    PATTERNS = "\n".join([
        "# self-test patterns",
        r"block|(?<!\S)lonename(?!\S)",
        r"block|^linestart",
        r"block|lineend$",
        r"block|(?-i:ExactCase)",
        r"block|alpha|beta",
        r"block|7656119\d{10}",
        r"block|[A-Z]:\\Users\\[^\\\s]+",
        r"block|\wword\b",
        r"block|\sgap\s",
        r"block|kelvin",
        r"block|two words",
        r"block|item #\d",
        r"allow|7656119900000000\d|*.md",
    ])

    def load(self, text: str) -> Patterns:
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "patterns.txt")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(text)
            return Patterns(path)

    def assert_same(self, patterns: Patterns, text: str, where: str) -> list[tuple[int, str]]:
        joined = patterns.findings(text, where)
        self.assertEqual(joined, patterns.findings(text, where, joined=False))
        return joined

    def test_joined_regex_is_built(self) -> None:
        self.assertIsNotNone(self.load(self.PATTERNS).joined)

    def test_line_edges_anchors_and_case(self) -> None:
        patterns = self.load(self.PATTERNS)
        text = "\r\n".join([
            "lonename", "  lonename", "xlonename", "lonename\tafter", "linestart here", " linestart", "the lineend",
            "lineend no", "ExactCase", "exactcase", "ALPHA", "Beta", "id 76561199000000001", "id 76561197960265728",
            r"C:\Users\someone\Desktop", "nothing at all", "",
        ]) + "\vlonename\x85linestart\u2028lineend"
        found = self.assert_same(patterns, text, "notes.txt")
        self.assertEqual([n for n, _ in found], [1, 2, 4, 5, 7, 9, 11, 12, 13, 14, 15, 18, 19, 20])
        allowed = self.assert_same(patterns, text, "README.md")
        self.assertNotIn((13, "76561199000000001"), allowed)
        self.assertIn((14, "76561197960265728"), allowed)

    def test_unicode_classes_case_folding_spaces_and_hashes(self) -> None:
        """The joined regex keeps the patterns' Unicode classes and case folding, literal spaces and '#'."""
        patterns = self.load(self.PATTERNS)
        text = "\n".join([
            "\u00e9word", "\u0436word", "word\u00e9", "\u00e9word\u00e9", "\u3000gap\u3000", " gap ", "gap",
            "\u212aelvin", "KELVIN", "two words", "twowords", "item #7", "item 7", "",
        ])
        found = self.assert_same(patterns, text, "notes.txt")
        self.assertEqual([n for n, _ in found], [1, 2, 5, 6, 8, 9, 10, 12])

    def test_random_lines(self) -> None:
        patterns = self.load(self.PATTERNS)
        pieces = ["lonename", "linestart", "lineend", "ExactCase", "exactcase", "alpha", "BETA", "7656119", "0000000001",
                  "C:\\Users\\", "x", " ", "\t", "\\", "\r\n", "\n", "\r", "\x0c", "\u2028", "9", "word", "\u00e9",
                  "gap", "\u3000", "\u212aelvin", "two", "item #", "#"]
        chance = random.Random(1566200)
        lines = ["".join(chance.choice(pieces) for _ in range(chance.randint(0, 12))) for _ in range(4000)]
        found = self.assert_same(patterns, "\n".join(lines), "notes.txt")
        self.assertGreater(len(found), 100)

    def test_group_reference_or_whole_regex_flag_turns_joining_off(self) -> None:
        for extra in (r"block|(ab)\1", r"block|(?P<x>cd)(?P=x)", r"block|(e)?(?(1)f|g)", r"block|(?x) h i"):
            patterns = self.load(self.PATTERNS + "\n" + extra)
            self.assertIsNone(patterns.joined, extra)
        patterns = self.load(self.PATTERNS + "\n" + r"block|(ab)\1")
        self.assertEqual(patterns.findings("x abab", "notes.txt"), [(1, "abab")])

    def test_a_joined_regex_that_does_not_compile_turns_joining_off(self) -> None:
        patterns = self.load(self.PATTERNS + "\n" + r"block|(?P<n>j)" + "\n" + r"block|(?P<n>k)")
        self.assertIsNone(patterns.joined)
        self.assertEqual(patterns.findings("j k", "notes.txt"), [(1, "j"), (1, "k")])

    def test_a_patterns_file_without_a_block_pattern_is_refused(self) -> None:
        """A scan against an empty patterns file (or one with allow lines only) is an error, never a clean result."""
        with tempfile.TemporaryDirectory() as folder:
            target = os.path.join(folder, "notes.txt")
            with open(target, "w", encoding="utf-8") as handle:
                handle.write("lonename\n")
            for text in ("", "# nothing here\n", "allow|lonename|*.txt\n"):
                path = os.path.join(folder, "patterns.txt")
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(text)
                with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(main(["--patterns", path, "files", target]), 2, repr(text))
            with open(path, "w", encoding="utf-8") as handle:
                handle.write("block|lonename\n")
            with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(main(["--patterns", path, "files", target]), 1)

    def test_a_pattern_nested_to_the_parsers_limit_turns_joining_off(self) -> None:
        def nested(depth: int) -> str:
            return "(?:" * depth + "a" + ")" * depth

        bound = sys.getrecursionlimit()  # the parser recurses at least once a level
        low, high = 1, bound
        while low < high:  # the deepest nesting that compiles here; joining wraps it in one more group
            middle = (low + high + 1) // 2
            try:
                re.compile(nested(middle), re.IGNORECASE)
                low = middle
            except (re.error, RecursionError):
                high = middle - 1
        if low == bound:
            self.skipTest("this Python's regex parser has no nesting limit below the recursion limit")
        self.assertIsNone(joined_block([re.compile(nested(low), re.IGNORECASE)]))


def selftest() -> int:
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(SelfTest)
    stream = io.StringIO()
    result = unittest.TextTestRunner(stream=stream, verbosity=0).run(suite)
    print(f"forbidden_strings selftest: {result.testsRun} tests, {len(result.failures) + len(result.errors)} failed")
    if not result.wasSuccessful():
        print(stream.getvalue())
        return 1
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--patterns", help="the patterns file (keep it outside the repository); every mode but selftest")
    parser.add_argument("mode", choices=["tree", "staged", "message", "zip", "history", "files", "selftest"])
    parser.add_argument("--skip-identical-to", metavar="REF.zip",
                        help="zip mode: leave out entries whose bytes equal the same path's in this zip")
    parser.add_argument("targets", nargs="*")
    args = parser.parse_args(argv)
    if args.mode == "selftest":
        return selftest()
    if not args.patterns:
        parser.error("--patterns is required")
    if args.skip_identical_to and args.mode != "zip":
        parser.error("--skip-identical-to applies to zip mode only")
    try:
        patterns = Patterns(args.patterns)
        if not patterns.block:
            # An empty file, or the wrong one: every scan would pass without having looked for anything.
            print(f"forbidden_strings: {os.path.basename(args.patterns)} holds no block pattern, so a scan would prove nothing",
                  file=sys.stderr)
            return 2
        out: list[str] = []
        if args.mode in ("tree", "staged", "history"):
            repo = args.targets[0] if args.targets else "."
            {"tree": scan_tree, "staged": scan_staged, "history": scan_history}[args.mode](patterns, repo, out)
        elif args.mode == "files":
            if not args.targets:
                parser.error("files mode needs at least one file or folder")
            read = scan_files(patterns, args.targets, out)
            print(f"forbidden_strings: {read} file(s) read", file=sys.stderr)
        elif args.mode == "message":
            for path in args.targets:
                with open(path, "rb") as handle:
                    report(patterns, "commit message", handle.read(), out)
        else:
            reference = zipfile.ZipFile(args.skip_identical_to) if args.skip_identical_to else None
            try:
                for path in args.targets:
                    skipped = scan_zip(patterns, path, out, reference)
                    if skipped:
                        print(f"forbidden_strings: {os.path.basename(path)}: {skipped} entries identical to "
                              f"{os.path.basename(args.skip_identical_to)} left out", file=sys.stderr)
            finally:
                if reference is not None:
                    reference.close()
    except (OSError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        print(f"forbidden_strings: {error}", file=sys.stderr)
        return 2
    for line in sorted(set(out)):
        print(line)
    print(f"forbidden_strings: {len(set(out))} match(es)", file=sys.stderr)
    return 1 if out else 0


if __name__ == "__main__":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
    sys.exit(main(sys.argv[1:]))
