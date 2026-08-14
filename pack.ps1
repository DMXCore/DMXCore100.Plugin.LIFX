# Builds the plugin and packages it as a .dmxplugin archive (a zip containing
# manifest.json plus the plugin assemblies) ready for upload to a DMX Core 100.
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$publishDir = Join-Path $root 'artifacts/publish'
$output = Join-Path $root 'artifacts/lifx-plugin.dmxplugin'

dotnet publish (Join-Path $root 'src/DMXCore100.LIFX') --configuration Release --output $publishDir

if (Test-Path $output)
{
    Remove-Item $output
}

# The SDK assemblies are excluded from the build output by the project file;
# everything published belongs in the archive.
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $output

Write-Host "Created $output"
