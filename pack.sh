#!/usr/bin/env bash
# Builds the plugin and packages it as a .dmxplugin archive (a zip containing
# manifest.json plus the plugin assemblies) ready for upload to a DMX Core 100.
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
publish_dir="$root/artifacts/publish"
output="$root/artifacts/lifx-plugin.dmxplugin"

rm -rf "$publish_dir"

dotnet publish "$root/src/DMXCore100.LIFX" --configuration Release --output "$publish_dir"

rm -f "$output"

# The SDK assemblies are excluded from the build output by the project file;
# everything published belongs in the archive.
(cd "$publish_dir" && zip -r "$output" .)

echo "Created $output"
