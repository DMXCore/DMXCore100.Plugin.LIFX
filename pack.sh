#!/usr/bin/env bash
# Builds the plugin and packages it for the plugin registry: artifacts/ gets
# the NuGet package (<PackageId>.<Version>.nupkg, what CI pushes to nuget.org)
# and the bare .dmxplugin archive (for manual upload / deploy-dev.ps1). Both
# are produced by the DMXCore.PluginSdk pack targets from the project file.
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
artifacts="$root/artifacts"

rm -f "$artifacts"/*.nupkg "$artifacts"/*.dmxplugin

dotnet pack "$root/src/DMXCore100.LIFX" --configuration Release --output "$artifacts"

ls -1 "$artifacts"/*.nupkg "$artifacts"/*.dmxplugin | sed 's/^/Created /'
