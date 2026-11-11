#!/usr/bin/env python3
"""Read the game's prefabs (which components sit where, and how they're wired) from its Addressables bundles.

Usage:
  bundle_prefabs.py <bundle> tree <root-name-regex> [script-regex-to-print-fields] [maxdepth]
  bundle_prefabs.py <bundle> find <script-regex> [field-regex]
<bundle> is a file in '<game>/Tape to Tape_Data/StreamingAssets/aa/StandaloneWindows64/' (the UI is in 'ui-*.bundle':
ui-rogue holds the campaign's menus and cards). The MonoScript bundle that names each component ('*_monoscripts_*.bundle')
is found next to it.

'tree' prints each prefab whose root GameObject matches, as its hierarchy with every object's components (script class
names; 'cNN' for an engine class id), and the serialized fields of the components whose script matches the second regex,
references shown as 'Class<path>'. 'find' lists every component of the matching scripts, with its path and the matching
fields. Example: which shortcut a menu's keyword tooltip waits for while held:
  bundle_prefabs.py ui-rogue_assets_all_<hash>.bundle find "^KeywordsTooltip$" "m_HoldToShowInput|m_Mode"

Reads the game files as data only: UnityFS bundles (LZ4/LZMA blocks), serialized files v22+ with their type trees (the
Addressables bundles keep them; the player's data.unity3d doesn't, so it isn't read). Its output is game-derived:
keep it out of the repository."""
import lzma
import os
import re
import struct
import sys


def lz4_block(src, usize):
    dst = bytearray()
    i, n = 0, len(src)
    while i < n:
        token = src[i]
        i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]
        i += lit
        if i >= n:
            break
        off = src[i] | (src[i + 1] << 8)
        i += 2
        ml = token & 15
        if ml == 15:
            while True:
                b = src[i]
                i += 1
                ml += b
                if b != 255:
                    break
        ml += 4
        start = len(dst) - off
        if off >= ml:
            dst += dst[start:start + ml]
        else:
            for k in range(ml):
                dst.append(dst[start + k])
    if len(dst) != usize:
        raise ValueError(f'lz4: {len(dst)} bytes, expected {usize}')
    return bytes(dst)


def decompress(data, usize, comp):
    if comp == 0:
        return data
    if comp == 1:
        filt = lzma._decode_filter_properties(lzma.FILTER_LZMA1, data[:5])
        return lzma.LZMADecompressor(lzma.FORMAT_RAW, filters=[filt]).decompress(data[5:], usize)
    if comp in (2, 3):
        return lz4_block(data, usize)
    raise ValueError(f'compression {comp}')


class R:
    def __init__(self, b, be=True):
        self.b, self.p, self.be = b, 0, be

    def f(self, fmt, size):
        v = struct.unpack_from(('>' if self.be else '<') + fmt, self.b, self.p)[0]
        self.p += size
        return v

    def u8(self):
        self.p += 1
        return self.b[self.p - 1]

    def cstr(self):
        e = self.b.index(b'\0', self.p)
        s = self.b[self.p:e].decode('utf-8', 'replace')
        self.p = e + 1
        return s

    def align(self, n=4):
        self.p = (self.p + n - 1) & ~(n - 1)

    def take(self, n):
        self.p += n
        return self.b[self.p - n:self.p]


def load_bundle(path):
    """The bundle's files: {name: bytes}."""
    data = open(path, 'rb').read()
    r = R(data)
    if r.cstr() != 'UnityFS':
        raise ValueError(f'{path}: not a UnityFS bundle')
    ver = r.f('I', 4)
    r.cstr()
    r.cstr()
    r.f('q', 8)
    csize, usize, flags = r.f('I', 4), r.f('I', 4), r.f('I', 4)
    if ver >= 7:
        r.align(16)
    info = data[len(data) - csize:] if flags & 0x80 else r.take(csize)
    bi = R(decompress(info, usize, flags & 0x3F))
    if flags & 0x200:
        r.align(16)
    bi.take(16)
    blocks = [(bi.f('I', 4), bi.f('I', 4), bi.f('H', 2)) for _ in range(bi.f('i', 4))]
    nodes = []
    for _ in range(bi.f('i', 4)):
        off, size, _flags = bi.f('q', 8), bi.f('q', 8), bi.f('I', 4)
        nodes.append((off, size, bi.cstr()))
    out = bytearray()
    for bu, bc, bf in blocks:
        out += decompress(r.take(bc), bu, bf & 0x3F)
    return {name: bytes(out[off:off + size]) for off, size, name in nodes}


