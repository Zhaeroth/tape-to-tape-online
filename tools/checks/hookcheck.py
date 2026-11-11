#!/usr/bin/env python3
"""Check a hook manifest against a decompiled build.

Usage: hookcheck.py [<dumpdir>] <manifest.txt>
<dumpdir> is the decompiled listing of the game build to check (default: the T2T_LISTING_DIR environment variable). It is
game-derived, so it never enters the repository. It holds three UTF-8 files made from the game's GameAssembly.dll and
global-metadata.dat: types.json (every type with its parent, kind, methods with signature and RVA, fields and
properties), methods.tsv (one 'RVA<TAB>Type::Method(parameters)' line per method, so methods that share an RVA are
folded) and dump.cs (C#-style declarations, each instance field with its '// 0x' offset).
manifest.txt: one 'Namespace.Type::Member' per line ('#' starts a comment). Members can be methods, fields or
properties; members inherited from a parent class are found too. A row names its type in full ('Tape2Tape.Hockey.UI.GameMenu', not
'Hockey.UI.GameMenu'): the mod's start-up check looks types up by their full names, and a short one there turns online off.
Prints one line per entry and exits 1 if anything is missing, or if a method the mod may patch is folded (its machine
code is shared with other methods, so patching it would patch them all), generic (no body of its own) or abstract (no
body at all). A member the mod only calls may be any of those: mark it with a 'called only' comment on its line.
Folding is checked overload by overload, so two overloads of one name that share code count too. A property row is
read through the interop and never patched as such: the line names its folded accessors, and an accessor the mod patches
is listed by its own name (Type::get_X), which is checked like any method. The mod logs at start-up and at the end of a
session any patched method that no row lists as a patch target ("Patch audit"), so a patch can't escape this check.
clientmode.txt's '[section]' headers say what the mod does with the rows below them: [block], [audit] and [saves] rows
are patched and checked as above; [disable] rows are folded methods whose component is switched off instead, and
[presentation] rows are never patched, so neither has to be unfolded. Engine members the game never calls itself (icall
wrappers MelonLoader restores in its interop assemblies, so they aren't in the dump) are marked 'interop only': missing
from the dump is fine for them (the mod checks them against the interop assemblies at startup).
A patch target must also not take or return a small non-blittable struct by value: MelonLoader 0.7.3's Il2CppInterop
detour (Il2CppDetourMethodPatcher) boxes every struct argument the interop wraps as a class (one holding a reference,
and every generic struct: Nullable<T>, CancellationToken, UniTask<T>...) from the argument as if it were a pointer to the
value, and returns such a struct as a pointer to a boxed copy. On x64 a struct of 1, 2, 4 or 8 bytes is passed and
returned in a register, by value, so patching such a method reads garbage or crashes the game the first time it's
called (STRUCT ARG). Larger ones go by pointer, and blittable structs (only numbers, as Vector2 or Color) pass as they
are; both are fine."""
import collections
import json
import os
import re
import sys

if len(sys.argv) == 3:
    D, MAN = sys.argv[1], sys.argv[2]
elif len(sys.argv) == 2:
    D, MAN = os.environ.get('T2T_LISTING_DIR', ''), sys.argv[1]
else:
    print(__doc__)
    sys.exit(2)
if not D or not os.path.isfile(os.path.join(D, 'types.json')):
    print(f"No decompiled listing at '{D}' (no types.json): pass its folder as the first argument or set T2T_LISTING_DIR.")
    sys.exit(2)

# The listing is written as UTF-8, whatever the platform's default encoding.
T = json.load(open(os.path.join(D, 'types.json'), encoding='utf-8'))
by = {t['name']: t for t in T}
short = collections.defaultdict(list)
for t in T:
    short[t['name'].split('.')[-1].split('<')[0]].append(t)
rva_methods = collections.defaultdict(list)  # RVA -> every method at it, with its parameters ('Type::Name(int value)')
for line in open(os.path.join(D, 'methods.tsv'), encoding='utf-8'):
    r, n = line.rstrip('\n').split('\t', 1)
    rva_methods[r].append(n)

