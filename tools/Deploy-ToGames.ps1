<#
.SYNOPSIS
  Installs the staged harness payload (artifacts/payload) into the detected game installs through the
  manifest-based installer. Optionally writes a test configuration (only if the harness config file
  does not exist yet or was written by this script before).
.PARAMETER Game        Game1, Game2 or Both.
.PARAMETER TestConfig  Path to a .cfg file to install as BepInEx/config/wwds.harness.cfg.
#>
param(
    [ValidateSet('Game1', 'Game2', 'Both')][string]$Game = 'Both',
    [string]$TestConfig
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$local = Get-Content -LiteralPath (Join-Path $root 'build\local.props') -Encoding UTF8
$dirs = @{ Game1 = $local.Project.PropertyGroup.Game1Dir; Game2 = $local.Project.PropertyGroup.Game2Dir }
$payload = Join-Path $root 'artifacts\payload'
if (-not (Test-Path -LiteralPath $payload)) { throw 'Run tools/Stage-Payload.ps1 first' }

$folders = @($payload)
if ($TestConfig) {
    $cfgStage = Join-Path $root 'artifacts\stage-config'
    if (Test-Path -LiteralPath $cfgStage) { Remove-Item -LiteralPath $cfgStage -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $cfgStage 'BepInEx\config') | Out-Null
    Copy-Item -LiteralPath $TestConfig -Destination (Join-Path $cfgStage 'BepInEx\config\wwds.harness.cfg')
    $folders += $cfgStage
}

$targets = if ($Game -eq 'Both') { @('Game1', 'Game2') } else { @($Game) }
foreach ($g in $targets) {
    & (Join-Path $PSScriptRoot 'Install-ToGame.ps1') -GameDir $dirs[$g] -Folder $folders -Update
}
