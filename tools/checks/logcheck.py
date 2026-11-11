#!/usr/bin/env python3
"""Log content check: no log call writes personal data (D127).

Finds every call that writes a log line in src/ (ModLog.Info/Warn/Error/Trace/SteamInfo/SteamWarn, EarlyLog.Info/Warn/Error
(the housekeeping blocks' log, also on a mixed install), the Steam library's
_info/_warn/_log callbacks, the Log/Note/NoteOnce helpers that forward to ModLog, the files' own once-only wrappers
Once/ReportOnce (a key and a line), the shared-progress apply log's
log.Part/log.Note, the notices' Notify/_notify (a notice is logged as its words say it; one naming a player goes through
NotifyAbout or NotifyText, whose log copy names them by tag), and MelonLoader's own loggers, a logger held in a local
named log included: the mixed install's lines, which no scrubber cleans) and reads its code: the
holes of interpolated strings and everything outside string literals. A call fails when that code can write one of:

    name        a Steam name: NameOf(, DisplayNameOf(, PersonaName, _hostName, hostName, HostName, JoiningName,
                FriendName, {name}, {who}, {host}, {_host}, {_joinName}, {_failureHost}, {pad.Name}, .PersonaName
    steamid     a SteamID64 as a number: {_self}, {steamId}, {friend}, {owner}, {member}, {peer}, {sender}, ...
    lobby       a lobby ID: {lobby}, {_lobby}, {lobbyId}, LobbyId}, {_link.Id}, {Id}, {_rejoinLobby}, {_target}, ...
    path        a folder path: FilePath, GameRootDirectory, UserDataDirectory, persistentDataPath, SteamRuntime.Status,
                DataFolder, ModFolder, {path}, {full}, {folder}, {directory}, .FullName}, .Path} (a file's Path property)
    exception   a raw exception message: ex.Message and the like (IO messages hold full paths), or, in any call but
                ModLog.Error (which writes an exception's stack after its line on purpose), the whole exception written
                into the line ({ex}, {exception}, {inner}, inside a catch block the name its catch gives it ({e}),
                .ToString() of one, .StackTrace): use ModLog.SafeErrorText(ex)
    objectname  any Unity object's or Rewired's name (a lowercase .name): a skater's object is named after the skater, so
                each such call is vetted: the allow file holds the ones that name a pad, an action, a scene or a UI part
    playertext  player-written text: team, skater, goalie and logo names (teamName, firstName, lastName, logoName, a
                run's best scorer, a team's city or nickname, ...), the Unity object name of a skater, forward,
                goalie, team or logo (skater.name, skaterRoot.name, _forwardData.name, team.logo.name, victim.name: the
                game names a skater's object after the skater, and a custom skater's, team's or logo's names are typed by
                its player), and a skater's FullName (forward.FullName: its first and last names). That name is also
                found through the file's own string helpers that return it (a call of SafeName(forward) whose body reads
                forward.name), and through a local of the calling method that is assigned or added to from it (string
                said = ...; said.Add(...); then {said} in the call, or said passed on: said.Take(5).Select(...),
                line.ToString())
    localtime   a local time or time zone: DateTime.Now, ToLocalTime, TimeZoneInfo, UtcOffset
    salt        the log tag key: _logKey, LogTags.ToHex(, salt, the t2t.logtag lobby data value
    culture     a number or a date written in the PC's culture. The game's thread runs in the player's, so "1.5 s" reads "1,5 s"
                on a PC that writes a comma, and logs from two PCs no longer read alike. Found: a hole with a decimal, group
                or date format (:0.0, :0.##, :F1, :N0, :yyyy-MM-dd HH:mm), a .ToString("0.0") or a string.Format("{0:0.0}")
                with no provider, anywhere in src (a text built to be logged is often made by a helper first); and, in a log
                call, a hole with no format that is a float, double, decimal or a TimeSpan's Total... (known by a cast, a
                literal, Total..., Math.Round, or the name's declaration as a float in the same file, or in src when every
                declaration of that name is a float). Safe: the string is the argument after a provider that is not the
                current culture (string.Create(CultureInfo.InvariantCulture, $"..."), text.Append(CultureInfo.InvariantCulture,
                $"...")) or inside FormattableString.Invariant(...). A whole-number format ({x:0}) carries no separator.
                Never change the thread's culture to fix one: the game's own screens use it.
    jsonname    a JSON member named like a name (firstName, lastName, name, displayName, teamName, city, nickname, logoName,
                squadName, playerName) read anywhere in src: TryGetProperty("firstName"), GetProperty("teamName"), or a
                helper's text argument after the element it reads (Text(seat, "firstName")). A run file's or a save's JSON
                holds custom content's names, typed by a player, and on a friend a run file is the host's bytes: a value read
                there ends up in a line through a record's ToString, which no log call shows. Read by id and number instead;
                a reader of the mod's own JSON (a backup manifest's file name) is allowed with its reason.

The vetted helpers are left out of the reading: Tag(, .Player(, .Lobby(, HostTag(, SafeError(/SafeErrorText(,
ErrorText(, NameShape.Describe(, TextShape( (player-written text as its length and a keyed hash), DevOutput.Label( (a
developer file as its place under UserData/T2TOnline), LogNames.ObjectName( (a skater's object by its asset id, any other
by its name) and LogNames.ReferenceText( (a text with every skater's name met so far as its shape). ModLog.Error(message,
ex) is fine: the backstop cleans the whole exception.

What it can't see: a call of a log helper not named above (a file's own wrapper by another name), a lambda's
parameter whose name doesn't say it is a skater (f => f.name), a value passed through more than one local or from
another method. The other rules read the log call itself only: in a helper's arguments or a local's sources the same
names (name, target, from, Message) mean other things too often to be read without false alarms. The culture rule can't
see a float written by + or string.Join, or one with no format in a string that is built outside a log call (only a
format gives it away there), or a value whose type its name doesn't tell.

The allow file (tools/checks/logcheck_allow.txt) lists the calls that may still match, per file and rule, each with a
reason: "<path> | <rule> | <count> | <reason>". A file and rule with more matches than allowed fails; with fewer, the
entry is stale and fails too (lower it, or remove it), so the file only ever shrinks. Every reason says why those
lines can't write what the check looks for. A source tree in which no log call is found is an error, never a pass.

Usage:
    logcheck.py [REPO]              check (exit 0: clean; 1: findings; 2: bad usage, an unreadable input or no call found)
    logcheck.py [REPO] --list RULE  every matching call of RULE, allowed or not
    logcheck.py [REPO] --suggest    allow-file lines for today's matches (reason TODO), to start an allow file
    logcheck.py --selftest          the checker's own tests
"""

from __future__ import annotations

import argparse
import bisect
import contextlib
import functools
import io
import os
import re
import sys
import tempfile
import unittest
from collections import defaultdict

ALLOW_FILE = os.path.join("tools", "checks", "logcheck_allow.txt")

CALL = re.compile(
    r"(?:(?<![\w.])(?:ModLog\.(?:Info|Warn|Error|Trace|SteamInfo|SteamWarn)|EarlyLog\.(?:Info|Warn|Error)|_info|_warn|_log|Log|Note"
    r"|NoteOnce|Once|ReportOnce|MelonLogger\.(?:Msg|Warning|Error)|LoggerInstance\.(?:Msg|Warning|Error)|melon\.(?:Msg|Warning|Error)"
    r"|_melon\?\.(?:Msg|Warning|Error)|Notify|_notify)|\b\w+\??\.Notify|\blog\.(?:Part|Note|Msg|Warning|Error))\s*\(")

DEFINITION = re.compile(r"\b(?:void|string|static|private|public|internal|protected|override|delegate)\s+$")

ID_NAMES = (r"_self|steamId|SteamId|friend|owner|member|peer|sender|hostId|HostId|from|target|joiner|leaver|user"
            r"|_helloHost|FriendSteamId|f\.FriendSteamId|player\.SteamId|e\.SteamId|seat\.SteamId")

