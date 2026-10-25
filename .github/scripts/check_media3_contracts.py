"""Check generated Java bridges against the actual restored Media3 interface contracts.

CLR default interface implementations can compile without exporting a Java method, causing
AbstractMethodError on device. Run after the Android build; this check requires no emulator.
"""

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile
import xml.etree.ElementTree as ET


CONTRACTS = (
    ("PlaybackListener.java", "Common", "androidx.media3.common.Player$Listener"),
    ("Mp3ExtractorsFactory.java", "Extractor", "androidx.media3.extractor.ExtractorsFactory"),
    ("BoundedMp3Extractor.java", "Extractor", "androidx.media3.extractor.Extractor"),
)

OVERRIDES = {
    "SessionPlayer.java": {
        ("androidx.media3.common.SimpleBasePlayer.State", "getState", ()),
        ("androidx.media3.common.SimpleBasePlayer.State", "getPlaceholderState", ("androidx.media3.common.SimpleBasePlayer.State",)),
        ("com.google.common.util.concurrent.ListenableFuture", "handleStop", ()),
        ("com.google.common.util.concurrent.ListenableFuture", "handleSeek", ("int", "long", "int")),
        ("com.google.common.util.concurrent.ListenableFuture", "handleSetPlayWhenReady", ("boolean",)),
    },
    "StreamingPlaybackService.java": {
        ("androidx.media3.session.MediaSession", "onGetSession", ("androidx.media3.session.MediaSession.ControllerInfo",)),
        ("com.google.common.util.concurrent.ListenableFuture", "onUpdateNotificationAsync", ("androidx.media3.session.MediaSession", "boolean")),
    },
}

WRAPPER_OVERRIDES = {
    "UnknownLengthExtractorInput": {
        ("long", "getLength", ()),
    },
    "TrackingExtractorOutput": {
        ("androidx.media3.extractor.TrackOutput", "track", ("int", "int")),
        ("void", "seekMap", ("androidx.media3.extractor.SeekMap",)),
    },
    "TrackingTrackOutput": {
        ("void", "sampleMetadata", ("long", "int", "int", "int",
         "androidx.media3.extractor.TrackOutput.CryptoData")),
    },
    "SafeSeekMap": {
        ("boolean", "isSeekable", ()),
        ("long", "getDurationUs", ()),
        ("androidx.media3.extractor.SeekMap.SeekPoints", "getSeekPoints", ("long",)),
        # Java default methods still need an explicit generated bridge.
        ("boolean", "isEstimated", ()),
    },
}


def methods(source, generated=False):
    # Erase nested generic arguments, just as the JVM does when matching method descriptors.
    while True:
        erased = re.sub(r"<[^<>]*>", "", source)
        if erased == source:
            break
        source = erased
    pattern = r"\b(?:public|protected)\s+((?:(?:abstract|default|final|static|native|synchronized)\s+)*)([\w.$\[\]]+)\s+(\w+)\s*\(([^)]*)\)"
    result = set()
    for modifiers, returns, name, arguments in re.findall(pattern, source):
        if "static" in modifiers.split():
            continue
        parameters = []
        for argument in arguments.split(",") if arguments.strip() else ():
            argument = argument.strip()
            parameters.append(argument.split()[0] if generated else argument)
        normalize = lambda value: value.replace("$", ".")
        result.add((normalize(returns), name, tuple(normalize(p) for p in parameters)))
    return result


def interface_methods(assets, component, interface, javap, temporary):
    package = "xamarin.androidx.media3." + component.lower()
    library = next(value for name, value in assets["libraries"].items()
                   if name.split("/")[0].lower() == package)
    directory = next(Path(root) / library["path"] for root in assets["packageFolders"]
                     if (Path(root) / library["path"]).is_dir())
    aar = next((directory / "aar").glob("*.aar"))
    jar = Path(temporary) / (component + ".jar")
    with zipfile.ZipFile(aar) as archive:
        jar.write_bytes(archive.read("classes.jar"))
    output = subprocess.check_output([javap, "-classpath", str(jar), "-public", interface], text=True)
    expected = methods(output)
    if not expected:
        raise RuntimeError("No methods found for " + interface)
    return expected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--obj", type=Path, default=Path("Seeker/obj/Debug/net10.0-android36.0"))
    parser.add_argument("--assets", type=Path, default=Path("Seeker/obj/project.assets.json"))
    parser.add_argument("--javap", default=str(Path(os.environ["JAVA_HOME"]) / "bin/javap")
                        if "JAVA_HOME" in os.environ else shutil.which("javap"))
    args = parser.parse_args()
    assets = json.loads(args.assets.read_text())
    failures = []
    with tempfile.TemporaryDirectory(prefix="noiser-media3-contracts-") as temporary:
        for filename, component, interface in CONTRACTS:
            sources = list((args.obj / "android/src").rglob(filename))
            if len(sources) != 1:
                raise RuntimeError(f"Expected one generated {filename}; found {len(sources)}")
            expected = interface_methods(assets, component, interface, args.javap, temporary)
            missing = expected - methods(sources[0].read_text(), generated=True)
            if missing:
                for returns, name, parameters in sorted(missing):
                    failures.append(f"{filename}: missing {returns} {name}({', '.join(parameters)})")
            else:
                print(f"{filename}: all {len(expected)} Media3 Java methods exported")
    for filename, expected in OVERRIDES.items():
        sources = list((args.obj / "android/src").rglob(filename))
        if len(sources) != 1:
            raise RuntimeError(f"Expected one generated {filename}; found {len(sources)}")
        missing = expected - methods(sources[0].read_text(), generated=True)
        for returns, name, parameters in sorted(missing):
            failures.append(f"{filename}: missing {returns} {name}({', '.join(parameters)})")
        if not missing:
            print(f"{filename}: all {len(expected)} session overrides exported")
    generated_sources = list((args.obj / "android/src").rglob("*.java"))
    for type_name, expected in WRAPPER_OVERRIDES.items():
        wrappers = []
        for path in generated_sources:
            source = path.read_text()
            if re.search(r"\bclass\s+[A-Za-z0-9_$]*" + re.escape(type_name) + r"\b", source):
                wrappers.append((path, source))
        if len(wrappers) != 1:
            raise RuntimeError(f"Expected one generated {type_name} wrapper; found {len(wrappers)}")
        path, source = wrappers[0]
        missing = expected - methods(source, generated=True)
        for returns, name, parameters in sorted(missing):
            failures.append(f"{path.name}: missing {returns} {name}({', '.join(parameters)})")
        if not missing:
            print(f"{type_name}: all {len(expected)} extractor callback overrides exported")
    android = "{http://schemas.android.com/apk/res/android}"
    manifests = [args.obj / "android/AndroidManifest.xml", args.obj / "AndroidManifest.xml"]
    manifest = ET.parse(next(path for path in manifests if path.exists())).getroot()
    service = next(node for node in manifest.findall("application/service")
                   if node.get(android + "name") == "com.companyname.andriodapp1.StreamingPlaybackService")
    assert service.get(android + "foregroundServiceType") == "mediaPlayback", "Incorrect foreground service type"
    assert service.get(android + "exported") == "true", "Media session service is not exported"
    assert any(node.get(android + "name") == "androidx.media3.session.MediaSessionService"
               for node in service.findall("intent-filter/action")), "Media session intent filter is missing"
    assert any(node.get(android + "name") == "android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK"
               for node in manifest.findall("uses-permission")), "Media playback foreground permission is missing"
    print("Manifest: exported mediaPlayback service, session intent filter, and permission verified")
    if failures:
        raise SystemExit("\n".join(failures))


if __name__ == "__main__":
    main()
