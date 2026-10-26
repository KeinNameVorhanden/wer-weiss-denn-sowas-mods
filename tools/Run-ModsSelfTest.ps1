<#
.SYNOPSIS
  Runtime check of the player-facing mods: installs the staged payload, switches on the unattended
  self-test (ModsSelfTestFeature) for one run, starts the game silent in a small background window and
  waits until the test reports DONE. Afterwards only what the test changed is put back: its config keys,
  Unity's window settings in the registry, and the saves the test played.
  The backup lives in artifacts\selftest-backup\<game>; if a run is interrupted (runner stopped), the
  next run repairs it first, or run this script with -RestoreOnly.
  A key or click inside the focused game aborts the test: the game stays open (and silent) for you and
  your saves are kept.
.PARAMETER Game         Game1 or Game2.
.PARAMETER Timeout      Seconds before the game is closed.
.PARAMETER NoStage      Skip staging (use the current artifacts/payload).
.PARAMETER Mode         Full | Screens (menu captures) | Catalogue (show 31 from the other game) | Custom (a temporary own question pack) | Long (long show with 2 short boards, resume).
.PARAMETER RestoreOnly  Only undo an interrupted run.
#>
param(
    [ValidateSet('Game1', 'Game2')][string]$Game = 'Game1',
    [int]$Timeout = 420,
    [switch]$NoStage,
    [ValidateSet('Full', 'Screens', 'Catalogue', 'Custom', 'Long')][string]$Mode = 'Full',
    [string]$ScreenshotDir = '',
    [switch]$RestoreOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$local = Get-Content -LiteralPath (Join-Path $root 'build\local.props') -Encoding UTF8
$gameDir = if ($Game -eq 'Game1') { $local.Project.PropertyGroup.Game1Dir } else { $local.Project.PropertyGroup.Game2Dir }
$exe = Get-ChildItem -LiteralPath $gameDir -Filter '*.exe' | Where-Object { $_.Name -notmatch 'UnityCrashHandler' } | Select-Object -First 1
$product = if ($Game -eq 'Game1') { 'Wer Wei' + [char]0x00DF + ' Denn Sowas' } else { 'WWDS2' }
$saveDir = Join-Path $env:USERPROFILE ('AppData\LocalLow\BitComposer\' + $product)
$regPath = "HKCU:\Software\BitComposer\$product"
$cfg = Join-Path $gameDir 'BepInEx\config\wwds.harness.cfg'
$log = Join-Path $gameDir 'BepInEx\LogOutput.log'
$backup = Join-Path $root ('artifacts\selftest-backup\' + $Game)
$pending = Join-Path $backup 'pending.txt'
$packDir = Join-Path $gameDir 'BepInEx\wwds\questions\zz-selftest'
$dataDir = Join-Path $gameDir 'BepInEx\wwds'
$voiceDir = Join-Path $gameDir 'BepInEx\wwds\voices\zz-selftest'
# Mod data files a run may change (pack show ids, long show boards): put back exactly as they were.
$dataFiles = @('question-packs.txt', 'long-games.txt')

# Keys the test sets (section, key, value). Only these are ever put back.
$testKeys = @(
    @('Features', 'ModsSelfTest', 'true'),
    @('ModsSelfTest', 'Mode', $Mode),
    @('ModsSelfTest', 'ScreenshotDir', $ScreenshotDir),
    @('FirstRun', 'Shown', $(if ($Mode -eq 'Screens') { 'false' } else { 'true' })),
    @('FirstRun', 'ShowAtEveryStart', 'false'),
    @('SkipIntro', 'SkipShowIntro', 'true'),
    @('SkipIntro', 'SkipLogos', 'true'),
    @('Twitch', 'Mode', 'Off'),
    @('General', 'RunInBackground', 'true'),
    @('Game', 'ManageDisplay', 'false'),
    @('Game', 'MuteInBackground', 'false'),
    @('Game', 'MasterVolume', '1'),
    @('Quickplay', 'Enabled', 'true'),
    @('Quickplay', 'TeamA1', 'Keyboard'),
    @('Quickplay', 'TeamA2', 'Empty'),
    @('Quickplay', 'TeamB1', 'Bot'),
    @('Quickplay', 'TeamB2', 'Empty'),
    @('Quickplay', 'ShowId', $(if ($Mode -eq 'Catalogue') { '31' } else { '-1' })),
    @('Catalogue', 'OtherGameShows', 'true'),
    @('LongGame', 'Boards', $(if ($Mode -eq 'Long') { '2' } else { '1' })),
    @('LongGame', 'Source', 'NextUnplayed'),
    @('Quickplay', 'QuickResume', 'true'),
    @('Opponent', 'Active', 'false'),
    @('Debug', 'AutoStartShow', 'false')
)

function Get-Ini([string]$path, [string]$section, [string]$key) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $inSection = $false
    foreach ($line in (Get-Content -LiteralPath $path -Encoding UTF8)) {
        $l = $line.Trim()
        if ($l.StartsWith('[')) { $inSection = ($l -eq "[$section]"); continue }
        if ($inSection -and $l -match ('^' + [regex]::Escape($key) + '\s*=\s*(.*)$')) { return $Matches[1] }
    }
    return $null
}

function Set-Ini([string]$path, [string]$section, [string]$key, [string]$value) {
    $lines = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $path) { $lines.AddRange([string[]](Get-Content -LiteralPath $path -Encoding UTF8)) }
    $inSection = $false
    $sectionIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i].Trim()
        if ($l.StartsWith('[')) { $inSection = ($l -eq "[$section]"); if ($inSection) { $sectionIndex = $i }; continue }
        if ($inSection -and $l -match ('^' + [regex]::Escape($key) + '\s*=')) { $lines[$i] = "$key = $value"; Set-Content -LiteralPath $path -Value $lines -Encoding UTF8; return }
    }
    if ($sectionIndex -lt 0) { $lines.Add(''); $lines.Add("[$section]"); $lines.Add("$key = $value") }
    else { $lines.Insert($sectionIndex + 1, "$key = $value") }
    Set-Content -LiteralPath $path -Value $lines -Encoding UTF8
}

