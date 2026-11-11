#!/usr/bin/env python3
"""Release checks for T2T Online's two zips and the assemblies in them.

Usage:
  zipcheck.py zip --zip <file> --variant public|friends --version <v> --commit <sha> --commit-time <UTC time>
                  --repo <root> --game-dir <folder> --ml-zip <file> [--public-zip <file>] [--previous-zip <file>]
  zipcheck.py built --dir <folder with the three DLLs and PDBs> --repo <root> --game-dir <folder> [--commit <sha>]
  zipcheck.py template <READ ME template>
  zipcheck.py melonpin --repo <root>
  zipcheck.py selftest

'zip' reads a written zip back (never the staging folder) and fails on any miss:
  entries      every name is plain ASCII with '/' separators and unique ignoring case; the public zip holds exactly the
               allowed entries (the D124 layout), the friends' zip those plus exactly the MelonLoader zip's entries, byte
               for byte; every entry carries the release commit's UTC time, fixed attributes, in sorted order
  size         a budget per zip, and at most 25% over the previous release's public zip
  documents    the READ ME (ASCII, CRLF, no '{{' left, the version, no friends'-zip text in the public variant), the
               manifest (name, version, description only, no address) and the licence texts equal the repository's
  assemblies   what 'built' checks, on the DLLs and PDBs read back from the zip
  install      the zip extracted over MelonLoader's own files (an empty game folder): MelonLoader 0.7.3's folder rules
               (ported from its code) load the plugin in the Plugins phase and Core and Steam in the
               UserLibs phase from Mods/T2TOnline, nothing in the Mods phase, nothing of ours collides with MelonLoader's
               files; the friends' zip gives the same tree
  update       the previous release's zip extracted, then this one over it: the new copies win every phase, and the files
               left behind are exactly the previous layout's (the ones the mod's install check names)
  melonloader  the MelonLoader the zip carries is the version the plugin is tested with
'built' checks the three assemblies and their PDBs in one folder: the version equals VersionPrefix-VersionSuffix, the
references resolve against each other, MelonLoader's, the game's interop assemblies and the .NET 6 framework, the PDB
holds only '/_/' source paths, no Source Link and no embedded source beyond the generated obj files, and the PDB's id is
the DLL's; MelonOptionalDependencies, once the plugin declares it (its sources or its project file), names every interop
assembly the plugin references and nothing that isn't an interop assembly.
'template' checks the READ ME template's markers; 'melonpin' checks the pinned MelonLoader against TestedMelonLoader.
Assembly facts come from tools/checks/asmcheck (metadata only). Prints 'ok' or 'FAIL' per check; exit 1 on any FAIL.
READ ME template markers (each on a line of its own, never nested): {{#FRIENDS}}...{{/FRIENDS}} (kept in the friends'
zip only), {{#PUBLIC}}...{{/PUBLIC}} (kept in the public zip only); {{VERSION}} anywhere is the version as a player reads it.
"""
import argparse
import glob
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime

README_NAME = 'T2T Online - READ ME FIRST.txt'
MOD_DIR = 'Mods/T2TOnline'
OWN_ENTRIES = (
    f'{MOD_DIR}/LICENSE.txt',
    f'{MOD_DIR}/Plugins/T2TOnline.dll',
    f'{MOD_DIR}/Plugins/T2TOnline.pdb',
    f'{MOD_DIR}/THIRD-PARTY-NOTICES.txt',
    f'{MOD_DIR}/UserLibs/T2TOnline.Core.dll',
    f'{MOD_DIR}/UserLibs/T2TOnline.Core.pdb',
    f'{MOD_DIR}/UserLibs/T2TOnline.Steam.dll',
    f'{MOD_DIR}/UserLibs/T2TOnline.Steam.pdb',
    f'{MOD_DIR}/manifest.json',
    README_NAME,
)
# What a release in the earlier flat layout (0.10.0) leaves behind when the new zip is extracted over it: the mod's
# install check names these.
LEGACY_FLAT_LEFTOVERS = (
    'LICENSE',
    'Plugins/T2TOnline.dll',
    'Plugins/T2TOnline.pdb',
    'THIRD_PARTY_NOTICES.md',
    'UserLibs/T2TOnline.Core.dll',
    'UserLibs/T2TOnline.Core.pdb',
    'UserLibs/T2TOnline.Steam.dll',
    'UserLibs/T2TOnline.Steam.pdb',
)
# The attributes tools/package/write_zip.py gives every entry: made by a Unix system, a regular file with mode 644, a
# directory with mode 755. (Python's zipfile can't write "no attributes": it replaces zero with its own default.)
ZIP_CREATE_SYSTEM = 3
FILE_ATTR = 0o100644 << 16
DIR_ATTR = (0o40755 << 16) | 0x10
PUBLIC_MAX_BYTES = 4 * 1024 * 1024
FRIENDS_MAX_BYTES = 28 * 1024 * 1024
GROWTH_LIMIT = 1.25
FRIENDS_ONLY_TEXT = 'with-melonloader'
ROLES = ('plugin', 'core', 'steam')
ROLE_FILES = {'plugin': 'T2TOnline.dll', 'core': 'T2TOnline.Core.dll', 'steam': 'T2TOnline.Steam.dll'}
ROLE_ASSEMBLY = {'plugin': 'T2TOnline', 'core': 'T2TOnline.Core', 'steam': 'T2TOnline.Steam'}
HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_REPO = os.path.abspath(os.path.join(HERE, '..', '..'))


class Report:
    """Collects one line per check; a FAIL makes the run fail."""

    def __init__(self):
        self.lines = []
        self.failed = 0
        self.checks = 0

    def ok(self, name, detail=''):
        self.checks += 1
        self.lines.append(f'ok   {name}' + (f': {detail}' if detail else ''))

    def fail(self, name, detail):
        self.checks += 1
        self.failed += 1
        self.lines.append(f'FAIL {name}: {detail}')

    def note(self, name, detail):
        self.lines.append(f'note {name}: {detail}')

    def check(self, name, problems, detail=''):
        """ok when `problems` is empty, else one FAIL listing (up to 8 of) them."""
        if problems:
            more = f' (+{len(problems) - 8} more)' if len(problems) > 8 else ''
            self.fail(name, '; '.join(problems[:8]) + more)
        else:
            self.ok(name, detail)

    def print_all(self):
        for line in self.lines:
            print(line)
        print(f'zipcheck: {self.checks} checks, {self.failed} failed')


# ---------------------------------------------------------------------------------------------------------------------
# Small helpers


def version_shown(version):
    """The release as a player reads it (ReleaseVersion.ForScreen): '0.11.0-beta' is '0.11.0 Beta'."""
    return re.sub(r'-beta$', ' Beta', version)


def version_tuple(text):
    return tuple(int(p) for p in re.findall(r'\d+', text)[:4])


def expected_zip_time(commit_time):
    """The DOS time a zip entry can hold for the commit's UTC time: even seconds, rounded down."""
    t = datetime.strptime(commit_time, '%Y-%m-%dT%H:%M:%SZ')
    return (t.year, t.month, t.day, t.hour, t.minute, t.second - t.second % 2)


def read_version(repo):
    """VersionPrefix and VersionSuffix of Directory.Build.props, as '0.11.0-beta'."""
    root = ET.parse(os.path.join(repo, 'Directory.Build.props')).getroot()
    prefix = suffix = None
    for group in root.iter('PropertyGroup'):
        for element in group:
            if element.tag == 'VersionPrefix' and prefix is None:
                prefix = (element.text or '').strip()
            if element.tag == 'VersionSuffix' and suffix is None:
                suffix = (element.text or '').strip()
    return f'{prefix}-{suffix}' if suffix else prefix


