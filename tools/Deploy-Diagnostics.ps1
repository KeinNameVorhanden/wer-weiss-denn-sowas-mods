<#
.SYNOPSIS
  Stages the diagnostics plugin (+ optional autopilot config) and installs it into the game(s)
  through the manifest-based installer.
.PARAMETER Game          Game1, Game2 or Both.
.PARAMETER Autopilot     Write an autopilot-enabled config.
.PARAMETER DriveTeamB    Off | Correct | Wrong.
.PARAMETER RenameTeamB   Name for Team B's bot.
.PARAMETER TimeScale     Time scale during the show.
#>
param(
    [ValidateSet('Game1', 'Game2', 'Both')][string]$Game = 'Both',
    [switch]$Autopilot,
    [string]$DriveTeamB = 'Correct',
    [string]$RenameTeamB = 'WWDS Test',
    [double]$TimeScale = 3,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$local = Get-Content -LiteralPath (Join-Path $root 'build\local.props') -Encoding UTF8
$dirs = @{ Game1 = $local.Project.PropertyGroup.Game1Dir; Game2 = $local.Project.PropertyGroup.Game2Dir }

$stage = Join-Path $root 'artifacts\stage-diagnostics'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$pluginDir = Join-Path $stage 'BepInEx\plugins\WWDS.Diagnostics'
New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item -LiteralPath (Join-Path $root "src\Tools\WWDS.Diagnostics\bin\$Configuration\net35\WWDS.Diagnostics.dll") -Destination $pluginDir

$cfgDir = Join-Path $stage 'BepInEx\config'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
$ts = $TimeScale.ToString([System.Globalization.CultureInfo]::InvariantCulture)
@"
[Autopilot]
Enabled = $(if ($Autopilot) { 'true' } else { 'false' })
TimeScale = $ts
DriveTeamB = $DriveTeamB
RenameTeamB = $RenameTeamB
QuitAfterShow = true
ShowId = -1
"@ | Set-Content -LiteralPath (Join-Path $cfgDir 'wwds.diagnostics.cfg') -Encoding UTF8

$targets = if ($Game -eq 'Both') { @('Game1', 'Game2') } else { @($Game) }
foreach ($g in $targets) {
    & (Join-Path $PSScriptRoot 'Install-ToGame.ps1') -GameDir $dirs[$g] -Folder $stage -Update
}