function Remove-Ini([string]$path, [string]$section, [string]$key) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]](Get-Content -LiteralPath $path -Encoding UTF8))
    $inSection = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i].Trim()
        if ($l.StartsWith('[')) { $inSection = ($l -eq "[$section]"); continue }
        if ($inSection -and $l -match ('^' + [regex]::Escape($key) + '\s*=')) { $lines.RemoveAt($i); break }
    }
    Set-Content -LiteralPath $path -Value $lines -Encoding UTF8
}

# Undo a run from the backup on disk: the test's config keys (from the config copy taken before the run),
# Unity's window values (registry.json) and, unless a person played, the saves.
function Restore-FromBackup([bool]$restoreSaves) {
    $cfgCopy = Join-Path $backup 'wwds.harness.cfg'
    foreach ($k in $testKeys) {
        $original = Get-Ini $cfgCopy $k[0] $k[1]
        if ($null -ne $original) { Set-Ini $cfg $k[0] $k[1] $original }
        else { Remove-Ini $cfg $k[0] $k[1] }   # was not there: BepInEx writes its default again
    }
    if (-not (Test-Path -LiteralPath $cfgCopy) -and (Test-Path -LiteralPath $cfg)) { Set-Ini $cfg 'Features' 'ModsSelfTest' 'false' }

    $regFile = Join-Path $backup 'registry.json'
    if (Test-Path -LiteralPath $regFile) {
        foreach ($v in (Get-Content -LiteralPath $regFile -Raw | ConvertFrom-Json)) {
            Set-ItemProperty -LiteralPath $regPath -Name $v.Name -Value ([int]$v.Value) -Type DWord
        }
    }

    if ($restoreSaves) {
        $savedFiles = @(Get-ChildItem -LiteralPath (Join-Path $backup 'saves') -File -Filter '*.bin' | ForEach-Object { $_.Name })
        if (Test-Path -LiteralPath $saveDir) {
            foreach ($f in (Get-ChildItem -LiteralPath $saveDir -File -Filter '*.bin')) {
                if ($savedFiles -notcontains $f.Name) { Remove-Item -LiteralPath $f.FullName -Force }
            }
        }
        foreach ($f in $savedFiles) { Copy-Item -LiteralPath (Join-Path (Join-Path $backup 'saves') $f) -Destination (Join-Path $saveDir $f) -Force }
    }

    # The temporary question pack and its show id.
    if (Test-Path -LiteralPath $packDir) { Remove-Item -LiteralPath $packDir -Recurse -Force }
    if (Test-Path -LiteralPath $voiceDir) { Remove-Item -LiteralPath $voiceDir -Recurse -Force }
    foreach ($name in $dataFiles) {
        $live = Join-Path $dataDir $name
        $copy = Join-Path (Join-Path $backup 'data') $name
        if (Test-Path -LiteralPath $copy) { Copy-Item -LiteralPath $copy -Destination $live -Force }
        elseif ((Test-Path -LiteralPath (Join-Path (Join-Path $backup 'data') ($name + '.absent'))) -and (Test-Path -LiteralPath $live)) { Remove-Item -LiteralPath $live -Force }
    }

    Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue
}

