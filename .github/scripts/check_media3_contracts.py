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


CONTRACTS = (
    ("StreamingPlaybackController_MetadataListener.java", "Common", "androidx.media3.common.Player$Listener"),
    ("StreamingPlaybackController_Mp3ExtractorsFactory.java", "Extractor", "androidx.media3.extractor.ExtractorsFactory"),
    ("BoundedMp3Extractor.java", "Extractor", "androidx.media3.extractor.Extractor"),
)


def methods(source, generated=False):
    # Erase nested generic arguments, just as the JVM does when matching method descriptors.
    while True:
        erased = re.sub(r"<[^<>]*>", "", source)
        if erased == source:
            break
        source = erased
    pattern = r"\bpublic\s+((?:(?:abstract|default|final|static|native|synchronized)\s+)*)([\w.$\[\]]+)\s+(\w+)\s*\(([^)]*)\)"
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
    if failures:
        raise SystemExit("\n".join(failures))


if __name__ == "__main__":
    main()
