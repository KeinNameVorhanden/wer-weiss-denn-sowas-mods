<#
.SYNOPSIS
  End-user uninstall (Uninstall.cmd runs it): finds both games through Steam and removes exactly what
  the setup added (manifest-based, see Uninstall-FromGame.ps1). BepInEx is removed too when the setup
  installed it, including its logs and the mod settings. Game files and saves are never touched.
  Runs in Windows PowerShell 5.1 and PowerShell 7. Keep this file ASCII.
#>
param(
    [string]$Game1Dir,
    [string]$Game2Dir,
    [ValidateSet('Game1', 'Game2', 'Both')][string]$Game = 'Both',
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'SteamGames.ps1')

$found = @()
foreach ($g in ($WwdsGames | Where-Object { $Game -eq 'Both' -or $_.Key -eq $Game })) {
    $given = if ($g.Key -eq 'Game1') { $Game1Dir } else { $Game2Dir }
    $dir = if ($given) { $given } else { Find-SteamGame $g.AppId }
    if (-not (Test-GameDir $dir $g)) { continue }
    $dir = (Resolve-Path -LiteralPath $dir).Path
    if (-not (Test-Path -LiteralPath (Join-Path $dir '.wwds-install-manifest.txt'))) { continue }
    $found += [pscustomobject]@{ Game = $g; Dir = $dir }
}

if ($found.Count -eq 0) {
    'No installation of the WWDS harness found.'
    exit 0
}

$found | ForEach-Object { "Installed in: $($_.Dir)" }
if (-not $Yes) {
    $answer = Read-Host 'Remove the mods (and BepInEx, if the setup installed it) from these folders? (y/n)'
    if ($answer -notmatch '^(y|j)') { 'Cancelled.'; exit 0 }
}

foreach ($f in $found) {
    if (Get-Process -Name $f.Game.Exe -ErrorAction SilentlyContinue) {
        Write-Warning "$($f.Game.Name) is running: close it and run the uninstall again. Skipped."
        continue
    }

    & (Join-Path $here 'Uninstall-FromGame.ps1') -GameDir $f.Dir | Out-Host
}
