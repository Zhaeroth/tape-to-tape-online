# The native lobby's words against the game's fonts: every glyph of every word in every language, and every label's width.
# Reads the words from src/T2TOnline.Core/Lobby/LobbyWords.cs (one word to a line) and the game's term names from LobbyTerms.cs.
# The game-derived data stays outside the repo: font metrics in a JSON file (font_metrics.json) and the game's terms in a TSV
# (terms_full.tsv: the game's text terms exported as a table under a header row, one term to a row: its name, its type, then a
# column per language). Both sit in one folder, given by --data or the T2T_GAME_DATA environment variable; --metrics and --terms name a
# file each instead. Both are made from the game's files and must be remade after a game patch (the metrics: --make-metrics).
#
#   python tools/checks/lobbywords_check.py [--data DIR | --metrics PATH --terms PATH] [--source DIR]
#   python tools/checks/lobbywords_check.py --make-metrics [--game DIR] [--data DIR | --metrics PATH]
#   python tools/checks/lobbywords_check.py --selftest
#
# Exit 0: all drawable and all labels fit. Exit 1: a missing glyph, a too-wide label, a term the game doesn't have, or a broken
# word table (a word or term line not in the one form read here, or a table read as empty). Exit 2: the game-derived data
# isn't there (nothing was checked).
import argparse
import contextlib
import csv
import glob
import importlib.util
import io
import json
import os
import re
import sys
import tempfile
import unittest

METRICS_FILE = "font_metrics.json"
TERMS_FILE = "terms_full.tsv"
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
LANGS = ["en", "fr", "es", "de", "it", "pt-BR", "fi", "ru", "sv", "zh-CN", "cs", "uk", "pl", "ro", "ja"]

# Which fonts draw what, in each language (verified in the game's code: TmpFontRegistry): the asset's own atlas, then for Chinese and
# Japanese the role's font and the global one. LiberationSans isn't counted: a character only it has reads in another weight.
ROLES = {
    "title button": {"base": "NotoSans-BlackItalic SDF - Outline Black", "zh-CN": ["NotoSansSC-Black SDF", "NotoSansSC-Regular SDF"], "ja": ["NotoSansJP-Black SDF", "NotoSansJP-Regular SDF"]},
    "header": {"base": "Bungee-Regular SDF - Outline", "zh-CN": ["ResourceHanRoundedCN-Heavy SDF", "NotoSansSC-Regular SDF"], "ja": ["MPLUSRounded1c-Black SDF", "NotoSansJP-Regular SDF"]},
    "body black": {"base": "NotoSans-Black SDF", "zh-CN": ["NotoSansSC-Black SDF", "NotoSansSC-Regular SDF"], "ja": ["NotoSansJP-Black SDF", "NotoSansJP-Regular SDF"]},
    "body semibold": {"base": "NotoSans-SemiBold SDF", "zh-CN": ["NotoSansSC-Regular SDF"], "ja": ["NotoSansJP-Regular SDF"]},
    "toast italic": {"base": "NotoSans-SemiBoldItalic SDF", "zh-CN": ["NotoSansSC-Regular SDF"], "ja": ["NotoSansJP-Regular SDF"]},
}