# The Unity object name of a skater, forward, goalie, team or logo, known by what it is read from (skater, skaterRoot,
# forward, _forwardData, goalie, team, logo, victim, skaters[i]): player-written for custom content; and a skater's
# FullName (the game's SkaterData.FullName: its first and last names). A pad's, an action's, a scene's or a menu part's
# name is the device's or the game's, and is not matched.
OBJECT_NAME = (r"\b(?:\w*(?:[Ss]kater|[Ff]orward|[Gg]oalie|[Gg]oaltender|(?<![Ss])[Tt]eam|[Ll]ogo)\w*|victim)"
               r"(?:\[[^\]]*\])?\??\.(?:name|FullName)\b")
OBJECT_NAME_RX = re.compile(OBJECT_NAME)

# What a log call's code gets in place of a call of a name helper, or beside it for a local holding such a name: the
# playertext rule matches it (NAME_SIGN too), so the call is counted like one that reads the name itself.
THROUGH = " SKATER_NAME_THROUGH "
NAME_SIGN = re.compile(OBJECT_NAME + r"|\bSKATER_NAME_THROUGH\b")

RULES = {
    "name": re.compile(
        r"\bNameOf\(|\bDisplayNameOf\(|PersonaName|\b_hostName\b|\bhostName\b|\bHostName\b|\bJoiningName\b|\bFriendName\b"
        r"|\{name\}|\{who\}|\{host\}|\{_host\}|\{_joinName\}|\{_failureHost\}|\.PersonaName\b"
        r"|\{(?:pad|remote|friend|seat|peer|player)\.(?:Display)?Name\}"),
    "steamid": re.compile(r"\{(?:" + ID_NAMES + r")(?:[,:][^}]*)?\}"),
    "lobby": re.compile(
        r"\{(?:lobby|_lobby|lobbyId|LobbyId|_link\.Id|Id|_rejoinLobby|_target|tracked\.Lobby|group\.Key|\w+\.LobbyId"
        r"|\w+\.LobbySteamId|LobbySteamId)\}"),
    "path": re.compile(
        r"\bFilePath\b|GameRootDirectory|UserDataDirectory|persistentDataPath|SteamRuntime\.Status|\bDataFolder\b"
        r"|\bModFolder\b|\b_logFolder\b|\{path\}|\{full\}|\{folder\}|\{directory\}|\.FullName\}|\.Path\}"),
    "exception": re.compile(r"\b\w+\.Message\b(?!s)|\.Message\}"),
    "playertext": re.compile(r"\b(?:teamName|firstName|lastName|logoName|customName|TeamName|FirstName|LastName|LogoName|CustomName)\b"
                             r"|\b\w*Scorer\b|\.(?:city|nickname)\b|\bSKATER_NAME_THROUGH\b|" + OBJECT_NAME),
    "localtime": re.compile(r"DateTime\.Now\b|ToLocalTime\(|TimeZoneInfo\b|UtcOffset"),
    "objectname": re.compile(r"\b\w+(?:\[[^\]]*\])?\??\.name\b"),
    "salt": re.compile(r"\b_?logKey\b|LogTags\.ToHex\(|(?i:salt)|GetLobbyData\([^)]*LogTag"),
}

# The rules that read more than a log call's own code: culture_hits reads every string of src, jsonname_hits every JSON
# member a name could be read from.
RULE_NAMES = set(RULES) | {"culture", "jsonname"}

# The jsonname rule: the members of the game's JSON (a run file, a save) whose text a player can type, read by name: as the
# argument of TryGetProperty or GetProperty, or as a helper's second argument after the element (Text(seat, "firstName")).
JSON_NAMES = frozenset({"firstName", "lastName", "name", "displayName", "teamName", "city", "nickname", "logoName", "squadName",
                        "playerName"})
JSON_READ_BEFORE = re.compile(r"(?:\bTryGetProperty|\bGetProperty)\s*\(\s*$|\b\w+\s*\(\s*[\w.?\[\]]+\s*,\s*$")

# The whole exception written into a line (its message and its stack), counted under "exception" in any call but an
# error's: ModLog.Error writes an exception's stack after its line on purpose, through the same scrubber, whether it is
# given as ModLog.Error(message, ex) or written into the message. The exception is known by these names (a helper's
# parameter, a property) and, inside a catch block, by whatever name every catch around the call gives it.
EXCEPTION_NAMES = ("ex", "exception", "inner", "innerException")
ERROR_CALLS = ("ModLog.Error(",)

# A catch clause that names its exception: catch (Type name), the type plain, qualified or generic.
CATCH = re.compile(r"\bcatch\s*\(\s*[A-Za-z_][\w.]*(?:<[^()]*>)?\s+([A-Za-z_]\w*)\s*\)")
CATCH_FILTER = re.compile(r"\s*when\s*\(")


@functools.lru_cache(maxsize=None)
def whole_exception(caught: frozenset[str] = frozenset()) -> re.Pattern:
    """The whole-exception pattern for a log call inside catch blocks naming their exceptions caught."""
    names = "|".join(sorted(set(EXCEPTION_NAMES) | caught))
    return re.compile(r"\{(?:" + names + r")(?:\.ToString\(\))?(?:,[^}]*)?\}|\.StackTrace\b")


def catch_blocks(src: str) -> list[tuple[int, int, str]]:
    """Every catch clause of src that names its exception, outside comments and strings: (the index of its block's opening
    brace, of its closing one, the name)."""
    found: list[tuple[int, int, str]] = []
    for match in CATCH.finditer(src):
        if in_comment_or_string(src, src.rfind("\n", 0, match.start()) + 1, match.start()):
            continue
        i = match.end()
        when = CATCH_FILTER.match(src, i)
        if when:
            i = matching(src, when.end() - 1, "(", ")") + 1
        while i < len(src) and src[i].isspace():
            i += 1
        if i < len(src) and src[i] == "{":
            found.append((i, matching(src, i, "{", "}"), match.group(1)))
    return found


def caught_at(blocks: list[tuple[int, int, str]], at: int) -> frozenset[str]:
    """The names the catch blocks around index at give their exceptions."""
    return frozenset(name for start, end, name in blocks if start < at < end)

VETTED = re.compile(r"(?:\bTag|\.Player|\.Lobby|\bHostTag|\bSafeError(?:Text)?(?:\.Text)?|\bErrorText|\bNameShape\.Describe|\bTextShape|\bDevOutput\.Label|\bLogNames\.ObjectName|\bLogNames\.ReferenceText)\s*\(")


def skip_string(src: str, i: int, holes: list[str] | None) -> int:
    """From a string literal's first prefix character at i, returns the index after it; the holes of an interpolated
    string are added to holes (their code, read recursively)."""
    j = i
    interpolated = verbatim = False
    while src[j] in "$@":
        interpolated |= src[j] == "$"
        verbatim |= src[j] == "@"
        j += 1
    j += 1  # the opening quote
    n = len(src)
    while j < n:
        c = src[j]
        if verbatim and c == '"' and j + 1 < n and src[j + 1] == '"':
            j += 2
            continue
        if not verbatim and c == "\\":
            j += 2
            continue
        if c == '"':
            return j + 1
        if interpolated and c == "{":
            if j + 1 < n and src[j + 1] == "{":
                j += 2
                continue
            end = matching(src, j, "{", "}")
            if holes is not None:
                holes.append("{" + code_of(src[j + 1:end]) + "}")
            j = end + 1
            continue
        j += 1
    return n


def matching(src: str, start: int, opening: str, closing: str) -> int:
    """The index of the bracket closing the one at start, skipping strings and chars."""
    depth = 0
    i = start
    n = len(src)
    while i < n:
        c = src[i]
        if c == '"' or (c in "$@" and i + 1 < n and src[i + 1] in '"$@'):
            i = skip_string(src, i, None)
            continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == "\\" else 1
            i = j + 1
            continue
        if c == opening:
            depth += 1
        elif c == closing:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return n - 1