def read_pin(repo):
    pin = {}
    with open(os.path.join(repo, 'tools', 'package', 'melonloader.pin'), encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith('#') and '=' in line:
                key, value = line.split('=', 1)
                pin[key.strip()] = value.strip()
    return pin


def find_tested_melonloader(repo):
    """The version the plugin is tested with: the one `TestedMelonLoader = "x.y.z"` in the plugin's sources."""
    found = set()
    for path in glob.glob(os.path.join(repo, 'src', 'T2TOnline.Plugin', '**', '*.cs'), recursive=True):
        with open(path, encoding='utf-8') as f:
            for match in re.finditer(r'TestedMelonLoader\s*=\s*"([^"]+)"', f.read()):
                found.add(match.group(1))
    return found


def plugin_declares_optional_dependencies(repo):
    """True once the plugin declares MelonOptionalDependencies: in its sources, or written by its project file at build
    time (T2TOnline.Plugin.csproj, target WriteOptionalDependencies). Build outputs (bin, obj) don't count, so the answer
    is the same before and after a build."""
    plugin = os.path.join(repo, 'src', 'T2TOnline.Plugin')
    for path in glob.glob(os.path.join(plugin, '*.csproj')):
        with open(path, encoding='utf-8') as f:
            if re.search(r'MelonOptionalDependencies\(', f.read()):
                return True
    for path in glob.glob(os.path.join(plugin, '**', '*.cs'), recursive=True):
        if {'bin', 'obj'} & set(os.path.relpath(path, plugin).split(os.sep)[:-1]):
            continue
        with open(path, encoding='utf-8') as f:
            if re.search(r'^\s*\[assembly:\s*(MelonLoader\.)?MelonOptionalDependencies', f.read(), re.MULTILINE):
                return True
    return False


def find_framework_dir():
    """The newest .NET 6 shared framework folder, where the references of a net6.0 assembly resolve."""
    roots = [os.environ.get('DOTNET_ROOT'), os.path.dirname(shutil.which('dotnet') or ''), r'C:\Program Files\dotnet']
    best = None
    for root in roots:
        if not root:
            continue
        for path in glob.glob(os.path.join(root, 'shared', 'Microsoft.NETCore.App', '6.0.*')):
            if best is None or version_tuple(os.path.basename(path)) > version_tuple(os.path.basename(best)):
                best = path
    return best


def run_facts(repo, paths, light=False):
    """Facts of DLLs and PDBs from tools/checks/asmcheck, keyed by normalised path."""
    project = os.path.join(repo, 'tools', 'checks', 'asmcheck')
    command = ['dotnet', 'run', '--project', project, '-c', 'Release', '--no-launch-profile', '--']
    if light:
        command.append('--light')
    facts = {}
    for start in range(0, len(paths), 60):
        chunk = [os.path.normpath(p) for p in paths[start:start + 60]]
        proc = subprocess.run(command + chunk, capture_output=True, text=True, encoding='utf-8', check=False)
        objects = []
        for line in proc.stdout.splitlines():
            if line.startswith('{'):
                objects.append(json.loads(line))
        if len(objects) != len(chunk):
            raise RuntimeError(f'asmcheck read {len(objects)} of {len(chunk)} files (exit {proc.returncode}): {proc.stdout[-400:]}{proc.stderr[-400:]}')
        for path, fact in zip(chunk, objects):
            facts[path] = fact
    return facts


# ---------------------------------------------------------------------------------------------------------------------
# Zip entries, times, sizes


def check_entries(rep, zf, variant, commit_time, ml_zf=None, public_zf=None):
    """Names, the allow-list, the times, the attributes and the order of every entry."""
    infos = zf.infolist()
    names = [i.filename for i in infos]
    problems = []
    seen = {}
    for name in names:
        low = name.lower()
        if low in seen:
            problems.append(f'duplicate (ignoring case): {name}')
        seen[low] = True
        if '\\' in name or name.startswith('/') or ':' in name or '//' in name or any(p == '..' for p in name.split('/')):
            problems.append(f'unsafe name: {name}')
        if not name.isascii() or any(ord(c) < 32 or ord(c) == 127 for c in name):
            problems.append(f'name is not plain ASCII: {name!r}')
        if low.startswith('userdata/'):
            problems.append(f'a zip never carries UserData: {name}')
    own = set(OWN_ENTRIES)
    if variant == 'public':
        extra = sorted(set(names) - own)
        missing = sorted(own - set(names))
        problems += [f'not allowed: {n}' for n in extra] + [f'missing: {n}' for n in missing]
    else:
        missing = sorted(own - set(names))
        problems += [f'missing: {n}' for n in missing]
        if ml_zf is None:
            problems.append('no MelonLoader zip to compare the friends zip with')
        else:
            ml_infos = {i.filename: i for i in ml_zf.infolist()}
            rest = set(names) - own
            problems += [f'not in MelonLoader\'s zip nor ours: {n}' for n in sorted(rest - set(ml_infos))]
            problems += [f'MelonLoader entry missing: {n}' for n in sorted(set(ml_infos) - set(names))]
            for n in sorted(rest & set(ml_infos)):
                mine = zf.getinfo(n)
                theirs = ml_infos[n]
                if mine.CRC != theirs.CRC or mine.file_size != theirs.file_size:
                    problems.append(f'MelonLoader file differs from the official zip: {n}')
        if public_zf is not None:
            for n in OWN_ENTRIES:
                # The READ ME is the one file that differs by variant (the friends' blocks).
                if n != README_NAME and n in names and n in set(public_zf.namelist()):
                    if zf.getinfo(n).CRC != public_zf.getinfo(n).CRC:
                        problems.append(f'differs from the public zip: {n}')
    rep.check('entries', problems, f'{len(names)} entries' + (', exactly the allow-list' if variant == 'public' else ", ours plus exactly MelonLoader's"))

    want_time = expected_zip_time(commit_time)
    bad_time, bad_attr = [], []
    for i in infos:
        if i.date_time != want_time:
            bad_time.append(f'{i.filename} {i.date_time}')
        is_dir = i.filename.endswith('/')
        kind_ok = (
            i.create_system == ZIP_CREATE_SYSTEM and not i.extra and not i.comment and i.flag_bits & ~0x06 == 0
            and ((is_dir and i.compress_type == zipfile.ZIP_STORED and i.external_attr == DIR_ATTR and i.file_size == 0)
                 or (not is_dir and i.compress_type == zipfile.ZIP_DEFLATED and i.external_attr == FILE_ATTR))
        )
        if not kind_ok:
            bad_attr.append(i.filename)
    rep.check('times', [f'not {want_time}: {b}' for b in bad_time], f'every entry is {want_time[0]:04}-{want_time[1]:02}-{want_time[2]:02} {want_time[3]:02}:{want_time[4]:02}:{want_time[5]:02} (the commit, UTC)')
    rep.check('attributes', [f'unexpected attributes, extra field or comment: {b}' for b in bad_attr], 'one fixed set on every entry, no extra fields, no zip comment')
    problems = []
    if zf.comment:
        problems.append('the zip has a comment')
    if names != sorted(names):
        first = next(n for n, s in zip(names, sorted(names)) if n != s)
        problems.append(f'entries are not in sorted order (first out of place: {first})')
    rep.check('order', problems, 'sorted by name')
    bad = zf.testzip()
    rep.check('crc', [f'bad CRC: {bad}'] if bad else [], 'every entry reads back')


def check_size(rep, zip_path, variant, previous_zip=None):
    size = os.path.getsize(zip_path)
    limit = PUBLIC_MAX_BYTES if variant == 'public' else FRIENDS_MAX_BYTES
    problems = [f'{size} bytes is over the budget of {limit}'] if size > limit else []
    detail = f'{size:,} bytes, budget {limit:,}'
    if previous_zip and variant == 'public':
        before = os.path.getsize(previous_zip)
        if size > before * GROWTH_LIMIT:
            problems.append(f'{size} bytes is more than {int((GROWTH_LIMIT - 1) * 100)}% over the previous release ({before} bytes)')
        detail += f'; previous release {before:,}'
    rep.check('size', problems, detail)


# ---------------------------------------------------------------------------------------------------------------------
# The documents


def check_readme(rep, data, version, variant):
    """The READ ME as written to the zip: ASCII, CRLF throughout, no placeholder left, the version, and no text for the
    friends' zip in the public one."""
    problems = []
    if data.startswith(b'\xef\xbb\xbf'):
        problems.append('starts with a byte order mark')
    if any(b >= 0x80 for b in data):
        problems.append('not ASCII')
    if data.count(b'\n') != data.count(b'\r\n') or re.search(rb'\r(?!\n)', data):
        problems.append('not CRLF line endings throughout')
    text = data.decode('latin-1')
    if '{{' in text or '}}' in text:
        problems.append("a '{{' or '}}' placeholder is left")
    first = next((line for line in text.splitlines() if line.strip()), '')
    if f'T2T Online {version_shown(version)}' not in first:
        problems.append(f'the first line does not name T2T Online {version_shown(version)}')
    if variant == 'public' and FRIENDS_ONLY_TEXT in text.lower():
        problems.append("the public READ ME mentions the friends' zip")
    rep.check('readme', problems, f'ASCII, CRLF, {version_shown(version)}' + (", no friends'-zip text" if variant == 'public' else ''))


def check_manifest(rep, data, version):
    problems = []
    try:
        manifest = json.loads(data.decode('utf-8'))
    except ValueError:
        rep.fail('manifest', 'not valid JSON')
        return
    if not isinstance(manifest, dict) or set(manifest) != {'name', 'version_number', 'description'}:
        problems.append('keys are not exactly name, version_number and description')
    else:
        if manifest['name'] != 'T2T Online':
            problems.append('name is not "T2T Online"')
        if manifest['version_number'] != version:
            problems.append(f'version_number is not {version}')
        description = manifest['description']
        if not isinstance(description, str) or not description or '\n' in description:
            problems.append('description is not one line')
    if re.search(rb'://|@|www\.', data):
        problems.append('holds an address')
    if any(b >= 0x80 for b in data):
        problems.append('not ASCII')
    rep.check('manifest', problems, f'name, version {version}, a one-line description, no address')


def normal_lines(data):
    return data.replace(b'\r\n', b'\n')


def check_licences(rep, zf, repo):
    problems = []
    for entry, source in ((f'{MOD_DIR}/LICENSE.txt', 'LICENSE'), (f'{MOD_DIR}/THIRD-PARTY-NOTICES.txt', 'THIRD_PARTY_NOTICES.md')):
        with open(os.path.join(repo, source), 'rb') as f:
            wanted = normal_lines(f.read())
        data = zf.read(entry)
        if normal_lines(data) != wanted:
            problems.append(f'{entry} is not the repository\'s {source}')
        if any(b >= 0x80 for b in data) or data.count(b'\n') != data.count(b'\r\n'):
            problems.append(f'{entry} is not ASCII with CRLF line endings')
    rep.check('licences', problems, "the repository's LICENSE and THIRD_PARTY_NOTICES.md, ASCII with CRLF")


# ---------------------------------------------------------------------------------------------------------------------
# Assemblies and PDBs


def judge_dlls(rep, facts, version, commit=None):
    """`facts` maps role -> asmcheck's object for the DLL. Versions, the plugin's MelonInfo and its references to Core
    and Steam."""
    asm_version = '.'.join((version.split('-')[0].split('.') + ['0'] * 4)[:4])
    problems = []
    commits = set()
    for role in ROLES:
        fact = facts[role]
        if not fact.get('managed'):
            problems.append(f'{role} is not a managed assembly')
            continue
        if fact['name'] != ROLE_ASSEMBLY[role]:
            problems.append(f'{role} is named {fact["name"]}')
        if fact['version'] != asm_version:
            problems.append(f'{fact["name"]} assembly version is {fact["version"]}, not {asm_version}')
        info = fact.get('informational') or ''
        release, _, rev = info.partition('+')
        if release != version:
            problems.append(f'{fact["name"]} informational version is {info!r}, not {version}')
        commits.add(rev)
    if len(commits) > 1:
        problems.append(f'the three assemblies were built from different commits: {sorted(commits)}')
    if commit and not all(c and (commit.startswith(c) or c.startswith(commit)) for c in commits):
        problems.append(f'built from {sorted(commits)}, the release commit is {commit[:12]}')
    plugin = facts['plugin']
    melon = plugin.get('melon') if plugin.get('managed') else None
    if not melon:
        problems.append('the plugin has no MelonInfo')
    else:
        if melon.get('version') != version:
            problems.append(f'MelonInfo says version {melon.get("version")}, not {version}')
        if melon.get('name') != 'T2T Online':
            problems.append(f'MelonInfo names {melon.get("name")!r}')
        if melon.get('kind') != 'MelonPlugin':
            problems.append(f'the plugin\'s base class is {melon.get("kind")!r}, not MelonPlugin')
    refs = {r['name']: r['version'] for r in plugin.get('references', [])} if plugin.get('managed') else {}
    for role in ('core', 'steam'):
        name = ROLE_ASSEMBLY[role]
        if refs.get(name) != facts[role].get('version'):
            problems.append(f'the plugin references {name} {refs.get(name)}, the shipped one is {facts[role].get("version")}')
    rep.check('assembly versions', problems, f'the three assemblies are {version} ({asm_version}), one commit, MelonInfo matches')


def judge_pdb(rep, role, pdb, dll):
    """One PDB: every source path starts '/_/', no Source Link, embedded source only for generated obj files, and its id
    is the one its DLL points at."""
    problems = []
    if 'error' in pdb:
        rep.fail(f'pdb {role}', pdb['error'])
        return
    docs = pdb.get('documents', [])
    if not docs:
        problems.append('no documents')
    bad = [d for d in docs if not d.startswith('/_/') or '\\' in d or ':' in d]
    problems += [f'source path outside /_/: {d}' for d in bad[:3]]
    if pdb.get('customDebugInfo', {}).get('SourceLink'):
        problems.append('holds a Source Link map')
    stray = [d for d in pdb.get('embeddedSourceDocuments', []) if '/obj/' not in d]
    problems += [f'embedded source that is not a generated obj file: {d}' for d in stray[:3]]
    codeview = dll.get('codeview') if dll.get('managed') else None
    if not codeview:
        problems.append('the DLL has no CodeView entry')
    else:
        if not codeview['path'].startswith('/_/') or ':' in codeview['path']:
            problems.append(f'the DLL names a PDB path outside /_/: {codeview["path"]}')
        if codeview['guid'] != pdb['id']['guid'] or codeview['stamp'] != pdb['id']['stamp']:
            problems.append("the PDB is not the DLL's own (ids differ)")
    rep.check(f'pdb {role}', problems, f'{len(docs)} source paths under /_/, no Source Link, embedded source only for obj files, id matches the DLL')


def judge_references(rep, role_facts, resolve):
    """Every reference of the three assemblies: Core and Steam exactly, the rest in a folder where MelonLoader or the
    runtime finds it, at a version not lower than the one asked for. resolve(name) -> version text or None."""
    own = {ROLE_ASSEMBLY[r]: role_facts[r]['version'] for r in ROLES}
    problems = []
    count = 0
    for role in ROLES:
        for ref in role_facts[role].get('references', []):
            count += 1
            if ref['name'] in own:
                if ref['version'] != own[ref['name']]:
                    problems.append(f'{ROLE_ASSEMBLY[role]} wants {ref["name"]} {ref["version"]}, shipped {own[ref["name"]]}')
                continue
            found = resolve(ref['name'])
            if found is None:
                problems.append(f'{ROLE_ASSEMBLY[role]} references {ref["name"]}, found nowhere MelonLoader looks')
            elif version_tuple(found) < version_tuple(ref['version']):
                problems.append(f'{ROLE_ASSEMBLY[role]} wants {ref["name"]} {ref["version"]}, only {found} is there')
    rep.check('references', problems, f'{count} references of the three assemblies resolve (each other, MelonLoader, the interop assemblies, the .NET 6 framework)')


def judge_optional(rep, plugin, interop_names, required):
    """MelonOptionalDependencies lists every interop assembly the plugin references, and names nothing that isn't an
    interop assembly. The build generates the list from all of the project's interop references, a superset of what the
    compiler keeps, so a new use of the game's code is covered before anyone thinks of it. Required once the sources or
    the project file declare it (plugin_declares_optional_dependencies); before that only noted."""
    declared = plugin.get('optionalDependencies')
    interop = set(interop_names)
    wanted = sorted({r['name'] for r in plugin.get('references', [])} & interop)
    if declared is None:
        if required:
            rep.fail('optional dependencies', 'the plugin declares MelonOptionalDependencies but the built plugin has none')
        else:
            rep.note('optional dependencies', f'not declared yet ({len(wanted)} interop references would be listed)')
        return
    declared_sorted = sorted(declared)
    missing = sorted(set(wanted) - set(declared_sorted))
    foreign = sorted(set(declared_sorted) - interop)
    problems = []
    if missing:
        problems.append('interop references not listed: ' + ', '.join(missing[:6]))
    if foreign:
        problems.append('listed but not an interop assembly: ' + ', '.join(foreign[:6]))
    if len(declared_sorted) != len(set(declared_sorted)):
        problems.append('a name is listed twice')
    rep.check('optional dependencies', problems,
              f"MelonOptionalDependencies lists all {len(wanted)} of the plugin's interop references ({len(declared_sorted)} interop assemblies in all)")


def interop_names_of(interop_dir):
    return {os.path.splitext(f)[0] for f in os.listdir(interop_dir) if f.lower().endswith('.dll')}


def check_assemblies(rep, repo, folder_of, version, commit, game_dir, ml_dir):
    """The three DLLs and their PDBs: folder_of(role) is where each lives (the zip's extracted copies or a build)."""
    interop = os.path.join(game_dir, 'MelonLoader', 'Il2CppAssemblies')
    framework = find_framework_dir()
    if not os.path.isdir(interop):
        rep.fail('assemblies', f'no interop assemblies in the game folder ({interop}): the references cannot be resolved')
        return
    dlls = {r: os.path.join(folder_of(r), ROLE_FILES[r]) for r in ROLES}
    pdbs = {r: os.path.splitext(p)[0] + '.pdb' for r, p in dlls.items()}
    missing = [p for p in list(dlls.values()) + list(pdbs.values()) if not os.path.isfile(p)]
    if missing:
        rep.fail('assemblies', 'missing: ' + ', '.join(os.path.basename(m) for m in missing))
        return
    facts = run_facts(repo, list(dlls.values()) + list(pdbs.values()))
    role_facts = {r: facts[os.path.normpath(dlls[r])] for r in ROLES}
    judge_dlls(rep, role_facts, version, commit)
    for r in ROLES:
        judge_pdb(rep, r, facts[os.path.normpath(pdbs[r])], role_facts[r])

    search = [d for d in (ml_dir, interop, framework) if d]
    if framework is None:
        rep.note('references', 'no .NET 6 shared framework found: framework references are not resolved')
    needed = {ref['name'] for r in ROLES for ref in role_facts[r].get('references', [])} - {ROLE_ASSEMBLY[r] for r in ROLES}
    paths = {}
    for name in sorted(needed):
        for d in search:
            candidate = os.path.join(d, name + '.dll')
            if os.path.isfile(candidate):
                paths[name] = candidate
                break
    light = run_facts(repo, list(paths.values()), light=True) if paths else {}
    resolved = {n: light[os.path.normpath(p)]['version'] for n, p in paths.items() if light[os.path.normpath(p)].get('managed')}
    judge_references(rep, role_facts, lambda n: resolved.get(n))
    judge_optional(rep, role_facts['plugin'], interop_names_of(interop), plugin_declares_optional_dependencies(repo))


# ---------------------------------------------------------------------------------------------------------------------
# MelonLoader 0.7.3's folder rules (a port of MelonFolderHandler and MelonPreprocessor, read from its code)

USERLIBS, PLUGINS, MODS = 0, 1, 2
PHASE_NAMES = {USERLIBS: 'UserLibs', PLUGINS: 'Plugins', MODS: 'Mods'}
EXCLUDED_NAMES = ('Broken', 'Retired', 'Disabled')


def _excluded(name):
    return name.startswith('~') or name.startswith('.') or name in EXCLUDED_NAMES


def _add_folder(phase, path, name, dirs):
    if name == 'UserLibs':
        phase = USERLIBS
    elif name == 'Plugins' and phase >= PLUGINS:
        phase = PLUGINS
    elif name == 'Mods' and phase >= MODS:
        phase = MODS
    dirs[phase].append(path)
    _find_sub(phase, path, False, dirs)


def _find_sub(phase, path, require_manifest, dirs):
    if not os.path.isdir(path):
        return
    for entry in sorted(os.listdir(path)):
        full = os.path.join(path, entry)
        if not os.path.isdir(full) or _excluded(entry):
            continue
        if require_manifest and not os.path.isfile(os.path.join(full, 'manifest.json')):
            continue
        _add_folder(phase, full, entry, dirs)


def scan_game_folder(root):
    """The folders MelonLoader scans in each phase, in its order."""
    dirs = {USERLIBS: [], PLUGINS: [], MODS: []}
    for phase in (USERLIBS, PLUGINS, MODS):
        dirs[phase].append(os.path.join(root, PHASE_NAMES[phase]))
    for phase in (USERLIBS, PLUGINS, MODS):
        _find_sub(phase, os.path.join(root, PHASE_NAMES[phase]), True, dirs)
    return dirs


def dlls_in_scanned_folders(root):
    dirs = scan_game_folder(root)
    found = []
    for phase in dirs:
        for d in dirs[phase]:
            if os.path.isdir(d):
                found += [os.path.join(d, f) for f in sorted(os.listdir(d)) if f.lower().endswith('.dll') and os.path.isfile(os.path.join(d, f))]
    return sorted(set(os.path.normpath(p) for p in found))


def simulate_phases(root, facts):
    """What MelonLoader loads in each phase from this game folder: per assembly name the highest version (a tie keeps the
    first found); the Plugins and Mods phases only take DLLs with MelonInfo; the UserLibs phase every managed DLL.
    `facts` maps normalised DLL paths to asmcheck's objects. Returns {phase: {name: (version, path, fact)}}."""
    dirs = scan_game_folder(root)
    loads = {}
    for phase in (USERLIBS, PLUGINS, MODS):
        found = {}
        for d in dirs[phase]:
            if not os.path.isdir(d):
                continue
            for f in sorted(os.listdir(d)):
                full = os.path.normpath(os.path.join(d, f))
                if not f.lower().endswith('.dll') or full not in facts:
                    continue
                fact = facts[full]
                if not fact.get('managed'):
                    continue
                if phase != USERLIBS and not fact.get('melon'):
                    continue
                version = version_tuple(fact['version'])
                if fact['name'] not in found or not found[fact['name']][0] >= version:
                    found[fact['name']] = (version, full, fact)
        loads[phase] = found
    return loads


def rel(root, path):
    return os.path.relpath(path, root).replace(os.sep, '/')


def judge_install(rep, root, loads, version, ml_names, title='install'):
    """The tree after extracting the zip (over MelonLoader's files): where each of our DLLs is loaded from."""
    problems = []
    want_libs = {'T2TOnline.Core', 'T2TOnline.Steam'}
    libs = {n: v for n, v in loads[USERLIBS].items() if n.startswith('T2TOnline')}
    if set(libs) != want_libs:
        problems.append(f'UserLibs phase loads {sorted(libs)}, expected {sorted(want_libs)}')
    for name, (_, path, _) in libs.items():
        if not rel(root, path).startswith(f'{MOD_DIR}/UserLibs/'):
            problems.append(f'{name} loads from {rel(root, path)}')
    plugins = loads[PLUGINS]
    if set(plugins) != {'T2TOnline'}:
        problems.append(f'Plugins phase loads {sorted(plugins)}, expected only T2TOnline')
    for name, (v, path, fact) in plugins.items():
        if not rel(root, path).startswith(f'{MOD_DIR}/Plugins/'):
            problems.append(f'{name} loads from {rel(root, path)}')
        if fact['melon']['kind'] != 'MelonPlugin':
            problems.append(f'{name} is not a MelonPlugin')
    if loads[MODS]:
        problems.append(f'Mods phase loads {sorted(loads[MODS])}: a plugin there is never run')
    collide = sorted(n for n in ml_names if n in {os.path.basename(p).lower() for p in OWN_ENTRIES})
    if collide:
        problems.append(f'file names of ours that MelonLoader\'s zip also has: {collide}')
    rep.check(title, problems, f'plugin from {MOD_DIR}/Plugins in the Plugins phase, Core and Steam from {MOD_DIR}/UserLibs in the UserLibs phase, nothing in the Mods phase, no collision with MelonLoader\'s files')


def judge_update(rep, root, loads, new_names, old_names):
    """The tree after the previous release's zip and then this one are extracted: the new copies win every phase, and
    the files left are exactly the previous layout's."""
    problems = []
    for name in ('T2TOnline.Core', 'T2TOnline.Steam'):
        entry = loads[USERLIBS].get(name)
        if not entry or not rel(root, entry[1]).startswith(f'{MOD_DIR}/UserLibs/'):
            problems.append(f'{name} (UserLibs phase) comes from {rel(root, entry[1]) if entry else "nowhere"}, not the new copy')
    entry = loads[PLUGINS].get('T2TOnline')
    if not entry or not rel(root, entry[1]).startswith(f'{MOD_DIR}/Plugins/'):
        problems.append(f'T2TOnline (Plugins phase) comes from {rel(root, entry[1]) if entry else "nowhere"}, not the new copy')
    if loads[MODS]:
        problems.append(f'Mods phase loads {sorted(loads[MODS])}')
    rep.check('update: new copy wins', problems, 'after extracting the new zip over the previous one, the new Core, Steam and plugin load in every phase')
    leftovers = sorted(n for n in old_names if n.lower() not in {x.lower() for x in new_names} and not n.endswith('/'))
    flat = any(n.lower() == 'plugins/t2tonline.dll' for n in old_names)
    wanted = sorted(LEGACY_FLAT_LEFTOVERS) if flat else []
    problems = []
    if leftovers != wanted:
        problems.append(f'left behind: {leftovers}; the install check expects: {wanted}')
    rep.check('update: leftovers', problems, f'{len(leftovers)} files stay from the previous layout (the mod\'s install check names them): ' + ', '.join(leftovers) if leftovers else 'none')


def extract(zip_path, target):
    """Extracts like Windows' Extract All (refusing unsafe names)."""
    with zipfile.ZipFile(zip_path) as z:
        for info in z.infolist():
            name = info.filename
            if name.startswith('/') or ':' in name or '\\' in name or any(p == '..' for p in name.split('/')):
                raise ValueError(f'unsafe entry {name}')
        z.extractall(target)


def tree_hashes(root):
    result = {}
    for folder, _, files in os.walk(root):
        for f in files:
            full = os.path.join(folder, f)
            with open(full, 'rb') as fh:
                result[rel(root, full)] = hashlib.sha256(fh.read()).hexdigest()
    return result


# ---------------------------------------------------------------------------------------------------------------------
# Subcommands


def cmd_zip(args):
    rep = Report()
    repo = os.path.abspath(args.repo)
    version = args.version
    commit_time = args.commit_time
    with zipfile.ZipFile(args.zip) as zf, zipfile.ZipFile(args.ml_zip) as ml_zf:
        public_zf = zipfile.ZipFile(args.public_zip) if args.public_zip else None
        try:
            check_entries(rep, zf, args.variant, commit_time, ml_zf, public_zf)
            check_size(rep, args.zip, args.variant, args.previous_zip)
            if all(n in zf.namelist() for n in OWN_ENTRIES):
                check_readme(rep, zf.read(README_NAME), version, args.variant)
                check_manifest(rep, zf.read(f'{MOD_DIR}/manifest.json'), version)
                check_licences(rep, zf, repo)
        finally:
            if public_zf:
                public_zf.close()
        ml_names = {os.path.basename(n).lower() for n in ml_zf.namelist() if n.lower().endswith('.dll')}
    if rep.failed:
        rep.note('stopped', 'the assembly, install and update checks need an intact zip')
        rep.print_all()
        return 1
    with tempfile.TemporaryDirectory(prefix='t2t-zipcheck-') as tmp:
        tree = os.path.join(tmp, 'game')
        os.makedirs(tree)
        if args.variant == 'public':
            extract(args.ml_zip, tree)
        extract(args.zip, tree)
        if args.variant == 'friends' and args.public_zip:
            other = os.path.join(tmp, 'game2')
            os.makedirs(other)
            extract(args.ml_zip, other)
            extract(args.public_zip, other)
            a, b = tree_hashes(tree), tree_hashes(other)
            a.pop(README_NAME, None)
            b.pop(README_NAME, None)
            diff = sorted(set(a) ^ set(b)) + sorted(k for k in set(a) & set(b) if a[k] != b[k])
            rep.check('same tree', [f'differs: {d}' for d in diff], "the friends' zip extracted equals MelonLoader's zip plus the public zip (the READ ME apart: its variant)")
            shutil.rmtree(other)
        mod = os.path.join(tree, *MOD_DIR.split('/'))
        folder_of = lambda role: os.path.join(mod, 'Plugins' if role == 'plugin' else 'UserLibs')
        ml_dir = os.path.join(tree, 'MelonLoader', 'net6')
        check_assemblies(rep, repo, folder_of, version, args.commit, os.path.abspath(args.game_dir), ml_dir)

        facts = run_facts(repo, dlls_in_scanned_folders(tree))
        loads = simulate_phases(tree, facts)
        judge_install(rep, tree, loads, version, ml_names)

        ml_dll = os.path.join(ml_dir, 'MelonLoader.dll')
        tested = find_tested_melonloader(repo)
        pin = read_pin(repo)
        if os.path.isfile(ml_dll):
            ml_version = run_facts(repo, [ml_dll], light=True)[os.path.normpath(ml_dll)]['version']
            major_minor_build = '.'.join(ml_version.split('.')[:3])
            problems = []
            if tested != {major_minor_build}:
                problems.append(f'the zip carries MelonLoader {ml_version}, the plugin is tested with {sorted(tested)}')
            if pin.get('version') != major_minor_build:
                problems.append(f'melonloader.pin says {pin.get("version")}, the zip carries {ml_version}')
            rep.check('melonloader', problems, f'MelonLoader {ml_version} is the version the plugin is tested with')
        else:
            rep.fail('melonloader', 'MelonLoader/net6/MelonLoader.dll is not in the install')

        if args.previous_zip and args.variant == 'public':
            upd = os.path.join(tmp, 'update')
            os.makedirs(upd)
            extract(args.previous_zip, upd)
            with zipfile.ZipFile(args.previous_zip) as old, zipfile.ZipFile(args.zip) as new:
                old_names, new_names = old.namelist(), new.namelist()
            extract(args.zip, upd)
            facts = run_facts(repo, dlls_in_scanned_folders(upd))
            judge_update(rep, upd, simulate_phases(upd, facts), new_names, old_names)
        elif args.variant == 'public':
            rep.note('update', 'no previous zip given: the update simulation was not run')
    rep.print_all()
    return 1 if rep.failed else 0


def cmd_built(args):
    rep = Report()
    repo = os.path.abspath(args.repo)
    game_dir = os.path.abspath(args.game_dir)
    folder = os.path.abspath(args.dir)
    check_assemblies(rep, repo, lambda role: folder, read_version(repo), args.commit, game_dir, os.path.join(game_dir, 'MelonLoader', 'net6'))
    rep.print_all()
    return 1 if rep.failed else 0


TEMPLATE_MARKER = re.compile(r'^\s*\{\{([#/])(FRIENDS|PUBLIC)\}\}\s*$')


def judge_template(rep, data):
    problems = []
    text = data.decode('utf-8-sig', errors='replace')
    if any(b >= 0x80 for b in data.replace(b'\xef\xbb\xbf', b'', 1)):
        problems.append('the template is not ASCII')
    if '{{VERSION}}' not in text:
        problems.append('no {{VERSION}} placeholder')
    open_block = None
    public_lines = []
    skip = False
    for number, line in enumerate(text.splitlines(), 1):
        marker = TEMPLATE_MARKER.match(line)
        if marker:
            kind, name = marker.groups()
            if kind == '#':
                if open_block:
                    problems.append(f'line {number}: {{{{#{name}}}}} inside {{{{#{open_block}}}}}')
                open_block = name
                skip = name == 'FRIENDS'
            else:
                if open_block != name:
                    problems.append(f'line {number}: {{{{/{name}}}}} without its opening marker')
                open_block = None
                skip = False
            continue
        if re.search(r'\{\{[#/]', line):
            problems.append(f'line {number}: a marker must be alone on its line')
        if not skip:
            public_lines.append(line)
    if open_block:
        problems.append(f'{{{{#{open_block}}}}} is never closed')
    unknown = set(re.findall(r'\{\{([^}]*)\}\}', text)) - {'VERSION', '#FRIENDS', '/FRIENDS', '#PUBLIC', '/PUBLIC'}
    problems += [f'unknown placeholder {{{{{u}}}}}' for u in sorted(unknown)]
    if FRIENDS_ONLY_TEXT in '\n'.join(public_lines).lower():
        problems.append("the public variant would mention the friends' zip (wrap that text in {{#FRIENDS}})")
    rep.check('readme template', problems, "markers balanced, {{VERSION}} present, ASCII, no friends'-zip text in the public variant")


def cmd_template(args):
    rep = Report()
    with open(args.path, 'rb') as f:
        judge_template(rep, f.read())
    rep.print_all()
    return 1 if rep.failed else 0


def judge_pin(rep, pin, tested):
    problems = []
    if tested != {pin.get('version')}:
        problems.append(f'melonloader.pin says {pin.get("version")}, the plugin is tested with {sorted(tested)}')
    expected_url = f'https://github.com/LavaGang/MelonLoader/releases/download/v{pin.get("version")}/MelonLoader.x64.zip'
    if pin.get('url') != expected_url:
        problems.append(f'the pinned address is not {expected_url}')
    if not re.fullmatch(r'[0-9a-f]{64}', pin.get('sha256', '')):
        problems.append('the pinned SHA-256 is not 64 lower-case hex digits')
    rep.check('melonloader pin', problems, f'MelonLoader {pin.get("version")} is the version the plugin is tested with (TestedMelonLoader)')


def cmd_melonpin(args):
    rep = Report()
    repo = os.path.abspath(args.repo)
    judge_pin(rep, read_pin(repo), find_tested_melonloader(repo))
    rep.print_all()
    return 1 if rep.failed else 0


# ---------------------------------------------------------------------------------------------------------------------
# Self test: the judgments above against zips, trees and facts built here that break each rule in turn


def _write_zip(path, entries, when=(2026, 1, 2, 3, 4, 4), sort=True, deflate=True):
    names = sorted(entries) if sort else list(entries)
    with zipfile.ZipFile(path, 'w') as z:
        for name in names:
            zi = zipfile.ZipInfo(name, date_time=when)
            zi.create_system = ZIP_CREATE_SYSTEM
            if name.endswith('/'):
                zi.compress_type = zipfile.ZIP_STORED
                zi.external_attr = DIR_ATTR
                z.writestr(zi, b'')
            else:
                zi.compress_type = zipfile.ZIP_DEFLATED if deflate else zipfile.ZIP_STORED
                zi.external_attr = FILE_ATTR
                z.writestr(zi, entries[name])


def _own_entries():
    return {n: (n.encode() + b'\n') for n in OWN_ENTRIES}


class SelfTest(unittest.TestCase):
    TIME = '2026-01-02T03:04:05Z'

    def _entries(self, entries, variant='public', **kw):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, 'a.zip')
            _write_zip(path, entries, **{k: v for k, v in kw.items() if k in ('when', 'sort', 'deflate')})
            ml = os.path.join(tmp, 'ml.zip')
            _write_zip(ml, {'MelonLoader/': b'', 'MelonLoader/a.dll': b'a', 'version.dll': b'v'})
            rep = Report()
            with zipfile.ZipFile(path) as z, zipfile.ZipFile(ml) as m:
                check_entries(rep, z, variant, self.TIME, m)
            return rep

    def test_good_public_zip_passes(self):
        self.assertEqual(self._entries(_own_entries()).failed, 0)

    def test_friends_zip_with_melonloader_passes(self):
        entries = _own_entries()
        entries.update({'MelonLoader/': b'', 'MelonLoader/a.dll': b'a', 'version.dll': b'v'})
        self.assertEqual(self._entries(entries, 'friends').failed, 0)

    def test_extra_or_missing_entry_fails(self):
        entries = _own_entries()
        entries['stray.txt'] = b'x'
        self.assertGreater(self._entries(entries).failed, 0)
        entries = _own_entries()
        del entries[f'{MOD_DIR}/manifest.json']
        self.assertGreater(self._entries(entries).failed, 0)

    def test_old_flat_layout_fails(self):
        entries = _own_entries()
        entries['Plugins/T2TOnline.dll'] = b'x'
        self.assertGreater(self._entries(entries).failed, 0)

    def test_wrong_time_fails(self):
        self.assertGreater(self._entries(_own_entries(), when=(2026, 1, 2, 9, 4, 4)).failed, 0)

    def test_odd_commit_second_rounds_down(self):
        self.assertEqual(expected_zip_time('2026-01-02T03:04:05Z'), (2026, 1, 2, 3, 4, 4))

    def test_unsorted_fails(self):
        self.assertGreater(self._entries(dict(reversed(list(_own_entries().items()))), sort=False).failed, 0)

    def test_stored_entry_fails(self):
        self.assertGreater(self._entries(_own_entries(), deflate=False).failed, 0)

    def test_unsafe_and_case_duplicate_names_fail(self):
        for bad in ('../x.txt', '/x.txt', 'a\\b.txt', 'C:/x.txt', 'caf\u00e9.txt'):
            entries = _own_entries()
            entries[bad] = b'x'
            self.assertGreater(self._entries(entries).failed, 0, bad)
        entries = _own_entries()
        entries[f'{MOD_DIR}/license.txt'] = b'x'
        self.assertGreater(self._entries(entries).failed, 0)

    def test_userdata_never_ships(self):
        entries = _own_entries()
        entries['MelonLoader/'] = b''
        entries['MelonLoader/a.dll'] = b'a'
        entries['version.dll'] = b'v'
        entries['UserData/Loader.cfg'] = b'x'
        self.assertGreater(self._entries(entries, 'friends').failed, 0)

    def test_friends_zip_must_match_the_public_zip_except_the_readme(self):
        ml = {'MelonLoader/': b'', 'MelonLoader/a.dll': b'a', 'version.dll': b'v'}
        def run(friends_changes):
            with tempfile.TemporaryDirectory() as tmp:
                public, friends, mlzip = (os.path.join(tmp, n) for n in ('p.zip', 'f.zip', 'm.zip'))
                _write_zip(public, _own_entries())
                _write_zip(friends, {**_own_entries(), **ml, **friends_changes})
                _write_zip(mlzip, ml)
                rep = Report()
                with zipfile.ZipFile(friends) as f, zipfile.ZipFile(mlzip) as m, zipfile.ZipFile(public) as p:
                    check_entries(rep, f, 'friends', self.TIME, m, p)
                return rep.failed
        self.assertEqual(run({README_NAME: b'the friends variant'}), 0)
        self.assertGreater(run({f'{MOD_DIR}/Plugins/T2TOnline.dll': b'another build'}), 0)

    def test_changed_melonloader_file_fails(self):
        entries = _own_entries()
        entries.update({'MelonLoader/': b'', 'MelonLoader/a.dll': b'CHANGED', 'version.dll': b'v'})
        self.assertGreater(self._entries(entries, 'friends').failed, 0)

    def test_readme_rules(self):
        def run(data, variant='public'):
            rep = Report()
            check_readme(rep, data, '0.11.0-beta', variant)
            return rep.failed
        good = b'T2T Online 0.11.0 Beta - setup guide\r\n\r\nText.\r\n'
        self.assertEqual(run(good), 0)
        self.assertGreater(run(b'\xef\xbb\xbf' + good), 0)
        self.assertGreater(run(good.replace(b'\r\n', b'\n')), 0)
        self.assertGreater(run(good + b'caf\xc3\xa9\r\n'), 0)
        self.assertGreater(run(good + b'{{VERSION}}\r\n'), 0)
        self.assertGreater(run(good.replace(b'0.11.0', b'0.10.0')), 0)
        self.assertGreater(run(good + b'the with-MelonLoader zip\r\n'), 0)
        self.assertEqual(run(good + b'the with-MelonLoader zip\r\n', 'friends'), 0)

    def test_manifest_rules(self):
        def run(text):
            rep = Report()
            check_manifest(rep, text.encode(), '0.11.0-beta')
            return rep.failed
        good = '{"name": "T2T Online", "version_number": "0.11.0-beta", "description": "Online co-op."}'
        self.assertEqual(run(good), 0)
        self.assertGreater(run(good.replace('Online co-op.', 'see https://example.org')), 0)
        self.assertGreater(run(good.replace('}', ', "website_url": ""}')), 0)
        self.assertGreater(run(good.replace('0.11.0-beta', '0.11.0')), 0)
        self.assertGreater(run('not json'), 0)

    def test_template_rules(self):
        def run(text):
            rep = Report()
            judge_template(rep, text.encode())
            return rep.failed
        self.assertEqual(run('T2T Online {{VERSION}}\nplain\n'), 0)
        self.assertEqual(run('T2T Online {{VERSION}}\n{{#FRIENDS}}\nthe with-MelonLoader zip\n{{/FRIENDS}}\n'), 0)
        self.assertGreater(run('T2T Online {{VERSION}}\nthe with-MelonLoader zip\n'), 0)
        self.assertGreater(run('T2T Online {{VERSION}}\n{{#FRIENDS}}\nx\n'), 0)
        self.assertGreater(run('T2T Online {{VERSION}}\n{{#FRIENDS}}\n{{#PUBLIC}}\n{{/PUBLIC}}\n{{/FRIENDS}}\n'), 0)
        self.assertGreater(run('T2T Online\n'), 0)
        self.assertGreater(run('T2T Online {{VERSION}} {{OTHER}}\n'), 0)
        self.assertGreater(run('T2T Online {{VERSION}} {{#FRIENDS}} x\n'), 0)

    def test_pin_rules(self):
        good = {'version': '0.7.3', 'url': 'https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip', 'sha256': 'a' * 64}
        def run(pin, tested):
            rep = Report()
            judge_pin(rep, pin, tested)
            return rep.failed
        self.assertEqual(run(good, {'0.7.3'}), 0)
        self.assertGreater(run(good, {'0.7.4'}), 0)
        self.assertGreater(run(dict(good, url=good['url'].replace('v0.7.3', 'v0.7.4')), {'0.7.3'}), 0)
        self.assertGreater(run(dict(good, sha256='XYZ'), {'0.7.3'}), 0)

    # --- assemblies -----------------------------------------------------------------------------------------------

    def _facts(self, **change):
        def dll(name, **kw):
            fact = {'file': name + '.dll', 'kind': 'dll', 'managed': True, 'name': name, 'version': '0.11.0.0',
                    'informational': '0.11.0-beta+abc123', 'references': [], 'melon': None, 'optionalDependencies': None,
                    'codeview': {'guid': 'g-' + name, 'stamp': 7, 'path': f'/_/src/{name}/obj/Release/net6.0/{name}.pdb'}}
            fact.update(kw)
            return fact
        facts = {
            'plugin': dll('T2TOnline', melon={'type': 'T2TOnline.Plugin.T2TOnlinePlugin', 'name': 'T2T Online', 'version': '0.11.0-beta', 'author': 'A', 'kind': 'MelonPlugin'},
                          references=[{'name': 'T2TOnline.Core', 'version': '0.11.0.0'}, {'name': 'T2TOnline.Steam', 'version': '0.11.0.0'},
                                      {'name': 'MelonLoader', 'version': '0.7.3.0'}, {'name': 'Il2CppX', 'version': '0.0.0.0'}]),
            'core': dll('T2TOnline.Core', references=[{'name': 'System.Runtime', 'version': '6.0.0.0'}]),
            'steam': dll('T2TOnline.Steam'),
        }
        for key, value in change.items():
            role, field = key.split('__')
            facts[role][field] = value
        return facts

    def _dll_failed(self, facts):
        rep = Report()
        judge_dlls(rep, facts, '0.11.0-beta', 'abc123def456')
        return rep.failed

    def test_assembly_versions(self):
        self.assertEqual(self._dll_failed(self._facts()), 0)
        self.assertGreater(self._dll_failed(self._facts(core__version='0.10.0.0')), 0)
        self.assertGreater(self._dll_failed(self._facts(steam__informational='0.11.0-beta+zzz')), 0)
        self.assertGreater(self._dll_failed(self._facts(plugin__informational='0.10.0-beta+abc123')), 0)
        facts = self._facts()
        facts['plugin']['melon']['version'] = '0.10.0-beta'
        self.assertGreater(self._dll_failed(facts), 0)
        facts = self._facts()
        facts['plugin']['references'][0]['version'] = '0.10.0.0'
        self.assertGreater(self._dll_failed(facts), 0)
        facts = self._facts()
        facts['plugin']['melon']['kind'] = 'MelonMod'
        self.assertGreater(self._dll_failed(facts), 0)

    def _pdb(self, **change):
        pdb = {'file': 'x.pdb', 'kind': 'pdb', 'id': {'guid': 'g-T2TOnline', 'stamp': 7}, 'documents': ['/_/src/a.cs', '/_/src/obj/b.cs'],
               'customDebugInfo': {'EmbeddedSource': 1}, 'embeddedSourceDocuments': ['/_/src/T2TOnline/obj/Release/net6.0/X.g.cs']}
        pdb.update(change)
        return pdb

    def test_pdb_rules(self):
        def run(pdb, dll=None):
            rep = Report()
            judge_pdb(rep, 'plugin', pdb, dll or self._facts()['plugin'])
            return rep.failed
        self.assertEqual(run(self._pdb()), 0)
        self.assertGreater(run(self._pdb(documents=['/_/src/a.cs', 'C:\\work\\b.cs'])), 0)
        self.assertGreater(run(self._pdb(documents=['/home/me/b.cs'])), 0)
        self.assertGreater(run(self._pdb(customDebugInfo={'SourceLink': 1})), 0)
        self.assertGreater(run(self._pdb(embeddedSourceDocuments=['/_/src/Secret.cs'])), 0)
        self.assertGreater(run(self._pdb(id={'guid': 'other', 'stamp': 7})), 0)
        self.assertGreater(run(self._pdb(), self._facts(plugin__codeview={'guid': 'g-T2TOnline', 'stamp': 7, 'path': 'C:\\build\\T2TOnline.pdb'})['plugin']), 0)

    def test_reference_resolution(self):
        facts = self._facts()
        def run(table):
            rep = Report()
            judge_references(rep, facts, lambda n: table.get(n))
            return rep.failed
        table = {'MelonLoader': '0.7.3.0', 'Il2CppX': '0.0.0.0', 'System.Runtime': '6.0.0.0'}
        self.assertEqual(run(table), 0)
        self.assertGreater(run({k: v for k, v in table.items() if k != 'Il2CppX'}), 0)
        self.assertGreater(run(dict(table, MelonLoader='0.6.0.0')), 0)

    def test_optional_dependencies(self):
        def run(declared, required=True):
            plugin = self._facts(plugin__optionalDependencies=declared)['plugin']
            rep = Report()
            judge_optional(rep, plugin, {'Il2CppX', 'Il2CppY'}, required)
            return rep.failed
        self.assertEqual(run(['Il2CppX']), 0)
        # Another interop assembly the plugin doesn't reference yet is fine (the build lists all of them)...
        self.assertEqual(run(['Il2CppX', 'Il2CppY']), 0)
        # ...but a name that isn't an interop assembly, a missing reference, or a name twice is not.
        self.assertGreater(run(['Il2CppX', 'NotInterop']), 0)
        self.assertGreater(run(['Il2CppY']), 0)
        self.assertGreater(run(['Il2CppX', 'Il2CppX']), 0)
        self.assertGreater(run([]), 0)
        self.assertGreater(run(None), 0)
        self.assertEqual(run(None, required=False), 0)

    def test_optional_dependencies_are_required_once_the_plugin_declares_them(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        plugin = os.path.join(tmp.name, 'src', 'T2TOnline.Plugin')
        os.makedirs(os.path.join(plugin, 'obj', 'Release'))
        os.makedirs(os.path.join(plugin, 'Properties'))

        def write(relative, text):
            with open(os.path.join(plugin, relative), 'w', encoding='utf-8') as f:
                f.write(text)

        write('T2TOnline.Plugin.csproj', '<Project Sdk="Microsoft.NET.Sdk"></Project>\n')
        write(os.path.join('Properties', 'MelonInfo.cs'), '[assembly: MelonInfo(typeof(P), "T2T Online", "1", "x")]\n')
        # A build's own output names it, but only the sources and the project file count.
        write(os.path.join('obj', 'Release', 'OptionalDependencies.g.cs'), '[assembly: MelonLoader.MelonOptionalDependencies(new string[] { "A" })]\n')
        self.assertFalse(plugin_declares_optional_dependencies(tmp.name))
        # The project file writing the attribute at build time (the WriteOptionalDependencies target) makes it required...
        write('T2TOnline.Plugin.csproj', '<Project><Target><WriteLinesToFile Lines="[assembly: MelonLoader.MelonOptionalDependencies(new string[];{;})]" /></Target></Project>\n')
        self.assertTrue(plugin_declares_optional_dependencies(tmp.name))
        # ...and so does the attribute in the sources, qualified or not.
        write('T2TOnline.Plugin.csproj', '<Project Sdk="Microsoft.NET.Sdk"></Project>\n')
        write(os.path.join('Properties', 'MelonInfo.cs'), '[assembly: MelonLoader.MelonOptionalDependencies("A")]\n')
        self.assertTrue(plugin_declares_optional_dependencies(tmp.name))

    def test_this_repository_requires_optional_dependencies(self):
        self.assertTrue(plugin_declares_optional_dependencies(DEFAULT_REPO))

    # --- MelonLoader's folder rules ----------------------------------------------------------------------------------

    def _tree(self, files):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        for name in files:
            path = os.path.join(tmp.name, *name.split('/'))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, 'wb') as f:
                f.write(b'x')
        return tmp.name

    def _tree_facts(self, root, versions):
        facts = {}
        for path in dlls_in_scanned_folders(root):
            name = os.path.basename(path)
            asm = os.path.splitext(name)[0]
            plugin = asm == 'T2TOnline'
            version = versions.get(rel(root, path), '0.11.0.0')
            facts[path] = {'managed': True, 'name': asm, 'version': version,
                           'melon': {'kind': 'MelonPlugin'} if plugin else None}
        return facts

    NEW = (f'{MOD_DIR}/manifest.json', f'{MOD_DIR}/Plugins/T2TOnline.dll', f'{MOD_DIR}/UserLibs/T2TOnline.Core.dll', f'{MOD_DIR}/UserLibs/T2TOnline.Steam.dll')
    OLD = ('Plugins/T2TOnline.dll', 'UserLibs/T2TOnline.Core.dll', 'UserLibs/T2TOnline.Steam.dll')

    def _install(self, root, versions=None):
        rep = Report()
        loads = simulate_phases(root, self._tree_facts(root, versions or {}))
        judge_install(rep, root, loads, '0.11.0-beta', set())
        return rep.failed

    def test_install_loads_from_the_mod_folder(self):
        self.assertEqual(self._install(self._tree(self.NEW)), 0)

    def test_folder_without_manifest_loads_nothing(self):
        self.assertGreater(self._install(self._tree([n for n in self.NEW if not n.endswith('manifest.json')])), 0)

    def test_everything_in_mods_is_never_run(self):
        self.assertGreater(self._install(self._tree(['Mods/T2TOnline.dll', 'Mods/T2TOnline.Core.dll', 'Mods/T2TOnline.Steam.dll'])), 0)

    def test_excluded_folder_names_are_skipped(self):
        files = [n.replace('Mods/T2TOnline', 'Mods/Disabled') for n in self.NEW]
        self.assertGreater(self._install(self._tree(files)), 0)

    def test_update_new_copy_wins_and_leftovers_are_named(self):
        root = self._tree(self.NEW + self.OLD)
        versions = {n: '0.10.0.0' for n in self.OLD}
        facts = self._tree_facts(root, versions)
        loads = simulate_phases(root, facts)
        rep = Report()
        old_names = list(self.OLD) + ['LICENSE', 'THIRD_PARTY_NOTICES.md', 'Plugins/T2TOnline.pdb', 'UserLibs/T2TOnline.Core.pdb', 'UserLibs/T2TOnline.Steam.pdb']
        judge_update(rep, root, loads, list(self.NEW), old_names)
        self.assertEqual(rep.failed, 0, rep.lines)

    def test_update_fails_when_the_old_copy_is_newer_or_leftovers_differ(self):
        root = self._tree(self.NEW + self.OLD)
        versions = {n: '0.12.0.0' for n in self.OLD}
        loads = simulate_phases(root, self._tree_facts(root, versions))
        rep = Report()
        judge_update(rep, root, loads, list(self.NEW), list(self.OLD))
        self.assertGreater(rep.failed, 0)
        root = self._tree(self.NEW + self.OLD)
        loads = simulate_phases(root, self._tree_facts(root, {n: '0.10.0.0' for n in self.OLD}))
        rep = Report()
        judge_update(rep, root, loads, list(self.NEW), list(self.OLD))  # some legacy files are not in the previous list
        self.assertGreater(rep.failed, 0)

    def test_size_budget(self):
        with tempfile.TemporaryDirectory() as tmp:
            big = os.path.join(tmp, 'big.zip')
            with open(big, 'wb') as f:
                f.write(b'0' * (PUBLIC_MAX_BYTES + 1))
            rep = Report()
            check_size(rep, big, 'public')
            self.assertGreater(rep.failed, 0)
            small = os.path.join(tmp, 'small.zip')
            with open(small, 'wb') as f:
                f.write(b'0' * 1000)
            prev = os.path.join(tmp, 'prev.zip')
            with open(prev, 'wb') as f:
                f.write(b'0' * 700)
            rep = Report()
            check_size(rep, small, 'public', prev)
            self.assertGreater(rep.failed, 0)


