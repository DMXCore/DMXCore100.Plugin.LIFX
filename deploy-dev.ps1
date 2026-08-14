# Dev loop: build + pack the plugin, then upload it to a running DMX Core 100.
# The Core applies the upload immediately by hot-reloading the plugin — no
# restart needed (requires Core 2026.8+; older Cores stage it for the next
# restart).
#
# Requires PowerShell 6.1+ (Invoke-RestMethod -Form). Invoke with pwsh:
#   pwsh ./deploy-dev.ps1
#   pwsh ./deploy-dev.ps1 -Server https://core.example:8080 -User Manager
#   pwsh ./deploy-dev.ps1 -Server http://192.168.1.50:8080 -AllowInsecureHttp
#Requires -Version 6.1
param(
    [string]$Server = 'http://localhost:8080',
    [string]$User = 'Administrator',
    [switch]$AllowInsecureHttp
)
$ErrorActionPreference = 'Stop'

function ConvertTo-PlainText([securestring]$Value)
{
    if ($null -eq $Value -or $Value.Length -eq 0)
    {
        return ''
    }

    return [System.Net.NetworkCredential]::new('', $Value).Password
}

function Assert-ServerUri([string]$ServerUri, [bool]$AllowInsecure)
{
    $uri = $null
    if (-not [Uri]::TryCreate($ServerUri, [UriKind]::Absolute, [ref]$uri))
    {
        throw "Server '$ServerUri' is not a valid absolute URI."
    }

    if ($uri.Scheme -eq 'https')
    {
        return
    }

    if ($uri.Scheme -ne 'http')
    {
        throw "Server must be http or https (got '$($uri.Scheme)')."
    }

    if ($uri.IsLoopback)
    {
        return
    }

    if (-not $AllowInsecure)
    {
        throw "HTTP is only allowed for loopback hosts. Use HTTPS, or pass -AllowInsecureHttp to send credentials over HTTP to $ServerUri."
    }

    Write-Warning "Sending login credentials over unencrypted HTTP to $ServerUri"
}

Assert-ServerUri -ServerUri $Server -AllowInsecure:$AllowInsecureHttp

& (Join-Path $PSScriptRoot 'pack.ps1')

$package = Get-ChildItem (Join-Path $PSScriptRoot 'artifacts') -Filter '*.dmxplugin' | Select-Object -First 1
if (-not $package)
{
    throw 'pack.ps1 produced no .dmxplugin package'
}

$users = Invoke-RestMethod "$Server/api/website/login-users"
$account = $users.data | Where-Object { $_.name -eq $User } | Select-Object -First 1
if (-not $account)
{
    throw "No user named '$User' on $Server (available: $(($users.data | ForEach-Object { $_.name }) -join ', '))"
}

$pinSecure = Read-Host "PIN for $User on $Server" -AsSecureString
$passwordSecure = Read-Host "Password for $User on $Server (empty if PIN-only)" -AsSecureString
$pin = ConvertTo-PlainText $pinSecure
$password = ConvertTo-PlainText $passwordSecure

$loginBody = @{ userId = $account.userId; pin = $pin; password = $password } | ConvertTo-Json
$login = Invoke-RestMethod -Method Post -Uri "$Server/api/website/login" -ContentType 'application/json' -Body $loginBody
if (-not $login.success)
{
    throw "Login failed: $($login.errorText)"
}

$headers = @{ Authorization = "Bearer $($login.data.token)" }
$response = Invoke-RestMethod -Method Post -Uri "$Server/api/website/plugins/upload" -Headers $headers -Form @{ file = $package }
if (-not $response.success)
{
    throw "Upload failed: $($response.errorText)"
}

$result = $response.data
if ($result.applied)
{
    Write-Host "Deployed $($result.code) $($result.version) -> $($result.state)"
    if ($result.error)
    {
        Write-Host "Plugin error: $($result.error)"
    }
}
else
{
    Write-Host "Staged $($result.code) $($result.version) — applied on next restart (Core in safe mode or pre-hot-reload version)"
}
