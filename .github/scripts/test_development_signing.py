"""Exercise private-key handling and publication rejection with disposable keys."""

import base64
import importlib.util
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("development_signing", Path(__file__).with_name("development-signing.py"))
signing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(signing)


@unittest.skipUnless(shutil.which("keytool"), "JDK keytool is required")
class KeystoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = tempfile.TemporaryDirectory()
        cls.key = Path(cls.fixture.name) / "fixture.jks"
        subprocess.run([
            "keytool", "-genkeypair", "-noprompt", "-keystore", str(cls.key),
            "-storetype", "JKS", "-alias", signing.ALIAS,
            "-storepass", signing.PASSWORD, "-keypass", signing.PASSWORD,
            "-keyalg", "RSA", "-validity", "2", "-dname", "CN=Disposable Test",
        ], check=True, capture_output=True)
        cls.encoded = base64.b64encode(cls.key.read_bytes()).decode()
        cls.fingerprint = signing.certificate_fingerprint(cls.key)

    @classmethod
    def tearDownClass(cls):
        cls.fixture.cleanup()

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.target = Path(self.directory.name) / "runner.jks"

    def test_same_secret_restores_same_certificate_on_independent_runners(self):
        for index in range(2):
            target = self.target.with_name(f"runner-{index}.jks")
            actual = signing.prepare(target, self.encoded, self.fingerprint)
            self.assertEqual(actual, self.fingerprint)
            self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o600)

    def test_missing_secret_never_creates_or_generates_a_key(self):
        with self.assertRaisesRegex(ValueError, "missing"):
            signing.prepare(self.target, "", self.fingerprint)
        self.assertFalse(self.target.exists())

    def test_malformed_secret_is_rejected_before_writing(self):
        with self.assertRaisesRegex(ValueError, "valid base64"):
            signing.prepare(self.target, "not a key!", self.fingerprint)
        self.assertFalse(self.target.exists())

    def test_changed_certificate_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "certificate changed"):
            signing.prepare(self.target, self.encoded, "0" * 64)

    def test_existing_key_is_never_overwritten(self):
        self.target.write_bytes(b"existing backup")
        with self.assertRaises(FileExistsError):
            signing.prepare(self.target, self.encoded, self.fingerprint)
        self.assertEqual(self.target.read_bytes(), b"existing backup")

    def test_creation_refuses_to_replace_a_pinned_identity(self):
        pin = self.target.with_suffix(".sha256")
        pin.write_text(self.fingerprint)
        with patch.object(signing, "FINGERPRINT", pin):
            with self.assertRaisesRegex(ValueError, "already exists"):
                signing.create(self.target)
        self.assertFalse(self.target.exists())


class ApkPublicationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.pin = Path(self.directory.name) / "certificate.sha256"
        self.pin.write_text("a" * 64 + "\n")

    def verify(self, output, returncode=0):
        result = subprocess.CompletedProcess([], returncode, stdout=output, stderr="")
        with patch.object(signing, "FINGERPRINT", self.pin), \
             patch.object(signing.shutil, "which", return_value="apksigner"), \
             patch.object(signing.subprocess, "run", return_value=result):
            signing.verify_apk(Path("Noiser.apk"))

    def test_accepts_pinned_certificate_after_successful_signature_verification(self):
        self.verify("Signer #1 certificate SHA-256 digest: " + "a" * 64 + "\n")

    def test_rejects_random_debug_certificate(self):
        with self.assertRaisesRegex(ValueError, "pinned development certificate"):
            self.verify("Signer #1 certificate SHA-256 digest: " + "b" * 64 + "\n")

    def test_rejects_invalid_apk_even_if_output_mentions_pinned_certificate(self):
        with self.assertRaisesRegex(ValueError, "signature verification failed"):
            self.verify("Signer #1 certificate SHA-256 digest: " + "a" * 64 + "\n", returncode=1)

    def test_rejects_additional_signer(self):
        with self.assertRaisesRegex(ValueError, "pinned development certificate"):
            self.verify("Signer #1 certificate SHA-256 digest: " + "a" * 64 + "\n"
                        "Signer #2 certificate SHA-256 digest: " + "b" * 64 + "\n")


if __name__ == "__main__":
    unittest.main()
