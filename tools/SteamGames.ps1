<#
.SYNOPSIS
  Shared helpers for the end-user scripts (dot-source this file): the supported games and how to find
  them through Steam (registry -> libraryfolders.vdf -> appmanifest_<id>.acf). Works in Windows
  PowerShell 5.1 and PowerShell 7. Keep this file ASCII: Windows PowerShell reads BOM-less scripts as ANSI.
#>

$sz = [char]0x00DF
$WwdsGames = @(
    [pscustomobject]@{ Key = 'Game1'; Name = "Wer wei${sz} denn sowas? - Das Spiel"; AppId = 782130; Arch = 'x86'; Exe = "Wer Wei${sz} Denn Sowas" },
    [pscustomobject]@{ Key = 'Game2'; Name = "Wer wei${sz} denn sowas? - Das 2. Spiel"; AppId = 1112380; Arch = 'x64'; Exe = 'WWDS2' }
)

function Get-SteamLibraries {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $p = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        foreach ($v in @($p.SteamPath, $p.InstallPath)) { if ($v) { $roots.Add(($v -replace '/', '\')) } }
    }

    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($r in ($roots | Select-Object -Unique)) {
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf)) { continue }
        $libs.Add($r)
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
        }
    }

    $libs | Select-Object -Unique
}

function Find-SteamGame([int]$AppId) {
    foreach ($lib in Get-SteamLibraries) {
        $acf = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path -LiteralPath $acf)) { continue }
        $m = [regex]::Match((Get-Content -LiteralPath $acf -Raw -Encoding UTF8), '"installdir"\s+"([^"]+)"')
        if (-not $m.Success) { continue }
        $dir = Join-Path $lib ('steamapps\common\' + $m.Groups[1].Value)
        if (Test-Path -LiteralPath $dir) { return $dir }
    }

    return $null
}

function Test-GameDir([string]$Dir, $Game) {
    $Dir -and (Test-Path -LiteralPath (Join-Path $Dir ($Game.Exe + '.exe')))
}
