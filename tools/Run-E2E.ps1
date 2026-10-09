<#
.SYNOPSIS
  End-to-end run: starts WWDS.Bridge in mock mode with a traffic script, runs the game until it exits,
  stops the bridge and prints both logs' key lines.
#>
param(
    [ValidateSet('Game1', 'Game2')][string]$Game = 'Game1',
    [Parameter(Mandatory = $true)][string]$Script,
    [int]$TimeoutMinutes = 40
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bridgeExe = Join-Path $root 'artifacts\bridge\WWDS.Bridge.exe'
$bridgeLog = Join-Path $root 'artifacts\bridge-e2e.log'
$scriptPath = (Resolve-Path -LiteralPath $Script).Path

$emptyInput = Join-Path $root 'artifacts\empty-stdin.txt'
Set-Content -LiteralPath $emptyInput -Value '' -NoNewline
$bridge = Start-Process -FilePath $bridgeExe -ArgumentList @('--mock', '--script', "`"$scriptPath`"") -RedirectStandardOutput $bridgeLog -RedirectStandardInput $emptyInput -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 3
try {
    & (Join-Path $PSScriptRoot 'Run-Game.ps1') -Game $Game -TimeoutMinutes $TimeoutMinutes
}
finally {
    if (-not $bridge.HasExited) { Stop-Process -Id $bridge.Id -Force }
}

"---- bridge log ($bridgeLog)"
Get-Content -LiteralPath $bridgeLog -Encoding UTF8 | Where-Object { $_ -notmatch 'mock: sent' } | Select-Object -Last 60