# Per kind of word (WordFit in LobbyWords.cs): the roles that draw it, and the widest it may be in canvas units at a size.
# A label's room is its box less a margin; two-line labels may take two widths; a sentence wraps, so only its glyphs are checked.
FITS = {
    # The title's list: size 36, no wrap, no auto-size; the button is 480 wide, and the longest of the game's own is 422.
    "TitleRow": {"roles": ["title button"], "font": "title button", "size": 36, "max": 440},
    # A header auto-sizes in 731: checked at its smallest (48).
    "Header": {"roles": ["header", "title button"], "font": "header", "size": 48, "max": 1200},
    # The bottom bar, list buttons and a selector's label (372 wide): NotoSans-BlackItalic 32, auto-size floor 26 (the floor for what must be read).
    "Prompt": {"roles": ["title button", "body black"], "font": "title button", "size": 26, "max": 360},
    # A card (277 wide less margins, up to two lines) at the small labels' 24; a 5x2 card is narrower and auto-sizes.
    "Seat": {"roles": ["body black", "toast italic"], "font": "body black", "size": 24, "max": 500},
    "Sentence": {"roles": ["body black", "body semibold", "toast italic"], "font": None, "size": 0, "max": 0},
    # The Settings menu's own prefab (ui-menu_settings): a tab is NotoSans-Black 26 (auto-size 14-254, a FontSizeMatcher over the bar), and with the
    # Online tab eight share the 1920 bar: (1920 - 64 padding - 2 x 48 shortcut glyphs - 9 x 10 spacing) / 8 = 208.75 each, less the text's 32 margin = 177.
    "SettingsTab": {"roles": ["body black"], "font": "body black", "size": 26, "max": 177},
    # A row's title (Toggle_*/Text/Line1, a button row's Title, a cycle row's Title): NotoSans-SemiBoldItalic 24 (auto-size 18-24) in the 600 px Text
    # (985 wide row, less its 12 padding and the 372 px cycle list or 360 px button). Checked at 24, which also covers a second line (Line2, 18 in 600).
    "SettingsRow": {"roles": ["toast italic"], "font": "toast italic", "size": 24, "max": 600},
    # A button row's button (ButtonWithTitle_*/Button/Text (TMP)): NotoSans-Black 24, auto-size 12-24, in the button's 360 px less a 10 px margin each side.
    "SettingsButton": {"roles": ["body black"], "font": "body black", "size": 24, "max": 340},
    # The tooltip box's description (Tooltip_DeactivateWhenEmpty/.../TooltipDesc): NotoSans-SemiBoldItalic 18 in 312 px, in a scroll view that FitChildren
    # holds to 24-120 px high at every aspect ratio: four lines (25 px each) show and anything more scrolls by itself (AutoScrollingRect, after 2.5 s).
    # The limit is five lines' width, 5 x 312 = 1560 and a little (1600): a tooltip of ours is at most about as long as the game's own longest in English
    # (1639, "settings/tooltip/camera"; the game's Polish one is 2169), and the fifth line scrolls in as the game's own do.
    "SettingsTooltip": {"roles": ["toast italic"], "font": "toast italic", "size": 18, "max": 1600},
    # A tab of the Glossary frame (the GlossaryTab prefab, 275 wide): NotoSans-Black 32 with 8 character spacing (hundredths of an em), auto-size 14-254;
    # checked at 24, the least that reads, in the 243 px of text (a -32 margin).
    "ListTab": {"roles": ["body black"], "font": "body black", "size": 24, "max": 243, "spacing": 0.08},
}

# Words that appear in more than one place: the C# name, and the other fits it must also meet (a prompt's title is a header; a button's text also stands
# in a prompt's bottom bar).
ALSO = {
    "SaveBackups": ["Header"],
    "ProblemReport": ["Header"],
    "MakeReport": ["Prompt"],
    # The Online menu's Report a Problem row reads it while a report is made.
    "Making": ["TitleRow"],
}

STRING = re.compile(r'"((?:[^"\\]|\\.)*)"')
WORD = re.compile(r'^\s*public static readonly LobbyWord (\w+) = W\("(t2t/[^"]+)", WordFit\.(\w+), (.*)\);\s*$')
TERM = re.compile(r'^\s*public const string (\w+) = "([^"]+)";\s*$')


def unescape(text):
    # The escapes a word's string may hold: nothing else appears in the table.
    return re.sub(r"\\(.)", lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), text)


def read_words(source):
    # Every line that calls the table's helper with a key (W("t2t/...") or assigns from it is a word, and must be in the
    # one form this tool reads: a word it skipped would go unchecked.
    path = os.path.join(source, "LobbyWords.cs")
    words = []
    with open(path, encoding="utf-8") as fh:
        for number, line in enumerate(fh, 1):
            if ' W("' in line or " = W(" in line:
                match = WORD.match(line)
                if not match:
                    raise SystemExit(f"{path}:{number}: a word that isn't on one line, or not in the expected form")
                texts = [unescape(t) for t in STRING.findall(match.group(4))]
                words.append({"name": match.group(1), "key": match.group(2), "fit": match.group(3), "texts": texts, "line": number})
    return words