# An earlier run was interrupted: repair it before anything else (saves kept: someone may have played).
if (Test-Path -LiteralPath $pending) {
    Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object {
        throw "The game is running ($($_.ProcessName)). Close it, then run again to repair the interrupted test run."
    }
    'Repairing an interrupted self-test run.'
    Restore-FromBackup $false
}
if ($RestoreOnly) { 'Nothing else to do.'; return }

if (-not $NoStage) { & (Join-Path $PSScriptRoot 'Stage-Payload.ps1') | Out-Null }
& (Join-Path $PSScriptRoot 'Deploy-ToGames.ps1') -Game $Game | Out-Null

# Backup: config copy, saves, Unity window values. The pending marker means "not restored yet".
if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $backup 'saves') | Out-Null
if (Test-Path -LiteralPath $cfg) { Copy-Item -LiteralPath $cfg -Destination (Join-Path $backup 'wwds.harness.cfg') }
if (Test-Path -LiteralPath $saveDir) {
    Get-ChildItem -LiteralPath $saveDir -File -Filter '*.bin' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $backup 'saves') }
}
$regValues = @()
if (Test-Path -LiteralPath $regPath) {
    $regKey = Get-Item -LiteralPath $regPath
    foreach ($name in $regKey.GetValueNames()) {
        if ($name -like 'Screenmanager*' -or $name -like 'UnitySelectMonitor*') { $regValues += [pscustomobject]@{ Name = $name; Value = $regKey.GetValue($name) } }
    }
}
ConvertTo-Json -InputObject @($regValues) | Set-Content -LiteralPath (Join-Path $backup 'registry.json') -Encoding UTF8
New-Item -ItemType Directory -Force (Join-Path $backup 'data') | Out-Null
foreach ($name in $dataFiles) {
    $live = Join-Path $dataDir $name
    if (Test-Path -LiteralPath $live) { Copy-Item -LiteralPath $live -Destination (Join-Path (Join-Path $backup 'data') $name) }
    else { Set-Content -LiteralPath (Join-Path (Join-Path $backup 'data') ($name + '.absent')) -Value 'none' }
}
Set-Content -LiteralPath $pending -Value (Get-Date -Format o)

