#!/usr/bin/env python3
"""Check the native lobby's prefab parts against a game's bundles.

Usage: uipartscheck.py [<game folder> [ui_parts.txt]]
<game folder> defaults to the T2T_GAME_DIR environment variable.
ui_parts.txt (default: src/T2TOnline.Plugin/Hooks/ui_parts.txt) lists every prefab part the native lobby clones or finds by path:
  @<menu definition> | <start of its bundle's file name> | <prefab root's name>
  <menu definition> | <path under the root; empty for the root> | <script classes it must carry, comma separated>
Prints one line per row and exits 1 if a bundle, a root, a part or a script is missing. A hook list's row that's missing turns online off;
a part that's missing turns off only the native lobby (the mod checks the same rows in the game when a source prefab has loaded), so this
isn't a hook list. For the after-every-game-patch checklist and the release gate. Reads the game's files as data only.
"""
import importlib.util
import os
import sys

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("bundle_prefabs", os.path.join(HERE, "bundle_prefabs.py"))
bp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bp)

# Unity's own classes, by the id a serialized file gives them (scripts are named by their MonoScript).
ENGINE = {95: "Animator", 223: "Canvas", 225: "CanvasGroup", 222: "CanvasRenderer", 224: "RectTransform", 4: "Transform"}


def parse(path):
    """The sources and the parts in the list, or a problem naming the line."""
    sources, parts = {}, []
    with open(path, encoding="utf-8") as f:
        for number, raw in enumerate(f, 1):
            line = raw.split("#", 1)[0].strip()
            if not line:
                continue
            cells = [c.strip() for c in line.split("|")]
            if len(cells) != 3:
                raise SystemExit(f"{path}:{number}: expected three cells, got {len(cells)}")
            if cells[0].startswith("@"):
                sources[cells[0][1:]] = (cells[1], cells[2])
            elif cells[0] not in sources:
                raise SystemExit(f"{path}:{number}: the source {cells[0]} isn't declared above it")
            else:
                parts.append((cells[0], cells[1], [c.strip() for c in cells[2].split(",") if c.strip()]))
    return sources, parts


def components_of(prefabs, go):
    """The class names on a game object."""
    names = set()
    sf = prefabs.sf
    for c in sf.read(go)["m_Component"]:
        cp = c["component"]["m_PathID"]
        if cp not in sf.objects:
            continue
        if sf.cid(cp) == 114:
            names.add(prefabs.script(sf.read(cp)))
        else:
            names.add(ENGINE.get(sf.cid(cp), f"c{sf.cid(cp)}"))
    return names


def find(prefabs, root, path):
    """The game object at a path under a root game object, or None."""
    sf = prefabs.sf
    go = root
    for name in [p for p in path.split("/") if p]:
        found = None
        for child in sf.read(prefabs.tr[go])["m_Children"]:
            if child["m_PathID"] in sf.objects:
                candidate = sf.read(child["m_PathID"])["m_GameObject"]["m_PathID"]
                if prefabs.names[candidate] == name:
                    found = candidate
                    break
        if found is None:
            return None
        go = found
    return go


def main(argv):
    game = argv[1] if len(argv) > 1 else os.environ.get("T2T_GAME_DIR", "")
    if not game:
        print(__doc__)
        print("No game folder: pass it as the first argument or set T2T_GAME_DIR.")
        return 2
    folder = os.path.join(game, "Tape to Tape_Data", "StreamingAssets", "aa", "StandaloneWindows64")
    if not os.path.isdir(folder):
        print(f"No game bundles at {folder}: pass the Tape to Tape folder (the one with 'Tape to Tape.exe').")
        return 2
    default = os.path.join(HERE, "..", "..", "src", "T2TOnline.Plugin", "Hooks", "ui_parts.txt")
    sources, parts = parse(argv[2] if len(argv) > 2 else default)
    bad = 0
    opened = {}
    for name, (bundle, root_name) in sources.items():
        files = [f for f in os.listdir(folder) if f.startswith(bundle + "_") and f.endswith(".bundle")]
        if len(files) != 1:
            print(f"MISSING source {name}: {len(files)} bundles start with {bundle}_")
            bad += 1
            continue
        prefabs = bp.Prefabs(os.path.join(folder, files[0]))
        roots = [g for g, t in prefabs.tr.items() if prefabs.sf.read(t)["m_Father"]["m_PathID"] == 0 and prefabs.names[g] == root_name]
        if len(roots) != 1:
            print(f"MISSING source {name}: {len(roots)} roots named {root_name} in {files[0]}")
            bad += 1
            continue
        opened[name] = (prefabs, roots[0])
        print(f"ok source {name}: {root_name} in {files[0]}")
    for name, path, wanted in parts:
        if name not in opened:
            continue
        prefabs, root = opened[name]
        go = find(prefabs, root, path)
        if go is None:
            print(f"MISSING part {name}/{path}")
            bad += 1
            continue
        lacking = [c for c in wanted if c not in components_of(prefabs, go)]
        if lacking:
            print(f"MISSING script on {name}/{path}: {', '.join(lacking)}")
            bad += 1
        else:
            print(f"ok {name}/{path}")
    print(f"{len(parts)} parts, {len(sources)} sources, {bad} missing")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
