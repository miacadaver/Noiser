#!/usr/bin/env python3
"""Generate bounded public updater metadata for an already signature-verified APK."""
import argparse
import hashlib
import json
import re
from pathlib import Path
import xml.etree.ElementTree as ET

ANDROID = '{http://schemas.android.com/apk/res/android}'
PACKAGE = 'com.companyname.andriodapp1.rollback'
REPOSITORY = 'miacadaver/Noiser'


def generate(apk, manifest):
    root = ET.parse(manifest).getroot()
    version = root.get(ANDROID + 'versionName', '')
    code = int(root.get(ANDROID + 'versionCode', '0'))
    size = Path(apk).stat().st_size
    if (root.get('package') != PACKAGE or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9.+_-]{0,127}', version)
            or not 0 < code <= 2147483647 or not 0 < size <= 512 * 1024 * 1024):
        raise ValueError('Invalid package, version, or APK size for Noiser updates')
    digest = hashlib.sha256()
    with open(apk, 'rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return {
        'SchemaVersion': 1, 'VersionCode': code, 'VersionName': version, 'PackageName': PACKAGE,
        'Size': size, 'Sha256': digest.hexdigest(),
        'ApkUrl': f'https://github.com/{REPOSITORY}/releases/download/noiser-{version}/Noiser.apk',
    }


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apk', required=True, type=Path)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    data = generate(args.apk, args.manifest)
    args.output.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