def code_of(text: str) -> str:
    """The code of an expression: string literals' text left out, interpolation holes kept as {hole}."""
    out: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"' or (c in "$@" and i + 1 < n and text[i + 1] in '"$@'):
            holes: list[str] = []
            i = skip_string(text, i, holes)
            out.append(" " + " ".join(holes) + " ")
            continue
        if c == "'":
            j = i + 1
            while j < n and text[j] != "'":
                j += 2 if text[j] == "\\" else 1
            i = j + 1
            out.append(" ")
            continue
        out.append(c)
        i += 1
    return "".join(out)


def strip_vetted(code: str) -> str:
    """The code without the vetted helpers' calls (their arguments are what they make safe)."""
    while True:
        match = VETTED.search(code)
        if not match:
            return code
        end = matching(code, match.end() - 1, "(", ")")
        code = code[:match.start()] + " SAFE " + code[end + 1:]


def without_comments(code: str) -> str:
    """Code (string literals already left out by code_of) without its // and /* */ comments."""
    return re.sub(r"/\*.*?\*/|//[^\n]*", " ", code, flags=re.S)


def statement_end(src: str, i: int) -> int:
    """The index of the ; that ends the statement or expression starting at i (brackets, strings and chars skipped)."""
    depth = 0
    n = len(src)
    while i < n:
        c = src[i]
        if c == '"' or (c in "$@" and i + 1 < n and src[i + 1] in '"$@'):
            i = skip_string(src, i, None)
            continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == "\\" else 1
            i = j + 1
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
            if depth < 0:
                return i
        elif c == ";" and depth == 0:
            return i
        i += 1
    return n


STRING_METHOD = re.compile(
    r"^[ \t]*(?:(?:public|private|internal|protected|static|override|virtual|new|sealed|unsafe)\s+)*string\??\s+(\w+)\s*\(", re.M)
MEMBER_LINE = re.compile(r"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(?:public|private|internal|protected)\b", re.M)
# A local the log call prints: not called, not read through (said.Count, said[0]) and not only compared (logo != other,
# said is null): a value used that way writes a number or a choice, not the text. A call that passes its elements or its
# text on is a print (said.Take(5).Select(...), line.ToString()).
LOCAL_WORD = re.compile(
    r"(?<![\w.])(?<![=!<>]=)(?<![=!<>]=\s)([a-z_]\w*)\b"
    r"(?!\s*(?:[(<\[]|\.(?!\s*(?:Take|Skip|TakeLast|SkipLast|Select|Where|Concat|Distinct|Reverse|OrderBy|OrderByDescending"
    r"|ToArray|ToList|ToString)\s*\()|\?\.|[=!<>]=|>|\bis\b))")


def body_of(src: str, after_parameters: int) -> str:
    """The body of the method whose parameter list closes just before after_parameters: an expression body up to its ;,
    a block up to its }, or "" (an abstract or extern method)."""
    i = after_parameters
    n = len(src)
    while i < n and src[i] not in "{;=":
        i += 1  # whitespace and generic constraints (where T : ...)
    if src.startswith("=>", i):
        return src[i + 2:statement_end(src, i + 2)]
    if i < n and src[i] == "{":
        return src[i + 1:matching(src, i, "{", "}")]
    return ""


def name_helpers(src: str) -> re.Pattern | None:
    """A call of the file's own string methods that return an object name OBJECT_NAME matches (directly, or through
    another of them), or None when it has none."""
    bodies = []
    for match in STRING_METHOD.finditer(src):
        close = matching(src, match.end() - 1, "(", ")")
        bodies.append((match.group(1), strip_vetted(without_comments(code_of(body_of(src, close + 1))))))
    helpers: set[str] = set()
    while True:
        rx = re.compile(r"(?<![\w.])(?:" + "|".join(sorted(helpers)) + r")\s*\(") if helpers else None
        added = {name for name, body in bodies
                 if name not in helpers and (OBJECT_NAME_RX.search(body) or (rx is not None and rx.search(body)))}
        if not added:
            return rx
        helpers |= added


def through_names(code: str, helpers: re.Pattern | None) -> str:
    """The code with each call of a name helper standing as THROUGH."""
    return helpers.sub(THROUGH + "(", code) if helpers is not None else code


def local_holds_name(scope: str, local: str, helpers: re.Pattern | None) -> bool:
    """True when the method's code before the log call (scope) assigns or adds to local from an object name OBJECT_NAME
    matches, or from a call of a name helper: local = ...; local += ...; local.Add(...), .Append(...), .Insert(...)."""
    word = r"(?<![\w.])" + re.escape(local) + r"\s*"
    sources = [scope[m.end():statement_end(scope, m.end())] for m in re.finditer(word + r"\+?=(?![=>])", scope)]
    sources += [scope[m.end():matching(scope, m.end() - 1, "(", ")")]
                for m in re.finditer(word + r"\.\s*(?:Add|AddRange|Append|AppendLine|Insert)\s*\(", scope)]
    return any(NAME_SIGN.search(through_names(strip_vetted(without_comments(code_of(s))), helpers)) for s in sources)


def log_call_ranges(src: str):
    """Every log call in one file's text: (the regex match of its name, the index of its opening bracket, the index of the
    closing one). A definition, or a mention in a comment or a string, is not a call."""
    for match in CALL.finditer(src):
        line_start = src.rfind("\n", 0, match.start()) + 1
        prefix = src[line_start:match.start()]
        if DEFINITION.search(prefix) or prefix.lstrip().startswith(("//", "///", "*")):
            continue
        if in_comment_or_string(src, line_start, match.start()):
            continue
        open_at = match.end() - 1
        yield match, open_at, matching(src, open_at, "(", ")")


def calls(repo: str):
    """Every log call in src/: (path, line, code, text, the names the catch blocks around it give their exceptions). A call
    of a name helper in the code stands as THROUGH, and THROUGH is added for a local of the calling method that holds such
    a name (see the playertext rule)."""
    for root, dirs, files in os.walk(os.path.join(repo, "src")):
        dirs[:] = sorted(d for d in dirs if d not in ("obj", "bin"))
        for name in sorted(files):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(root, name)
            with open(path, encoding="utf-8-sig") as handle:
                src = handle.read()
            rel = os.path.relpath(path, repo).replace(os.sep, "/")
            # A first look at the raw text: most files read no such name at all, and skip the closer reading.
            names_here = OBJECT_NAME_RX.search(src) is not None
            helpers = name_helpers(src) if names_here else None
            members = [m.start() for m in MEMBER_LINE.finditer(src)] if names_here else []
            blocks = catch_blocks(src)
            for match, open_at, end in log_call_ranges(src):
                code = through_names(strip_vetted(code_of(src[open_at + 1:end])), helpers)
                if names_here and not NAME_SIGN.search(code):
                    # The calling method's code before the call: from the last member's first line (a line that starts
                    # with an access modifier; a method's own lines never do).
                    member = bisect.bisect_left(members, match.start()) - 1
                    scope = src[members[member] if member >= 0 else 0:match.start()]
                    if any(local_holds_name(scope, local, helpers) for local in set(LOCAL_WORD.findall(code))):
                        code += THROUGH
                yield (rel, src.count("\n", 0, match.start()) + 1, code, " ".join(src[match.start():end + 1].split()),
                       caught_at(blocks, match.start()))


def in_comment_or_string(src: str, line_start: int, at: int) -> bool:
    """True when at is inside a // comment or a string on its line (a rough look: enough for call names)."""
    segment = src[line_start:at]
    if "//" in code_of(segment):
        return True
    return segment.count('"') % 2 == 1


