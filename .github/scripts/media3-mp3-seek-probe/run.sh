#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
nuget_packages="${NOISER_NUGET_PACKAGES:-/tmp/noiser-nuget}"
android_sdk="${NOISER_ANDROID_SDK:-/tmp/noiser-android-sdk}"
jdk_home="${NOISER_JDK_HOME:-/home/codespace/java/21.0.12+1-ms}"
probe_source="$repo_root/.github/scripts/media3-mp3-seek-probe"
temporary="$(mktemp -d)"
trap 'rm -rf "$temporary"' EXIT

extract_aar_classes() {
    local aar="$1"
    local jar="$temporary/$(basename "$aar").jar"
    local entry
    entry="$(unzip -Z1 "$aar" | sed -n '/\.jar$/ {p;q;}')"
    if [[ -z "$entry" ]]; then
        printf 'No embedded JAR found in %s\n' "$aar" >&2
        return 1
    fi
    unzip -p "$aar" "$entry" > "$jar"
    printf '%s' "$jar"
}

media3_version=1.11.1
media3_common="$(extract_aar_classes "$nuget_packages/xamarin.androidx.media3.common/$media3_version/aar/androidx.media3.media3-common.aar")"
media3_container="$(extract_aar_classes "$nuget_packages/xamarin.androidx.media3.container/$media3_version/aar/androidx.media3.media3-container.aar")"
media3_decoder="$(extract_aar_classes "$nuget_packages/xamarin.androidx.media3.decoder/$media3_version/aar/androidx.media3.media3-decoder.aar")"
media3_extractor="$(extract_aar_classes "$nuget_packages/xamarin.androidx.media3.extractor/$media3_version/aar/androidx.media3.media3-extractor.aar")"
guava="$(extract_aar_classes "$nuget_packages/xamarin.google.guava/33.7.1/lib/net10.0-android36.0/Xamarin.Google.Guava.aar")"
listenable_future="$(extract_aar_classes "$nuget_packages/xamarin.google.guava.listenablefuture/1.0.0.32/lib/net10.0-android36.0/Xamarin.Google.Guava.ListenableFuture.aar")"
android_jar="$android_sdk/platforms/android-36/android.jar"
class_dir="$temporary/classes"
mkdir -p "$class_dir"
classpath="$class_dir:$android_jar:$media3_common:$media3_container:$media3_decoder:$media3_extractor:$guava:$listenable_future"

"$jdk_home/bin/javac" -cp "$classpath" -d "$class_dir" \
    "$probe_source/android/os/Build.java" \
    "$probe_source/android/app/ActivityManager.java" \
    "$probe_source/Mp3SeekProbe.java"
"$jdk_home/bin/java" -cp "$classpath" Mp3SeekProbe
