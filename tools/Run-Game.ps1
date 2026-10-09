<#
.SYNOPSIS
  Starts a game, waits until it exits (or the timeout passes, then kills it) and prints the newest
  diagnostics trace summary. Used for unattended autopilot runs.
#>
param(
    [ValidateSet('Game1', 'Game2')][string]$Game = 'Game1',
    [int]$TimeoutMinutes = 45
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$local = Get-Content -LiteralPath (Join-Path $root 'build\local.props') -Encoding UTF8
$dir = if ($Game -eq 'Game1') { $local.Project.PropertyGroup.Game1Dir } else { $local.Project.PropertyGroup.Game2Dir }
$exe = Get-ChildItem -LiteralPath $dir -Filter '*.exe' | Where-Object Name -NotMatch 'Crash' | Select-Object -First 1

$p = Start-Process -FilePath $exe.FullName -WorkingDirectory $dir -PassThru
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
if (-not $p.HasExited) {
    "TIMEOUT after $TimeoutMinutes min, killing game"
    Stop-Process -Id $p.Id -Force
} else {
    "Game exited with code $($p.ExitCode)"
}

$log = Join-Path $dir 'BepInEx\LogOutput.log'
$harness = Get-Content -LiteralPath $log -Encoding UTF8 | Where-Object { $_ -match 'WWDS Harness\]' }
"Harness log lines: $($harness.Count)"
$harness | Where-Object { $_ -match 'Error|Warning|failed|Show finished|Quitting|NOT APPLIED|timed out' } | Select-Object -Last 40