if ($Mode -eq 'Custom') {
    # 12 questions (half with a video file that does not exist: must be skipped), a final, an estimate.
    New-Item -ItemType Directory -Force $packDir | Out-Null
    $rows = @('Typ;Kategorie;Thema;Frage;A;B;C;Richtig;Video;Reaktion')
    for ($i = 1; $i -le 12; $i++) {
        $video = if ($i % 2 -eq 0) { 'videos/fehlt.mp4' } else { '' }
        $rows += "Frage;Test $i;Tiere;Selbsttest Frage $i?;Alpha;Beta;Gamma;C;$video;3"
    }
    $rows += 'Finale;Finale;Weltweit;Selbsttest Finale?;Eins;Zwei;Drei;B;;'
    $rows += 'Schaetzfrage;;;Selbsttest Schaetzung?;;;;250;;'
    Set-Content -LiteralPath (Join-Path $packDir 'questions.csv') -Value $rows -Encoding UTF8

    # An own voice line: 0.5 s of a quiet 440 Hz tone as 16-bit mono WAV.
    New-Item -ItemType Directory -Force $voiceDir | Out-Null
    $rate = 22050
    $count = [int]($rate / 2)
    $ms = [System.IO.MemoryStream]::new()
    $w = [System.IO.BinaryWriter]::new($ms)
    $w.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + 2 * $count)); $w.Write([System.Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
    $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]$rate); $w.Write([int](2 * $rate)); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([System.Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int](2 * $count))
    for ($i = 0; $i -lt $count; $i++) { $w.Write([int16](3000 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $rate))) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $voiceDir 'wwds_selftest_line.wav'), $ms.ToArray())
}

foreach ($k in $testKeys) { Set-Ini $cfg $k[0] $k[1] $k[2] }

Add-Type -Namespace WwdsTest -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr after, int x, int y, int cx, int cy, uint flags);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
'@
$previous = [WwdsTest.Win]::GetForegroundWindow()

# The self-test keeps the game silent; here: a window that stays behind whatever you are doing.
$size = if ($Mode -eq 'Screens') { @('1920', '1080') } else { @('1280', '720') }
$gameArgs = @('-screen-fullscreen', '0', '-screen-width', $size[0], '-screen-height', $size[1])
$started = Get-Date
$aborted = $false
$proc = Start-Process -FilePath $exe.FullName -ArgumentList $gameArgs -WorkingDirectory $gameDir -PassThru -WindowStyle Minimized
try {
    # Unity creates (and may re-show) its window after start: keep it in the background without activating it.
    $pushUntil = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $pushUntil -and -not $proc.HasExited) {
        $proc.Refresh()
        $hwnd = $proc.MainWindowHandle
        if ($hwnd -ne [IntPtr]::Zero) {
            if ($Mode -eq 'Screens') {
                # Screenshots need a rendered window: visible, at the bottom, not active.
                [void][WwdsTest.Win]::ShowWindow($hwnd, 4)
                [void][WwdsTest.Win]::SetWindowPos($hwnd, [IntPtr]1, 0, 0, 0, 0, 0x0013)
            }
            else {
                [void][WwdsTest.Win]::ShowWindow($hwnd, 7)
            }
            if ($previous -ne [IntPtr]::Zero -and [WwdsTest.Win]::GetForegroundWindow() -eq $hwnd) { [void][WwdsTest.Win]::SetForegroundWindow($previous) }
        }
        Start-Sleep -Milliseconds 500
    }

    while (-not $proc.HasExited -and ((Get-Date) - $started).TotalSeconds -lt $Timeout) {
        Start-Sleep -Seconds 3
        if (-not (Test-Path -LiteralPath $log)) { continue }
        if (Select-String -LiteralPath $log -Pattern '\[SelfTest\] ABORTED' -Quiet) {
            # Someone took over the game: never close it, wait until they do.
            $aborted = $true
            'Self-test aborted by user input; waiting for the game to be closed.'
            $proc.WaitForExit()
            break
        }
        if (Select-String -LiteralPath $log -Pattern '\[SelfTest\] DONE' -Quiet) {
            Start-Sleep -Seconds 5
            break
        }
    }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Seconds 2
    Restore-FromBackup (-not $aborted)
}

$lines = if (Test-Path -LiteralPath $log) { Select-String -LiteralPath $log -Pattern '\[SelfTest\]|\[Mods\]|Exception' | ForEach-Object { $_.Line } } else { @('no log') }
$lines
if (-not ($lines -match 'DONE all passed')) { exit 1 }