# Unity's built-in string table for type trees (a constant of the serialized file format: offsets with the high bit
# set index into it).
COMMON = (b'AABB\0AnimationClip\0AnimationCurve\0AnimationState\0Array\0Base\0BitField\0bitset\0bool\0char\0ColorRGBA\0'
          b'Component\0data\0deque\0double\0dynamic_array\0FastPropertyName\0first\0float\0Font\0GameObject\0Generic Mono\0'
          b'GradientNEW\0GUID\0GUIStyle\0int\0list\0long long\0map\0Matrix4x4f\0MdFour\0MonoBehaviour\0MonoScript\0m_ByteSize\0'
          b'm_Curve\0m_EditorClassIdentifier\0m_EditorHideFlags\0m_Enabled\0m_ExtensionPtr\0m_GameObject\0m_Index\0m_IsArray\0'
          b'm_IsStatic\0m_MetaFlag\0m_Name\0m_ObjectHideFlags\0m_PrefabInternal\0m_PrefabParentObject\0m_Script\0'
          b'm_StaticEditorFlags\0m_Type\0m_Version\0Object\0pair\0PPtr<Component>\0PPtr<GameObject>\0PPtr<Material>\0'
          b'PPtr<MonoBehaviour>\0PPtr<MonoScript>\0PPtr<Object>\0PPtr<Prefab>\0PPtr<Sprite>\0PPtr<TextAsset>\0PPtr<Texture>\0'
          b'PPtr<Texture2D>\0PPtr<Transform>\0Prefab\0Quaternionf\0Rectf\0RectInt\0RectOffset\0second\0set\0short\0size\0SInt16\0'
          b'SInt32\0SInt64\0SInt8\0staticvector\0string\0TextAsset\0TextMesh\0Texture\0Texture2D\0Transform\0TypelessData\0'
          b'UInt16\0UInt32\0UInt64\0UInt8\0unsigned int\0unsigned long long\0unsigned short\0vector\0Vector2f\0Vector3f\0'
          b'Vector4f\0m_ScriptingClassIdentifier\0Gradient\0Type*\0int2_storage\0int3_storage\0BoundsInt\0'
          b'm_CorrespondingSourceObject\0m_PrefabInstance\0m_PrefabAsset\0FileSize\0Hash128\0RenderingLayerMask\0')

PRIMS = {'bool': 'B', 'char': 'B', 'SInt8': 'b', 'UInt8': 'B', 'SInt16': 'h', 'short': 'h', 'UInt16': 'H',
         'unsigned short': 'H', 'SInt32': 'i', 'int': 'i', 'UInt32': 'I', 'unsigned int': 'I', 'Type*': 'I',
         'SInt64': 'q', 'long long': 'q', 'UInt64': 'Q', 'unsigned long long': 'Q', 'FileSize': 'Q', 'float': 'f',
         'double': 'd'}


class Node:
    def __init__(self, typ, name, level, flags, array):
        self.type, self.name, self.level, self.flags, self.array, self.children = typ, name, level, flags, array, []


def read_tree(r, ver):
    count, strsize = r.f('i', 4), r.f('i', 4)
    raw = []
    for _ in range(count):
        r.f('H', 2)
        level, typeflags = r.u8(), r.u8()
        tso, nso = r.f('I', 4), r.f('I', 4)
        r.f('i', 4)
        r.f('i', 4)
        meta = r.f('i', 4)
        if ver >= 19:
            r.f('Q', 8)
        raw.append((level, typeflags, tso, nso, meta))
    strings = r.take(strsize)

    def name(o):
        buf, o = (COMMON, o & 0x7FFFFFFF) if o & 0x80000000 else (strings, o)
        return buf[o:buf.index(b'\0', o)].decode('utf-8', 'replace')

    nodes = [Node(name(t), name(n), lv, meta, bool(tf & 1)) for lv, tf, t, n, meta in raw]
    stack = [nodes[0]]
    for n in nodes[1:]:
        while stack[-1].level >= n.level:
            stack.pop()
        stack[-1].children.append(n)
        stack.append(n)
    return nodes[0]


