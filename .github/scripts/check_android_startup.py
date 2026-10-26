#!/usr/bin/env python3
"""Exercise normal and failed startup on a dedicated emulator. Resets this app's emulator data."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET


PACKAGE = "com.companyname.andriodapp1"
MAIN = PACKAGE + "/crc646350f44cde9e1cf7.MainActivity"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--device", default="emulator-5554")
    parser.add_argument("--output", required=True)
    parser.add_argument("--reset-emulator-app-data", action="store_true", required=True)
    args = parser.parse_args()
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    results = []

    def run(*command, data=None, check=True):
        result = subprocess.run([args.adb, "-s", args.device, *command], input=data,
                                capture_output=True, check=check)
        return result.stdout.decode(errors="replace")

    def passed(message):
        results.append("PASS: " + message)
        print(results[-1], flush=True)
        (output / "results.txt").write_text("\n".join(results) + "\n")

    def read_pref(name):
        return run("shell", "run-as", PACKAGE, "cat", "shared_prefs/" + name + ".xml")

    def write_pref(name, content):
        # Paths are fixed test constants; only the XML is supplied through stdin.
        run("shell", "run-as", PACKAGE, "sh", "-c",
            "'mkdir -p shared_prefs; rm -f shared_prefs/" + name
            + ".xml.bak; cat > shared_prefs/" + name + ".xml'", data=content)

    def launch(name):
        run("shell", "am", "force-stop", PACKAGE)
        run("logcat", "-c")
        result = run("shell", "am", "start", "-W", "-n", MAIN)
        assert "Status: ok" in result, result
        time.sleep(3)
        log = run("logcat", "-d")
        (output / (name + "-logcat.txt")).write_text(log)
        assert run("shell", "pidof", PACKAGE).strip(), "App exited: " + name
        assert "FATAL EXCEPTION" not in log and "Fatal signal" not in log, log[-4000:]
        passed(name + " remains running without a fatal exception")

    def ui():
        for _ in range(5):
            run("shell", "rm", "-f", "/sdcard/noiser-startup.xml")
            run("shell", "uiautomator", "dump", "/sdcard/noiser-startup.xml", check=False)
            content = run("shell", "cat", "/sdcard/noiser-startup.xml", check=False)
            try:
                return ET.fromstring(content)
            except ET.ParseError:
                time.sleep(1)
        raise AssertionError("Could not read startup screen")

    assert run("shell", "getprop", "ro.kernel.qemu").strip() == "1", "Requires a dedicated emulator"
    run("uninstall", PACKAGE, check=False)
    assert "Success" in run("install", args.apk), "APK installation failed"
    launch("Fresh cold launch")

    run("shell", "am", "force-stop", PACKAGE)
    entry = {"Id": "startup-test", "Username": "validation-peer", "RemotePath": "Music\\Song.mp3",
             "Size": 1000000, "DiscNumber": None, "FilenameDecoded": False, "FolderDecoded": False}
    document = {"Version": 2, "SelectedId": entry["Id"], "RepeatMode": 0, "Autoplay": True,
                "PositionMilliseconds": 65000, "Entries": [entry]}
    saved = ET.Element("map")
    ET.SubElement(saved, "string", name="queue_v1").text = json.dumps(document)
    write_pref("noiser_playback_queue", ET.tostring(saved))
    launch("Saved queue cold launch")
    for _ in range(30):
        sessions = run("shell", "dumpsys", "media_session")
        if "noiser-stream" in sessions:
            break
        assert run("shell", "pidof", PACKAGE).strip(), "App exited during player initialization"
        time.sleep(1)
    (output / "media-session.txt").write_text(sessions)
    assert "noiser-stream" in sessions, "Saved queue did not connect its session"
    assert "PLAYING(3)" not in sessions and "state=3," not in sessions, "Unexpected autoplay"
    passed("Saved queue connects its session without autoplay")

    # A real malformed preference triggers failure before account/UI/player setup completes.
    # The error screen must work with those services absent and must preserve the original data.
    run("shell", "am", "force-stop", PACKAGE)
    original = read_pref("SoulSeekPrefs").encode()
    malformed = b'<map><int name="Momento_Username" value="42"/><string name="startup-test-marker">keep me</string></map>'
    write_pref("SoulSeekPrefs", malformed)
    queue_hash = hashlib.sha256(read_pref("noiser_playback_queue").encode()).hexdigest()
    launch("Malformed preferences startup")
    screen = ui()
    (output / "error-screen.xml").write_text(ET.tostring(screen, encoding="unicode"))
    text = "\n".join(n.get("text", "") for n in screen.iter("node"))
    assert "Noiser couldn't start" in text and "App setup" in text and "ClassCastException" in text, text
    passed("Managed startup failure displays its stage and readable exception")
    button = next(n for n in screen.iter("node") if n.get("text", "").casefold() == "copy error details")
    x1, y1, x2, y2 = map(int, re.findall(r"\d+", button.get("bounds")))
    run("shell", "input", "tap", str((x1 + x2) // 2), str((y1 + y2) // 2))
    text = "\n".join(n.get("text", "") for n in ui().iter("node"))
    assert "copied" in text.casefold(), text
    passed("Error details copy through the phone UI")
    run("shell", "input", "keyevent", "3")
    time.sleep(1)
    assert ET.tostring(ET.fromstring(read_pref("SoulSeekPrefs"))) == ET.tostring(ET.fromstring(malformed))
    assert hashlib.sha256(read_pref("noiser_playback_queue").encode()).hexdigest() == queue_hash
    passed("Failed startup and backgrounding preserve original preferences and saved queue")

    run("shell", "am", "force-stop", PACKAGE)
    write_pref("SoulSeekPrefs", original)
    launch("Corrected preferences cold launch")
    assert "Noiser couldn't start" not in "\n".join(n.get("text", "") for n in ui().iter("node"))
    passed("Normal startup returns after the test restores valid preferences")
    print("RESULT: PASS", flush=True)


if __name__ == "__main__":
    main()
