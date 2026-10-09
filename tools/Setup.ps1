<#
.SYNOPSIS
  End-user setup for the WWDS harness and mods (ships in the release zip; Install.cmd runs it).
  Finds both games through Steam, installs BepInEx 5.4.23.5 where it is missing (downloaded from the
  official GitHub release and checked against a pinned SHA-256, or taken from -BepInExX86Zip /
  -BepInExX64Zip or the downloads folder next to this script), then installs or updates the harness.
  Nothing that already exists in a game folder is overwritten; everything added is recorded in
  <game>\.wwds-install-manifest.txt for Uninstall.cmd / Uninstall-FromGame.ps1.
  Runs in Windows PowerShell 5.1 (built into Windows) and PowerShell 7. Keep this file ASCII.

.PARAMETER Game1Dir / Game2Dir  Game folders when Steam does not know them.
.PARAMETER NoDownload           Never download; use zips passed in or found in .\downloads.
.PARAMETER Unattended           No questions: skip games that are not found or running.

.EXAMPLE
  .\Install.cmd
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Setup.ps1 -Game Game2 -Unattended
#>
param(
    [string]$Game1Dir,
    [string]$Game2Dir,
    [string]$BepInExX86Zip,
    [string]$BepInExX64Zip,
    [string]$InspectorZip,
    [ValidateSet('Game1', 'Game2', 'Both')][string]$Game = 'Both',
    [switch]$NoDownload,
    [switch]$Unattended
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'SteamGames.ps1')
$installer = Join-Path $here 'Install-ToGame.ps1'
$payload = Join-Path $here 'payload'
$downloads = Join-Path $here 'downloads'

# BepInEx 5.4.23.5 from https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5 (hashes of the release assets).
$BepInExVersion = '5.4.23.5'
$BepInEx = @{
    x86 = [pscustomobject]@{ File = "BepInEx_win_x86_$BepInExVersion.zip"; Sha256 = '37651C79E40D6F909572A4F461AC25350BB3EF8FE7FBD29F1AA8791A33B84C82' }
    x64 = [pscustomobject]@{ File = "BepInEx_win_x64_$BepInExVersion.zip"; Sha256 = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4' }
}

function Write-Step([string]$text) { Write-Host $text -ForegroundColor Cyan }

function Get-BepInExZip([string]$arch, [string]$given) {
    $spec = $BepInEx[$arch]
    if ($given) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $given).Hash
        if ($hash -ne $spec.Sha256) { Write-Warning "$given is not the expected BepInEx $BepInExVersion $arch zip (SHA-256 differs); using it anyway." }
        return (Resolve-Path -LiteralPath $given).Path
    }

    $cached = Join-Path $downloads $spec.File
    if ((Test-Path -LiteralPath $cached) -and (Get-FileHash -Algorithm SHA256 -LiteralPath $cached).Hash -eq $spec.Sha256) { return $cached }
    if ($NoDownload) { throw "BepInEx $arch is missing. Put $($spec.File) into $downloads or pass -BepInEx$($arch.ToUpper())Zip." }

    $url = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/$($spec.File)"
    Write-Step "Downloading $url"
    New-Item -ItemType Directory -Force $downloads | Out-Null
    $partial = Join-Path $downloads ($spec.File + '.part')
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $old = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $partial }
    finally { $ProgressPreference = $old }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $partial).Hash
    if ($hash -ne $spec.Sha256) {
        Remove-Item -LiteralPath (Resolve-Path -LiteralPath $partial).Path -Force
        throw "Downloaded $($spec.File) has an unexpected SHA-256 ($hash). Nothing was installed."
    }

    Move-Item -LiteralPath $partial -Destination $cached -Force
    return $cached
}

function Resolve-GameDir($g, [string]$given) {
    if ($given) {
        if (Test-GameDir $given $g) { return (Resolve-Path -LiteralPath $given).Path }
        throw "$given does not contain $($g.Exe).exe"
    }

    $dir = Find-SteamGame $g.AppId
    if (Test-GameDir $dir $g) { return $dir }
    if ($Unattended) { return $null }
    Write-Host "$($g.Name) was not found through Steam."
    while ($true) {
        $answer = Read-Host "Game folder (contains $($g.Exe).exe), or Enter to skip"
        if (-not $answer) { return $null }
        $answer = $answer.Trim('"', ' ')
        if (Test-GameDir $answer $g) { return (Resolve-Path -LiteralPath $answer).Path }
        Write-Warning "$($g.Exe).exe is not in $answer"
    }
}

function Wait-GameClosed($g) {
    while (Get-Process -Name $g.Exe -ErrorAction SilentlyContinue) {
        if ($Unattended) { return $false }
        Read-Host "$($g.Name) is running. Close it, then press Enter (files in use cannot be replaced)" | Out-Null
    }

    return $true
}

$selected = $WwdsGames | Where-Object { $Game -eq 'Both' -or $_.Key -eq $Game }
$done = @()
foreach ($g in $selected) {
    $given = if ($g.Key -eq 'Game1') { $Game1Dir } else { $Game2Dir }
    $dir = Resolve-GameDir $g $given
    if (-not $dir) {
        if ($Game -ne 'Both' -or $given) { Write-Warning "$($g.Name): not found, skipped." }
        else { Write-Host "$($g.Name): not installed, skipped." }
        continue
    }

    Write-Step "== $($g.Name): $dir"
    if (-not (Wait-GameClosed $g)) {
        Write-Warning "$($g.Name) is running: skipped."
        continue
    }

    $zips = @()
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'BepInEx\core\BepInEx.dll'))) {
        $zips += Get-BepInExZip $g.Arch $(if ($g.Arch -eq 'x86') { $BepInExX86Zip } else { $BepInExX64Zip })
        Write-Host "Installing BepInEx $BepInExVersion ($($g.Arch))"
    }
    else {
        Write-Host 'BepInEx already installed.'
    }

    if ($InspectorZip -and -not (Test-Path -LiteralPath (Join-Path $dir 'BepInEx\plugins\RuntimeUnityEditor'))) { $zips += (Resolve-Path -LiteralPath $InspectorZip).Path }
    if ($zips.Count -gt 0) { & $installer -GameDir $dir -Zip $zips -Update | Out-Host }
    & $installer -GameDir $dir -Folder $payload -Update | Out-Host
    $done += $g.Name
}

if ($done.Count -eq 0) {
    Write-Warning 'Nothing was installed: no supported game found. Pass -Game1Dir / -Game2Dir.'
    exit 1
}

Write-Step ('Done: ' + ($done -join ', '))
'Start the game: F10 opens the mod menu, Escape the ESC menu. Twitch: docs\twitch-setup.md.'