def read_value(r, node):
    if node.type in PRIMS:
        fmt = PRIMS[node.type]
        v = r.f(fmt, struct.calcsize(fmt))
        if node.flags & 0x4000:
            r.align()
        return v
    if node.type == 'string':
        s = r.take(r.f('i', 4)).decode('utf-8', 'replace')
        r.align()
        return s
    if node.array or (node.children and node.children[0].array):
        arr = node if node.array else node.children[0]
        count = r.f('i', 4)
        elem = arr.children[1]
        if elem.type in ('UInt8', 'char', 'SInt8') and not elem.children:
            v = r.take(count)
            values = bytes(v)
        else:
            values = [read_value(r, elem) for _ in range(count)]
        if (arr.flags | node.flags) & 0x4000:
            r.align()
        return values
    if node.type == 'TypelessData':
        r.take(r.f('i', 4))
        r.align()
        return None
    value = {c.name: read_value(r, c) for c in node.children}
    if node.flags & 0x4000:
        r.align()
    return value


class SerializedFile:
    def __init__(self, data):
        self.data = data
        r = R(data)
        r.take(8)
        self.ver = r.f('I', 4)
        if self.ver < 22:
            raise ValueError(f'serialized file version {self.ver} (this reader needs 22+)')
        r.take(4)
        self.be = r.u8() != 0
        r.take(3)
        r.f('I', 4)
        r.f('q', 8)
        doff = r.f('q', 8)
        r.f('q', 8)
        r.be = self.be
        r.cstr()
        r.f('i', 4)
        if not r.u8():
            raise ValueError('no type trees in this file')
        self.types = []
        for _ in range(r.f('i', 4)):
            cid = r.f('i', 4)
            r.u8()
            r.f('h', 2)
            if cid == 114:
                r.take(16)
            r.take(16)
            tree = read_tree(r, self.ver)
            for _ in range(r.f('i', 4)):
                r.f('i', 4)
            self.types.append((cid, tree))
        self.objects = {}
        for _ in range(r.f('i', 4)):
            r.align()
            pid = r.f('q', 8)
            off = r.f('q', 8)
            r.f('I', 4)
            self.objects[pid] = (doff + off, r.f('i', 4))
        for _ in range(r.f('i', 4)):
            r.f('i', 4)
            r.align()
            r.f('q', 8)
        self.externals = []
        for _ in range(r.f('i', 4)):
            r.cstr()
            r.take(16)
            r.f('i', 4)
            self.externals.append(r.cstr())
        self._cache = {}

    def cid(self, pid):
        return self.types[self.objects[pid][1]][0]

    def read(self, pid):
        if pid not in self._cache:
            off, tidx = self.objects[pid]
            r = R(self.data, self.be)
            r.p = off
            self._cache[pid] = read_value(r, self.types[tidx][1])
        return self._cache[pid]


def serialized(path):
    return [SerializedFile(v) for k, v in load_bundle(path).items() if not k.endswith(('.resS', '.resource'))]