def read_terms(source):
    # Every constant is a term, in the one form this tool reads.
    path = os.path.join(source, "LobbyTerms.cs")
    terms = []
    with open(path, encoding="utf-8") as fh:
        for number, line in enumerate(fh, 1):
            if " const string " in f" {line.strip()} ":
                match = TERM.match(line)
                if not match:
                    raise SystemExit(f"{path}:{number}: a term that isn't on one line, or not in the expected form")
                terms.append(match.group(2))
    return terms


LINK = re.compile(r'^\s*public static string (\w+Short) => "([^"]+)";\s*$')


def read_links(source):
    # The short forms of the project's address that words show (ProjectLinks.cs, one folder above the words): never written in this tool.
    path = os.path.join(os.path.dirname(os.path.abspath(source)), "ProjectLinks.cs")
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as fh:
        return {m.group(1): m.group(2) for m in (LINK.match(line) for line in fh) if m}


def check_links(links, fonts):
    # A short form is drawn inside a sentence (the report prompt's body): every glyph must be in each language's fonts for each role that draws one.
    problems = []
    if not links:
        return ["LINK  ProjectLinks.cs has no short forms (IssuesShort, ReleasesShort)"]
    for name, text in links.items():
        for lang in LANGS:
            for role in FITS["Sentence"]["roles"]:
                chars = chain(fonts, role, lang)
                missing = sorted({ch for ch in text if not any(str(ord(ch)) in f["chars"] for f in chars)})
                if missing:
                    problems.append(f"LINK  {name} {lang} in {role}: {' '.join(missing)} ({' '.join(f'U+{ord(c):04X}' for c in missing)})")
    return problems


def make_metrics(game, out):
    tool = os.path.join(HERE, "bundle_prefabs.py")
    spec = importlib.util.spec_from_file_location("bundle_prefabs", tool)
    bp = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(bp)
    aa = os.path.join(game, "Tape to Tape_Data", "StreamingAssets", "aa", "StandaloneWindows64")
    ms_bundle = next(iter(glob.glob(os.path.join(aa, "*monoscripts*"))))
    ms_data = next(v for k, v in bp.load_bundle(ms_bundle).items() if not k.endswith(".resS"))
    ms = bp.SerializedFile(ms_data)
    scripts = {pid: ms.read(pid).get("m_ClassName", "?") for pid in ms.objects if ms.cid(pid) == 115}
    fonts = {}
    for path in sorted(glob.glob(os.path.join(aa, "fonts_*.bundle"))):
        for name, blob in bp.load_bundle(path).items():
            if name.endswith((".resS", ".resource")):
                continue
            sf = bp.SerializedFile(blob)
            for pid in sf.objects:
                if sf.cid(pid) != 114:
                    continue
                mb = sf.read(pid)
                script = mb.get("m_Script", {})
                if script.get("m_FileID", 0) > 0 and scripts.get(script.get("m_PathID")) == "TMP_FontAsset":
                    glyphs = {g["m_Index"]: g for g in mb.get("m_GlyphTable", [])}
                    chars = {}
                    for c in mb.get("m_CharacterTable", []):
                        g = glyphs.get(c["m_GlyphIndex"])
                        advance = g["m_Metrics"]["m_HorizontalAdvance"] if g else 0
                        chars[str(c["m_Unicode"])] = round(advance * c.get("m_Scale", 1.0) * (g.get("m_Scale", 1.0) if g else 1.0), 3)
                    fonts[mb["m_Name"]] = {"point": mb["m_FaceInfo"]["m_PointSize"], "chars": chars}
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(fonts, fh)
    print(f"{out}: {len(fonts)} fonts")


def chain(fonts, role, lang):
    names = [ROLES[role]["base"]] + ROLES[role].get(lang, [])
    return [fonts[n] for n in names]


def width(fonts, role, lang, text, size, spacing=0.0):
    total = 0.0
    links = chain(fonts, role, lang)
    for ch in text:
        total += spacing * size
        for font in links:
            advance = font["chars"].get(str(ord(ch)))
            if advance is not None:
                total += advance / font["point"] * size
                break
        else:
            total += links[0]["point"] * 0.5 / links[0]["point"] * size
    return total


# What stands in for an argument when a label is measured: a kept seat's clock, or a short name or number.
def stand_in(text):
    return re.sub(r"\{\d\}", "0:27", text)


