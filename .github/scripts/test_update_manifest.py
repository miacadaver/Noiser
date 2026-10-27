import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('update_manifest', Path(__file__).with_name('update-manifest.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class UpdateManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.apk = Path(self.temp.name) / 'Noiser.apk'
        self.apk.write_bytes(b'APK fixture')
        self.manifest = Path(self.temp.name) / 'AndroidManifest.xml'

    def write_manifest(self, version='3.11.2-stream.13', code='156', package=module.PACKAGE):
        self.manifest.write_text(f'<manifest xmlns:android="http://schemas.android.com/apk/res/android" '
                                 f'package="{package}" android:versionName="{version}" android:versionCode="{code}"/>')

    def test_real_bytes_determine_hash_and_size(self):
        self.write_manifest()
        metadata = module.generate(self.apk, self.manifest)
        self.assertEqual(metadata['Sha256'], hashlib.sha256(b'APK fixture').hexdigest())
        self.assertEqual(metadata['Size'], len(b'APK fixture'))
        self.assertEqual(metadata['VersionCode'], 156)
        self.assertEqual(metadata['ApkUrl'], 'https://github.com/miacadaver/Noiser/releases/download/noiser-3.11.2-stream.13/Noiser.apk')

    def test_invalid_identity_and_versions_fail_closed(self):
        for values in ({'package': 'other.app'}, {'version': '../bad'}, {'version': ''}, {'code': '0'}, {'code': '2147483648'}):
            with self.subTest(values=values):
                self.write_manifest(**values)
                with self.assertRaises(ValueError):
                    module.generate(self.apk, self.manifest)

    def test_empty_apk_is_rejected(self):
        self.write_manifest()
        self.apk.write_bytes(b'')
        with self.assertRaises(ValueError):
            module.generate(self.apk, self.manifest)

    def test_missing_version_is_rejected(self):
        self.manifest.write_text('<manifest/>')
        with self.assertRaises(ValueError):
            module.generate(self.apk, self.manifest)


if __name__ == '__main__':
    unittest.main()