class Prefabs:
    SKIP = {'m_GameObject', 'm_Enabled', 'm_Script', 'm_Name', 'm_ObjectHideFlags', 'm_CorrespondingSourceObject',
            'm_PrefabInstance', 'm_PrefabAsset', 'm_EditorHideFlags', 'm_EditorClassIdentifier'}

    def __init__(self, bundle):
        folder = os.path.dirname(os.path.abspath(bundle))
        ms_bundle = next(os.path.join(folder, f) for f in os.listdir(folder) if '_monoscripts_' in f)
        ms_name, ms_data = next((k, v) for k, v in load_bundle(ms_bundle).items() if not k.endswith('.resS'))
        ms = SerializedFile(ms_data)
        self.ms_cab = ms_name
        self.scripts = {pid: ms.read(pid).get('m_ClassName', '?') for pid in ms.objects if ms.cid(pid) == 115}
        self.sf = serialized(bundle)[0]
        sf = self.sf
        self.names, self.tr = {}, {}
        for pid in sf.objects:
            if sf.cid(pid) == 1:
                g = sf.read(pid)
                self.names[pid] = g['m_Name']
                for c in g['m_Component']:
                    cp = c['component']['m_PathID']
                    if cp in sf.objects and sf.cid(cp) in (4, 224):
                        self.tr[pid] = cp

    def script(self, mb):
        s = mb.get('m_Script', {})
        fid = s.get('m_FileID', 0)
        if fid > 0 and self.ms_cab in self.sf.externals[fid - 1]:
            return self.scripts.get(s['m_PathID'], '?')
        return f'?{fid}:{s.get("m_PathID")}'

    def path(self, go):
        out, tr = [], self.tr.get(go)
        while tr:
            t = self.sf.read(tr)
            out.append(self.names.get(t['m_GameObject']['m_PathID'], '?'))
            tr = t['m_Father']['m_PathID']
        return '/'.join(reversed(out))

    def fmt(self, v, depth=0):
        sf = self.sf
        if isinstance(v, dict) and set(v) == {'m_FileID', 'm_PathID'}:
            pid = v['m_PathID']
            if pid == 0:
                return 'null'
            if v['m_FileID'] == 0 and pid in sf.objects:
                if sf.cid(pid) == 1:
                    return f'GO<{self.path(pid)}>'
                o = sf.read(pid)
                g = o.get('m_GameObject', {}).get('m_PathID') if isinstance(o, dict) else None
                if g:
                    kind = self.script(o) if sf.cid(pid) == 114 else f'c{sf.cid(pid)}'
                    return f'{kind}<{self.path(g)}>'
                return f'c{sf.cid(pid)}:{pid}'
            return f'ext{v["m_FileID"]}:{pid}'
        if isinstance(v, list):
            items = [self.fmt(x, depth + 1) for x in v[:12]]
            return '[' + ', '.join(items) + (f' ... ({len(v)})' if len(v) > 12 else '') + ']'
        if isinstance(v, bytes):
            return f'<{len(v)} bytes>'
        if isinstance(v, dict):
            if depth > 2:
                return '{...}'
            return '{' + ', '.join(f'{k}={self.fmt(x, depth + 1)}' for k, x in list(v.items())[:12]) + '}'
        return repr(v)

    def tree(self, root_rx, field_rx, maxdepth):
        sf = self.sf
        roots = [g for g, t in self.tr.items() if sf.read(t)['m_Father']['m_PathID'] == 0 and root_rx.search(self.names[g])]
        for root in sorted(roots, key=lambda g: self.names[g]):
            print('=' * 20, self.names[root])
            self.walk(root, 0, field_rx, maxdepth)

    def walk(self, go, depth, field_rx, maxdepth):
        sf = self.sf
        g = sf.read(go)
        comps, fields = [], []
        for c in g['m_Component']:
            cp = c['component']['m_PathID']
            if cp not in sf.objects or sf.cid(cp) in (4, 224):
                continue
            if sf.cid(cp) != 114:
                comps.append(f'c{sf.cid(cp)}')
                continue
            mb = sf.read(cp)
            name = self.script(mb)
            comps.append(name + ('' if mb.get('m_Enabled', 1) else '(off)'))
            if field_rx and field_rx.search(name):
                fields += [f'{"  " * (depth + 2)}{name}.{k} = {self.fmt(v)}' for k, v in mb.items() if k not in self.SKIP]
        print(f'{"  " * depth}{self.names[go]}{"" if g.get("m_IsActive", 1) else " (inactive)"}  [{", ".join(comps)}]')
        for line in fields:
            print(line)
        if depth < maxdepth and go in self.tr:
            for child in sf.read(self.tr[go])['m_Children']:
                if child['m_PathID'] in sf.objects:
                    self.walk(sf.read(child['m_PathID'])['m_GameObject']['m_PathID'], depth + 1, field_rx, maxdepth)

    def find(self, script_rx, field_rx):
        sf = self.sf
        for pid in sf.objects:
            if sf.cid(pid) != 114:
                continue
            mb = sf.read(pid)
            name = self.script(mb)
            if not script_rx.search(name):
                continue
            print(f'{name} on {self.path(mb["m_GameObject"]["m_PathID"])}{"" if mb.get("m_Enabled", 1) else " (disabled)"}')
            for k, v in mb.items():
                if field_rx and k not in self.SKIP and field_rx.search(k):
                    print(f'    {k} = {self.fmt(v)}')


def main(argv):
    if len(argv) < 4 or argv[2] not in ('tree', 'find'):
        print(__doc__)
        return 2
    prefabs = Prefabs(argv[1])
    if argv[2] == 'tree':
        field_rx = re.compile(argv[4]) if len(argv) > 4 and argv[4] else None
        prefabs.tree(re.compile(argv[3]), field_rx, int(argv[5]) if len(argv) > 5 else 99)
    else:
        prefabs.find(re.compile(argv[3]), re.compile(argv[4]) if len(argv) > 4 else None)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