def check(words, terms, fonts, game_terms, also=None):
    also = ALSO if also is None else also
    problems = []
    arguments = re.compile(r"\{\d\}")
    seen = set()
    # A table read as empty checks nothing: that is a broken table, never a pass.
    if not words:
        problems.append("TABLE no words were read from LobbyWords.cs")
    if not terms:
        problems.append("TERM  no terms were read from LobbyTerms.cs")
    names = {word["name"] for word in words}
    problems += [f"TABLE {name} has a second fit (ALSO) but no word has that name" for name in sorted(set(also) - names)]
    for word in words:
        where = f"{word['key']} ({word['name']})"
        if word["key"] in seen:
            problems.append(f"TABLE {where}: the term twice")
        seen.add(word["key"])
        if len(word["texts"]) != len(LANGS):
            problems.append(f"TABLE {where}: {len(word['texts'])} columns, not {len(LANGS)}")
            continue
        if word["fit"] not in FITS:
            problems.append(f"TABLE {where}: unknown fit {word['fit']}")
            continue
        expected = sorted(set(arguments.findall(word["texts"][0])))
        for lang, text in zip(LANGS, word["texts"]):
            if not text.strip():
                problems.append(f"TABLE {where}: blank in {lang}")
            if sorted(set(arguments.findall(text))) != expected:
                problems.append(f"TABLE {where}: {lang} has arguments {sorted(set(arguments.findall(text)))}, English {expected}")
            bare = arguments.sub("", text)
            for fit_name in [word["fit"]] + also.get(word["name"], []):
                fit = FITS[fit_name]
                for role in fit["roles"]:
                    links = chain(fonts, role, lang)
                    missing = sorted({ch for ch in bare if ch != " " and not any(str(ord(ch)) in f["chars"] for f in links)})
                    if missing:
                        problems.append(f"GLYPH {where} {lang} in {role}: {' '.join(missing)} ({' '.join(f'U+{ord(c):04X}' for c in missing)}): {text}")
                if fit["font"]:
                    wide = width(fonts, fit["font"], lang, stand_in(text), fit["size"], fit.get("spacing", 0.0))
                    if wide > fit["max"]:
                        problems.append(f"WIDE  {where} {lang} as {fit_name}: {wide:.0f} over {fit['max']} at size {fit['size']}: {text}")
    if game_terms is not None:
        for term in terms:
            row = game_terms.get(term)
            if row is None:
                problems.append(f"TERM  {term}: not in the game's source")
            elif any(not cell.strip() for cell in row):
                print(f"note: {term} is blank in {sum(1 for cell in row if not cell.strip())} language(s): the game falls back there")
    for separator in ["\u2022"]:
        for lang in LANGS:
            for role in ROLES:
                if not any(str(ord(separator)) in f["chars"] for f in chain(fonts, role, lang)):
                    problems.append(f"GLYPH separator U+{ord(separator):04X} in {role}, {lang}")
    return problems


