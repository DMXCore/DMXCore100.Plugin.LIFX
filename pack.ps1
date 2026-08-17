# Builds the plugin and packages it for the plugin registry: artifacts/ gets
# the NuGet package (<PackageId>.<Version>.nupkg, what CI pushes to nuget.org)
# and the bare .dmxplugin archive (for manual upload / deploy-dev.ps1). Both
# are produced by the DMXCore.PluginSdk pack targets from the project file.
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

if (Test-Path $artifacts)
{
    Get-ChildItem $artifacts -Include '*.nupkg', '*.dmxplugin' -Recurse | Remove-Item
}

dotnet pack (Join-Path $root 'src/DMXCore100.LIFX') --configuration Release --output $artifacts

Get-ChildItem $artifacts -Include '*.nupkg', '*.dmxplugin' -Recurse | ForEach-Object { Write-Host "Created $($_.FullName)" }