# The culture rule: a number in a log line reads the same on every PC (the game's thread runs in the player's culture, so
# "1.5" is "1,5" on a PC that writes a comma). Two readings, both of interpolated strings (nested ones too), `.ToString("...")`
# and `string.Format("...")` of the whole of src:
#   - a hole whose format writes a decimal point, a group or a date separator in the PC's culture (0.0, 0.##, #,0, F1, N0, P,
#     E3, yyyy-MM-dd HH:mm), wherever the string is (a text built to be logged is often returned by a helper first);
#   - in a log call only, a hole with no format whose value is a float, a double, a decimal or a TimeSpan's Total...: known by
#     what it is (a cast, a literal, Total..., Math.Round) or by the name it was declared with as float, double or decimal
#     in the same file (or, for a name the file doesn't declare, in src, when every declaration of the name there is one).
# A string is safe when it is the argument after CultureInfo.InvariantCulture, or after any provider that is not the current
# culture, in string.Create and StringBuilder.Append (a local holding the invariant culture, as the file's own style does),
# or inside FormattableString.Invariant(...); a ToString("...") or string.Format("...") is safe with a provider. The numbers a whole
# integer format writes ({x:0}) or the default of an integer carry no separator, so they are not read.
INVARIANT_BEFORE = re.compile(
    r"\b(?:InvariantCulture|InvariantInfo)\s*,\s*$|\bFormattableString\s*\.\s*Invariant\s*\(\s*$"
    r"|\b(?:string\s*\.\s*Create|Append|AppendLine)\s*\(\s*(?![^,()]*Current)[^(),]+,\s*$")
FLOATING = re.compile(
    r"\.Total(?:Seconds|Milliseconds|Minutes|Hours|Days|Microseconds|Nanoseconds)\b|\(\s*(?:float|double|decimal)\s*\)"
    r"|(?<![\w.])\d[\d_]*\.\d[\d_]*(?:[fFdDmM])?\b|(?<![\w.])\d[\d_]*[fFdDmM]\b"
    r"|\bMath\.(?:Round|Floor|Ceiling|Truncate|Sqrt|Pow|Log\w*|Exp|Sin|Cos|Tan|Atan2?)\s*\(|\bMathf\.(?!\w*ToInt)\w+\s*\(")
WHOLE_RESULT = re.compile(r"^\s*(?:\(\s*(?:int|uint|long|ulong|short|ushort|byte|sbyte)\s*\)|Mathf\.\w+ToInt\s*\()")
UNDECIDED = re.compile(r"==|!=|[<>]|&&|\|\||\bis\b|(?<!\?)\?(?![?.\[])")
PASS_THROUGH = re.compile(r"\b(?:Math|Mathf)\.(?:Max|Min|Abs|Clamp|Sign)\s*\($")
FLOAT_DECL = re.compile(r"\b(?:float|double|decimal)\??\s+(\w+)\s*(?=[=;,)({:]|=>|\bin\b)")
OTHER_DECL = re.compile(
    r"\b(?:int|uint|long|ulong|short|ushort|byte|sbyte|string|bool|char|object|[A-Z][\w.<>]*)(?:\?|\[\])?\s+(\w+)\s*(?=[=;,)({:]|=>|\bin\b)")
VAR_DECL = re.compile(r"\bvar\s+(\w+)\s*=\s*([^;]+);")
TO_STRING = re.compile(r"\.ToString\(\s*\"([^\"\\\n]*)\"\s*\)")
STRING_FORMAT = re.compile(r"\bstring\.Format\(\s*\"((?:[^\"\\\n]|\\.)*)\"")
FORMAT_SPEC = re.compile(r"\{\d+(?:,-?\d+)?:([^}]*)\}")


def string_literals(src: str) -> list[tuple[int, int, bool, list[tuple[int, int]]]]:
    """Every string literal of src, nested ones included, outside comments: (start, end, interpolated, its holes' code as
    (start, end) of the text between the braces)."""
    out: list[tuple[int, int, bool, list[tuple[int, int]]]] = []
    scan_code(src, 0, len(src), out)
    return out


def scan_code(src: str, i: int, end: int, out: list) -> None:
    """Reads the code of src[i:end]: its string literals go to out; comments and char literals are skipped."""
    while i < end:
        c = src[i]
        if c == "/" and src.startswith("//", i):
            j = src.find("\n", i)
            i = end if j < 0 else j
        elif c == "/" and src.startswith("/*", i):
            j = src.find("*/", i + 2)
            i = end if j < 0 else j + 2
        elif c == '"' or (c in "$@" and i + 1 < len(src) and src[i + 1] in '$@"'):
            i = scan_string(src, i, out)
        elif c == "'":
            j = i + 1
            while j < len(src) and src[j] != "'":
                j += 2 if src[j] == "\\" else 1
            i = j + 1
        else:
            i += 1


def scan_string(src: str, i: int, out: list) -> int:
    """From a literal's first prefix character at i: records it (and the literals in its holes) in out; returns the index
    after it."""
    j = i
    interpolated = verbatim = False
    while src[j] in "$@":
        interpolated |= src[j] == "$"
        verbatim |= src[j] == "@"
        j += 1
    j += 1
    n = len(src)
    holes: list[tuple[int, int]] = []
    nested: list = []
    while j < n:
        c = src[j]
        if verbatim and c == '"' and j + 1 < n and src[j + 1] == '"':
            j += 2
        elif not verbatim and c == "\\":
            j += 2
        elif c == '"':
            out.append((i, j + 1, interpolated, holes))
            out.extend(nested)
            return j + 1
        elif interpolated and c == "{":
            if j + 1 < n and src[j + 1] == "{":
                j += 2
                continue
            close = matching(src, j, "{", "}")
            holes.append((j + 1, close))
            scan_code(src, j + 1, close, nested)
            j = close + 1
        else:
            j += 1
    return n


def split_hole(text: str) -> tuple[str, str]:
    """A hole's code and its format: {code,alignment:format}."""
    depth = 0
    code_end = None
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"' or (c in "$@" and i + 1 < n and text[i + 1] in '$@"'):
            i = skip_string(text, i, None)
            continue
        if c == "'":
            j = i + 1
            while j < n and text[j] != "'":
                j += 2 if text[j] == "\\" else 1
            i = j + 1
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif depth == 0 and c == "," and code_end is None:
            code_end = i
        elif depth == 0 and c == ":":
            return text[:i if code_end is None else code_end], text[i + 1:]
        i += 1
    return (text if code_end is None else text[:code_end]), ""


def culture_format(spec: str) -> bool:
    """True when a number or a date written with this format changes with the PC's culture: a decimal point or a group
    (0.0, #,0), a standard fixed, number, percent, exponent, currency, general or round-trip format (F1, N0, P, E3), a date
    or time with separators (yyyy-MM-dd HH:mm). A whole-number format (0, 00, D2, x16) and the default carry none."""
    spec = spec.strip()
    if not spec:
        return False
    standard = re.fullmatch(r"([A-Za-z])(\d{0,2})", spec)
    if standard:
        kind = standard.group(1).upper()
        if kind in "FN":
            return kind == "N" or standard.group(2) != "0"
        return kind in "PECGR"
    if re.search(r"yy|MM|dd|HH|hh|mm|ss", spec) and re.search(r"[:/]", spec):
        return True
    return re.search(r"[.,]", spec) is not None


def bare_code(text: str) -> str:
    """The code of an expression with its string literals left out whole (code_of keeps an interpolated string's holes)."""
    out: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"' or (c in "$@" and i + 1 < n and text[i + 1] in '$@"'):
            i = skip_string(text, i, None)
            out.append(" ")
        elif c == "'":
            j = i + 1
            while j < n and text[j] != "'":
                j += 2 if text[j] == "\\" else 1
            i = j + 1
            out.append(" ")
        else:
            out.append(c)
            i += 1
    return "".join(out)


def declared_names(src: str) -> tuple[set[str], set[str]]:
    """The names src declares as float, double or decimal, and the names it declares as anything else (a `var` by what it
    is assigned)."""
    floats = {m.group(1) for m in FLOAT_DECL.finditer(src)}
    others = {m.group(1) for m in OTHER_DECL.finditer(src)}
    for m in VAR_DECL.finditer(src):
        (floats if floating_value(m.group(2), set(), set()) else others).add(m.group(1))
    return floats, others


