#!/usr/bin/env python3
"""Writes a release zip that is the same bytes every time it is built from the same files.

Usage: write_zip.py --out <zip> --time <UTC time, YYYY-MM-DDTHH:MM:SSZ> --stage <folder> [--copy-from <zip>]

Every file under --stage becomes an entry named by its path below it, with '/' separators. With --copy-from, every entry of
that zip is added too, its bytes unchanged (a name in both is an error). All entries are written in sorted order, with one
time (the release commit's UTC time, rounded down to the even second a zip can hold), one set of attributes, Deflate at
level 9 and no extra fields or comment, so building twice from one commit gives an identical zip and no entry says where
or in which time zone the build ran. tools/checks/zipcheck.py reads the written zip back and checks all of it.
"""
import argparse
import os
import sys
import zipfile
from datetime import datetime

# Must equal the constants in tools/checks/zipcheck.py: Python's zipfile can't write "no attributes", so entries are made
# by a Unix system, a regular file with mode 644, a directory with mode 755.
ZIP_CREATE_SYSTEM = 3
FILE_ATTR = 0o100644 << 16
DIR_ATTR = (0o40755 << 16) | 0x10


def entry_time(text):
    t = datetime.strptime(text, '%Y-%m-%dT%H:%M:%SZ')
    return (t.year, t.month, t.day, t.hour, t.minute, t.second - t.second % 2)


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--out', required=True)
    parser.add_argument('--time', required=True)
    parser.add_argument('--stage', required=True)
    parser.add_argument('--copy-from')
    args = parser.parse_args(argv)
    when = entry_time(args.time)
    if when[0] < 1980:
        print('write_zip: a zip cannot hold a time before 1980')
        return 2

    contents = {}
    if args.copy_from:
        with zipfile.ZipFile(args.copy_from) as source:
            for info in source.infolist():
                contents[info.filename] = b'' if info.filename.endswith('/') else source.read(info)
    stage = os.path.abspath(args.stage)
    for folder, _, files in os.walk(stage):
        for name in files:
            full = os.path.join(folder, name)
            entry = os.path.relpath(full, stage).replace(os.sep, '/')
            if entry in contents:
                print(f'write_zip: {entry} is in both the copied zip and the stage folder')
                return 1
            with open(full, 'rb') as f:
                contents[entry] = f.read()

    with zipfile.ZipFile(args.out, 'w') as archive:
        for name in sorted(contents):
            info = zipfile.ZipInfo(name, date_time=when)
            info.create_system = ZIP_CREATE_SYSTEM
            if name.endswith('/'):
                info.compress_type = zipfile.ZIP_STORED
                info.external_attr = DIR_ATTR
                archive.writestr(info, b'')
            else:
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = FILE_ATTR
                archive.writestr(info, contents[name], compresslevel=9)
    print(f'write_zip: {len(contents)} entries in {os.path.basename(args.out)}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