def cmd_selftest(_args):
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(SelfTest)
    stream = io.StringIO()
    result = unittest.TextTestRunner(stream=stream, verbosity=0).run(suite)
    print(f'zipcheck selftest: {result.testsRun} tests, {len(result.failures) + len(result.errors)} failed')
    if not result.wasSuccessful():
        print(stream.getvalue())
        return 1
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    p = sub.add_parser('zip')
    p.add_argument('--zip', required=True)
    p.add_argument('--variant', choices=('public', 'friends'), required=True)
    p.add_argument('--version', required=True)
    p.add_argument('--commit', required=True)
    p.add_argument('--commit-time', required=True)
    p.add_argument('--repo', default=DEFAULT_REPO)
    p.add_argument('--game-dir', required=True)
    p.add_argument('--ml-zip', required=True)
    p.add_argument('--public-zip')
    p.add_argument('--previous-zip')
    p.set_defaults(func=cmd_zip)
    p = sub.add_parser('built')
    p.add_argument('--dir', required=True)
    p.add_argument('--repo', default=DEFAULT_REPO)
    p.add_argument('--game-dir', required=True)
    p.add_argument('--commit')
    p.set_defaults(func=cmd_built)
    p = sub.add_parser('template')
    p.add_argument('path')
    p.set_defaults(func=cmd_template)
    p = sub.add_parser('melonpin')
    p.add_argument('--repo', default=DEFAULT_REPO)
    p.set_defaults(func=cmd_melonpin)
    p = sub.add_parser('selftest')
    p.set_defaults(func=cmd_selftest)
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