def floating_value(code: str, floats: set[str], others: set[str], everywhere: tuple[set[str], set[str]] = (set(), set())) -> bool:
    """True when the expression's value is evidently a float, a double or a decimal (a hole's code, strings left out)."""
    code = without_comments(bare_code(code))
    if WHOLE_RESULT.match(code) or UNDECIDED.search(code):
        return False
    if FLOATING.search(code):
        return True
    # A call's arguments say nothing about its result, except for the math functions that return what they are given.
    kept: list[str] = []
    called: list[str] = []
    i = 0
    while i < len(code):
        match = re.compile(r"[A-Za-z_]\w*\s*\(").search(code, i)
        if match is None:
            kept.append(code[i:])
            break
        kept.append(code[i:match.start()])
        open_at = match.end() - 1
        if PASS_THROUGH.search(code[:open_at + 1]):
            kept.append(code[match.start():open_at + 1])
            i = open_at + 1
            continue
        called.append(match.group(0)[:-1].strip())
        i = matching(code, open_at, "(", ")") + 1
        kept.append(" ")
    # A method's name counts only where the file declares it as a method returning a float: another file's Sum or Max may
    # be anything.
    if any(name in floats and name not in others for name in called):
        return True
    for name in re.findall(r"(?<![\w])[A-Za-z_]\w*", "".join(kept)):
        if name in floats and name not in others:
            return True
        if name not in floats and name not in others and name in everywhere[0] and name not in everywhere[1]:
            return True
    return False


def culture_hits(repo: str):
    """Every place src writes a number or a date in the PC's culture (see the culture rule): (path, line, text)."""
    files = []
    for root, dirs, names in os.walk(os.path.join(repo, "src")):
        dirs[:] = sorted(d for d in dirs if d not in ("obj", "bin"))
        for name in sorted(names):
            if name.endswith(".cs"):
                path = os.path.join(root, name)
                with open(path, encoding="utf-8-sig") as handle:
                    src = handle.read()
                files.append((os.path.relpath(path, repo).replace(os.sep, "/"), src))
    declared = {rel: declared_names(src) for rel, src in files}
    everywhere = (set().union(*(d[0] for d in declared.values())), set().union(*(d[1] for d in declared.values())))
    for rel, src in files:
        floats, others = declared[rel]
        literals = string_literals(src)
        ends = {end: start for start, end, _, _ in literals}
        logged = [(open_at, end) for _, open_at, end in log_call_ranges(src)]
        hits: dict[int, str] = {}

        def add(at: int, text: str) -> None:
            hits.setdefault(at, " ".join(text.split()))

        for start, end, interpolated, holes in literals:
            if not interpolated:
                continue
            at = start
            while True:  # strings added together make one argument: the provider stands before the first of them
                before = src[:at].rstrip()
                if before.endswith("+") and len(before[:-1].rstrip()) in ends:
                    at = ends[len(before[:-1].rstrip())]
                    continue
                break
            if INVARIANT_BEFORE.search(src[:at].rstrip()):
                continue
            in_log = any(open_at < start < close for open_at, close in logged)
            for hole_start, hole_end in holes:
                code, spec = split_hole(src[hole_start:hole_end])
                if culture_format(spec) or (in_log and not spec.strip() and floating_value(code, floats, others, everywhere)):
                    add(start, src[start:end])
                    break
        for match in TO_STRING.finditer(src):
            if culture_format(match.group(1)) and not src[src.rfind("\n", 0, match.start()) + 1:match.start()].lstrip().startswith("//"):
                add(match.start(), match.group(0))
        for match in STRING_FORMAT.finditer(src):
            if any(culture_format(spec) for spec in FORMAT_SPEC.findall(match.group(1))):
                add(match.start(), match.group(0))
        for at, text in sorted(hits.items()):
            yield rel, src.count("\n", 0, at) + 1, text


def jsonname_hits(repo: str):
    """Every read of a JSON member named like a name in src (see the jsonname rule): (path, line, the line's text). A
    literal in a comment, or one that isn't a read's argument (a wire key, a value written), is not one."""
    for root, dirs, names in os.walk(os.path.join(repo, "src")):
        dirs[:] = sorted(d for d in dirs if d not in ("obj", "bin"))
        for name in sorted(names):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(root, name)
            with open(path, encoding="utf-8-sig") as handle:
                src = handle.read()
            rel = os.path.relpath(path, repo).replace(os.sep, "/")
            for start, end, interpolated, _ in string_literals(src):
                if interpolated or src[start] != '"' or src[start + 1:end - 1] not in JSON_NAMES:
                    continue
                if JSON_READ_BEFORE.search(src[max(0, start - 200):start]):
                    line_start = src.rfind("\n", 0, start) + 1
                    line_end = src.find("\n", start)
                    yield rel, src.count("\n", 0, start) + 1, " ".join(src[line_start:len(src) if line_end < 0 else line_end].split())