class SelfTest(unittest.TestCase):
    """The checker's own tests, on a made-up table and made-up fonts: each kind of problem is found, and a sound table passes."""

    @staticmethod
    def fonts(also_draws=""):
        # Every font the roles name, drawing printable ASCII and the separator at half an em (advance 50 at point 100).
        chars = {str(c): 50.0 for c in [*range(32, 127), 0x2022, *map(ord, also_draws)]}
        names = {n for role in ROLES.values() for v in role.values() for n in ([v] if isinstance(v, str) else v)}
        return {n: {"point": 100, "chars": dict(chars)} for n in names}

    @staticmethod
    def word(name="Ok", fit="Sentence", text="Ok", texts=None, key=None):
        return {"name": name, "key": key or f"t2t/{name.lower()}", "fit": fit, "texts": texts or [text] * len(LANGS), "line": 1}

    def problems(self, words, terms=("general/back",), fonts=None, game_terms=None, also=None):
        with contextlib.redirect_stdout(io.StringIO()):
            return check(words, list(terms), fonts or self.fonts(), game_terms, also={} if also is None else also)

    def test_a_sound_table_passes(self):
        words = [self.word(), self.word("Title", "TitleRow", "Online Game"), self.word("Left", "Seat", "{0} left")]
        self.assertEqual(self.problems(words, game_terms={"general/back": ["Back"] * len(LANGS)}), [])

    def test_an_empty_table_is_a_problem(self):
        self.assertTrue(any("no words" in p for p in self.problems([])))
        self.assertTrue(any("no terms" in p for p in self.problems([self.word()], terms=())))

    def test_a_second_fit_naming_no_word_is_a_problem(self):
        self.assertTrue(any("Missing has a second fit" in p for p in self.problems([self.word()], also={"Missing": ["Header"]})))
        self.assertEqual(self.problems([self.word("Making", "SettingsButton", "Making...")], also={"Making": ["TitleRow"]}), [])

    def test_a_missing_glyph_is_found_in_the_word_and_its_second_fit(self):
        self.assertTrue(any(p.startswith("GLYPH") and "U+4E00" in p for p in self.problems([self.word(text="Ok一")])))
        self.assertEqual(self.problems([self.word(text="Ok一")], fonts=self.fonts("一")), [])

    def test_a_label_too_wide_for_its_fit_is_found(self):
        # TitleRow: size 36, at most 440; half an em is 18 a character, so 30 characters are 540 and 20 are 360.
        self.assertTrue(any(p.startswith("WIDE") for p in self.problems([self.word("Long", "TitleRow", "x" * 30)])))
        self.assertEqual(self.problems([self.word("Twice", "Sentence", "x" * 30)]), [])
        self.assertTrue(any(p.startswith("WIDE") for p in self.problems([self.word("Twice", "Sentence", "x" * 30)], also={"Twice": ["TitleRow"]})))
        self.assertEqual(self.problems([self.word("Short", "TitleRow", "x" * 20)]), [])

    def test_a_broken_row_is_found(self):
        self.assertTrue(any("columns" in p for p in self.problems([self.word(texts=["Ok"] * (len(LANGS) - 1))])))
        self.assertTrue(any("unknown fit" in p for p in self.problems([self.word(fit="Banner")])))
        self.assertTrue(any("the term twice" in p for p in self.problems([self.word("A", key="t2t/a"), self.word("B", key="t2t/a")])))
        self.assertTrue(any("has arguments" in p for p in self.problems([self.word(texts=["{0} left"] + ["left"] * (len(LANGS) - 1))])))
        self.assertTrue(any("blank in" in p for p in self.problems([self.word(texts=["Ok"] * (len(LANGS) - 1) + [" "])])))

    def test_a_term_the_game_lacks_is_found(self):
        self.assertTrue(any(p.startswith("TERM") for p in self.problems([self.word()], game_terms={"general/start": ["Start"] * len(LANGS)})))

    def test_the_link_forms_need_their_glyphs(self):
        self.assertTrue(any(p.startswith("LINK") for p in check_links(None, self.fonts())))
        self.assertTrue(any(p.startswith("LINK") for p in check_links({"IssuesShort": "github.com/a/bé"}, self.fonts())))
        self.assertEqual(check_links({"IssuesShort": "github.com/a/b/issues"}, self.fonts()), [])

    def test_every_word_and_term_line_is_read_or_refused(self):
        word = 'public static readonly LobbyWord Ok = W("t2t/ok", WordFit.Sentence, ' + ", ".join(['"Ok"'] * len(LANGS)) + ");\n"
        with tempfile.TemporaryDirectory() as source:
            def write(name, text):
                with open(os.path.join(source, name), "w", encoding="utf-8") as fh:
                    fh.write(text)
            write("LobbyWords.cs", word + "private static LobbyWord W(string key, WordFit fit, params string[] texts) => new(key, fit, texts);\n")
            write("LobbyTerms.cs", '/// <summary>Back.</summary>\n    public const string Back = "general/back";\n')
            self.assertEqual([w["name"] for w in read_words(source)], ["Ok"])
            self.assertEqual(read_terms(source), ["general/back"])
            for broken in (word.replace("public static readonly", "internal static"), word.replace(");\n", ",\n    );\n")):
                write("LobbyWords.cs", broken)
                with self.assertRaises(SystemExit):
                    read_words(source)
            write("LobbyTerms.cs", '    internal const string Back = "general/back";\n')
            with self.assertRaises(SystemExit):
                read_terms(source)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", default=os.environ.get("T2T_GAME_DATA", ""),
                        help=f"the folder with {METRICS_FILE} and {TERMS_FILE} (default: the T2T_GAME_DATA environment variable)")
    parser.add_argument("--metrics", help=f"the font metrics JSON (made by --make-metrics; default: {METRICS_FILE} in --data)")
    parser.add_argument("--terms", help=f"the game's terms TSV (default: {TERMS_FILE} in --data)")
    parser.add_argument("--source", default=os.path.join(REPO, "src", "T2TOnline.Core", "Lobby"), help="the folder with LobbyWords.cs and LobbyTerms.cs")
    parser.add_argument("--game", default=os.environ.get("T2T_GAME_DIR", ""),
                        help="the game's folder, for --make-metrics (default: the T2T_GAME_DIR environment variable)")
    parser.add_argument("--make-metrics", action="store_true", help="read the fonts from the game's bundles into --metrics, and stop")
    parser.add_argument("--widest", type=int, default=0, help="also list this many widest labels per fit")
    parser.add_argument("--selftest", action="store_true", help="run the checker's own tests (no game data needed), and stop")
    args = parser.parse_args()
    if args.selftest:
        result = unittest.TextTestRunner(stream=sys.stderr, verbosity=1).run(unittest.defaultTestLoader.loadTestsFromTestCase(SelfTest))
        print(f"lobbywords_check selftest: {result.testsRun} tests, {len(result.failures) + len(result.errors)} failed")
        return 0 if result.wasSuccessful() and result.testsRun > 0 else 1
    if not args.metrics and args.data:
        args.metrics = os.path.join(args.data, METRICS_FILE)
    if not args.terms and args.data:
        args.terms = os.path.join(args.data, TERMS_FILE)

    if args.make_metrics:
        if not args.game or not args.metrics:
            print("--make-metrics needs the game's folder (--game or T2T_GAME_DIR) and where to write (--data, T2T_GAME_DATA or --metrics).")
            return 2
        make_metrics(args.game, args.metrics)
        return 0

    if not args.metrics:
        print(f"No game data: pass --data (the folder with {METRICS_FILE} and {TERMS_FILE}), set T2T_GAME_DATA, or pass --metrics. "
              "Nothing was checked.")
        return 2

    if not os.path.isfile(args.metrics):
        print(f"No font metrics at {args.metrics}: make them with --make-metrics. Nothing was checked.")
        return 2

    with open(args.metrics, encoding="utf-8") as fh:
        fonts = json.load(fh)
    gone = sorted({n for role in ROLES.values() for names in role.values() for n in ([names] if isinstance(names, str) else names) if n not in fonts})
    if gone:
        print(f"Fonts the metrics don't have (a game patch? --make-metrics): {', '.join(gone)}. Nothing else was checked.")
        return 1

    words = read_words(args.source)
    terms = read_terms(args.source)
    game_terms = None
    if args.terms and os.path.isfile(args.terms):
        with open(args.terms, encoding="utf-8", newline="") as fh:
            rows = list(csv.reader(fh, delimiter="\t", quoting=csv.QUOTE_NONE))
        game_terms = {r[0]: r[2:17] for r in rows[1:] if len(r) >= 17}
    else:
        print(f"note: no game terms at {args.terms or '(none given)'}: the need-to-term table wasn't checked")

    problems = check(words, terms, fonts, game_terms)
    links = read_links(args.source)
    problems += check_links(links, fonts)
    if args.widest:
        for fit_name, fit in FITS.items():
            if not fit["font"]:
                continue
            rows = []
            for word in words:
                if word["fit"] == fit_name and len(word["texts"]) == len(LANGS):
                    rows += [(width(fonts, fit["font"], lang, stand_in(text), fit["size"], fit.get("spacing", 0.0)), word["key"], lang, text) for lang, text in zip(LANGS, word["texts"])]
            print(f"{fit_name}, limit {fit['max']} at size {fit['size']}:")
            for wide, key, lang, text in sorted(rows, reverse=True)[:args.widest]:
                print(f"  {wide:6.0f} {key:24} {lang:6} {text}")

    for problem in problems:
        print(problem)
    print(f"{len(words)} words in {len(LANGS)} languages, {len(terms)} game terms, {len(links or {})} link form(s): {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
