<#
.SYNOPSIS
  Builds a release zip in dist/: harness payload, bridge, setup/uninstall scripts, docs.
  Third-party binaries (BepInEx, RuntimeUnityEditor) and game files are NOT included; Setup.ps1
  installs BepInEx from zips the user downloads.
#>
param(
    [string]$Configuration = 'Release',
    # Optional Twitch application Client ID (Client Type: Public) baked into the bridge, so players can log
    # in without registering an app. Not a secret. Also read from WwdsTwitchClientId (env or build/local.props).
    [string]$TwitchClientId = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup[0].Version
$staged = & (Join-Path $PSScriptRoot 'Stage-Payload.ps1') -Configuration $Configuration -TwitchClientId $TwitchClientId
$staged | Where-Object { $_ -like 'Twitch Client ID*' }

$name = "WWDS-Harness-$version"
$out = Join-Path $root "dist\$name"
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Copy-Item -LiteralPath (Join-Path $root 'artifacts\payload') -Destination (Join-Path $out 'payload') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'artifacts\bridge') -Destination (Join-Path $out 'bridge') -Recurse
Get-ChildItem -LiteralPath (Join-Path $out 'bridge') -Filter '*.pdb' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
foreach ($s in 'Install.cmd', 'Uninstall.cmd', 'Setup.ps1', 'Uninstall.ps1', 'SteamGames.ps1', 'Install-ToGame.ps1', 'Uninstall-FromGame.ps1', 'Remove-FromGame.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $s) -Destination $out }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $out
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $out
New-Item -ItemType Directory -Force (Join-Path $out 'docs') | Out-Null
foreach ($d in 'installation.md', 'configuration.md', 'mods.md', 'twitch-setup.md', 'troubleshooting.md', 'compatibility.md', 'architecture.md', 'extending.md', 'reference-config.cfg') {
    $src = Join-Path $root "docs\$d"
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $out 'docs') }
}

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($out, $zip)
"Release: $zip ($([math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 2)) MB)"
