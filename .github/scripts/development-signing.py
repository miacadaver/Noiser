"""Prepare a private development keystore and verify its public certificate.

The password/alias follow Android's standard debug-key convention. The private
key is still a secret: never commit it or print the base64 payload.
"""

import argparse
import base64
import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


ALIAS = "androiddebugkey"
PASSWORD = "android"
ROOT = Path(__file__).resolve().parents[2]
FINGERPRINT = ROOT / ".github/noiser-development-certificate.sha256"


def private_file(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(data)


def certificate_fingerprint(keystore):
    result = subprocess.run(
        ["keytool", "-exportcert", "-keystore", str(keystore),
         "-alias", ALIAS, "-storepass", PASSWORD],
        capture_output=True, check=False,
    )
    if result.returncode:
        raise ValueError("Cannot open the development key using the expected alias/password.")
    return hashlib.sha256(result.stdout).hexdigest()


def prepare(keystore, encoded, expected):
    if not encoded.strip():
        raise ValueError("NOISER_DEBUG_KEYSTORE_BASE64 is missing. Configure the repository secret before publishing an update.")
    try:
        data = base64.b64decode(encoded.strip(), validate=True)
    except (ValueError, base64.binascii.Error):
        raise ValueError("NOISER_DEBUG_KEYSTORE_BASE64 is not valid base64.") from None
    private_file(keystore, data)
    actual = certificate_fingerprint(keystore)
    if actual != expected.strip().lower():
        raise ValueError("Development certificate changed. Refusing to publish an incompatible APK.")
    return actual


def create(keystore):
    if keystore.exists() or FINGERPRINT.exists():
        raise ValueError("A development signing identity already exists. Restore its backup instead of generating another key.")
    keystore.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    subprocess.run(
        ["keytool", "-genkeypair", "-noprompt", "-storetype", "JKS",
         "-keystore", str(keystore), "-alias", ALIAS,
         "-storepass", PASSWORD, "-keypass", PASSWORD,
         "-keyalg", "RSA", "-keysize", "3072", "-sigalg", "SHA256withRSA",
         "-validity", "10000", "-dname", "CN=Noiser Development, O=Noiser"],
        check=True, capture_output=True,
    )
    keystore.chmod(0o600)
    fingerprint = certificate_fingerprint(keystore)
    FINGERPRINT.write_text(fingerprint + "\n")
    payload = keystore.with_suffix(".base64")
    private_file(payload, base64.b64encode(keystore.read_bytes()))
    print(f"Private backup: {keystore}")
    print(f"GitHub secret payload: {payload}")
    print(f"Public certificate SHA-256: {fingerprint}")


def verify_apk(apk):
    apksigner = shutil.which("apksigner")
    if not apksigner:
        sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
        if not sdk:
            raise ValueError("Set ANDROID_HOME or put apksigner on PATH.")
        candidates = list((Path(sdk) / "build-tools").glob("*/apksigner"))
        candidates.sort(key=lambda path: tuple(int(part) for part in re.findall(r"\d+", path.parent.name)))
        if not candidates:
            raise ValueError("Android SDK apksigner was not found.")
        apksigner = str(candidates[-1])
    result = subprocess.run([apksigner, "verify", "--print-certs", str(apk)],
                            capture_output=True, check=False, text=True)
    if result.returncode:
        raise ValueError("APK signature verification failed.")
    # New build-tools label certificates by signature scheme / SDK range rather
    # than only "Signer #1". A certificate may be repeated for several schemes.
    # Source-stamp certificates and public-key digests are not app identities.
    digests = re.findall(
        r"^(?:Signer\b|V\d+(?:\.\d+)? Signer\b)[^\r\n]*?"
        r"certificate SHA-256 digest:\s*([0-9a-fA-F]{64})[ \t\r]*$",
        result.stdout, re.MULTILINE,
    )
    expected = FINGERPRINT.read_text().strip().lower()
    if not digests or {digest.lower() for digest in digests} != {expected}:
        found = ", ".join(sorted(set(digests))) or "no recognized app certificate"
        raise ValueError(f"APK does not use the pinned development certificate. Expected {expected}; found {found}. Refusing to publish.")
    print(f"Verified APK certificate SHA-256: {expected}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("create", "prepare", "verify-apk"))
    parser.add_argument("--keystore", type=Path)
    parser.add_argument("--apk", type=Path)
    arguments = parser.parse_args()
    try:
        if arguments.command == "verify-apk":
            if not arguments.apk:
                parser.error("verify-apk requires --apk")
            verify_apk(arguments.apk)
        elif not arguments.keystore:
            parser.error("create and prepare require --keystore")
        elif arguments.command == "create":
            keystore = arguments.keystore.resolve()
            create(keystore)
        else:
            keystore = arguments.keystore.resolve()
            fingerprint = prepare(keystore, os.environ.get("NOISER_DEBUG_KEYSTORE_BASE64", ""), FINGERPRINT.read_text())
            if os.environ.get("GITHUB_OUTPUT"):
                with open(os.environ["GITHUB_OUTPUT"], "a") as output:
                    output.write(f"keystore={keystore}\n")
            print(f"Verified development certificate SHA-256: {fingerprint}")
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f"Signing setup failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