# clientmode.txt sections whose rows are never patched.
NOT_PATCHED = {'disable': 'its component is disabled instead', 'presentation': 'never patched'}
# presentation.txt's sections (Streaming/EventChannel.cs): [listeners] rows are class names, [local] and [menu] rows are
# events ('GameplayEvents.OnGamePause', then an action in [menu]); none is patched, so only existence is checked.
CLASS_ROWS = {'listeners'}
EVENT_ROWS = {'local', 'menu'}


def find_type(name, exact=True):
    """The dump's type for a row's type name. The mod's start-up check looks a type up by its full name, so a row must give it
    (exact); only presentation.txt's event rows name a bus class without its namespace ('GameplayEvents'), and take a suffix match."""
    if name in by:
        return by[name]
    if exact:
        return None
    c = [t for t in short.get(name.split('.')[-1], []) if t['name'].endswith(name)]
    return c[0] if c else None


def generic_definition(name):
    """'Utils.MonoSingleton<CameraController>' -> 'Utils.MonoSingleton`1' (the dump names a generic type by its arity)."""
    m = re.match(r'^(.*?)<(.*)>$', name)
    if not m:
        return None
    depth, count = 0, 1
    for ch in m.group(2):
        if ch == '<':
            depth += 1
        elif ch == '>':
            depth -= 1
        elif ch == ',' and depth == 0:
            count += 1
    return f'{m.group(1)}`{count}'


def parent(t):
    p = t['parent']
    if not p or p == 'object':
        return None
    if p in by:
        return by[p]
    g = generic_definition(p)
    if g:
        found = by.get(g) or next((x for x in short.get(g.split('.')[-1], []) if x['name'].endswith(g)), None)
        if found:
            return found
    return (short.get(p.split('.')[-1].split('<')[0]) or [None])[0]


def is_generic(sig, mem):
    """True for a generic method ('T Get<T>(...)'): its name is followed by type parameters."""
    return re.search(r'(^|[\s.])' + re.escape(mem) + r'<[^(]*\(', sig) is not None


def shared_with(owner, m):
    """(RVA or None, the other methods at that RVA, overloads of the same name included) for one overload."""
    rva = m.get('rva')
    if not rva:
        return None, []
    r = f'{rva:X}'
    others = list(rva_methods.get(r, []))
    own = f"{owner['name']}::{m['name']}({m['sig'].split('(', 1)[1]}" if '(' in m['sig'] else None
    if own in others:
        others.remove(own)
    else:
        key = f"{owner['name']}::{m['name']}("
        mine = next((n for n in others if n.startswith(key)), None)
        if mine:
            others.remove(mine)
    return r, sorted(set(others))


# Value types by layout, for the struct-argument rule: every struct's instance fields in declaration order, from dump.cs
# ('struct Name<T> : ...' headers; instance fields carry an offset comment, statics and constants don't).
STRUCTS = {}  # 'System.Nullable`1' -> (['T'], [field type, ...])
_cur = None
for _line in open(os.path.join(D, 'dump.cs'), encoding='utf-8'):
    if _line.startswith('struct '):
        _head = _line[7:].split(' : ')[0].strip()
        _m = re.match(r'^(.*?)(?:<([^<>]*)>)?$', _head)
        _cur = _m.group(1)
        STRUCTS[_cur] = ([p.strip() for p in _m.group(2).split(',')] if _m.group(2) else [], [])
    elif _line.startswith(('class ', 'interface ', 'enum ', '}')):
        _cur = None
    elif _cur is not None:
        _f = re.match(r'^\t(?:(?:public|private|protected|internal|readonly|volatile|fixed) )*(?P<t>[^;]+?) [^\s;]+; // 0x', _line)
        if _f and ' static ' not in f' {_line.strip()} ' and ' const ' not in f' {_line.strip()} ':
            STRUCTS[_cur][1].append(_f.group('t').strip())
STRUCT_SHORT = collections.defaultdict(list)
for _name in STRUCTS:
    STRUCT_SHORT[_name.split('.')[-1]].append(_name)
PRIMITIVES = {'bool': 1, 'byte': 1, 'sbyte': 1, 'short': 2, 'ushort': 2, 'char': 2, 'int': 4, 'uint': 4, 'float': 4,
              'long': 8, 'ulong': 8, 'double': 8, 'IntPtr': 8, 'UIntPtr': 8, 'nint': 8, 'nuint': 8}