def read_allow(path: str) -> dict[tuple[str, str], tuple[int, str, int]]:
    allowed: dict[tuple[str, str], tuple[int, str, int]] = {}
    if not os.path.isfile(path):
        return allowed
    with open(path, encoding="utf-8") as handle:
        for number, raw in enumerate(handle, 1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            parts = [p.strip() for p in line.split("|")]
            if len(parts) != 4 or parts[1] not in RULE_NAMES or not parts[2].isdigit() or not parts[3]:
                raise ValueError(f"{path}:{number}: expected '<path> | <rule> | <count> | <reason>' with a known rule and a reason")
            key = (parts[0], parts[1])
            if key in allowed:
                raise ValueError(f"{path}:{number}: {parts[0]} | {parts[1]} is listed twice")
            allowed[key] = (int(parts[2]), parts[3], number)
    return allowed


class SelfTest(unittest.TestCase):
    """The checker's own tests: each finds what it should in a small source tree, and passes what it should."""

    def check(self, source: str, allow: str = "") -> tuple[int, str]:
        """Runs main on a tree holding source as src/A.cs (and allow as the allow file): (exit code, stdout)."""
        with tempfile.TemporaryDirectory() as repo:
            os.makedirs(os.path.join(repo, "src"))
            with open(os.path.join(repo, "src", "A.cs"), "w", encoding="utf-8") as handle:
                handle.write(source)
            if allow:
                os.makedirs(os.path.join(repo, "tools", "checks"))
                with open(os.path.join(repo, ALLOW_FILE), "w", encoding="utf-8") as handle:
                    handle.write(allow)
            out = io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                code = main([repo])
            return code, out.getvalue()

    def rules_of(self, source: str) -> set[str]:
        """The rules the tree's log calls match (the allow file empty)."""
        code, out = self.check(source)
        return {line.split(" can write ")[1].split(",")[0] for line in out.splitlines() if " can write " in line}

    def test_every_logger_is_read_and_a_definition_or_a_comment_is_not(self) -> None:
        for call in ("ModLog.Info", "ModLog.Warn", "ModLog.Error", "ModLog.Trace", "EarlyLog.Warn", "Log", "Note", "NoteOnce", "Once",
                     "ReportOnce",
                     "MelonLogger.Msg", "LoggerInstance.Warning", "log.Msg", "log.Part", "_notify", "session.Notify"):
            self.assertEqual(self.rules_of(f"class A {{ void M() {{ {call}($\"x {{_hostName}}\"); }} }}"), {"name"}, call)
        self.assertEqual(self.rules_of("class A { private static void Log(string _hostName) { } // Log($\"{_hostName}\")\n }"), set())

    def test_each_rule_finds_its_own(self) -> None:
        cases = {
            "name": "ModLog.Info($\"{NameOf(id)} joined\");",
            "steamid": "ModLog.Info($\"{steamId}\");",
            "lobby": "ModLog.Info($\"{lobbyId}\");",
            "path": "ModLog.Info($\"{path}\");",
            "exception": "ModLog.Warn($\"failed: {ex.Message}\");",
            "playertext": "ModLog.Info($\"{teamName}\");",
            "localtime": "ModLog.Info($\"{DateTime.Now}\");",
            "salt": "ModLog.Info($\"{_logKey}\");",
            "objectname": "ModLog.Info($\"using {pad.name}\");",
        }
        for rule, call in cases.items():
            self.assertEqual(self.rules_of(f"class A {{ void M() {{ {call} }} }}"), {rule}, rule)

    def test_a_vetted_helper_makes_its_argument_safe(self) -> None:
        self.assertEqual(self.rules_of("class A { void M() { ModLog.Info($\"{Tag(steamId)} {TextShape(skater.name)}\"); } }"), set())
        self.assertEqual(self.rules_of("class A { void M() { ModLog.Info($\"{LogNames.ObjectName(root)} {LogNames.ReferenceText(o.name)}\"); } }"), set())

    def test_a_skaters_object_name_is_player_text_and_a_pads_is_not(self) -> None:
        for read in ("skater.name", "skaterRoot.name", "victim.name", "_forwardData.name", "team.logo.name", "goalie?.name",
                     "skaters[i].name"):
            self.assertEqual(self.rules_of(f"class A {{ void M() {{ ModLog.Info($\"on {{{read}}}\"); }} }}"), {"playertext", "objectname"}, read)
        for read in ("pad.name", "action.name", "_matchScene.name", "player.name", "root.name", "gameObject.name", "steamAvatar.name"):
            # Any other object's name is objectname: allowed per file once a reason says what it names (a pad, an action).
            self.assertEqual(self.rules_of(f"class A {{ void M() {{ ModLog.Info($\"on {{{read}}}\"); }} }}"), {"objectname"}, read)

    def test_a_skaters_full_name_is_player_text_and_a_types_is_not(self) -> None:
        # A hole ending in .FullName is the path rule's too (a file's full path); the playertext rule is what names the skater.
        for read in ("forward.FullName", "_forwardData.FullName", "skater?.FullName", "SafeText.Clean(forward.FullName, 40)"):
            self.assertIn("playertext", self.rules_of(f"class A {{ void M() {{ ModLog.Info($\"on {{{read}}}\"); }} }}"), read)
        self.assertEqual(self.rules_of("class A { void M() { ModLog.Info($\"on {SafeText.Clean(forward.FullName, 40)}\"); } }"), {"playertext"})
        for read in ("GetType().FullName", "typeof(Forward).FullName", "relic.GetIl2CppType().FullName", "skaterFile.Name"):
            # A type's name is the code's.
            self.assertNotIn("playertext", self.rules_of(f"class A {{ void M() {{ ModLog.Info($\"on {{{read}}}\"); }} }}"), read)

    def test_a_name_is_found_through_the_files_own_helpers(self) -> None:
        helpers = ("private static string SafeName(Forward forward) => SafeText.Clean(forward.name ?? \"?\", 48);\n"
                   "private static string Label(Forward forward)\n{\n    return $\"the host's {SafeName(forward)}\";\n}\n"
                   "// private static string Commented(Forward forward) => forward.name;\n"
                   "private static string Kind(Forward forward)\n{\n    // never forward.name: a player types it\n    return forward.Kind;\n}\n")
        for helper, rules in (("SafeName", {"playertext"}), ("Label", {"playertext"}), ("Kind", set())):
            source = f"class A\n{{\n{helpers}private void M() {{ ModLog.Info($\"{{{helper}(f)}}\"); }}\n}}\n"
            self.assertEqual(self.rules_of(source), rules, helper)

    def test_a_name_is_found_through_a_local_of_the_calling_method(self) -> None:
        helper = "private static string SafeName(Forward forward) => forward.name;\n"
        joined = ("    private void M()\n    {\n        string said = string.Join(\", \", picks.Select(p => SafeName(p.Forward)));\n"
                  "        ModLog.Info($\"Picks: {said}\");\n    }\n")
        added = ("    private void M()\n    {\n        var said = new List<string>();\n        said.Add($\"{skater.name} frogged\");\n"
                 "        ModLog.Info($\"Frogs: {string.Join(\", \", said)}\");\n    }\n")
        other = ("    private void N()\n    {\n        string said = skater.name;\n    }\n"
                 "    private void M()\n    {\n        string said = \"a skater\";\n        ModLog.Info($\"{said}\");\n    }\n")
        compared = ("    private void M()\n    {\n        string logo = team.logo.name;\n        var said = new List<string> { skater.name };\n"
                    "        ModLog.Info($\"logo ({TextShape(logo)}){(wanted != logo ? \" changed\" : \"\")}, {said.Count} names\");\n    }\n")
        self.assertEqual(self.rules_of(f"class A\n{{\n    {helper}{joined}}}\n"), {"playertext"})
        self.assertEqual(self.rules_of(f"class A\n{{\n{added}}}\n"), {"playertext"})
        self.assertEqual(self.rules_of(f"class A\n{{\n{other}}}\n"), set())
        self.assertEqual(self.rules_of(f"class A\n{{\n{compared}}}\n"), set())

    def test_a_name_is_found_through_a_local_passed_on_by_linq_or_a_builder(self) -> None:
        # A skater's full name through the file's own helper, into a list the log call passes on by LINQ.
        helper = "    private static string Name(ForwardData forward) => $\"{SafeText.Clean(forward.FullName, 40)} ({forward.id})\";\n"
        listed = ("    private static void M()\n    {\n        var failures = new List<string>();\n"
                  "        failures.Add($\"{Name(forward)} can't be read\");\n"
                  "        ModLog.Warn($\"{failures.Count} not: \" + string.Join(\"; \", failures.Take(5).Select(f => SafeText.Clean(f, 200))));\n    }\n")
        built = ("    private void M()\n    {\n        var line = new StringBuilder();\n        line.Append(skater.name);\n"
                 "        ModLog.Info(line.ToString());\n    }\n")
        counted = ("    private void M()\n    {\n        var failures = new List<string>();\n        failures.Add(skater.name);\n"
                   "        ModLog.Info($\"{failures.Count} failed, the first at {failures.IndexOf(\"x\")}\");\n    }\n")
        by_id = helper.replace("SafeText.Clean(forward.FullName, 40)", "ObjectEntryLogText.Skater(forward.id)")
        self.assertEqual(self.rules_of(f"class A\n{{\n{helper}{listed}}}\n"), {"playertext"})
        self.assertEqual(self.rules_of(f"class A\n{{\n{built}}}\n"), {"playertext"})
        self.assertEqual(self.rules_of(f"class A\n{{\n{counted}}}\n"), set())
        self.assertEqual(self.rules_of(f"class A\n{{\n{by_id}{listed}}}\n"), set())

    def test_a_files_path_and_a_whole_exception_are_found(self) -> None:
        self.assertEqual(self.rules_of("class A { void M() { ModLog.Info($\"saved to {capture.Path}\"); } }"), {"path"})
        for hole in ("{ex}", "{exception}", "{inner}", "{ex.ToString()}", "{ex.StackTrace}"):
            self.assertEqual(self.rules_of(f"class A {{ void M() {{ ModLog.Warn($\"failed: {hole}\"); }} }}"), {"exception"}, hole)
        # An error writes the stack after its line on purpose; its message is still read.
        self.assertEqual(self.rules_of("class A { void M() { ModLog.Error($\"failed: {ex}\"); } }"), set())
        self.assertEqual(self.rules_of("class A { void M() { ModLog.Error($\"failed: {ex.Message}\"); } }"), {"exception"})
        self.assertEqual(self.rules_of("class A { void M() { ModLog.Warn($\"failed: {ModLog.SafeErrorText(ex)}\"); } }"), set())

    def test_a_whole_exception_is_found_by_the_name_its_catch_gives_it(self) -> None:
        for clause, hole in (("catch (Exception e)", "{e}"), ("catch (IOException problem)", "{problem}"),
                             ("catch (Exception e) when (e is not OperationCanceledException)", "{e.ToString()}"),
                             ("catch (System.InvalidOperationException cleanup)", "{cleanup,-12}")):
            source = f"class A {{ void M() {{ try {{ Run(); }}\n        {clause}\n        {{\n            ModLog.Warn($\"failed: {hole}\"); }} }} }}"
            self.assertEqual(self.rules_of(source), {"exception"}, clause)
        # A catch inside another: both names are the exception's in the inner block.
        nested = ("class A { void M() { try { Run(); } catch (Exception outer) { try { Undo(); } catch (Exception again) "
                  "{ ModLog.Warn($\"undo failed: {again}; first: {outer}\"); } } } }")
        self.assertEqual(self.rules_of(nested), {"exception"})
        # An error writes the exception's stack after its line on purpose, by any name; the safe text is safe.
        for call in ("ModLog.Error(\"failed\", e);", "ModLog.Error($\"failed: {e}\");", "ModLog.Warn($\"failed: {ModLog.SafeErrorText(e)}\");"):
            self.assertEqual(self.rules_of(f"class A {{ void M() {{ try {{ Run(); }} catch (Exception e) {{ {call} }} }} }}"), set(), call)
        # The name is the exception's only inside its catch, and a catch in a comment or a string is none.
        outside = ("class A { void M() { try { Run(); } catch (Exception e) { Undo(); }\n"
                   "        foreach (var e in entries) { ModLog.Info($\"entry {e}\"); } } }")
        self.assertEqual(self.rules_of(outside), set())
        quoted = ("class A { void M() { // catch (Exception e) {\n        string s = \"catch (Exception e) {\";\n"
                  "        foreach (var e in entries) { ModLog.Info($\"entry {e}\"); } } }")
        self.assertEqual(self.rules_of(quoted), set())

    def test_the_allow_file_allows_its_count_and_fails_when_stale(self) -> None:
        source = "class A { void M() { ModLog.Info($\"{path}\"); } }"
        self.assertEqual(self.check(source)[0], 1)
        self.assertEqual(self.check(source, "src/A.cs | path | 1 | an FMOD event path\n")[0], 0)
        self.assertEqual(self.check(source, "src/A.cs | path | 2 | an FMOD event path\n")[0], 1)

    def culture_lines(self, source: str, extra: str = "") -> list[int]:
        """The lines of A.cs (the tree's other file B.cs holding extra) the culture rule reports."""
        with tempfile.TemporaryDirectory() as repo:
            os.makedirs(os.path.join(repo, "src"))
            for name, text in (("A.cs", source), ("B.cs", extra)):
                with open(os.path.join(repo, "src", name), "w", encoding="utf-8") as handle:
                    handle.write(text)
            return [line for rel, line, _ in culture_hits(repo) if rel == "src/A.cs"]

    def test_a_decimal_format_is_found_in_a_log_call_and_in_a_helper(self) -> None:
        for hole in ("{x:0.0}", "{x:0.##}", "{x:F1}", "{x:F}", "{x:N0}", "{x:P0}", "{x:E3}", "{x,8:0.0}", "{x:#,0}",
                     "{t:yyyy-MM-dd HH:mm:ss}", "{t:HH:mm}", "{t:dd/MM}"):
            self.assertEqual(self.culture_lines(f"class A {{ void M() {{ ModLog.Info($\"took {hole} s\"); }} }}"), [1], hole)
            self.assertEqual(self.culture_lines(f"class A {{ string Say() => $\"took {hole} s\"; }}"), [1], hole)
        self.assertEqual(self.rules_of("class A { void M() { ModLog.Info($\"took {x:0.0} s\"); } }"), {"culture"})

    def test_a_whole_number_or_a_plain_hole_carries_no_separator(self) -> None:
        for hole in ("{x:0}", "{x:00}", "{x:D2}", "{x:x16}", "{x:X8}", "{x:F0}", "{count}", "{x:+0;-0;0}", "{t:yyyyMMdd-HHmm}", "{{x:0.0}}"):
            self.assertEqual(self.culture_lines(f"class A {{ void M() {{ ModLog.Info($\"took {hole} s\"); }} }}"), [], hole)

    def test_a_string_with_the_invariant_culture_is_safe_and_one_in_the_current_culture_is_not(self) -> None:
        safe = ("string.Create(CultureInfo.InvariantCulture, $\"took {x:0.0} s\")",
                "string.Create(System.Globalization.CultureInfo.InvariantCulture,\n            $\"took {x:0.0} s\")",
                "string.Create(culture, $\"took {x:0.0} s\")",
                "text.Append(CultureInfo.InvariantCulture, $\"took {x:0.0} s\")",
                "sb.AppendLine(inv, $\"took {x:0.0} s\")",
                "FormattableString.Invariant($\"took {x:0.0} s\")",
                "string.Create(CultureInfo.InvariantCulture, $\"took {x:0.0} s, \" +\n            $\"then {y:0.0} s\")",
                "string.Create(CultureInfo.InvariantCulture, \"a \" + $\"took {x:0.0} s\")")
        for text in safe:
            self.assertEqual(self.culture_lines(f"class A {{ void M() {{ ModLog.Info({text}); }} }}"), [], text)
        unsafe = ("string.Create(CultureInfo.CurrentCulture, $\"took {x:0.0} s\")",
                  "string.Create(CultureInfo.InvariantCulture, $\"took {(ok ? $\"{x:0.0} s\" : \"none\")}\")",
                  "$\"took {x:0.0} s, \" + string.Create(CultureInfo.InvariantCulture, $\"then {y:0.0} s\")",
                  "string.Create(CultureInfo.InvariantCulture, $\"a\") + $\"took {x:0.0} s\"")
        for text in unsafe:
            self.assertEqual(self.culture_lines(f"class A {{ void M() {{ ModLog.Info({text}); }} }}"), [1], text)

    def test_a_tostring_or_format_with_a_decimal_format_needs_a_provider(self) -> None:
        for text in ("x.ToString(\"0.0\")", "x.ToString(\"F1\")", "string.Format(\"{0:0.0} s\", x)"):
            self.assertEqual(self.culture_lines(f"class A {{ string M() => {text}; }}"), [1], text)
        for text in ("x.ToString(\"0.0\", CultureInfo.InvariantCulture)", "x.ToString(\"0\")", "x.ToString()",
                     "string.Format(CultureInfo.InvariantCulture, \"{0:0.0} s\", x)", "string.Format(\"{0} s\", x)",
                     "\"a string holding .ToString(\\\"0.0\\\") is not a call\""):
            self.assertEqual(self.culture_lines(f"class A {{ string M() => {text}; }}"), [], text)

    def test_a_float_without_a_format_is_found_in_a_log_call_by_what_it_is_or_how_it_was_declared(self) -> None:
        declared = "class A { private double _wait; private const float Scale = 1.5f; private int _count; private TimeSpan _span;\n"
        for hole in ("{_wait}", "{Scale}", "{_wait * 2}", "{_span.TotalSeconds}", "{(float)_count}", "{_count / 2.0}", "{Math.Round(_wait)}",
                     "{Math.Max(_wait, 1)}", "{p?.Seconds}", "{3.5f}", "{_wait,8}"):
            source = declared + f"double Seconds; void M(A p) {{ ModLog.Info($\"took {hole}\"); }} }}"
            self.assertEqual(self.culture_lines(source), [2], hole)
        for hole in ("{_count}", "{_span}", "{(int)_wait}", "{Mathf.RoundToInt(_wait)}", "{_wait > 1}", "{_wait:0}", "{_count * 2}",
                     "{Count(_wait)}", "{_list.Sum(x => _wait)}", "{(_wait > 1 ? \"long\" : \"short\")}"):
            source = declared + f"void M() {{ ModLog.Info($\"took {hole}\"); }} }}"
            self.assertEqual(self.culture_lines(source), [], hole)
        # Outside a log call a hole with no format is not read: it is not known to be logged.
        self.assertEqual(self.culture_lines(declared + "string Say() => $\"took {_wait}\"; }"), [])

    def test_a_name_another_file_declares_counts_only_when_every_declaration_is_a_float(self) -> None:
        use = "class A { void M() { ModLog.Info($\"took {Lens} and {Both}\"); } }"
        other = "class B { public float Lens { get; } public float Both; public int Both2; }"
        self.assertEqual(self.culture_lines(use, other), [1])
        self.assertEqual(self.culture_lines(use, "class B { public int Lens; public float Both; }"), [1])  # Both is a float there
        self.assertEqual(self.culture_lines("class A { void M() { ModLog.Info($\"took {Lens}\"); } }", "class B { public float Lens; public int Lens; }"), [])
        self.assertEqual(self.culture_lines("class A { int Lens; void M() { ModLog.Info($\"took {Lens}\"); } }", "class B { public float Lens; }"), [])

    def test_a_var_is_a_float_by_what_it_is_assigned(self) -> None:
        source = "class A { void M() { var gap = 0.5; var n = 5; var t = elapsed.TotalSeconds; ModLog.Info($\"{gap} {n} {t}\"); } }"
        self.assertEqual(self.culture_lines(source), [1])
        self.assertEqual(self.culture_lines("class A { void M() { var n = 5; ModLog.Info($\"{n}\"); } }"), [])

    def test_comments_and_plain_strings_are_not_read_as_code(self) -> None:
        source = ("class A {\n    // string Say() => $\"{x:0.0}\";\n    /* $\"{x:0.0}\" */\n"
                  "    string M() => \"a $\\\"{x:0.0}\\\" in a string\";\n    char c = '\"';\n    string N() => $\"{x:0.0}\";\n}\n")
        self.assertEqual(self.culture_lines(source), [6])

    def test_the_allow_file_takes_the_culture_rule(self) -> None:
        source = "class A { void M() { ModLog.Info($\"took {x:0.0} s\"); } }"
        self.assertEqual(self.check(source)[0], 1)
        self.assertEqual(self.check(source, "src/A.cs | culture | 1 | a text shown on screen in the player's own culture\n")[0], 0)
        self.assertEqual(self.check("class A { void M() { ModLog.Info($\"took {x:0.0} s\"); ModLog.Info($\"{y:0.0}\"); } }",
                                    "src/A.cs | culture | 1 | a text shown on screen in the player's own culture\n")[0], 1)

    def jsonname_lines(self, source: str) -> list[int]:
        """The lines of A.cs the jsonname rule reports."""
        with tempfile.TemporaryDirectory() as repo:
            os.makedirs(os.path.join(repo, "src"))
            with open(os.path.join(repo, "src", "A.cs"), "w", encoding="utf-8") as handle:
                handle.write(source)
            return [line for rel, line, _ in jsonname_hits(repo) if rel == "src/A.cs"]

    def test_a_json_member_named_like_a_name_is_found_where_it_is_read(self) -> None:
        # A run file's team as a roster once read it: the names went into a record whose ToString a log line wrote.
        roster = ("class A {\n    void M(JsonElement seat) {\n"
                  "        string name = $\"{Text(seat, \"firstName\")} {Text(seat, \"lastName\")}\".Trim();\n"
                  "        skaters.Add(new RosterSkater(SafeText.Clean(id, IdChars), SafeText.Clean(name.Length > 0 ? name : Text(seat, \"name\"), NameChars), Whole(seat, \"number\")));\n"
                  "    }\n}\n")
        self.assertEqual(self.jsonname_lines(roster), [3, 3, 4])
        for read in ("team.TryGetProperty(\"teamName\", out JsonElement found)", "line.GetProperty(\"displayName\")",
                     "TryGetString(entry, \"name\", out string name)", "Text(run.Value, \"city\")", "Text(seats[i], \"nickname\")"):
            self.assertEqual(self.jsonname_lines(f"class A {{ void M() {{ var x = {read}; }} }}"), [1], read)
        self.assertEqual(self.rules_of(f"class A {{ void M() {{ ModLog.Info(\"started\"); var x = Text(seat, \"lastName\"); }} }}"), {"jsonname"})

    def test_an_id_a_wire_key_a_value_written_or_a_comment_is_not_a_names_read(self) -> None:
        for code in ("var x = Text(seat, \"id\");", "var x = run.TryGetProperty(\"runID\", out _);", "add(Value(\"name\", NetValue.FromString(n)));",
                     "writer.WriteString(\"name\", file.Name);", "var x = values.Text($\"{side}.name\");", "// Text(seat, \"firstName\")",
                     "var x = Name(\"first name\", a, b);", "var x = T(\"teamName\");", "/* GetProperty(\"lastName\") */"):
            self.assertEqual(self.jsonname_lines(f"class A {{ void M() {{ {code}\n }} }}"), [], code)

    def test_the_allow_file_takes_the_jsonname_rule(self) -> None:
        source = "class A { void M() { ModLog.Info(\"started\"); var x = TryGetString(entry, \"name\", out string name); } }"
        self.assertEqual(self.check(source)[0], 1)
        self.assertEqual(self.check(source, "src/A.cs | jsonname | 1 | a manifest's file name, the mod's own\n")[0], 0)

    def test_a_tree_without_a_log_call_is_an_error_not_a_pass(self) -> None:
        self.assertEqual(self.check("class A { void M() { } }")[0], 2)
        self.assertEqual(self.check("class A { void M() { ModLog.Info(\"started\"); } }")[0], 0)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("repo", nargs="?", default=".")
    parser.add_argument("--list", metavar="RULE", choices=sorted(RULE_NAMES))
    parser.add_argument("--suggest", action="store_true")
    parser.add_argument("--selftest", action="store_true", help="run the checker's own tests")
    args = parser.parse_args(argv)
    if args.selftest:
        result = unittest.TextTestRunner(stream=sys.stderr, verbosity=1).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(SelfTest))
        print(f"logcheck selftest: {result.testsRun} tests, {len(result.failures) + len(result.errors)} failed", file=sys.stderr)
        return 0 if result.wasSuccessful() and result.testsRun > 0 else 1
    repo = args.repo
    if not os.path.isdir(os.path.join(repo, "src")):
        print(f"logcheck: no src folder in {repo}", file=sys.stderr)
        return 2

    found: dict[tuple[str, str], list[tuple[int, str]]] = defaultdict(list)
    total = 0
    for rel, line, code, text, caught in calls(repo):
        total += 1
        for rule, regex in RULES.items():
            if regex.search(code) or (rule == "exception" and whole_exception(caught).search(code) and not text.startswith(ERROR_CALLS)):
                found[(rel, rule)].append((line, text))
    for rel, line, text in culture_hits(repo):
        found[(rel, "culture")].append((line, text))
    for rel, line, text in jsonname_hits(repo):
        found[(rel, "jsonname")].append((line, text))
    if total == 0:
        print(f"logcheck: no log call found under {os.path.join(repo, 'src')}: nothing was checked", file=sys.stderr)
        return 2

    if args.list:
        for (rel, rule), hits in sorted(found.items()):
            if rule == args.list:
                for line, text in hits:
                    print(f"{rel}:{line}: {text[:240]}")
        return 0

    if args.suggest:
        for (rel, rule), hits in sorted(found.items()):
            print(f"{rel} | {rule} | {len(hits)} | TODO")
        return 0

    try:
        allowed = read_allow(os.path.join(repo, ALLOW_FILE))
    except (OSError, ValueError) as error:
        print(f"logcheck: {error}", file=sys.stderr)
        return 2

    problems: list[str] = []
    for key, hits in sorted(found.items()):
        rel, rule = key
        limit, reason, _ = allowed.get(key, (0, "", 0))
        if len(hits) > limit:
            problems.append(f"{rel}: {len(hits)} call(s) can write {rule}, {limit} allowed{f' ({reason})' if reason else ''}:")
            problems.extend(f"  {rel}:{line}: {text[:200]}" for line, text in hits)
    for key, (limit, reason, number) in sorted(allowed.items()):
        count = len(found.get(key, []))
        if count < limit:
            problems.append(f"{ALLOW_FILE}:{number}: {key[0]} | {key[1]} allows {limit} but {count} remain ({reason}): "
                            f"{'remove the entry' if count == 0 else f'lower it to {count}'}")

    allowed_total = sum(len(hits) for key, hits in found.items() if key in allowed)
    for line in problems:
        print(line)
    print(f"logcheck: {total} log call(s) read, {sum(len(h) for h in found.values())} match(es), {allowed_total} allowed by "
          f"{len(allowed)} entr{'y' if len(allowed) == 1 else 'ies'}, {len(problems)} problem line(s)", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
    sys.exit(main(sys.argv[1:]))