def split_args(text):
    """'A, B<C, D>, E' -> ['A', 'B<C, D>', 'E']."""
    out, depth, cur = [], 0, ''
    for ch in text:
        depth += ch in '<[('
        depth -= ch in '>])'
        if ch == ',' and depth == 0:
            out.append(cur.strip())
            cur = ''
        else:
            cur += ch
    return out + ([cur.strip()] if cur.strip() else [])


def layout(name, subst=None, depth=0):
    """(blittable, size, alignment) of a value type as the game lays it out, or None for a reference type or one not found."""
    name = (subst or {}).get(name.strip(), name.strip())
    if name.endswith('?'):
        name = f'Nullable<{name[:-1]}>'
    if name in PRIMITIVES:
        return True, PRIMITIVES[name], PRIMITIVES[name]
    if depth > 8 or name.endswith('[]') or name in ('string', 'object'):
        return None
    m = re.match(r'^(.*?)<(.*)>$', name)
    base, args = (m.group(1), split_args(m.group(2))) if m else (name, [])
    key = f'{base}`{len(args)}' if args else base
    candidates = [key] if key in STRUCTS else STRUCT_SHORT.get(key.split('.')[-1], [])
    if not candidates:
        t = find_type(base, exact=False) if not args else None  # a signature names a type as short as the code does
        return (True, 4, 4) if t and t['kind'] == 'enum' else None
    params, fields = STRUCTS[candidates[0]]
    inner = dict(zip(params, args))
    blittable, size, align = not args, 0, 1  # the interop wraps every generic struct as a class
    for ftype in fields:
        sub = layout(ftype, inner, depth + 1)
        fb, fs, fa = (False, 8, 8) if sub is None else sub  # a reference field: 8 bytes, never blittable
        blittable = blittable and fb
        size = (size + fa - 1) // fa * fa + fs
        align = max(align, fa)
    size = max((size + align - 1) // align * align, 1)
    return blittable, size, align


def small_wrapped_struct(type_text):
    """Why a by-value parameter or return of this type breaks Il2CppInterop's detour (see the docstring), or None."""
    type_text = type_text.replace('readonly ', '').strip()
    got = layout(type_text) if type_text not in ('void',) else None
    if got and not got[0] and got[1] in (1, 2, 4, 8):
        return f"{type_text}: {'an' if got[1] == 8 else 'a'} {got[1]}-byte struct the interop wraps as a class"
    return None


def struct_problems(sig):
    """Every by-value parameter or return of a method signature that the detour can't pass (see small_wrapped_struct)."""
    if '(' not in sig:
        return []
    found = []
    for p in split_args(sig[sig.index('(') + 1:sig.rindex(')')]):
        words = p.split()
        if len(words) >= 2 and words[0] not in ('ref', 'out', 'in', 'params'):
            why = small_wrapped_struct(' '.join(words[:-1]))
            if why:
                found.append(f'parameter {words[-1]} is {why}')
    head = sig.split('(')[0].split()
    if len(head) >= 2:
        why = small_wrapped_struct(head[-2])
        if why:
            found.append(f'returns {why}')
    return found


bad = 0
section = None
for raw in open(MAN, encoding='utf-8'):
    line = raw.split('#')[0].strip()
    comment = raw.partition('#')[2]
    called_only = 'called only' in comment
    interop_only = 'interop only' in comment
    if not line:
        continue
    if line.startswith('['):  # a [section] header (clientmode.txt)
        section = line.strip('[]').strip()
        continue
    spared = NOT_PATCHED.get(section)  # why folding doesn't matter for this row, or None
    if section in CLASS_ROWS and '::' not in line:
        if find_type(line, exact=False):
            print(f'ok class        {line}')
        else:
            print(f'MISSING TYPE    {line}')
            bad += 1
        continue
    if section in EVENT_ROWS and '::' not in line:
        event = line.split()[0]
        if '.' not in event:
            print(f'BAD LINE        {line}   (expected Bus.Event)')
            bad += 1
            continue
        line = '::'.join(event.rsplit('.', 1))
        spared = 'an event, never patched'
    if '::' not in line:
        print(f'BAD LINE        {line}   (expected Type::Member)')
        bad += 1
        continue
    tname, mem = line.split('::', 1)
    t = find_type(tname, exact=section not in EVENT_ROWS)
    if not t:
        if interop_only:
            print(f'ok interop only {line}   (not in the dump: an engine member the game never calls)')
            continue
        short_match = find_type(tname, exact=False)
        print(f'MISSING TYPE    {line}' + (f"   (only a suffix of {short_match['name']}: the mod's start-up check needs the full name)" if short_match else ''))
        bad += 1
        continue
    hit = None
    cur, depth = t, 0
    while cur and depth < 20 and not hit:
        ms = [m for m in cur['methods'] if m['name'] == mem]
        fs = [f for f in cur['fields'] if f['name'] == mem]
        ps = [p for p in cur.get('props', []) if p == mem]
        if ms or fs or ps:
            hit = (cur, ms, fs, ps)
        cur, depth = parent(cur), depth + 1
    if not hit:
        if interop_only:
            print(f'ok interop only {line}   (not in the dump: an engine member the game never calls)')
            continue
        print(f'MISSING MEMBER  {line}')
        bad += 1
        continue
    owner, ms, fs, ps = hit
    if fs:
        print(f"ok field        {owner['name']}::{mem} : {fs[0]['type']}")
        continue
    if ps and not ms:
        # Read through the interop, never patched as a property: say which accessors are folded, for whoever would patch one.
        folded = [a['name'] for a in owner['methods'] if a['name'] in (f'get_{mem}', f'set_{mem}') and shared_with(owner, a)[1]]
        note = f"   (accessors folded: {', '.join(folded)}; to patch one, list it as {owner['name']}::{folded[0]})" if folded else ''
        print(f"ok property     {owner['name']}::{mem}{note}")
        continue
    only_generic = all(is_generic(m['sig'], mem) for m in ms)
    for m in ms:
        rva, shared = shared_with(owner, m)
        if is_generic(m['sig'], mem):
            # No body of its own (one per type argument, often shared): never patch it. A clientmode.txt row is patched
            # overload by overload, so any generic one fails there; in hooks.txt the name may mean another overload.
            if called_only or spared:
                print(f"ok generic, {'called only' if called_only else spared} {owner['name']}  {m['sig']}")
            elif section is None and not only_generic:
                print(f"ok generic overload {owner['name']}  {m['sig']}   (another overload has a body; never patch this one)")
            else:
                print(f"GENERIC         {owner['name']}  {m['sig']}   (no body of its own: never patch it; mark 'called only' if the mod only calls it)")
                bad += 1
        elif not rva:
            # No body at all: an interface or abstract method, called through the object.
            if called_only or spared:
                print(f"ok abstract, {'called only' if called_only else spared} {owner['name']}  {m['sig']}")
            else:
                print(f"ABSTRACT        {owner['name']}  {m['sig']}   (no body to patch; mark 'called only' if the mod only calls it)")
                bad += 1
        elif shared and called_only:
            print(f"ok called only  {owner['name']}  {m['sig']}   (folded with {', '.join(shared[:3])}; never patched)")
        elif shared and spared:
            print(f"ok [{section}]  {owner['name']}  {m['sig']}   (folded with {', '.join(shared[:3])}; {spared})")
        elif shared:
            print(f"FOLDED          {owner['name']}  {m['sig']}   shares code with {', '.join(shared[:3])}")
            bad += 1
        elif not called_only and not spared and struct_problems(m['sig']):
            # Il2CppInterop's detour would misread it (see the docstring); only one overload of a name may be patched, so
            # in hooks.txt a bad overload beside a good one is reported but doesn't fail the row.
            problems = '; '.join(struct_problems(m['sig']))
            if section is None and any(not struct_problems(o['sig']) for o in ms if o is not m and not is_generic(o['sig'], mem)):
                print(f"ok struct overload {owner['name']}  {m['sig']}   ({problems}: never patch this overload)")
            else:
                print(f"STRUCT ARG      {owner['name']}  {m['sig']}   ({problems}: Il2CppInterop's detour reads it as a pointer, "
                      "so patching it crashes the game; patch another method, or mark 'called only' if the mod only calls it)")
                bad += 1
        else:
            print(f"ok              {owner['name']}  {m['sig']}")
sys.exit(1 if bad else 0)
